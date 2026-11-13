# Crea en OBS (abierto) la escena "Grabación": pantalla completa + cámara (oculta, esquina inferior derecha)
# + supresión de ruido en el micrófono. Usa obs-websocket v5 con la contraseña de okla\config\obs.json.
$cfg = Get-Content $env:USERPROFILE\okla\config\obs.json -Raw | ConvertFrom-Json
$ws = [System.Net.WebSockets.ClientWebSocket]::new()
$ws.ConnectAsync([Uri]"ws://127.0.0.1:$($cfg.puerto)", [Threading.CancellationToken]::None).Wait()

function Recv {
  $buf = New-Object byte[] 1048576; $ms = New-Object IO.MemoryStream
  do { $r = $ws.ReceiveAsync([ArraySegment[byte]]::new($buf), [Threading.CancellationToken]::None).Result; $ms.Write($buf, 0, $r.Count) } while (-not $r.EndOfMessage)
  [Text.Encoding]::UTF8.GetString($ms.ToArray()) | ConvertFrom-Json -Depth 50
}
function Send($obj) {
  $b = [Text.Encoding]::UTF8.GetBytes(($obj | ConvertTo-Json -Depth 50 -Compress))
  $ws.SendAsync([ArraySegment[byte]]::new($b), 'Text', $true, [Threading.CancellationToken]::None).Wait()
}
$script:n = 0
function Req($type, $data = @{}) {
  $script:n++; $id = "r$script:n"
  Send @{ op = 6; d = @{ requestType = $type; requestId = $id; requestData = $data } }
  while ($true) { $m = Recv; if ($m.op -eq 7 -and $m.d.requestId -eq $id) { break } }
  if (-not $m.d.requestStatus.result) { Write-Output "  $type -> $($m.d.requestStatus.code) $($m.d.requestStatus.comment)" }
  $m.d.responseData
}

# Saludo + autenticación
$hello = Recv
$sha = [Security.Cryptography.SHA256]::Create()
$secret = [Convert]::ToBase64String($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($cfg.contrasena + $hello.d.authentication.salt)))
$auth = [Convert]::ToBase64String($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($secret + $hello.d.authentication.challenge)))
Send @{ op = 1; d = @{ rpcVersion = 1; authentication = $auth; eventSubscriptions = 0 } }
$idf = Recv; if ($idf.op -ne 2) { throw "OBS rechazó la conexión" }
"Conectado a OBS $((Req 'GetVersion').obsVersion)"

$escena = 'Grabación'
$lista = Req 'GetSceneList'
if (-not ($lista.scenes | Where-Object sceneName -eq $escena)) { Req 'CreateScene' @{ sceneName = $escena } | Out-Null }
Req 'SetCurrentProgramScene' @{ sceneName = $escena } | Out-Null
foreach ($s in $lista.scenes | Where-Object sceneName -ne $escena) { Req 'RemoveScene' @{ sceneName = $s.sceneName } | Out-Null }

$inputs = (Req 'GetInputList').inputs
# Pantalla
if (-not ($inputs | Where-Object inputName -eq 'Pantalla')) {
  Req 'CreateInput' @{ sceneName = $escena; inputName = 'Pantalla'; inputKind = 'monitor_capture'; inputSettings = @{ capture_cursor = $true } } | Out-Null
}
$mon = (Req 'GetInputPropertiesListPropertyItems' @{ inputName = 'Pantalla'; propertyName = 'monitor_id' }).propertyItems
"Pantallas: " + (($mon | ForEach-Object itemName) -join ' | ')
$m1 = $mon | Where-Object { $_.itemValue -and -not $_.itemName.StartsWith('[') } | Select-Object -First 1   # la primera opción es "[Seleccionar pantalla]", vacía
if ($m1) { Req 'SetInputSettings' @{ inputName = 'Pantalla'; inputSettings = @{ monitor_id = $m1.itemValue } } | Out-Null; "Pantalla elegida: $($m1.itemName)" }

