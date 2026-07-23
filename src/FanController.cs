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
        private string[] _gameNames = new string[0];
        private string _gamesRaw;

        public int? Cpu { get; private set; }
        public int? Gpu { get; private set; }
        public bool BoostActive { get; private set; }
        // Vrai quand la dernière lecture EC du bit Cooler Boost a échoué : BoostActive
        // ci-dessus n'a alors PAS été mis à jour depuis le cache/want (cf. Tick()) et
        // reflète seulement la dernière valeur RÉELLEMENT confirmée. À surfacer dans l'UI.
        public bool BoostStateUnknown { get; private set; }
        // Vrai quand le capteur de température CPU n'a produit aucune lecture exploitable
        // depuis un nombre significatif de sondages consécutifs alors que l'EC est
        // pourtant piloté avec succès (WriteSupported) : indique un état thermique
        // inconnu (WMI cassé en cours de route, modèle non supporté pour ce registre),
        // à distinguer d'une simple absence de GPU dédié (qui, elle, est normale et
        // silencieuse). Ne change PAS la décision de boost : sert uniquement à rendre
        // la panne de supervision visible plutôt que de la masquer en "tout va bien".
        public bool ThermalMonitoringFailed { get; private set; }
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

            // Suivi des échecs de lecture CPU pour détecter une supervision thermique
            // cassée (cf. ThermalMonitoringFailed ci-dessus). On ne fait pas ce suivi côté
            // GPU : un GPU dédié durablement absent est un cas normal (portable sans dGPU)
            // qu'on ne peut pas distinguer, avec les données dont on dispose ici, d'un
            // capteur GPU réellement en panne — on ne veut pas inventer une heuristique
            // matérielle non vérifiable pour ça.
            if (Cpu.HasValue) _cpuFailStreak = 0;
            else if (_cpuFailStreak < int.MaxValue) _cpuFailStreak++;
            // Seuil ~= 60s de sondages ratés consécutifs (ou au moins 5 sondages), quel
            // que soit PollSeconds. On n'exige la supervision que si l'EC est par ailleurs
            // piloté avec succès : si WriteSupported est faux, l'UI l'indique déjà
            // séparément ("PILOTAGE KO").
            int failThreshold = Math.Max(5, 60 / Math.Max(1, _cfg.PollSeconds));
            ThermalMonitoringFailed = _ec.WriteSupported && _cpuFailStreak >= failThreshold;

            if (_cfg.GameBoost && (_tick % 2 == 1)) DetectGame();
            else if (!_cfg.GameBoost) { GameDetected = false; GameName = ""; }

            bool want;
            if (_cfg.Mode == "boost") { want = true; Reason = "mode manuel"; }
            else if (_cfg.Mode == "silent") { want = false; Reason = "boost désactivé"; }
            else
            {
                if (Cpu.HasValue) _cpuEverSeen = true;
                if (Gpu.HasValue) _gpuEverSeen = true;

                bool hot = (Cpu.HasValue && Cpu.Value >= _cfg.CpuOn) || (Gpu.HasValue && Gpu.Value >= _cfg.GpuOn);
                // Un capteur jamais vu (absent sur ce matériel, ex. pas de GPU dédié) est ignoré
                // du calcul de "cool". Un capteur déjà vu mais dont la lecture échoue CE tick
                // (glitch WMI/EC transitoire) est en revanche traité comme "pas cool" : on ne
                // laisse pas une lecture manquante couper le Cooler Boost par erreur pendant une
                // vraie surchauffe (fail-safe : on préfère continuer à refroidir dans le doute).
                bool cpuCool = Cpu.HasValue ? Cpu.Value <= _cfg.CpuOff : !_cpuEverSeen;
                bool gpuCool = Gpu.HasValue ? Gpu.Value <= _cfg.GpuOff : !_gpuEverSeen;
                bool cool = cpuCool && gpuCool;
                bool prev = _lastWant.HasValue && _lastWant.Value;
                if (prev)
                {
                    bool heldLongEnough = (DateTime.UtcNow - _boostSince).TotalSeconds >= _cfg.MinBoostSeconds;
                    want = !(cool && !GameDetected && heldLongEnough);
                }
                else
                {
                    want = hot || GameDetected;
                }
                Reason = GameDetected ? "jeu détecté : " + GameName
                       : hot ? "température élevée"
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

                // Lecture EC en échec ce tick : on NE déduit PAS BoostActive du cache/want
                // (ça pourrait prétendre à tort que le boost est actif alors que le firmware
                // a pu remettre le bit à zéro, ex. reprise de veille — cf. commentaire plus
                // haut). On conserve la dernière valeur RÉELLEMENT confirmée par lecture et
                // on expose BoostStateUnknown pour que l'UI le rende visible.
                BoostStateUnknown = !actual.HasValue;
                if (actual.HasValue) BoostActive = actual.Value;
            }
            else
            {
                _lastWant = want;
                BoostActive = false;
                BoostStateUnknown = false;
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
