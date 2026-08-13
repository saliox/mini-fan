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

            // Filet de sécurité global : on journalise toute exception non gérée (dans un
            // fichier à côté de l'exe) et on garde l'app en vie là où c'est raisonnable,
            // plutôt que de crasher silencieusement.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
            { LogException("ThreadException", e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            { LogException("UnhandledException", e.ExceptionObject as Exception); };

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
                    Process.Start(psi);
                    // Ne relâcher le mutex qu'APRÈS le succès du relancement élevé : si
                    // Process.Start lève (UAC refusé), on tombe dans le catch ci-dessous et ce
                    // process NON élevé continue de tourner — il doit alors garder le mutex pour
                    // que la garantie d'instance unique reste valable.
                    _mutex.ReleaseMutex();
                    _mutex.Dispose();
                    return;
                }
                catch
                {
                    // UAC refusé : on continue sans pilotage (lecture seule impossible aussi,
                    // l'UI l'indiquera). Le mutex n'a pas été relâché : il reste détenu par ce
                    // process qui continue de tourner.
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

        private static void LogException(string source, Exception ex)
        {
            try
            {
                string path = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "minifan-error.log");
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + source + "] " +
                    (ex != null ? ex.ToString() : "(exception inconnue)") + Environment.NewLine;
                System.IO.File.AppendAllText(path, line);
            }
            catch { /* le logging ne doit jamais faire planter l'app */ }
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
