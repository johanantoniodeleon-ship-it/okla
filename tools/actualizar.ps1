# Actualiza okla sin permisos de administrador: señal de salida -> publicar -> relanzar la tarea elevada.
New-Item $env:USERPROFILE\okla\config\salir.senal -ItemType File -Force | Out-Null
for($i=0; $i -lt 20 -and (Get-Process okla -ErrorAction SilentlyContinue); $i++){ Start-Sleep -Milliseconds 300 }
& "C:\Program Files\dotnet\dotnet.exe" publish $env:USERPROFILE\okla\src\Okla\Okla.csproj -c Release -r win-x64 --self-contained false -o $env:USERPROFILE\okla\app 2>&1 | Select-String ' error |app\\'
schtasks /run /tn okla | Out-Null

