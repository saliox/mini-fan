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
    $rel = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ "User-Agent" = "MiniFan-Install" }
    $asset = $rel.assets | Where-Object { $_.name -eq "MiniFan.exe" } | Select-Object -First 1
    if (-not $asset) { throw "Aucun MiniFan.exe dans la dernière release de $repo" }
    Invoke-WebRequest $asset.browser_download_url -OutFile $exe -Headers @{ "User-Agent" = "MiniFan-Install" }
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
