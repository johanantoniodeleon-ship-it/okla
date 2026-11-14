param([string]$Okla = (Join-Path $env:USERPROFILE 'okla'))
# Iconos con estilo propio por modo, sacados de temas libres de Linux (SVG -> ICO de varios tamaños).
#   Fedora: Papirus · Gaming: Candy Icons (neón) · Potencia: WhiteSur (estilo macOS)
# Después cambia el icono de los accesos de okla\escritorios\<modo>. Canva no está en ningún tema: conserva el suyo.
$ff = @((Join-Path $Okla 'tools\ffmpeg.exe')) + @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages') -Filter ffmpeg.exe -Recurse -ErrorAction SilentlyContinue | ForEach-Object FullName) |
  Where-Object { (Test-Path $_) -and ((& $_ -hide_banner -decoders 2>$null) -match 'librsvg') } | Select-Object -First 1   # tiene que leer SVG (el de ScreenPlay no)
if (-not $ff) { 'No hay ffmpeg que lea SVG: no se crean iconos'; exit }
$base = Join-Path $Okla 'recursos\iconos'
$temas = @{
  fedora   = @{ url = 'https://raw.githubusercontent.com/PapirusDevelopmentTeam/papirus-icon-theme/master/Papirus/64x64/apps/{0}.svg'
                n = @{ Firefox='firefox'; Explorador='org.gnome.Nautilus'; Notion='notion'; Spotify='spotify'; Discord='discord'; VLC='vlc'; YT='youtube-dl'
                       Steam='steam'; Epic='com.heroicgameslauncher.hgl'; TLauncher='minecraft-launcher'; NVIDIA='nvidia-settings'; Windhawk='preferences-desktop-theme' } }
  gaming   = @{ url = 'https://raw.githubusercontent.com/EliverLara/candy-icons/master/apps/scalable/{0}.svg'
                n = @{ Firefox='firefox'; Explorador='org.gnome.Nautilus'; Notion='notion-desktop'; Spotify='spotify'; Discord='discord'; VLC='vlc'; YT='youtube-dl'
                       Steam='steam'; Epic='heroic'; TLauncher='minecraft-launcher'; NVIDIA='nvidia-settings'; Windhawk='org.gnome.tweaks' } }
  potencia = @{ url = 'https://raw.githubusercontent.com/vinceliuice/WhiteSur-icon-theme/master/src/apps/scalable/{0}.svg'
                n = @{ Firefox='firefox'; Explorador='file-manager'; Notion='notion'; Spotify='spotify-client'; Discord='discord'; VLC='vlc'; YT='youtube-dl'
                       Steam='steam'; Epic='heroic'; TLauncher='minecraft'; NVIDIA='nvidia'; Windhawk='gnome-tweak-tool' } }
}
# Acceso directo -> app
$accesos = @{ 'Firefox.lnk'='Firefox'; 'File Explorer.lnk'='Explorador'; 'Notion.lnk'='Notion'; 'Spotify.lnk'='Spotify'; 'Discord.lnk'='Discord'
              'VLC media player.lnk'='VLC'; 'YT Downloader.lnk'='YT'; 'Steam.lnk'='Steam'; 'Epic Games Launcher.lnk'='Epic'; 'TLauncher.lnk'='TLauncher'
              'NVIDIA App.lnk'='NVIDIA'; 'Windhawk.lnk'='Windhawk' }

# ICO con PNG dentro (256, 64, 48, 32, 16): nítido en cualquier tamaño de icono del escritorio.
function Write-Ico($pngs, $out) {
  $ms = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ms
  $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)
  $off = 6 + 16 * $pngs.Count
  foreach ($p in $pngs) {
    $s = $p.size; $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$p.data.Length); $bw.Write([uint32]$off)
    $off += $p.data.Length
  }
  foreach ($p in $pngs) { $bw.Write($p.data) }
  [IO.File]::WriteAllBytes($out, $ms.ToArray())
}

