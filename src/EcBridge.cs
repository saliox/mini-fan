using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Text;

namespace MiniFan
{
    /// <summary>
    /// Accès à l'Embedded Controller des portables MSI via l'interface WMI MSI_ACPI
    /// (exposée par le BIOS, aucun pilote tiers requis).
    /// Registres EC MSI (constants sur quasi tous les modèles, cf. projet msi-ec) :
    ///   0x68 = température CPU temps réel
    ///   0x80 = température GPU temps réel
    ///   0x98 bit 7 = Cooler Boost (ventilateurs à fond, équivalent FN+flèche haut)
    /// </summary>
    public class EcBridge
    {
        public const byte REG_CPU_TEMP = 0x68;
        public const byte REG_GPU_TEMP = 0x80;
        public const byte REG_COOLER_BOOST = 0x98;
        public const byte BOOST_BIT = 0x80;

        private ManagementObject _inst;
        private string _readMethod;
        private int _readOffset = -1;
        private string _writeMethod;
        private bool _nvidiaSmiAvailable = true;
        private DateTime _lastNvidiaCheck = DateTime.MinValue;
        private int? _lastNvidiaTemp;
        private readonly StringBuilder _log = new StringBuilder();

        public bool ClassPresent { get; private set; }
        public bool ReadSupported { get { return _inst != null && _readMethod != null; } }
        public bool WriteSupported { get; private set; }

