# Installe Mini Fan sur le portable MSI.
# Usage (PowerShell en ADMINISTRATEUR sur le portable) :
#   irm https://raw.githubusercontent.com/saliox/mini-fan/main/install.ps1 | iex
# ou en local :  .\install.ps1 [-LocalExe chemin\MiniFan.exe]
param([string]$LocalExe = "")

$ErrorActionPreference = "Stop"
$repo = "saliox/mini-fan"
$dir = Join-Path $env:LOCALAPPDATA "MiniFan"
$exe = Join-Path $dir "MiniFan.exe"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Relance ce script dans un PowerShell ADMINISTRATEUR (clic droit > Exécuter en tant qu'administrateur)." -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path $dir)) { New-Item -ItemType Directory $dir | Out-Null }

# Stoppe une éventuelle instance en cours
Get-Process MiniFan -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

if ($LocalExe -and (Test-Path $LocalExe)) {
    Copy-Item $LocalExe $exe -Force
    Write-Host "Copie locale installée."
} else {
    Write-Host "Téléchargement de la dernière version…"
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $hdr = @{ "User-Agent" = "MiniFan-Install" }
    $rel = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers $hdr
    $asset = $rel.assets | Where-Object { $_.name -eq "MiniFan.exe" } | Select-Object -First 1
    if (-not $asset) { throw "Aucun MiniFan.exe dans la dernière release de $repo" }

    # Téléchargement en fichier TEMPORAIRE : l'exe installé n'est remplacé qu'après
    # vérification d'intégrité (sinon un téléchargement corrompu/altéré casserait
    # l'install existante et serait lancé en admin au prochain boot).
    $tmp = "$exe.download"
    Invoke-WebRequest $asset.browser_download_url -OutFile $tmp -Headers $hdr
    try {
        # 1) Empreinte SHA-256 publiée avec la release (asset MiniFan.exe.sha256,
        #    généré par publish-update.ps1). Comparaison stricte si présent.
        $verified = $false
        $shaAsset = $rel.assets | Where-Object { $_.name -eq "MiniFan.exe.sha256" } | Select-Object -First 1
        if ($shaAsset) {
            # Téléchargé en fichier (GitHub sert les assets en octet-stream :
            # Invoke-RestMethod renverrait des octets, pas du texte).
            $shaTmp = "$tmp.sha256"
            Invoke-WebRequest $shaAsset.browser_download_url -OutFile $shaTmp -Headers $hdr
            $expected = ((Get-Content $shaTmp -Raw).Trim() -split '\s+')[0]
            Remove-Item $shaTmp -Force -ErrorAction SilentlyContinue
            $actual = (Get-FileHash $tmp -Algorithm SHA256).Hash
            if ($actual -ne $expected) {
                throw "SHA-256 du téléchargement ($actual) différent de l'empreinte publiée ($expected) — installation annulée."
            }
            $verified = $true
            Write-Host "Intégrité vérifiée (SHA-256 conforme à l'empreinte publiée)."
        }
        # 2) Repli pour les releases sans asset .sha256 : exige une signature
        #    Authenticode valide (même critère que l'auto-updater de l'app).
        if (-not $verified) {
            $sig = Get-AuthenticodeSignature $tmp
            if ($sig.Status -ne 'Valid') {
                throw "Pas d'empreinte SHA-256 publiée et signature Authenticode '$($sig.Status)' — installation annulée (binaire non vérifiable)."
            }
            Write-Host "Intégrité vérifiée (signature Authenticode valide)."
        }
        Move-Item $tmp $exe -Force
    } finally {
        foreach ($f in @($tmp, "$tmp.sha256")) {
            if (Test-Path $f) { Remove-Item $f -Force -ErrorAction SilentlyContinue }
        }
    }
    Write-Host "Version $($rel.tag_name) installée."
}

# Tâche planifiée : lancement à l'ouverture de session, droits élevés (pas d'UAC à chaque boot),
# autorisée sur batterie et sans limite de durée (portable !)
$act = New-ScheduledTaskAction -Execute $exe -Argument "--tray"
$trig = New-ScheduledTaskTrigger -AtLogOn
$set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable
Register-ScheduledTask -TaskName "MiniFan" -Action $act -Trigger $trig -Settings $set `
        -RunLevel Highest -Force | Out-Null
Write-Host "Démarrage automatique configuré (tâche planifiée MiniFan, active sur batterie)."

Start-Process $exe
Write-Host "Mini Fan est lancé — icône ventilateur dans la zone de notification." -ForegroundColor Green
