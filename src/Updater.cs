using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
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

        /// <summary>
        /// Levé juste avant Environment.Exit(0), pour laisser l'UI (icône de la zone de
        /// notification) se nettoyer proprement avant l'arrêt brutal du process.
        /// </summary>
        public event Action BeforeExit;

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
            // Nom de fichier de préproduction NON prévisible (suffixe GUID) : un nom fixe
            // comme "MiniFan.exe.new" permettrait à un autre processus local de préparer /
            // surveiller ce chemin à l'avance et de gagner la course pendant la fenêtre
            // TOCTOU entre l'écriture du téléchargement et son remplacement de l'exe final.
            string newExe = exe + "." + Guid.NewGuid().ToString("N") + ".new";
            using (var wc = NewClient())
            {
                wc.DownloadFile(url, newExe);
            }
            if (!File.Exists(newExe) || new FileInfo(newExe).Length < 50000)
            { SetStatus("Téléchargement invalide, MAJ annulée"); return; }

            // Vérification Authenticode RÉELLE (WinVerifyTrust, pas juste lecture du certificat
            // embarqué) : le binaire téléchargé doit porter une signature valide dont la chaîne
            // de confiance est vérifiée par Windows, ET si l'exe courant est lui-même signé, le
            // signataire doit être identique (continuité). Si l'un ou l'autre échoue, on refuse
            // — y compris quand l'exe courant n'est pas signé : un build non signé ne peut plus
            // servir de prétexte pour installer n'importe quel binaire téléchargé sans vérif.
            if (!VerifySignatureContinuity(exe, newExe)) return;

            SetStatus("Installation de la v" + remote + "…");
            string bat = Path.Combine(Path.GetTempPath(), "minifan-update-" + Guid.NewGuid().ToString("N") + ".bat");
            int pid = Process.GetCurrentProcess().Id;
            string newExeEscaped = newExe.Replace("'", "''");
            File.WriteAllText(bat,
                "@echo off\r\n" +
                ":wait\r\n" +
                "tasklist /fi \"PID eq " + pid + "\" | find \"" + pid + "\" >nul 2>&1 && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
                // Re-vérification juste avant utilisation : réduit la fenêtre TOCTOU entre le
                // contrôle fait plus haut et l'exécution réelle (le fichier temporaire pourrait
                // en théorie être substitué par un autre processus local entre les deux).
                "powershell -NoProfile -ExecutionPolicy Bypass -Command " +
                "\"if ((Get-AuthenticodeSignature -LiteralPath '" + newExeEscaped + "').Status -ne 'Valid') { exit 1 }\"\r\n" +
                "if errorlevel 1 (del \"" + newExe + "\" >nul 2>&1 & del \"%~f0\" & exit /b 1)\r\n" +
                "move /y \"" + newExe + "\" \"" + exe + "\" >nul\r\n" +
                "start \"\" \"" + exe + "\" --tray\r\n" +
                "del \"%~f0\"\r\n");
            var psi = new ProcessStartInfo(bat)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
            var be = BeforeExit;
            if (be != null) be();
            Environment.Exit(0);
        }

        // ---- Épinglage du certificat de signature de code (certificate pinning) ----
        //
        // IsAuthenticodeTrusted() (WinVerifyTrust) prouve seulement que le binaire est signé
        // par UN certificat dont la chaîne remonte à une autorité de confiance Windows —
        // n'IMPORTE QUEL certificat de signature de code valide (y compris un certificat
        // acheté par un attaquant, ou un certificat émis à un tiers) passe cette vérif. La
        // continuité de signataire plus bas (currentExe vs newExe) ne s'active elle-même que
        // si le build EXÉCUTÉ est déjà signé, ce qui n'est pas garanti (build.ps1 rend la
        // signature optionnelle, aucune CI ne l'impose) : en pratique, un build non signé ne
        // bénéficie d'AUCUNE des deux protections ci-dessus.
        //
        // L'épinglage ci-dessous fixe EXACTEMENT quelle empreinte de certificat est autorisée
        // à signer une mise à jour officielle de Mini Fan, indépendamment de la signature (ou
        // non) de l'exe courant.
        //
        // TODO(mainteneur) : AUCUN certificat de signature de code réel n'existe encore pour
        // ce projet à ce jour (aucun .pfx, aucune empreinte, aucune CI de signature dans le
        // dépôt — build.ps1 ne signe que si MINIFAN_SIGN_THUMBPRINT/MINIFAN_SIGN_PFX est
        // fourni manuellement, ce qui n'est fait nulle part actuellement). Dès qu'un
        // certificat officiel est acquis et utilisé pour publier les releases, renseigner ici
        // sa véritable empreinte (thumbprint SHA-1, format Windows standard — 40 caractères
        // hexadécimaux), obtenue par exemple avec :
        //   (Get-AuthenticodeSignature .\build\MiniFan.exe).SignerCertificate.Thumbprint
        // Exemple : private const string PinnedSigningThumbprint = "A1B2C3D4E5F6A1B2C3D4E5F6A1B2C3D4E5F6A1B2";
        private const string PinnedSigningThumbprint = ""; // TODO: renseigner l'empreinte réelle ici dès qu'un certificat officiel existe

        /// <summary>
        /// Compare l'empreinte du certificat signataire du binaire téléchargé à
        /// PinnedSigningThumbprint. Quand l'empreinte est renseignée, elle est AUTORITAIRE :
        /// tout binaire qui ne correspond pas exactement est refusé, même s'il est par
        /// ailleurs signé par un certificat valide et fiable (fail-closed). Tant qu'aucune
        /// empreinte officielle n'existe encore (voir TODO ci-dessus), cette étape ne peut
        /// pas filtrer sur UN certificat précis et se contente de laisser passer — mais ce
        /// n'est PAS un contournement pour autant : IsAuthenticodeTrusted() (chaîne de
        /// confiance + révocation) et la continuité de signataire restent toutes deux
        /// appliquées indépendamment, avant et après cet appel.
        /// </summary>
        private bool VerifyPinnedCertificate(string newExe)
        {
            if (string.IsNullOrEmpty(PinnedSigningThumbprint)) return true;

            string thumbprint;
            try
            {
                using (var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(newExe)))
                {
                    thumbprint = cert.Thumbprint;
                }
            }
            catch (Exception ex)
            {
                SetStatus("MAJ refusée : certificat illisible (" + Short(ex.Message) + ")");
                return false;
            }

            if (!string.Equals(thumbprint, PinnedSigningThumbprint, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("MAJ refusée : certificat de signature non reconnu (empreinte différente)");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Vérifie que le binaire téléchargé porte une signature Authenticode valide et
        /// FIABLE (chaîne de confiance résolue par Windows via WinVerifyTrust — pas seulement
        /// la présence d'un certificat, qui peut être falsifiée en copiant la table de
        /// certificats d'un exécutable légitime sur un binaire différent), que son certificat
        /// correspond à l'empreinte épinglée (quand elle est renseignée), et si l'exe courant
        /// est lui-même signé, exige en plus la continuité de signataire.
        /// Retourne true UNIQUEMENT si le téléchargement est cryptographiquement valide.
        /// </summary>
        private bool VerifySignatureContinuity(string currentExe, string newExe)
        {
            try
            {
                if (!IsAuthenticodeTrusted(newExe))
                {
                    SetStatus("MAJ refusée : signature du binaire téléchargé invalide ou non fiable");
                    return false;
                }

                if (!VerifyPinnedCertificate(newExe)) return false;

                X509Certificate currentCert;
                try
                {
                    currentCert = X509Certificate.CreateFromSignedFile(currentExe);
                }
                catch
                {
                    // Exe courant non signé (build de développement) : la continuité de
                    // signataire n'est pas vérifiable, mais le téléchargement a déjà été validé
                    // cryptographiquement ci-dessus (signature + chaîne de confiance réelles).
                    SetStatus("binaire courant non signé — continuité non vérifiable, signature du téléchargement validée");
                    return true;
                }

                X509Certificate newCert = X509Certificate.CreateFromSignedFile(newExe);
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

        // ---- WinVerifyTrust : vérification Authenticode réelle (signature + chaîne) ----

        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_NONE = 0;
        // Vérifie la révocation sur TOUTE la chaîne (pas seulement la feuille) : un certificat
        // de signature de code compromis et révoqué après coup doit être refusé, pas accepté
        // indéfiniment comme c'était le cas avec WTD_REVOKE_NONE.
        private const uint WTD_REVOKE_WHOLECHAIN = 1;
        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_STATEACTION_VERIFY = 1;
        private const uint WTD_STATEACTION_CLOSE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public string pszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public string pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern uint WinVerifyTrust(IntPtr hwnd,
            [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

        // Codes d'erreur WinVerifyTrust signalant que la révocation n'a PAS pu être
        // déterminée (typiquement : pas d'accès réseau pour joindre l'OCSP/CRL) — distincts
        // d'un certificat EFFECTIVEMENT révoqué (qui renvoie un autre code, ex. CERT_E_REVOKED
        // = 0x800B010C, et reste refusé sans repli possible ci-dessous).
        private const uint CRYPT_E_NO_REVOCATION_CHECK = 0x80092012;
        private const uint CRYPT_E_REVOCATION_OFFLINE = 0x80092013;
        private const uint CERT_E_REVOCATION_FAILURE = 0x800B010E;

        private static bool IsRevocationInconclusive(uint result)
        {
            return result == CRYPT_E_NO_REVOCATION_CHECK
                || result == CRYPT_E_REVOCATION_OFFLINE
                || result == CERT_E_REVOCATION_FAILURE;
        }

        private static bool IsAuthenticodeTrusted(string filePath)
        {
            uint result = RunWinVerifyTrust(filePath, WTD_REVOKE_WHOLECHAIN);
            if (result == 0) return true;

            if (IsRevocationInconclusive(result))
            {
                // Best-effort : la vérification de révocation exige un accès réseau
                // (OCSP/CRL). Sur une machine hors ligne ou dont le réseau bloque ces
                // requêtes, on ne bloque pas la MAJ pour ce seul motif — on retente SANS
                // vérif de révocation (la signature et la chaîne de confiance, elles, restent
                // pleinement vérifiées) et on journalise un avertissement. La vérif de
                // révocation reste activée par défaut : seule son indisponibilité réseau est
                // traitée comme "non concluante" plutôt que comme un échec dur ; un
                // certificat EFFECTIVEMENT révoqué (résultat différent) reste refusé.
                LogWarning("Révocation non vérifiable pour '" + filePath + "' (code 0x" +
                    result.ToString("X8") + "), nouvelle tentative sans vérif de révocation.");
                uint fallback = RunWinVerifyTrust(filePath, WTD_REVOKE_NONE);
                return fallback == 0;
            }

            return false;
        }

        private static uint RunWinVerifyTrust(string filePath, uint revocationChecks)
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)),
                pszFilePath = filePath,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero
            };

            IntPtr fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                    pPolicyCallbackData = IntPtr.Zero,
                    pSIPClientData = IntPtr.Zero,
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = revocationChecks,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = fileInfoPtr,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                    hWVTStateData = IntPtr.Zero,
                    pwszURLReference = null,
                    dwProvFlags = 0,
                    dwUIContext = 0
                };

                uint result = WinVerifyTrust(new IntPtr(-1), WINTRUST_ACTION_GENERIC_VERIFY_V2, ref data);

                data.dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust(new IntPtr(-1), WINTRUST_ACTION_GENERIC_VERIFY_V2, ref data);

                return result;
            }
            catch
            {
                return unchecked((uint)0x80004005); // E_FAIL générique : traité comme échec, pas comme "non concluant"
            }
            finally
            {
                Marshal.FreeHGlobal(fileInfoPtr);
            }
        }

        private static void LogWarning(string message)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "minifan-error.log");
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                    " [UpdaterWarning] " + message + Environment.NewLine);
            }
            catch { /* le logging ne doit jamais faire planter la MAJ */ }
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
                if (!string.Equals(name, "MiniFan.exe", StringComparison.OrdinalIgnoreCase)) continue;
                string assetUrl = asset.ContainsKey("browser_download_url") ? (string)asset["browser_download_url"] : null;
                if (string.IsNullOrEmpty(assetUrl) || !assetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    continue;
                return assetUrl;
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
