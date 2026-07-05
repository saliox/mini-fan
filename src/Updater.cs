using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Web.Script.Serialization;

namespace MiniFan
{
    /// <summary>
    /// Mise à jour automatique depuis les releases GitHub du dépôt configuré.
    /// Télécharge le nouveau MiniFan.exe, lance un .bat qui attend la fin du
    /// process, remplace l'exe et relance l'application.
    /// </summary>
    public class Updater
    {
        // Dépôt officiel figé à la COMPILATION : la source des MAJ ne doit JAMAIS
        // dépendre du config.json (modifiable par l'utilisateur) sous peine
        // d'élévation de privilèges / RCE via une release piégée.
        private const string OfficialRepo = "saliox/mini-fan";

        private readonly Config _cfg;
        private int _busy;

        public string Status { get; private set; }
        public event Action Changed;

        public Updater(Config cfg)
        {
            _cfg = cfg;
            Status = "v" + AppVersion.Number;
        }

        public void CheckAsync(bool notifyUpToDate)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { Check(notifyUpToDate); }
                catch (Exception ex)
                {
                    // En vérification silencieuse (auto), on n'affiche pas les erreurs réseau.
                    SetStatus(notifyUpToDate ? "MAJ : " + Short(ex.Message) : "v" + AppVersion.Number);
                }
                finally { _busy = 0; }
            });
        }

        private void Check(bool notifyUpToDate)
        {
            SetStatus("Recherche de mise à jour…");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string json;
            using (var wc = NewClient())
            {
                json = wc.DownloadString("https://api.github.com/repos/" + OfficialRepo + "/releases/latest");
            }
            var ser = new JavaScriptSerializer();
            var release = ser.Deserialize<Dictionary<string, object>>(json);
            string tag = release.ContainsKey("tag_name") ? (string)release["tag_name"] : null;
            if (string.IsNullOrEmpty(tag)) { SetStatus("v" + AppVersion.Number + " — pas de release"); return; }

            Version remote, local;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out remote) ||
                !Version.TryParse(AppVersion.Number, out local))
            { SetStatus("v" + AppVersion.Number); return; }

            if (Normalize(remote) <= Normalize(local))
            {
                SetStatus("v" + AppVersion.Number + (notifyUpToDate ? " — à jour ✓" : ""));
                return;
            }

            string url = FindAssetUrl(release);
            if (url == null) { SetStatus("v" + tag + " dispo mais sans MiniFan.exe"); return; }

            SetStatus("Téléchargement de la v" + remote + "…");
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string newExe = exe + ".new";
            using (var wc = NewClient())
            {
                wc.DownloadFile(url, newExe);
            }
            if (!File.Exists(newExe) || new FileInfo(newExe).Length < 50000)
            { SetStatus("Téléchargement invalide, MAJ annulée"); return; }

            // Vérification de continuité Authenticode : le binaire téléchargé doit être
            // signé par le MÊME signataire que l'exe en cours d'exécution. Sinon on refuse
            // (protège contre une release piégée). Si l'exe courant n'est PAS signé, on ne
            // peut pas imposer la continuité : on avertit et on continue (build non signé).
            if (!VerifySignatureContinuity(exe, newExe)) return;

            SetStatus("Installation de la v" + remote + "…");
            string bat = Path.Combine(Path.GetTempPath(), "minifan-update.bat");
            int pid = Process.GetCurrentProcess().Id;
            File.WriteAllText(bat,
                "@echo off\r\n" +
                ":wait\r\n" +
                "tasklist /fi \"PID eq " + pid + "\" | find \"" + pid + "\" >nul 2>&1 && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
                "move /y \"" + newExe + "\" \"" + exe + "\" >nul\r\n" +
                "start \"\" \"" + exe + "\" --tray\r\n" +
                "del \"%~f0\"\r\n");
            var psi = new ProcessStartInfo(bat)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
            Environment.Exit(0);
        }

        /// <summary>
        /// Compare le signataire Authenticode de l'exe courant et du binaire téléchargé.
        /// Retourne true si la MAJ peut se poursuivre :
        ///   - exe courant non signé  -> avertissement, on continue (build non signé toléré) ;
        ///   - signataires identiques  -> on continue ;
        ///   - signataires différents ou erreur de vérif -> on ABANDONNE (return false).
        /// </summary>
        private bool VerifySignatureContinuity(string currentExe, string newExe)
        {
            try
            {
                X509Certificate currentCert;
                try
                {
                    currentCert = X509Certificate.CreateFromSignedFile(currentExe);
                }
                catch
                {
                    // Exe courant non signé : impossible d'imposer la continuité.
                    SetStatus("binaire non signé — vérification de signature ignorée");
                    return true;
                }

                X509Certificate newCert;
                try
                {
                    newCert = X509Certificate.CreateFromSignedFile(newExe);
                }
                catch
                {
                    // L'exe courant est signé mais pas le téléchargement : refus.
                    SetStatus("MAJ refusée : binaire téléchargé non signé");
                    return false;
                }

                bool sameSubject = string.Equals(currentCert.Subject, newCert.Subject, StringComparison.Ordinal);
                bool sameHash = string.Equals(currentCert.GetCertHashString(), newCert.GetCertHashString(), StringComparison.OrdinalIgnoreCase);
                if (sameSubject && sameHash) return true;

                SetStatus("MAJ refusée : signature du binaire différente");
                return false;
            }
            catch (Exception ex)
            {
                // Toute erreur inattendue de vérification annule la MAJ (fail-safe).
                SetStatus("MAJ annulée : vérif signature (" + Short(ex.Message) + ")");
                return false;
            }
        }

        private static Version Normalize(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), 0);
        }

        private string FindAssetUrl(Dictionary<string, object> release)
        {
            if (!release.ContainsKey("assets")) return null;
            var assets = release["assets"] as System.Collections.IEnumerable;
            if (assets == null) return null;
            foreach (object a in assets)
            {
                var asset = a as IDictionary<string, object>;
                if (asset == null) continue;
                string name = asset.ContainsKey("name") ? (string)asset["name"] : "";
                if (string.Equals(name, "MiniFan.exe", StringComparison.OrdinalIgnoreCase))
                    return (string)asset["browser_download_url"];
            }
            return null;
        }

        private static WebClient NewClient()
        {
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "MiniFan-Updater/" + AppVersion.Number;
            return wc;
        }

        private static string Short(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 60 ? s.Substring(0, 60) + "…" : s;
        }

        private void SetStatus(string s)
        {
            Status = s;
            var h = Changed;
            if (h != null) h();
        }
    }
}
