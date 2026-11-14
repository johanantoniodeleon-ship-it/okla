# Crea <okla>\escritorios\<modo> con los accesos directos de cada modo (copias; el escritorio original no se toca).
# Funciona en cualquier PC: busca los accesos en el escritorio y el menú Inicio por nombre de app.
param([string]$Okla = (Join-Path $env:USERPROFILE 'okla'), [switch]$Probar)

$root = if ($Probar) { Join-Path $env:TEMP 'okla-escritorios-prueba' } else { Join-Path $Okla 'escritorios' }
$carpetas = @(
  [Environment]::GetFolderPath('Desktop'),
  [Environment]::GetFolderPath('CommonDesktopDirectory'),
  (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
  (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs')
) | Where-Object { $_ -and (Test-Path $_) }

# Todos los accesos que hay, por nombre (el primero que aparece gana: escritorio antes que menú Inicio).
$accesos = [ordered]@{}
foreach ($c in $carpetas) {
  foreach ($l in Get-ChildItem $c -Filter *.lnk -Recurse -ErrorAction SilentlyContinue) {
    if ($l.BaseName -match 'Uninstall|Desinstalar|Readme|Léame|Help|Ayuda|Website|Sitio web') { continue }
    if (-not $accesos.Contains($l.BaseName)) { $accesos[$l.BaseName] = $l.FullName }
  }
}

# Tipos de app: patrón sobre el nombre del acceso.
$tipos = [ordered]@{
  juegos    = '^(Steam|Epic Games Launcher|Battle\.net|Riot Client|EA( app)?|Ubisoft Connect|GOG Galaxy|Xbox|TLauncher|Minecraft( Launcher)?|Prism Launcher|CurseForge|Modrinth App|Heroic Games Launcher|Playnite)$'
  chat      = '^(Discord|Vesktop|TeamSpeak.*|WhatsApp|Telegram( Desktop)?)$'
  grafica   = '^(NVIDIA App|GeForce Experience|AMD Software.*|Intel.*Graphics Command Center)$'
  navegador = '^(Firefox|Mozilla Firefox|Google Chrome|Microsoft Edge|Brave|Opera( GX)?( Browser)?|Vivaldi|Zen Browser|Arc)$'
  archivos  = '^(File Explorer|Explorador de archivos)$'
  notas     = '^(Notion|Obsidian|OneNote|Evernote|Logseq|Anytype)$'
  musica    = '^(Spotify|Apple Music|Tidal|Deezer|YouTube Music)$'
  diseno    = '^(Canva|Figma|GIMP.*|Krita|Inkscape|Affinity.*|Adobe Photoshop.*|Blender.*)$'
  video     = '^(VLC media player|mpv|MPC-HC.*|PotPlayer.*|DaVinci Resolve|Shotcut|Kdenlive|CapCut|YT Downloader|Stacher.*)$'
  oficina   = '^(Word|Excel|PowerPoint|LibreOffice.*|Visual Studio Code)$'
  extras    = '^(Windhawk)$'
}
# Edge viene con Windows: solo se usa si no hay otro navegador.
$otros = @($accesos.Keys | Where-Object { $_ -match $tipos.navegador -and $_ -ne "Microsoft Edge" })
if ($otros.Count -gt 0) { $accesos.Remove("Microsoft Edge") }
function DeTipo([string[]]$t) {
  $r = @(); foreach ($k in $t) { $p = $tipos[$k]; $r += @($accesos.Keys | Where-Object { $_ -match $p }) }
  return @($r | Select-Object -Unique)
}
# Explorador: si no hay acceso, se crea uno.
if (-not ($accesos.Keys | Where-Object { $_ -match $tipos.archivos })) {
  $ws = New-Object -ComObject WScript.Shell; $tmp = Join-Path $env:TEMP 'Explorador de archivos.lnk'
  $s = $ws.CreateShortcut($tmp); $s.TargetPath = 'explorer.exe'; $s.Save(); $accesos['Explorador de archivos'] = $tmp
}

$modos = [ordered]@{
  gaming       = DeTipo 'juegos', 'chat', 'grafica'
  fedora       = DeTipo 'navegador', 'archivos', 'notas', 'musica', 'chat', 'diseno', 'video'
  potencia     = DeTipo 'navegador', 'archivos', 'juegos', 'chat', 'musica', 'notas', 'diseno', 'video', 'oficina', 'grafica', 'extras'
  presentacion = @()   # escritorio limpio para grabar
}
foreach ($m in $modos.Keys) {
  $dir = Join-Path $root $m
  New-Item -ItemType Directory -Force $dir | Out-Null
  foreach ($n in $modos[$m]) { Copy-Item $accesos[$n] $dir -Force }
  Write-Output ("{0}: {1} -> {2}" -f $m, $modos[$m].Count, ($modos[$m] -join ', '))
}

# Acceso a okla en los escritorios de los modos (menos Presentación, que queda limpio).
# Abrir okla estando ya en marcha abre el editor de modos.
$exe = @(
  (Join-Path $env:LOCALAPPDATA 'okla\current\okla.exe'),   # instalado con Velopack
  (Join-Path $Okla 'app\okla.exe')                         # copia de desarrollo
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($exe) {
  $ws = New-Object -ComObject WScript.Shell
  foreach ($m in 'gaming', 'fedora', 'potencia') {
    $s = $ws.CreateShortcut((Join-Path (Join-Path $root $m) 'okla.lnk'))
    $s.TargetPath = $exe; $s.WorkingDirectory = Split-Path $exe; $s.Description = 'okla: modos de escritorio'
    $s.IconLocation = "$exe,0"; $s.Save()
  }
  Write-Output "okla.lnk -> $exe"
}