$tmp = Join-Path $env:TEMP 'okla-iconos'; New-Item -ItemType Directory -Force $tmp | Out-Null
$sh = New-Object -ComObject WScript.Shell
foreach ($modo in $temas.Keys) {
  $dir = Join-Path $base $modo; New-Item -ItemType Directory -Force $dir | Out-Null
  foreach ($app in $temas[$modo].n.Keys) {
    $ico = Join-Path $dir "$app.ico"
    if (-not (Test-Path $ico)) {
      $svg = Join-Path $tmp "$modo-$app.svg"
      $url = $temas[$modo].url -f $temas[$modo].n[$app]
      try {
        # En git muchos iconos son enlaces simbólicos: el archivo trae la ruta del icono real en vez del SVG.
        for ($i = 0; $i -lt 4; $i++) {
          Invoke-WebRequest $url -OutFile $svg -UseBasicParsing -TimeoutSec 30
          $txt = [IO.File]::ReadAllText($svg).Trim()
          if ($txt.Contains('<')) { break }
          $url = [Uri]::new([Uri]$url, $txt).AbsoluteUri
        }
      } catch { "FALLO $modo/$app"; continue }
      $pngs = foreach ($s in 256, 64, 48, 32, 16) {
        $png = Join-Path $tmp "$modo-$app-$s.png"
        & $ff -hide_banner -loglevel error -y -width $s -height $s -i $svg -frames:v 1 -pix_fmt rgba $png
        if (Test-Path $png) { @{ size = $s; data = [IO.File]::ReadAllBytes($png) } }
      }
      if ($pngs.Count -eq 0) { "FALLO render $modo/$app"; continue }
      Write-Ico $pngs $ico
    }
  }
  # Accesos del escritorio del modo
  foreach ($lnk in Get-ChildItem (Join-Path $Okla "escritorios\$modo\*.lnk")) {
    $app = $accesos[$lnk.Name]
    if (-not $app) { continue }
    $ico = Join-Path $dir "$app.ico"
    if (-not (Test-Path $ico)) { continue }
    $l = $sh.CreateShortcut($lnk.FullName); $l.IconLocation = "$ico,0"; $l.Save()
  }
  "{0}: {1} iconos" -f $modo, (Get-ChildItem $dir -Filter *.ico).Count
}


# Iconos del sistema por modo (carpeta, papelera vacía/llena, Este equipo). okla los pone al cambiar de modo
# (IconosSistema.cs); aquí solo se preparan una vez, para que el cambio de modo no tarde más.
$sistema = @{
  fedora   = @{ base = 'https://raw.githubusercontent.com/PapirusDevelopmentTeam/papirus-icon-theme/master/Papirus/64x64/'
                n = @{ carpeta = 'places/folder'; papelera = 'places/user-trash'; 'papelera-llena' = 'places/user-trash-full'; equipo = 'devices/computer' } }
  gaming   = @{ base = 'https://raw.githubusercontent.com/EliverLara/candy-icons/master/'
                n = @{ carpeta = 'places/48/folder'; papelera = 'places/48/user-trash'; 'papelera-llena' = 'places/48/user-trash-full'; equipo = 'preferences/scalable/computer' } }
  potencia = @{ base = 'https://raw.githubusercontent.com/vinceliuice/WhiteSur-icon-theme/master/src/'
                n = @{ carpeta = 'places/scalable/folder'; papelera = 'places/scalable/user-trash'; 'papelera-llena' = 'places/scalable/user-trash-full'; equipo = 'devices/scalable/computer-laptop' } }   # computer = iMac con logo de Apple
}
foreach ($modo in $sistema.Keys) {
  $dir = Join-Path $base "$modo\sistema"; New-Item -ItemType Directory -Force $dir | Out-Null
  foreach ($k in $sistema[$modo].n.Keys) {
    $ico = Join-Path $dir "$k.ico"
    if (Test-Path $ico) { continue }
    $svg = Join-Path $tmp "sis-$modo-$k.svg"
    $url = $sistema[$modo].base + $sistema[$modo].n[$k] + '.svg'
    try {
      for ($i = 0; $i -lt 4; $i++) {
        Invoke-WebRequest $url -OutFile $svg -UseBasicParsing -TimeoutSec 30
        $txt = [IO.File]::ReadAllText($svg).Trim()
        if ($txt.Contains('<')) { break }
        $url = [Uri]::new([Uri]$url, $txt).AbsoluteUri
      }
    } catch { "FALLO sistema $modo/$k"; continue }
    $pngs = foreach ($s in 256, 64, 48, 32, 16) {
      $png = Join-Path $tmp "sis-$modo-$k-$s.png"
      & $ff -hide_banner -loglevel error -y -width $s -height $s -i $svg -frames:v 1 -pix_fmt rgba $png
      if (Test-Path $png) { @{ size = $s; data = [IO.File]::ReadAllBytes($png) } }
    }
    if ($pngs.Count -eq 0) { "FALLO render sistema $modo/$k"; continue }
    Write-Ico $pngs $ico
  }
  "{0}: {1} iconos del sistema" -f $modo, (Get-ChildItem $dir -Filter *.ico).Count
}
