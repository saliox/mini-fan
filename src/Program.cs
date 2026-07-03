using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace MiniFan
{
    public static class Program
    {
        private static Mutex _mutex;

        [STAThread]
        public static void Main(string[] args)
        {
            bool startInTray = args != null && args.Contains("--tray");

            // Instance unique
            bool created;
            _mutex = new Mutex(true, "MiniFan-Hasu-SingleInstance", out created);
            if (!created) return;

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // L'accès WMI à l'EC exige les droits admin : on ne s'élève que si le
            // matériel MSI est présent (sinon mode démo sans UAC).
            if (!IsAdmin() && EcBridge.HardwareLikelyPresent())
            {
                try
                {
                    var psi = new ProcessStartInfo(Application.ExecutablePath, string.Join(" ", args))
                    {
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    _mutex.ReleaseMutex();
                    _mutex.Dispose();
                    Process.Start(psi);
                    return;
                }
                catch
                {
                    // UAC refusé : on continue sans pilotage (lecture seule impossible aussi,
                    // l'UI l'indiquera).
                }
            }

            var cfg = Config.Load();
            var ec = new EcBridge();
            ec.Init();
            var ctl = new FanController(cfg, ec);
            var upd = new Updater(cfg);

            var form = new MainForm(cfg, ec, ctl, upd, !startInTray);

            if (cfg.AutoUpdate)
            {
                var t = new System.Windows.Forms.Timer { Interval = 6 * 60 * 60 * 1000 };
                t.Tick += delegate { upd.CheckAsync(false); };
                t.Start();
                var first = new System.Windows.Forms.Timer { Interval = 30000 };
                first.Tick += delegate { first.Stop(); upd.CheckAsync(false); };
                first.Start();
            }

            Application.Run(form);
        }

        public static bool IsAdmin()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