# Cámara (oculta: la enciendes con el ojo en OBS cuando la quieras)
if (-not ($inputs | Where-Object inputName -eq 'Cámara')) {
  Req 'CreateInput' @{ sceneName = $escena; inputName = 'Cámara'; inputKind = 'dshow_input'; sceneItemEnabled = $false } | Out-Null
}
$cams = (Req 'GetInputPropertiesListPropertyItems' @{ inputName = 'Cámara'; propertyName = 'video_device_id' }).propertyItems
"Cámaras: " + (($cams | ForEach-Object itemName) -join ' | ')
$cam = $cams | Where-Object itemName -like '*Integrated*' | Select-Object -First 1
if (-not $cam) { $cam = $cams | Where-Object { $_.itemValue } | Select-Object -First 1 }
if ($cam) { Req 'SetInputSettings' @{ inputName = 'Cámara'; inputSettings = @{ video_device_id = $cam.itemValue } } | Out-Null }
$item = (Req 'GetSceneItemId' @{ sceneName = $escena; sourceName = 'Cámara' }).sceneItemId
Req 'SetSceneItemTransform' @{ sceneName = $escena; sceneItemId = $item; sceneItemTransform = @{
  boundsType = 'OBS_BOUNDS_SCALE_INNER'; boundsWidth = 768; boundsHeight = 432; boundsAlignment = 0; alignment = 5
  positionX = 3840 - 768 - 60; positionY = 2160 - 432 - 60 } } | Out-Null
$pant = (Req 'GetSceneItemId' @{ sceneName = $escena; sourceName = 'Pantalla' }).sceneItemId
Req 'SetSceneItemTransform' @{ sceneName = $escena; sceneItemId = $pant; sceneItemTransform = @{
  boundsType = 'OBS_BOUNDS_SCALE_INNER'; boundsWidth = 3840; boundsHeight = 2160; boundsAlignment = 0; alignment = 5; positionX = 0; positionY = 0 } } | Out-Null
Req 'SetSceneItemIndex' @{ sceneName = $escena; sceneItemId = $item; sceneItemIndex = 1 } | Out-Null

# Audio: escritorio + micrófono con supresión de ruido
$inputs = (Req 'GetInputList').inputs
"Entradas: " + (($inputs | ForEach-Object { "$($_.inputName) [$($_.inputKind)]" }) -join ' | ')
$mic = $inputs | Where-Object inputKind -eq 'wasapi_input_capture' | Select-Object -First 1
if (-not $mic) {
  Req 'CreateInput' @{ sceneName = $escena; inputName = 'Micrófono'; inputKind = 'wasapi_input_capture'; inputSettings = @{ device_id = 'default' } } | Out-Null
  $mic = @{ inputName = 'Micrófono' }
}
if (-not ($inputs | Where-Object inputKind -eq 'wasapi_output_capture')) {
  Req 'CreateInput' @{ sceneName = $escena; inputName = 'Audio del escritorio'; inputKind = 'wasapi_output_capture'; inputSettings = @{ device_id = 'default' } } | Out-Null
}
$filtros = (Req 'GetSourceFilterList' @{ sourceName = $mic.inputName }).filters
if (-not ($filtros | Where-Object filterKind -like 'noise_suppress_filter*')) {
  Req 'CreateSourceFilter' @{ sourceName = $mic.inputName; filterName = 'Supresión de ruido'; filterKind = 'noise_suppress_filter_v2'; filterSettings = @{ method = 'rnnoise' } } | Out-Null
}
"Escena: " + (((Req 'GetSceneItemList' @{ sceneName = $escena }).sceneItems | ForEach-Object { "$($_.sourceName) (visible=$($_.sceneItemEnabled))" }) -join ', ')
"Micrófono '$($mic.inputName)' con supresión de ruido"
$rec = Req 'GetRecordDirectory'; "Carpeta de grabación: $($rec.recordDirectory)"
$ws.Dispose()

