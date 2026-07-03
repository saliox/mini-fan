# Compile Mini Fan avec le csc natif de Windows (.NET Framework 4.8, zéro dépendance).
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "build"
if (-not (Test-Path $out)) { New-Item -ItemType Directory $out | Out-Null }

# --- Génération de l'icône (ventilateur, ICO multi-tailles à base de PNG) ---
$icoPath = Join-Path $out "icon.ico"
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(30, 33, 44))
    $fg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(79, 195, 247))
    $m = $s * 0.03
    $g.FillEllipse($bg, $m, $m, $s - 2 * $m, $s - 2 * $m)
    $c = $s / 2.0
    for ($i = 0; $i -lt 3; $i++) {
        $g.TranslateTransform($c, $c)
        $g.RotateTransform(120)
        $g.TranslateTransform(-$c, -$c)
        $g.FillEllipse($fg, $c - $s * 0.08, $s * 0.10, $s * 0.16, $s * 0.34)
    }
    $g.ResetTransform()
    $g.FillEllipse($fg, $c - $s * 0.10, $c - $s * 0.10, $s * 0.20, $s * 0.20)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngs += , @($s, $ms.ToArray())
    $ms.Dispose()
}
# Conteneur ICO : header (6) + entrées (16 chacune) + données PNG
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $sz = $p[0]; $data = $p[1]
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))  # largeur
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))  # hauteur
    $bw.Write([byte]0); $bw.Write([byte]0)                     # palette, réservé
    $bw.Write([uint16]1); $bw.Write([uint16]32)                # plans, bpp
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($p in $pngs) { $bw.Write($p[1]) }
$bw.Close(); $fs.Close()

# --- Compilation ---
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$exe = Join-Path $out "MiniFan.exe"
& $csc /nologo /noconfig /target:winexe /platform:anycpu /optimize+ `
    /out:"$exe" `
    /win32icon:"$icoPath" `
    /win32manifest:"$root\src\app.manifest" `
    /r:mscorlib.dll /r:System.dll /r:System.Core.dll /r:System.Drawing.dll `
    /r:System.Windows.Forms.dll /r:System.Management.dll /r:System.Web.Extensions.dll `
    "$root\src\*.cs"
if ($LASTEXITCODE -ne 0) { throw "Échec de compilation csc" }
Write-Host "OK -> $exe ($([math]::Round((Get-Item $exe).Length/1KB)) Ko)"