        public void Init()
        {
            Log("Mini Fan v" + AppVersion.Number + " — sonde EC, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            try
            {
                var searcher = new ManagementObjectSearcher(
                    new ManagementScope(@"root\WMI"), new ObjectQuery("SELECT * FROM MSI_ACPI"));
                foreach (ManagementObject o in searcher.Get()) { _inst = o; break; }
            }
            catch (Exception ex)
            {
                Log("Classe MSI_ACPI introuvable (" + ex.GetType().Name + ": " + ex.Message + ")");
                _inst = null;
            }
            ClassPresent = _inst != null;
            if (!ClassPresent)
            {
                Log("=> Pas un portable MSI (ou interface WMI absente). Mode démo.");
                return;
            }
            Log("Classe MSI_ACPI trouvée.");
            ProbeRead();
            if (_readMethod != null) ProbeWrite();
        }

        public static bool HardwareLikelyPresent()
        {
            try
            {
                var searcher = new ManagementObjectSearcher(
                    new ManagementScope(@"root\WMI"), new ObjectQuery("SELECT * FROM MSI_ACPI"));
                return searcher.Get().Count > 0;
            }
            catch { return false; }
        }

        // ---------------- Lecture / écriture registres ----------------

        private byte[] Call(string method, byte[] input)
        {
            // Les ManagementBaseObject encapsulent des objets COM : sans Dispose explicite,
            // chaque lecture/écriture EC fuit un handle (problème sur une longue durée de vie).
            ManagementBaseObject inParams = null;
            ManagementBaseObject outParams = null;
            try
            {
                try { inParams = _inst.GetMethodParameters(method); } catch { }
                if (inParams != null && input != null)
                {
                    string pname = null;
                    foreach (PropertyData p in inParams.Properties) { pname = p.Name; break; }
                    if (pname != null)
                    {
                        var buf = new byte[32];
                        Array.Copy(input, buf, Math.Min(32, input.Length));
                        inParams[pname] = buf;
                    }
                }
                outParams = _inst.InvokeMethod(method, inParams, null);
                if (outParams == null) return null;
                foreach (PropertyData p in outParams.Properties)
                {
                    var arr = p.Value as byte[];
                    if (arr != null)
                    {
                        // On recopie avant de disposer outParams : la valeur retournée ne
                        // doit pas référencer un objet COM déjà libéré.
                        var copy = new byte[arr.Length];
                        Array.Copy(arr, copy, arr.Length);
                        return copy;
                    }
                }
                return null;
            }
            finally
            {
                if (inParams != null) inParams.Dispose();
                if (outParams != null) outParams.Dispose();
            }
        }

        public int? ReadReg(byte addr)
        {
            if (!ReadSupported) return null;
            try
            {
                var input = new byte[32];
                input[0] = addr;
                var output = Call(_readMethod, input);
                if (output == null || output.Length <= _readOffset) return null;
                return output[_readOffset];
            }
            catch { return null; }
        }

        private bool WriteReg(byte addr, byte value)
        {
            if (!ReadSupported || _writeMethod == null) return false;
            try
            {
                var input = new byte[32];
                input[0] = addr;
                input[1] = value;
                Call(_writeMethod, input);
                System.Threading.Thread.Sleep(80);
                int? back = ReadReg(addr);
                return back.HasValue && back.Value == value;
            }
            catch { return false; }
        }

        // ---------------- Sondes de validation ----------------

        private void ProbeRead()
        {
            // Sémantique attendue (interface MSI "WMI2") : entrée[0] = adresse EC,
            // sortie[0] = statut, sortie[1] = valeur. On valide en lisant la temp CPU (0x68),
            // qui doit être plausible et différente d'un simple écho de l'adresse.
            string[] candidates = { "Get_Data", "Get_WMI", "Get_EC" };
            int[] offsets = { 1, 0 };
            foreach (string m in candidates)
            {
                foreach (int off in offsets)
                {
                    try
                    {
                        var input = new byte[32];
                        input[0] = REG_CPU_TEMP;
                        var o1 = Call(m, input);
                        if (o1 == null || o1.Length <= off) { Log(m + ": pas de sortie exploitable"); break; }
                        int t = o1[off];
                        input[0] = REG_COOLER_BOOST;
                        var o2 = Call(m, input);
                        int other = (o2 != null && o2.Length > off) ? o2[off] : -1;
                        bool echo = (t == REG_CPU_TEMP && other == REG_COOLER_BOOST);
                        bool plausible = t >= 15 && t <= 105;
                        Log(string.Format("{0}[{1}] -> temp CPU={2}, reg 0x98={3}, echo={4}, plausible={5}",
                            m, off, t, other, echo, plausible));
                        if (plausible && !echo)
                        {
                            _readMethod = m;
                            _readOffset = off;
                            Log("=> Lecture EC validée via " + m + " (offset " + off + ").");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(m + " erreur: " + ex.Message);
                        break;
                    }
                }
            }
            Log("=> Aucune méthode de lecture EC validée.");
        }

        private void ProbeWrite()
        {
            // Écriture à blanc : on réécrit la valeur ACTUELLE de 0x98 (aucun changement d'état)
            // puis on vérifie que la relecture est identique et que la temp CPU reste saine.
            string writeName = _readMethod == "Get_Data" ? "Set_Data"
                             : _readMethod == "Get_WMI" ? "Set_WMI" : "Set_EC";
            int? cur = ReadReg(REG_COOLER_BOOST);
            if (!cur.HasValue) { Log("Écriture non testée (lecture 0x98 impossible)."); return; }
            _writeMethod = writeName;
            bool ok = WriteReg(REG_COOLER_BOOST, (byte)cur.Value);
            // Relecture explicite du registre VISÉ (0x98) : on confirme qu'il contient bien
            // la valeur écrite, en garde AND supplémentaire. Limitation : sur un firmware
            // inconnu on ne peut PAS garantir l'innocuité d'une écriture croisée sur un autre
            // registre ; ce test ne valide que 0x98 lui-même.
            int? readBack = ReadReg(REG_COOLER_BOOST);
            bool readBackOk = readBack.HasValue && readBack.Value == (byte)cur.Value;
            int? temp = ReadReg(REG_CPU_TEMP);
            bool tempOk = temp.HasValue && temp.Value >= 15 && temp.Value <= 105;
            if (ok && readBackOk && tempOk)
            {
                WriteSupported = true;
                Log("=> Écriture EC validée via " + writeName + " (écriture à blanc sur 0x98, relecture confirmée).");
            }
            else
            {
                _writeMethod = null;
                Log(string.Format("=> Écriture NON validée via {0} (relecture ok={1}, readback={2}, temp ok={3}).", writeName, ok, readBackOk, tempOk));
            }
        }

        // ---------------- API haut niveau ----------------

        public bool? GetCoolerBoost()
        {
            int? v = ReadReg(REG_COOLER_BOOST);
            if (!v.HasValue) return null;
            return (v.Value & BOOST_BIT) != 0;
        }

        public bool SetCoolerBoost(bool on)
        {
            if (!WriteSupported) return false;
            int? cur = ReadReg(REG_COOLER_BOOST);
            if (!cur.HasValue) return false;
            byte target = on ? (byte)(cur.Value | BOOST_BIT) : (byte)(cur.Value & ~BOOST_BIT);
            if (target == cur.Value) return true;
            return WriteReg(REG_COOLER_BOOST, target);
        }

        public int? CpuTemp()
        {
            int? t = ReadReg(REG_CPU_TEMP);
            if (t.HasValue && t.Value >= 5 && t.Value <= 115) return t;
            return FallbackAcpiTemp();
        }

        public int? GpuTemp()
        {
            int? t = ReadReg(REG_GPU_TEMP);
            // Un GPU dédié endormi renvoie souvent 0 : considéré comme froid (null).
            if (t.HasValue && t.Value >= 5 && t.Value <= 115) return t;
            return NvidiaTemp();
        }

        private int? FallbackAcpiTemp()
        {
            try
            {
                var searcher = new ManagementObjectSearcher(
                    new ManagementScope(@"root\WMI"),
                    new ObjectQuery("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"));
                int best = -1;
                foreach (ManagementObject o in searcher.Get())
                {
                    try
                    {
                        int deciKelvin = Convert.ToInt32(o["CurrentTemperature"]);
                        int c = (int)Math.Round(deciKelvin / 10.0 - 273.15);
                        if (c > best && c > 0 && c < 120) best = c;
                    }
                    catch { }
                }
                return best > 0 ? (int?)best : null;
            }
            catch { return null; }
        }

        private int? NvidiaTemp()
        {
            if (!_nvidiaSmiAvailable) return null;
            if ((DateTime.UtcNow - _lastNvidiaCheck).TotalSeconds < 10) return _lastNvidiaTemp;
            _lastNvidiaCheck = DateTime.UtcNow;
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=temperature.gpu --format=csv,noheader")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using (var p = Process.Start(psi))
                {
                    string line = p.StandardOutput.ReadLine();
                    p.WaitForExit(1500);
                    int v;
                    if (line != null && int.TryParse(line.Trim(), out v) && v > 0 && v < 120)
                    {
                        _lastNvidiaTemp = v;
                        return v;
                    }
                }
            }
            catch { _nvidiaSmiAvailable = false; }
            _lastNvidiaTemp = null;
            return null;
        }

        // ---------------- Diagnostic ----------------

        private void Log(string s) { _log.AppendLine(s); }

        public string ProbeLog { get { return _log.ToString(); } }

        /// <summary>Rapport complet à envoyer si le pilotage ne fonctionne pas sur un modèle donné.</summary>
        public string BuildDiagnostic()
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== DIAGNOSTIC MINI FAN =====");
            try
            {
                var cs = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
                foreach (ManagementObject o in cs.Get())
                    sb.AppendLine("Machine : " + o["Manufacturer"] + " " + o["Model"]);
            }
            catch { }
            sb.AppendLine("Version : " + AppVersion.Number + " | Admin : " + Program.IsAdmin());
            sb.AppendLine();
            sb.AppendLine("--- Journal de sonde ---");
            sb.Append(_log);
            sb.AppendLine();
            if (_inst != null)
            {
                sb.AppendLine("--- Méthodes MSI_ACPI ---");
                try
                {
                    var mc = new ManagementClass(new ManagementScope(@"root\WMI"), new ManagementPath("MSI_ACPI"), null);
                    foreach (MethodData m in mc.Methods)
                        sb.AppendLine("  " + m.Name);
                    sb.AppendLine();
                    sb.AppendLine("--- Dump des méthodes Get_* (entrée[0]=0x68) ---");
                    foreach (MethodData m in mc.Methods)
                    {
                        if (!m.Name.StartsWith("Get")) continue;
                        try
                        {
                            var input = new byte[32];
                            input[0] = REG_CPU_TEMP;
                            var output = Call(m.Name, input);
                            sb.AppendLine("  " + m.Name + " : " +
                                (output == null ? "(null)" : BitConverter.ToString(output)));
                        }
                        catch (Exception ex)
                        {
                            sb.AppendLine("  " + m.Name + " : ERREUR " + ex.Message);
                        }
                    }
                }
                catch (Exception ex) { sb.AppendLine("  (énumération impossible : " + ex.Message + ")"); }
            }
            sb.AppendLine();
            sb.AppendLine(string.Format("Temp CPU={0}  GPU={1}  CoolerBoost={2}",
                Fmt(CpuTemp()), Fmt(GpuTemp()), GetCoolerBoost()));
            sb.AppendLine("===== FIN DIAGNOSTIC =====");
            return sb.ToString();
        }

        private static string Fmt(int? v) { return v.HasValue ? v.Value.ToString() : "?"; }
    }
}
