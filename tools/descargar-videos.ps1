# Descarga videos de motionbgs (4K, o HD si no hay) y los deja listos para esta pantalla: 3840x2160, 30 fps, sin audio, con póster .jpg.
# Pixel art: ampliado con vecino más cercano para que los píxeles no se emborronen.
param([string]$Lista = (Join-Path $env:USERPROFILE 'okla\tools\videos-nuevos.csv'), [string]$Okla = (Join-Path $env:USERPROFILE 'okla'))
$ff = @((Join-Path $Okla 'tools\ffmpeg.exe')) + @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages') -Filter ffmpeg.exe -Recurse -ErrorAction SilentlyContinue | ForEach-Object FullName) | Where-Object { Test-Path $_ } | Select-Object -First 1
$orig = (Join-Path $Okla 'recursos\originales\videos')
New-Item -ItemType Directory -Force $orig | Out-Null
foreach ($v in Import-Csv $Lista) {
  $dst = (Join-Path $Okla "recursos\videos\$($v.modo)\$($v.nombre).mp4")
  if (Test-Path $dst) { "ya existe: $($v.nombre)"; continue }
  $src = "$orig\$($v.nombre).mp4"
  try { Invoke-WebRequest "https://motionbgs.com/dl/4k/$($v.id)" -OutFile $src -UseBasicParsing -TimeoutSec 300 }
  catch {
    try { Invoke-WebRequest "https://motionbgs.com/dl/hd/$($v.id)" -OutFile $src -UseBasicParsing -TimeoutSec 300 }
    catch { "FALLO descarga: $($v.nombre)"; continue }
  }
  & $ff -hide_banner -loglevel error -y -i $src -an -vf "scale=3840:2160:flags=neighbor:force_original_aspect_ratio=increase,crop=3840:2160,fps=30" `
      -c:v libx264 -preset medium -crf 18 -tune animation -pix_fmt yuv420p -movflags +faststart $dst
  if (-not (Test-Path $dst)) { "FALLO conversión: $($v.nombre)"; continue }
  & $ff -hide_banner -loglevel error -y -ss 1 -i $dst -frames:v 1 -q:v 2 ([IO.Path]::ChangeExtension($dst, '.jpg'))
  "{0} ({1}): {2} MB" -f $v.nombre, $v.modo, [math]::Round((Get-Item $dst).Length / 1MB, 1)
}
"TERMINADO"

