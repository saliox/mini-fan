# Publie une nouvelle version de Mini Fan (à lancer depuis la TOUR).
# Le portable se mettra à jour tout seul dans les 6 h (ou via le lien "MAJ" de l'app).
# Usage :  .\publish-update.ps1 -Version 1.1.0 -Notes "Ce qui change"
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = "Mise à jour de Mini Fan."
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version attendue au format X.Y.Z (ex: 1.1.0)" }

# 1. Bump de la version dans le code
$verFile = Join-Path $root "src\Version.cs"
$content = Get-Content $verFile -Raw
$content = $content -replace 'Number = "\d+\.\d+\.\d+"', ('Number = "' + $Version + '"')
Set-Content $verFile $content -Encoding UTF8

# 2. Build
& (Join-Path $root "build.ps1")
if ($LASTEXITCODE -ne 0 -and -not (Test-Path (Join-Path $root "build\MiniFan.exe"))) { throw "Build en échec" }

# 3. Commit + push + release GitHub
Push-Location $root
try {
    git add -A
    git commit -m "v$Version - $Notes"
    git push
    gh release create "v$Version" (Join-Path $root "build\MiniFan.exe") (Join-Path $root "install.ps1") `
        --title "Mini Fan v$Version" --notes $Notes
    Write-Host ""
    Write-Host "v$Version publiée. Le portable se mettra à jour automatiquement." -ForegroundColor Green
}
finally { Pop-Location }
