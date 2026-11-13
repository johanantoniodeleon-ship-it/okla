# okla

Cambia Windows entre modos con un atajo: tema, colores, fondo animado, barra de tareas, mods de Windhawk, energía y escritorio, todo de golpe.

| Atajo | Modo | Para qué |
|---|---|---|
| Ctrl+Alt+1 | Gaming | Consumo mínimo para jugar, congela apps de fondo |
| Ctrl+Alt+2 | Fedora | Amigable, inspirado en GNOME |
| Ctrl+Alt+3 | Potencia | Todo lo visual al máximo |
| Ctrl+Alt+4 | Presentación | Limpio para grabar (se activa solo al grabar con OBS) |
| Ctrl+Alt+5 | Batería | Todo al mínimo (se activa solo al desconectar el cargador) |
| Ctrl+Alt+0 | Pánico | Devuelve Windows a como estaba |

Ctrl+Alt+E abre el editor de modos.

## Instalar

Descarga `okla-win-Setup.exe` desde [Releases](../../releases/latest) y ábrelo. Windows puede avisar de que el programa no está firmado: "Más información" → "Ejecutar de todas formas". La primera vez okla pide permiso de administrador una sola vez y prepara todo: ffmpeg, Windhawk y sus mods, los fondos animados y los escritorios de cada modo.

Requisitos: Windows 11 de 64 bits (si falta .NET 8, el instalador lo descarga). okla se actualiza solo cuando sale una versión nueva.

## Licencia

MIT: puedes usar, copiar y modificar okla libremente manteniendo el aviso de autor (ver `LICENSE`).

## Créditos

- Fondos animados: [motionbgs.com](https://motionbgs.com) y sus autores. okla no los incluye: los descarga de su web (lista en `catalogo/fondos.json`).
- Mods de Windows: [Windhawk](https://windhawk.net) y los autores de cada mod.
- Conversión de video: [FFmpeg](https://ffmpeg.org) (compilación de [BtbN](https://github.com/BtbN/FFmpeg-Builds)).
- Icono de batería: [pixelarticons](https://github.com/halfmage/pixelarticons) (MIT).
- Logo de Windows del modo Pánico: [Font Awesome Free](https://fontawesome.com) (CC BY 4.0).
- Iconos de apps por modo: [Papirus](https://github.com/PapirusDevelopmentTeam/papirus-icon-theme), [Candy Icons](https://github.com/EliverLara/candy-icons) y [WhiteSur](https://github.com/vinceliuice/WhiteSur-icon-theme) (GPL-3.0), descargados al instalar.
- Lectura de YAML: [js-yaml](https://github.com/nodeca/js-yaml) (MIT).

