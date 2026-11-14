using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace Okla
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // Velopack: atiende la instalación, desinstalación y actualizaciones antes que nada (sale solo si le toca).
            Velopack.VelopackApp.Build().Run();

            var mutex = new Mutex(true, @"Local\okla-single-instance", out bool first);
            if (!first)
            {
                // Ya hay un okla abierto: se le pide que muestre el editor (así el icono del escritorio "abre" okla).
                mutex.Dispose();
                try { System.IO.File.WriteAllText(System.IO.Path.Combine(Paths.Config, "abrir.senal"), ""); } catch { }
                return;
            }

            Paths.Ensure();
            bool instalar = args.Contains("--instalar") || !Instalador.Hecho;
            bool admin = Instalador.EsAdmin();

            // Sin permisos: o se piden para instalar, o se abre la tarea "okla" (que ya los tiene) y este se cierra.
            if (!admin)
            {
                mutex.ReleaseMutex(); mutex.Dispose();
                if (instalar) Instalador.RelanzarElevado("--instalar");
                else
                {
                    using var pr = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", "/run /tn okla") { UseShellExecute = false, CreateNoWindow = true })!;
                    pr.WaitForExit(5000);
                    if (pr.ExitCode != 0) Instalador.RelanzarElevado("--instalar");   // la tarea no existe: se repara
                }
                return;
            }

            Log.Info("okla arrancando");
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (_, e) => { Log.Error("No controlado: " + e.Exception); e.Handled = true; };

            var core = new OklaCore();
            app.Startup += (_, _) =>
            {
                if (instalar)
                {
                    Log.Info("Instalador abierto");
                    var w = new InstaladorWindow();
                    if (w.ShowDialog() != true && !Instalador.Hecho) { app.Shutdown(); return; }
                }
                core.Start();
            };
            app.Exit += (_, _) => core.Dispose();
            app.Run();
        }
    }

    /// <summary>Corazón de okla: bandeja, atajos y cambio de modo.</summary>
    internal sealed class OklaCore : IDisposable
    {
        private List<ModeDef> _modes = new();
        private AppState _state = new();
        private Hotkeys? _hotkeys;
        private WinForms.NotifyIcon? _tray;
        private readonly Dictionary<string, WinForms.ToolStripMenuItem> _modeItems = new();

        private readonly Engine _engine = new();
        private MiniHub? _mini;
        private System.Windows.Threading.DispatcherTimer? _pillTimer;
        private readonly System.Threading.SemaphoreSlim _applyGate = new(1, 1);

        public void Start()
        {
            _modes = ModeStore.LoadModes();
            _state = ModeStore.LoadState();
            Regulador.Iniciar();   // antes que nada: decide a qué resolución se preparan los fondos
            Regulador.HayVideo = () =>
            {
                var m = ModeStore.Find(_modes, _state.ModoActual);
                return m?.Ajustes != null && m.Ajustes.TryGetValue("wh:live-video-wallpaper", out var v) && v.Contains("\"activo\":\"1\"");
            };
            Regulador.Cambio += motivo => Application.Current.Dispatcher.BeginInvoke(() => AlCambiarRendimiento(motivo));
            _engine.LoadOrCaptureBaseline();
            AppPause.Reanudar();   // por si okla se cerró de golpe con apps congeladas
            BuildTray();
            RegisterHotkeys();
            StartGameWatch();
            WatchQuitSignal();
            StartObsWatch();
            StartBatteryWatch();
            StartDisplayWatch();
            StartUpdates();

            // Arranca en el modo amigable (Fedora) o, si está desactivado, en el primero activo.
            var inicio = ModeStore.EstaActivo(_modes, "A") ? "A" : _modes.FirstOrDefault(m => m.Activo && m.Key != "B")?.Key;
            if (inicio != null) SwitchTo(inicio, initial: true);
            // Sin cargador: se pregunta (nunca se cambia a Batería sin avisar).
            if (EnBateria()) PreguntarBateria();
        }

        private void RegisterHotkeys()
        {
            _hotkeys = new Hotkeys();
            var fallos = new List<string>();
            foreach (var m in _modes)
            {
                var key = m.Key;
                if (!_hotkeys.Register(Hotkeys.MOD_CONTROL | Hotkeys.MOD_ALT, (uint)('0' + m.Tecla), () => SwitchTo(key)))
                    fallos.Add($"Ctrl+Alt+{m.Tecla}");
            }
            if (!_hotkeys.Register(Hotkeys.MOD_CONTROL | Hotkeys.MOD_ALT, '0', Panic))
                fallos.Add("Ctrl+Alt+0");
            _mini = new MiniHub(() => ModeStore.Find(_modes, _state.ModoActual), m =>
            {
                // Cambio desde el mini hub: se guarda y se aplica en silencio (sin pantalla de carga).
                ModeStore.SaveModes(_modes);
                SetTrayIcon(m.Color, m.Nombre);
                ApplyAsync(m.Ajustes ?? new(), m.Nombre + " (mini hub)");
            });
            if (!_hotkeys.Register(Hotkeys.MOD_CONTROL | Hotkeys.MOD_ALT, 'H', () => _mini.TogglePanel()))
                fallos.Add("Ctrl+Alt+H");
            if (!_hotkeys.Register(Hotkeys.MOD_CONTROL | Hotkeys.MOD_ALT, 'E', OpenEditor))
                fallos.Add("Ctrl+Alt+E");

            if (fallos.Count > 0)
            {
                var msg = "Otra aplicación ya usa: " + string.Join(", ", fallos);
                Log.Error(msg);
                _tray?.ShowBalloonTip(5000, "okla", msg, WinForms.ToolTipIcon.Warning);
            }
            else Log.Info("Atajos registrados: Ctrl+Alt+0..5, Ctrl+Alt+H y Ctrl+Alt+E");
        }

        // ---- Editor de modos ----
        private EditorWindow? _editor;

        private void OpenEditor()
        {
            if (_editor != null) { _editor.Activate(); return; }
            _editor = new EditorWindow(_modes, _state.ModoActual, _engine, (nuevos, probar) =>
            {
                _modes = nuevos;
                ModeStore.SaveModes(_modes);
                RehacerMenu();   // nombres, y modos activados o desactivados
                Log.Info("Editor: modos guardados" + (probar != null ? $", probando {probar}" : ""));
                var cur = ModeStore.Find(_modes, _state.ModoActual);
                if (probar != null) SwitchTo(probar);
                else if (cur != null) { SetTrayIcon(cur.Color, cur.Nombre); ApplyAsync(cur.Ajustes ?? new(), cur.Nombre + " (editor)"); }
            });
            _editor.Closed += (_, _) => _editor = null;
            _editor.Show();
            _editor.Activate();
        }

        private void BuildTray()
        {
            _tray = new WinForms.NotifyIcon { ContextMenuStrip = CrearMenu(), Visible = true, Text = "okla" };
        }

        /// <summary>Vuelve a armar el menú (al activar o desactivar modos en el editor).</summary>
        private void RehacerMenu()
        {
            if (_tray == null) return;
            var viejo = _tray.ContextMenuStrip;
            _modeItems.Clear(); _nivelItems.Clear();
            _tray.ContextMenuStrip = CrearMenu();
            viejo?.Dispose();
            foreach (var (k, it) in _modeItems) it.Checked = k == _state.ModoActual;
        }

        private WinForms.ContextMenuStrip CrearMenu()
        {
            var menu = new WinForms.ContextMenuStrip { ShowImageMargin = true, ImageScalingSize = new System.Drawing.Size(24, 24) };
            var title = new WinForms.ToolStripMenuItem("okla") { Enabled = false };
            menu.Items.Add(title);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            WinForms.ToolStripMenuItem ItemModo(ModeDef m, string? texto = null)
            {
                var key = m.Key;
                var it = new WinForms.ToolStripMenuItem($"{texto ?? m.Nombre}\tCtrl+Alt+{m.Tecla}") { Image = IconosModos.Gdi(key) };
                it.Click += (_, _) => SwitchTo(key);
                _modeItems[key] = it;
                return it;
            }
            // Modos normales (Batería va aparte, con el pánico: es un modo de emergencia, no de uso diario).
            foreach (var m in _modes.Where(m => m.Activo && m.Key != "B")) menu.Items.Add(ItemModo(m));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            if (ModeStore.Find(_modes, "B") is { Activo: true } bat) menu.Items.Add(ItemModo(bat, "Modo Batería"));
            var panic = new WinForms.ToolStripMenuItem("Restaurar Windows (pánico)\tCtrl+Alt+0") { Image = IconosModos.Gdi("panico") };
            panic.Click += (_, _) => Panic();
            menu.Items.Add(panic);
            var rebase = new WinForms.ToolStripMenuItem("Guardar Windows actual como base…");
            rebase.Click += (_, _) => Rebase();
            menu.Items.Add(rebase);
            menu.Items.Add(MenuRendimiento());
            // Modos activos: encender o apagar cada modo sin abrir el editor.
            var activos = new WinForms.ToolStripMenuItem("Modos activos");
            foreach (var m in _modes)
            {
                var mm = m;
                var it = new WinForms.ToolStripMenuItem(m.Nombre) { Checked = m.Activo, Image = IconosModos.Gdi(m.Key), CheckOnClick = false };
                it.Click += (_, _) =>
                {
                    if (mm.Activo && _modes.Count(x => x.Activo && x.Key != "B") == 1 && mm.Key != "B") { _tray?.ShowBalloonTip(3000, "okla", "Deja al menos un modo activo.", WinForms.ToolTipIcon.Info); return; }
                    mm.Activo = !mm.Activo;
                    ModeStore.SaveModes(_modes);
                    Log.Info($"{mm.Nombre}: {(mm.Activo ? "activado" : "desactivado")} desde la bandeja");
                    // Si se apaga el modo en uso, se pasa al primero que siga activo.
                    if (!mm.Activo && _state.ModoActual == mm.Key && _modes.FirstOrDefault(x => x.Activo && x.Key != "B") is { } otro) SwitchTo(otro.Key);
                    Application.Current.Dispatcher.BeginInvoke(RehacerMenu);
                };
                activos.DropDownItems.Add(it);
            }
            activos.DropDownItems.Add(new WinForms.ToolStripSeparator());
            activos.DropDownItems.Add(new WinForms.ToolStripMenuItem("Pánico (siempre activo)") { Checked = true, Enabled = false, Image = IconosModos.Gdi("panico") });
            menu.Items.Add(activos);
            if (ModeStore.EstaActivo(_modes, "J"))
            {
                var hub = new WinForms.ToolStripMenuItem("Abrir hub de juegos");
                hub.Click += (_, _) => { if (_state.ModoActual != "J") SwitchTo("J"); else ShowHub(); };
                menu.Items.Add(hub);
            }
            var editor = new WinForms.ToolStripMenuItem("Editor de modos\tCtrl+Alt+E");
            editor.Click += (_, _) => OpenEditor();
            menu.Items.Add(editor);
            var folder = new WinForms.ToolStripMenuItem("Abrir carpeta de recursos");
            folder.Click += (_, _) => System.Diagnostics.Process.Start("explorer.exe", Paths.Recursos);
            menu.Items.Add(folder);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            var exit = new WinForms.ToolStripMenuItem("Salir de okla");
            exit.Click += (_, _) => Application.Current.Shutdown();
            menu.Items.Add(exit);
            return menu;
        }

        // ---- OBS: al grabar, Presentación; al terminar, el modo de antes ----
        private ObsWatcher? _obs;
        private string? _antesDeGrabar;

        private void StartObsWatch()
        {
            _obs = new ObsWatcher();
            _obs.GrabacionEmpezo += () => Application.Current.Dispatcher.BeginInvoke(async () =>
            {
                if (_state.ModoActual == "P" || !ModeStore.EstaActivo(_modes, "P")) return;
                if (_gameRunning) { Log.Info("Grabando un juego: se queda en Gaming"); return; }
                _antesDeGrabar = _state.ModoActual;
                // La grabación se pausa mientras cambia el modo: el video empieza ya en Presentación, sin la transición.
                _obs.Enviar("PauseRecord");
                await SwitchTo("P");
                await System.Threading.Tasks.Task.Delay(2200);   // que la pantalla de carga termine de irse
                _obs.Enviar("ResumeRecord");
                Log.Info("Grabación: Presentación lista, grabación reanudada");
            });
            _obs.GrabacionTermino += () => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                var antes = _antesDeGrabar;
                _antesDeGrabar = null;
                if (antes != null && _state.ModoActual == "P") SwitchTo(antes == "" ? "A" : antes);
            });
        }

        // ---- Pantalla: fondos a la medida (Adaptador) y reaplicar al cambiar resolución o pantalla ----
        private void StartDisplayWatch()
        {
            var (w, h, hz) = Pantalla.Actual();
            Log.Info($"Pantalla: {w}x{h} a {hz} Hz");
            // Los fondos de los modos se preparan poco después de arrancar, sin estorbar al primer cambio de modo.
            var arranque = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            arranque.Tick += (_, _) => { arranque.Stop(); System.Threading.Tasks.Task.Run(() => Adaptador.Preparar(_modes)); };
            arranque.Start();

            System.Windows.Threading.DispatcherTimer? espera = null;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                // Windows manda varios avisos seguidos al cambiar de pantalla: se espera a que se calme.
                espera?.Stop();
                espera = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                espera.Tick += (_, _) =>
                {
                    espera!.Stop();
                    if (!Adaptador.Recalcular()) return;
                    var m = ModeStore.Find(_modes, _state.ModoActual);
                    if (m != null) ApplyAsync(m.Ajustes ?? new(), m.Nombre + " (pantalla nueva)");
                    System.Threading.Tasks.Task.Run(() => Adaptador.Preparar(_modes));
                };
                espera.Start();
            });
        }

        // ---- Actualizaciones (Velopack + GitHub Releases): al rato de arrancar y cada 6 horas ----
        private void StartUpdates()
        {
            Log.Info("Versión de okla: " + Actualizaciones.Version);
            if (!Actualizaciones.Instalado) return;   // copia de desarrollo
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
            t.Tick += async (_, _) =>
            {
                t.Interval = TimeSpan.FromHours(6);
                var v = await Actualizaciones.Buscar();
                if (v == null) return;
                // Nunca a mitad de una partida o una grabación: se reintenta en el siguiente aviso.
                if (_gameRunning || _obs?.Grabando == true) { t.Interval = TimeSpan.FromMinutes(15); return; }
                Toast.Show("okla " + v, "Actualizando… vuelve en unos segundos", "#7fd8be", Transition.Fundido, null);
                await System.Threading.Tasks.Task.Delay(4000);
                if (Actualizaciones.Aplicar()) Application.Current.Shutdown();
            };
            t.Start();
        }

        // ---- Regulador de rendimiento: nivel elegido o presión de carga -> reaplicar el modo actual ----
        private readonly Dictionary<string, (Nivel? nivel, WinForms.ToolStripMenuItem item)> _nivelItems = new();

        private void AlCambiarRendimiento(string motivo)
        {
            MarcarNivel();
            var m = ModeStore.Find(_modes, _state.ModoActual);
            if (motivo.StartsWith("nivel"))
            {
                Adaptador.Recalcular();
                System.Threading.Tasks.Task.Run(() => Adaptador.Preparar(_modes));
                Toast.Show("Rendimiento " + Regulador.Nombre(Regulador.Actual), Regulador.Actual switch
                {
                    Nivel.Alto => "Fondos a resolución completa, con video",
                    Nivel.Medio => "Fondos hasta 1440p, con video",
                    _ => "Fondos hasta 1080p, sin video ni translucidez",
                }, "#e5c07b", Transition.Fundido, null);
            }
            else if (motivo == "presion" && m != null)
                Toast.Show("Equipo al tope", "Pausé el video de fondo para aliviar la carga", "#e5c07b", Transition.Fundido, null);
            if (m != null) ApplyAsync(m.Ajustes ?? new(), m.Nombre + " (rendimiento)");
        }

        private WinForms.ToolStripMenuItem MenuRendimiento()
        {
            var raiz = new WinForms.ToolStripMenuItem("Rendimiento");
            foreach (var n in new Nivel?[] { null, Nivel.Alto, Nivel.Medio, Nivel.Bajo })
            {
                var it = new WinForms.ToolStripMenuItem(n == null ? "Automático" : char.ToUpper(Regulador.Nombre(n.Value)[0]) + Regulador.Nombre(n.Value)[1..]);
                var nivel = n;
                it.Click += (_, _) => Regulador.Forzar(nivel);
                _nivelItems[n?.ToString() ?? "auto"] = (n, it);
                raiz.DropDownItems.Add(it);
            }
            raiz.DropDownItems.Add(new WinForms.ToolStripSeparator());
            var p = Regulador.Perfil;
            raiz.DropDownItems.Add(new WinForms.ToolStripMenuItem($"{p.Hilos} hilos · {p.RamGB} GB · {p.GraficaPrincipal}") { Enabled = false });
            raiz.DropDownOpening += (_, _) => MarcarNivel();
            MarcarNivel();
            return raiz;
        }

        private void MarcarNivel()
        {
            foreach (var (n, it) in _nivelItems.Values) it.Checked = n == Regulador.Forzado;
            if (_nivelItems.TryGetValue("auto", out var auto))
                auto.item.Text = $"Automático (detectado: {Regulador.Nombre(Regulador.Perfil.Detectado)})" + (Regulador.Presion ? " · video en pausa por carga" : "");
        }

        // ---- Batería: al desconectar el cargador, modo Batería; al conectarlo, el modo de antes ----
        private string? _antesDeBateria;
        private bool _cambioPorCargador;

        private static bool EnBateria() =>
            WinForms.SystemInformation.PowerStatus.PowerLineStatus == WinForms.PowerLineStatus.Offline;

        private void StartBatteryWatch()
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
            {
                if (e.Mode != Microsoft.Win32.PowerModes.StatusChange) return;
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    bool bateria = EnBateria();
                    if (bateria && _state.ModoActual != "B") PreguntarBateria();
                    else if (!bateria) { _preguntaBateria?.Cancelar(); _preguntaBateria = null; }
                    if (!bateria && _state.ModoActual == "B" && _antesDeBateria != null)
                    {
                        var antes = _antesDeBateria == "" ? "A" : _antesDeBateria;
                        Log.Info($"Cargador conectado: vuelve a {antes}");
                        _antesDeBateria = null;
                        _cambioPorCargador = true;
                        SwitchTo(antes);
                    }
                });
            };
        }

        /// <summary>
        /// Cargador desconectado: pregunta si se activa el modo Batería (solo si ese modo está activado, y nunca
        /// jugando ni grabando). Si no hay respuesta en 30 s, no se cambia nada.
        /// </summary>
        private Pregunta? _preguntaBateria;

        private void PreguntarBateria()
        {
            if (!ModeStore.EstaActivo(_modes, "B") || _preguntaBateria != null) return;
            if (_gameRunning) { Log.Info("Cargador desconectado jugando: no se pregunta"); return; }
            if (_obs?.Grabando == true) { Log.Info("Cargador desconectado grabando: no se pregunta"); return; }
            Log.Info("Cargador desconectado: se pregunta por el modo Batería");
            var b = ModeStore.Find(_modes, "B")!;
            _preguntaBateria = new Pregunta("Cargador desconectado", "¿Activo el modo Batería? Baja el brillo al 40 %, apaga efectos y congela apps de fondo para que dure más.",
                b.Color, "B", "Activar", "Ahora no", 30, si =>
                {
                    _preguntaBateria = null;
                    Log.Info("Modo Batería: " + (si ? "aceptado" : "no"));
                    if (!si || !EnBateria() || _state.ModoActual == "B") return;
                    _antesDeBateria = _state.ModoActual;
                    _cambioPorCargador = true;
                    SwitchTo("B");
                });
            _preguntaBateria.Show();
        }

        public System.Threading.Tasks.Task SwitchTo(string key, bool initial = false)
        {
            var m = ModeStore.Find(_modes, key);
            if (m == null) return System.Threading.Tasks.Task.CompletedTask;
            if (!m.Activo) { Log.Info($"{m.Nombre} está desactivado: se ignora"); return System.Threading.Tasks.Task.CompletedTask; }
            // Si el usuario cambia de modo a mano, el cargador ya no decide a qué modo volver.
            if (!_cambioPorCargador && !initial) _antesDeBateria = key == "B" ? _antesDeBateria : null;
            _cambioPorCargador = false;

            Log.Info($"Cambio de modo: {_state.ModoActual} -> {key} ({m.Nombre})");
            Regulador.NuevoModo();
            _state.ModoActual = key;
            ModeStore.SaveState(_state);

            foreach (var (k, it) in _modeItems) it.Checked = k == key;
            SetTrayIcon(m.Color, m.Nombre);
            if (key == "J") { if (!_gameRunning) ShowHub(); }
            else CloseHub();
            // Batería congela las apps de fondo como Gaming con un juego; los demás modos las descongelan.
            if (key == "B") System.Threading.Tasks.Task.Run(() => AppPause.Pausar(null));
            else if (!(key == "J" && _gameRunning)) System.Threading.Tasks.Task.Run(AppPause.Reanudar);
            // La pastilla de fondos vuelve cuando ya se quitó la pantalla de carga.
            _mini?.ClosePill();
            _pillTimer?.Stop();
            _pillTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(initial ? 3 : 9) };
            _pillTimer.Tick += (_, _) => { _pillTimer.Stop(); if (_state.ModoActual == key) _mini?.ShowPill(); };
            _pillTimer.Start();
            var ls = LoadingScreen.Show(key, m.Nombre, m.Color, Poster(m));
            Toast.PlaySound(key);
            return ApplyAsync(m.Ajustes ?? new(), m.Nombre, ls);
        }

        private void Panic()
        {
            Log.Info("PÁNICO: restaurar Windows");
            _state.ModoActual = "";
            ModeStore.SaveState(_state);
            SetTrayIcon("#e06c75", "restaurado");
            foreach (var it in _modeItems.Values) it.Checked = false;
            CloseHub();
            System.Threading.Tasks.Task.Run(AppPause.Reanudar);
            _pillTimer?.Stop();
            _mini?.ClosePill();
            var ls = LoadingScreen.Show("panico", "Windows restaurado", "#e06c75", null);
            Toast.PlaySound("panico");
            ApplyAsync(null, "pánico", ls);
        }

        /// <summary>Aplica en segundo plano (powercfg y los avisos a Explorer tardan) y en orden.</summary>
        private System.Threading.Tasks.Task ApplyAsync(Dictionary<string, string>? perfil, string nombre, LoadingScreen? ls = null)
        {
            return System.Threading.Tasks.Task.Run(async () =>
            {
                await _applyGate.WaitAsync();
                if (ls != null) await ls.Covered;   // primero aparece la pantalla de carga, luego se cambia todo
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    List<string> errores;
                    bool whChanged = false;
                    try
                    {
                        errores = _engine.Apply(perfil, ls == null ? null : ls.Report);
                        whChanged = _engine.LastWindhawkChanges > 0;
                        if (whChanged)
                        {
                            // Segunda pasada: algunos mods, al apagarse, tocan cosas de Windows (p. ej. la barra
                            // en autoocultar al salir de Presentación). Se espera a que terminen y se corrige.
                            System.Threading.Thread.Sleep(2500);
                            ls?.Report("Revisando");
                            errores = _engine.Apply(perfil, ls == null ? null : ls.Report);
                        }
                    }
                    finally
                    {
                        ls?.Finish(TimeSpan.FromMilliseconds(whChanged ? 1200 : 800));
                    }
                    Log.Info($"Aplicado {nombre} en {sw.ElapsedMilliseconds} ms");
                    if (errores.Count > 0)
                        Application.Current.Dispatcher.Invoke(() =>
                            _tray?.ShowBalloonTip(6000, $"okla · {nombre}", "No se pudo cambiar:\n" + string.Join("\n", errores), WinForms.ToolTipIcon.Warning));
                }
                finally { _applyGate.Release(); }
            });
        }

        // ---- Gaming: hub de juegos + vigilante de juegos abiertos ----
        private GameHub? _hub;
        private bool _gameRunning;
        private List<Game> _games = new();
        private System.Windows.Threading.DispatcherTimer? _watch;

        /// <summary>
        /// okla corre como administrador y Claude no, así que no puede cerrarlo para actualizarlo.
        /// Si aparece config\salir.senal, okla se cierra solo (luego se relanza con schtasks /run /tn okla).
        /// </summary>
        private System.IO.FileSystemWatcher? _quitWatch;
        private void WatchQuitSignal()
        {
            var sig = System.IO.Path.Combine(Paths.Config, "salir.senal");
            try { System.IO.File.Delete(sig); } catch { }
            _quitWatch = new System.IO.FileSystemWatcher(Paths.Config, "salir.senal") { EnableRaisingEvents = true };
            _quitWatch.Created += (_, _) => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                Log.Info("Señal de salida recibida (actualización)");
                try { System.IO.File.Delete(sig); } catch { }
                Application.Current.Shutdown();
            });

            // Abrir okla (icono del escritorio o menú Inicio) con okla ya corriendo: se abre el editor.
            var abrir = System.IO.Path.Combine(Paths.Config, "abrir.senal");
            try { System.IO.File.Delete(abrir); } catch { }
            _openWatch = new System.IO.FileSystemWatcher(Paths.Config, "abrir.senal") { EnableRaisingEvents = true };
            _openWatch.Created += (_, _) => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                try { System.IO.File.Delete(abrir); } catch { }
                OpenEditor();
            });
        }
        private System.IO.FileSystemWatcher? _openWatch;

        private void ShowHub()
        {
            var m = ModeStore.Find(_modes, "J");
            // Al lanzar, el hub se oculta; el vigilante detecta el juego y lo vuelve a mostrar cuando se cierra.
            _hub ??= new GameHub(m == null ? null : Poster(m), _ => { });
            _hub.Reload();
            _hub.Show();
            _hub.Activate();
        }

        /// <summary>Al salir de Gaming el hub se cierra del todo (libera memoria).</summary>
        private void CloseHub() { _hub?.Close(); _hub = null; }

        private void StartGameWatch()
        {
            _games = GameLibrary.Detect();
            int n = 0;
            _watch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _watch.Tick += (_, _) =>
            {
                if (n++ % 20 == 0) _games = GameLibrary.Detect();   // cada minuto vuelve a buscar juegos nuevos
                var running = _games.FirstOrDefault(GameLibrary.IsRunning);
                if (running != null && !_gameRunning)
                {
                    _gameRunning = true;
                    Log.Info("Juego detectado: " + running.Nombre);
                    _hub?.Hide();
                    if (!ModeStore.EstaActivo(_modes, "J")) return;   // sin modo Gaming, okla no toca nada al jugar
                    _preguntaBateria?.Cancelar(); _preguntaBateria = null;
                    if (_state.ModoActual != "J") SwitchTo("J");
                    var proc = running.Proceso;
                    System.Threading.Tasks.Task.Run(() => AppPause.Pausar(proc));
                }
                else if (running == null && _gameRunning)
                {
                    _gameRunning = false;
                    Log.Info("Juego cerrado");
                    System.Threading.Tasks.Task.Run(AppPause.Reanudar);
                    if (_state.ModoActual == "J") ShowHub();   // se queda en Gaming y vuelve al hub
                }
            };
            _watch.Start();
        }

        /// <summary>Imagen para el fondo de la pantalla de carga: el póster del video del modo o su fondo.</summary>
        private static string? Poster(ModeDef m)
        {
            var a = m.Ajustes;
            if (a == null) return null;
            try
            {
                if (a.TryGetValue("wh:live-video-wallpaper", out var v))
                {
                    var d = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v);
                    if (d != null && d.GetValueOrDefault("activo") == "1" && d.TryGetValue("videoPath", out var vp))
                    {
                        var jpg = System.IO.Path.ChangeExtension(vp, ".jpg");
                        if (System.IO.File.Exists(jpg)) return jpg;
                    }
                }
                if (a.TryGetValue("fondo", out var f) && f.StartsWith("img:")) return f[4..];
            }
            catch { }
            return null;
        }

        private void Rebase()
        {
            var r = WinForms.MessageBox.Show(
                "okla guardará cómo está Windows AHORA (tema, acento, fondo, energía, barra, iconos…) como el estado al que vuelve el botón de pánico.\n\n" +
                "Hazlo solo si Windows está como te gusta fuera de okla. ¿Continuar?",
                "okla · Guardar base", WinForms.MessageBoxButtons.YesNo, WinForms.MessageBoxIcon.Question, WinForms.MessageBoxDefaultButton.Button2);
            if (r != WinForms.DialogResult.Yes) return;
            _engine.RecaptureBaseline();
            Toast.Show("Base guardada", "El pánico volverá a este estado", "#e5c07b", Transition.Fundido, null);
        }

        // ---- icono de bandeja: el icono de okla con un punto del color del modo ----
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
        private IntPtr _iconHandle;
        private Bitmap? _iconoBase;

        private void SetTrayIcon(string hex, string modo)
        {
            if (_tray == null) return;
            var c = ColorTranslator.FromHtml(hex);
            // Se lee a memoria para no dejar el archivo bloqueado (así se puede cambiar el icono con okla abierto).
            try { _iconoBase ??= new Bitmap(new System.IO.MemoryStream(System.IO.File.ReadAllBytes(Paths.Recurso(System.IO.Path.Combine("icono", "okla-64.png"))))); } catch { }
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.Transparent);
                if (_iconoBase != null)
                {
                    g.DrawImage(_iconoBase, 0, 0, 32, 32);
                    using var borde = new SolidBrush(Color.FromArgb(20, 20, 20));
                    g.FillEllipse(borde, 19f, 19f, 13f, 13f);
                    using var dot = new SolidBrush(c);
                    g.FillEllipse(dot, 21f, 21f, 9f, 9f);
                }
                else
                {
                    using var ring = new Pen(c, 5f);
                    g.DrawEllipse(ring, 4.5f, 4.5f, 23f, 23f);
                    using var dot = new SolidBrush(c);
                    g.FillEllipse(dot, 12f, 12f, 8f, 8f);
                }
            }
            var h = bmp.GetHicon();
            var old = _tray.Icon;
            _tray.Icon = Icon.FromHandle(h);
            _tray.Text = $"okla · {modo}";
            old?.Dispose();
            if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
            _iconHandle = h;
        }

        public void Dispose()
        {
            Log.Info("okla cerrando");
            _obs?.Dispose();
            AppPause.Reanudar();
            _hotkeys?.Dispose();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
        }
    }
}
