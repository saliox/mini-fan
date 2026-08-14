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
# NB : $ErrorActionPreference="Stop" n'intercepte PAS l'échec d'une commande native
# (git/gh). On vérifie donc $LASTEXITCODE après chaque appel pour ne jamais afficher
# « publiée » sur un échec partiel.
Push-Location $root
try {
    $exePath = Join-Path $root "build\MiniFan.exe"

    # Empreinte SHA-256 du binaire publié, écrite dans un fichier ANNEXE machine-lisible
    # (MiniFan.exe.sha256) publié comme asset de la release à côté de l'exe. C'est ce fichier
    # qu'install.ps1 télécharge et vérifie AVANT d'exécuter/installer quoi que ce soit — sans
    # lui, personne ne vérifiait jamais l'empreinte affichée en fin de script ci-dessous.
    $shaPath = Join-Path $root "build\MiniFan.exe.sha256"
    $sha = (Get-FileHash $exePath -Algorithm SHA256).Hash
    Set-Content -Path $shaPath -Value $sha -NoNewline -Encoding ASCII

    git add -A
    if ($LASTEXITCODE -ne 0) { throw "git add a échoué (code $LASTEXITCODE)." }
    git commit -m "v$Version - $Notes"
    if ($LASTEXITCODE -ne 0) { throw "git commit a échoué (code $LASTEXITCODE)." }
    git push
    if ($LASTEXITCODE -ne 0) { throw "git push a échoué (code $LASTEXITCODE)." }

    gh release create "v$Version" $exePath $shaPath (Join-Path $root "install.ps1") `
        --title "Mini Fan v$Version" --notes $Notes
    if ($LASTEXITCODE -ne 0) { throw "gh release create a échoué (code $LASTEXITCODE)." }

    Write-Host ""
    Write-Host "SHA-256 MiniFan.exe : $sha"
    Write-Host "v$Version publiée. Le portable se mettra à jour automatiquement." -ForegroundColor Green
}
finally { Pop-Location }
