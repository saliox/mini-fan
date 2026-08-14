using System;
using System.Diagnostics;
using System.Linq;

namespace MiniFan
{
    /// <summary>
    /// Boucle de décision : lit les températures, détecte les jeux, et pilote le
    /// Cooler Boost avec hystérésis (seuils ON/OFF distincts + durée minimum)
    /// pour éviter que les ventilateurs fassent du yo-yo.
    /// L'écriture EC ne se fait qu'aux TRANSITIONS de l'état voulu, pour respecter
    /// un appui manuel FN+haut de l'utilisateur entre deux décisions.
    /// </summary>
    public class FanController
    {
        private readonly Config _cfg;
        private readonly EcBridge _ec;
        private DateTime _boostSince = DateTime.MinValue;
        private bool? _lastWant;
        private int _tick;
        private bool _cpuEverSeen;
        private bool _gpuEverSeen;
        private int _cpuFailStreak;
        private int _gpuFailStreak;
        // Nombre de ticks d'échec CONSÉCUTIFS (capteur déjà vu auparavant qui ne répond plus)
        // à partir duquel on considère la panne comme PERSISTANTE plutôt que comme un simple
        // glitch WMI/EC transitoire (voir Tick()).
        private const int PersistentFailureThreshold = 3;
        private string[] _gameNames = new string[0];
        private string _gamesRaw;

        public int? Cpu { get; private set; }
        public int? Gpu { get; private set; }
        public bool BoostActive { get; private set; }
        public bool GameDetected { get; private set; }
        public string GameName { get; private set; }
        public string Reason { get; private set; }

        public event Action Updated;

        public FanController(Config cfg, EcBridge ec)
        {
            _cfg = cfg;
            _ec = ec;
            GameName = "";
            Reason = "";
        }

        public void Tick()
        {
            _tick++;
            Cpu = _ec.CpuTemp();
            Gpu = _ec.GpuTemp();

            if (_cfg.GameBoost && (_tick % 2 == 1)) DetectGame();
            else if (!_cfg.GameBoost) { GameDetected = false; GameName = ""; }

            bool want;
            if (_cfg.Mode == "boost") { want = true; Reason = "mode manuel"; }
            else if (_cfg.Mode == "silent") { want = false; Reason = "boost désactivé"; }
            else
            {
                if (Cpu.HasValue) { _cpuEverSeen = true; _cpuFailStreak = 0; }
                else if (_cpuEverSeen) _cpuFailStreak++;
                if (Gpu.HasValue) { _gpuEverSeen = true; _gpuFailStreak = 0; }
                else if (_gpuEverSeen) _gpuFailStreak++;

                bool hot = (Cpu.HasValue && Cpu.Value >= _cfg.CpuOn) || (Gpu.HasValue && Gpu.Value >= _cfg.GpuOn);
                // Un capteur jamais vu (absent sur ce matériel, ex. pas de GPU dédié) est ignoré
                // du calcul de "cool". Un capteur déjà vu mais dont la lecture échoue CE tick
                // (glitch WMI/EC transitoire) est en revanche traité comme "pas cool" : on ne
                // laisse pas une lecture manquante couper le Cooler Boost par erreur pendant une
                // vraie surchauffe (fail-safe : on préfère continuer à refroidir dans le doute).
                bool cpuCool = Cpu.HasValue ? Cpu.Value <= _cfg.CpuOff : !_cpuEverSeen;
                bool gpuCool = Gpu.HasValue ? Gpu.Value <= _cfg.GpuOff : !_gpuEverSeen;
                bool cool = cpuCool && gpuCool;
                // Panne PERSISTANTE (plusieurs ticks consécutifs, pas un simple glitch) d'un
                // capteur déjà vu auparavant : on ne peut plus se fier à sa lecture ni pour
                // "hot" ni pour "cool". Symétrique au fail-safe ci-dessus (qui ne s'applique
                // que si le boost est DÉJÀ actif) : sans ça, un capteur qui casse pendant que
                // le boost est inactif (idle) ne déclenchait jamais rien et laissait chauffer
                // la machine en silence. On préfère sur-refroidir (déclencher le boost) que
                // laisser un capteur mort masquer une vraie surchauffe.
                bool sensorFailurePersistent =
                    (_cpuEverSeen && !Cpu.HasValue && _cpuFailStreak >= PersistentFailureThreshold) ||
                    (_gpuEverSeen && !Gpu.HasValue && _gpuFailStreak >= PersistentFailureThreshold);
                bool prev = _lastWant.HasValue && _lastWant.Value;
                if (prev)
                {
                    bool heldLongEnough = (DateTime.UtcNow - _boostSince).TotalSeconds >= _cfg.MinBoostSeconds;
                    want = !(cool && !GameDetected && heldLongEnough);
                }
                else
                {
                    want = hot || GameDetected || sensorFailurePersistent;
                }
                Reason = GameDetected ? "jeu détecté : " + GameName
                       : hot ? "température élevée"
                       : sensorFailurePersistent ? "capteur en échec persistant — refroidissement de sécurité"
                       : want ? "refroidissement en cours" : "";
            }

            if (_ec.WriteSupported)
            {
                // On lit l'état matériel RÉEL : après une veille/reprise ou un appui FN
                // manuel qui remet le bit EC à zéro, le cache _lastWant ne suffit plus.
                // Si la lecture échoue, on retombe sur l'ancienne comparaison au cache.
                bool? actual;
                try { actual = _ec.GetCoolerBoost(); }
                catch { actual = null; }

                bool stateDiffers = actual.HasValue
                    ? (actual.Value != want)
                    : (!_lastWant.HasValue || _lastWant.Value != want);

                if (stateDiffers)
                {
                    if (_ec.SetCoolerBoost(want))
                    {
                        _lastWant = want;
                        if (want) _boostSince = DateTime.UtcNow;
                    }
                }
                else
                {
                    // État déjà conforme : on tient juste le cache à jour (et on démarre
                    // le chrono d'hystérésis si le boost vient d'être considéré actif).
                    if (want && (!_lastWant.HasValue || !_lastWant.Value))
                        _boostSince = DateTime.UtcNow;
                    _lastWant = want;
                }

                BoostActive = actual.HasValue ? actual.Value : want;
            }
            else
            {
                _lastWant = want;
                BoostActive = false;
            }

            var h = Updated;
            if (h != null) h();
        }

        private void DetectGame()
        {
            if (!ReferenceEquals(_gamesRaw, _cfg.Games))
            {
                _gamesRaw = _cfg.Games;
                _gameNames = (_gamesRaw ?? "")
                    .Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().ToLowerInvariant())
                    .Select(s => s.EndsWith(".exe") ? s.Substring(0, s.Length - 4) : s)
                    .Where(s => s.Length > 1)
                    .ToArray();
            }
            GameDetected = false;
            GameName = "";
            if (_gameNames.Length == 0) return;
            Process[] procs;
            // GetProcesses() peut lever (accès refusé, énumération transitoire) : en cas
            // d'échec on renvoie « aucun jeu » plutôt que de faire planter la boucle.
            try { procs = Process.GetProcesses(); }
            catch { return; }
            foreach (var p in procs)
            {
                try
                {
                    string n = p.ProcessName.ToLowerInvariant();
                    if (_gameNames.Contains(n))
                    {
                        GameDetected = true;
                        GameName = p.ProcessName;
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
    }
}
