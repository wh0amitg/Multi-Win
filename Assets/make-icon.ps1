Add-Type -AssemblyName System.Drawing
$src = Join-Path $PSScriptRoot 'app-logo.png'
$dst = Join-Path $PSScriptRoot 'app.ico'
if (-not (Test-Path $src)) { Write-Error "Put your logo at $src first"; exit 1 }
$bmp = [Drawing.Bitmap]::FromFile($src)
$big = New-Object Drawing.Bitmap($bmp, 256, 256)
$icon = [Drawing.Icon]::FromHandle($big.GetHicon())
$fs = [IO.File]::Create($dst)
$icon.Save($fs); $fs.Close()
$icon.Dispose(); $big.Dispose(); $bmp.Dispose()
"wrote $dst ($((Get-Item $dst).Length) bytes) - rebuild the project"
