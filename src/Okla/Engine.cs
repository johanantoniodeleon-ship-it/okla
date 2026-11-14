using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Okla
{
    /// <summary>
    /// Motor de modos. La primera vez guarda la "base" (cómo estaba Windows antes de okla).
    /// Cada modo indica solo los ajustes que cambia; el resto vuelve a la base.
    /// Pánico = aplicar la base completa.
    /// </summary>
    internal sealed class Engine
    {
        // El orden importa poco, pero el fondo va al final para que el cambio de tema no lo pise.
        public readonly List<Setting> Settings = new()
        {
            new ThemeSetting(), new TransparencySetting(), new AccentSetting(), new AnimationSetting(),
            new PowerSetting(), new TaskbarAutoHideSetting(), new WallpaperSetting(), new BrightnessSetting(),
            // Escritorio: van en orden (carpeta -> tamaño -> mostrar), después del grupo en paralelo.
            new DesktopFolderSetting(), new IconSizeSetting(), new DesktopIconsSetting(),
            new WindhawkModSetting("translucent-windows", "Ventanas translúcidas", "BackgroundEffects.type"),
            new WindhawkModSetting("taskbar-dock-animation", "Animación dock"),
            new WindhawkModSetting("windows-11-taskbar-styler", "Barra de tareas", "theme", "controlStyles[0].target", "controlStyles[0].styles[0]"),
            new WindhawkModSetting("windows-11-start-menu-styler", "Menú Inicio", "theme"),
            new WindhawkModSetting("windows-11-file-explorer-styler", "Explorador", "theme"),
            new WindhawkModSetting("windows-11-notification-center-styler", "Notificaciones", "theme"),
            new WindhawkModSetting("windows-11-settings-styler", "Configuración", "theme"),
            // Gaming
            new WindhawkModSetting("smart-process-priority-ram-optimizer", "Prioridad y RAM"),
            new WindhawkModSetting("ignore-focus-loss", "Ignorar pérdida de foco"),
            new WindhawkModSetting("taskbar-auto-hide-keyboard-only", "Barra solo con teclado"),
            // Fedora
            new WindhawkModSetting("windhawk-topbar", "Barra superior"),
            new WindhawkModSetting("taskbar-icon-size", "Tamaño de iconos de la barra"),
            new WindhawkModSetting("custom-corner-radius", "Esquinas redondeadas"),
            new WindhawkModSetting("taskbar-clock-customization", "Reloj de la barra", "TopLine", "MiddleLine", "BottomLine", "TooltipLine", "TimeFormat", "DateFormat", "DateLocale"),
            new WindhawkModSetting("slick-window-arrangement", "Acomodo suave de ventanas"),
            // Potencia
            new WindhawkModSetting("live-video-wallpaper", "Fondo de video", "videoPath", "fitMode", "targetFps", "filePickerHotkey", "visibilityHotkey", "batteryMode"),
            new WindhawkModSetting("dynamic-island-for-windows", "Isla dinámica"),
            new WindhawkModSetting("macos-minimize-animation", "Animación genio"),
            new WindhawkModSetting("taskbar-system-info", "Info del sistema en la barra"),
            new WindhawkModSetting("taskbar-fluent-media-player", "Reproductor en la barra"),
            // Presentación
            new WindhawkModSetting("taskbar-fade", "Barra que se atenúa"),
            new WindhawkModSetting("shake-to-find-cursor", "Agitar para ver el cursor"),
            new WindhawkModSetting("taskbar-tray-system-icon-tweaks", "Ocultar iconos de bandeja"),
            new WindhawkModSetting("zen-desktop-toggle-icons", "Iconos con doble clic", "enableAutoHide"),
            new WindhawkModSetting("monitor-rounded-edges", "Esquinas de pantalla"),
        };

        private static string BaselineFile => Path.Combine(Paths.Config, "base-windows.json");
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
        public Dictionary<string, string> Baseline { get; private set; } = new();

        public void LoadOrCaptureBaseline()
        {
            try
            {
                if (File.Exists(BaselineFile))
                    Baseline = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(BaselineFile)) ?? new();
            }
            catch (Exception ex) { Log.Error("base-windows.json ilegible: " + ex.Message); Baseline = new(); }

            // Ajustes nuevos (de versiones futuras) se añaden a la base sin tocar los ya guardados.
            bool changed = false;
            foreach (var s in Settings.Where(s => !Baseline.ContainsKey(s.Id)))
            {
                try { Baseline[s.Id] = s.Capture(); changed = true; Log.Info($"Base {s.Id} = {Baseline[s.Id]}"); }
                catch (Exception ex) { Log.Error($"No pude leer {s.Id}: {ex.Message}"); }
            }
            if (changed) SaveBaseline();

            var sin = Settings.Where(s => !s.Available).Select(s => s.Id).ToList();
            if (sin.Count > 0) Log.Info("Sin permiso o no instalados (se saltan): " + string.Join(", ", sin));
        }

        /// <summary>Reemplaza la base por cómo está Windows ahora mismo.</summary>
        public void RecaptureBaseline()
        {
            Baseline.Clear();
            foreach (var s in Settings)
                try { Baseline[s.Id] = s.Capture(); } catch (Exception ex) { Log.Error($"No pude leer {s.Id}: {ex.Message}"); }
            SaveBaseline();
            Log.Info("Base recapturada");
        }

        private void SaveBaseline() => File.WriteAllText(BaselineFile, JsonSerializer.Serialize(Baseline, Json));

        /// <summary>Aplica un perfil. null = solo la base (pánico).</summary>
        /// <returns>Lista de errores legibles (vacía si todo fue bien).</returns>
        /// <summary>Cuántos mods de Windhawk cambió el último Apply (la barra tarda en redibujarse).</summary>
        public int LastWindhawkChanges { get; private set; }

        public List<string> Apply(Dictionary<string, string>? perfil, Action<string>? progreso = null)
        {
            int wh = 0;
            var errores = new System.Collections.Concurrent.ConcurrentBag<string>();
            bool colores = false;

            bool Run(Setting s)
            {
                if (!s.Available) return false;
                string? target = null;
                if (perfil != null && perfil.TryGetValue(s.Id, out var v) && !string.IsNullOrWhiteSpace(v)) target = v;
                else if (Baseline.TryGetValue(s.Id, out var b)) target = b;
                if (target == null) return false;
                target = Adaptador.Adaptar(s.Id, target);   // fondos a la medida de la pantalla, si ya están listos
                target = Regulador.Ajustar(s.Id, target);   // sin video / translucidez si el equipo no da para más
                try
                {
                    var want = s.Normalize(target);
                    var now = s.Read();
                    if (s.Matches(target, now)) return false;
                    var t0 = System.Diagnostics.Stopwatch.StartNew();
                    progreso?.Invoke(s.Nombre);
                    s.Apply(target);
                    if (s is WindhawkModSetting) System.Threading.Interlocked.Increment(ref wh);
                    Log.Info($"  {s.Id} ({t0.ElapsedMilliseconds} ms): {Short(now)} -> {Short(want)}");
                    return true;
                }
                catch (Exception ex)
                {
                    errores.Add($"{s.Nombre}: {ex.Message}");
                    Log.Error($"  {s.Id} falló: {ex.Message}");
                    return false;
                }
            }

            // 1) Ajustes de registro: instantáneos, en orden.
            // El acento por la API de Windows ya avisa solo; el aviso general (lento, repinta todas las apps) solo hace falta para lo demás.
            foreach (var s in Settings.Where(IsColor))
                if (Run(s) && !(s is AccentSetting a && a.LastUsedApi)) colores = true;

            // 2) Lo lento va en paralelo: aviso a Windows, energía, barra, iconos y fondo.
            var tasks = new List<System.Threading.Tasks.Task>();
            if (colores) // sin esperar: alguna ventana lenta no debe frenar el cambio de modo
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    var tb = System.Diagnostics.Stopwatch.StartNew();
                    Native.BroadcastSettingChange("ImmersiveColorSet", "WindowsThemeElement", "Policy");
                    Log.Info($"  aviso a Windows ({tb.ElapsedMilliseconds} ms)");
                });
            bool fondoCambio = false;
            foreach (var s in Settings.Where(s => !IsColor(s) && !IsVideo(s) && !IsDesktop(s)))
                tasks.Add(System.Threading.Tasks.Task.Run(() => { if (Run(s) && s is WallpaperSetting) fondoCambio = true; }));
            System.Threading.Tasks.Task.WaitAll(tasks.ToArray(), 15000);

            // Escritorio del modo: carpeta, luego tamaño de iconos (sobre la vista ya cambiada), luego mostrar/ocultar.
            foreach (var s in Settings.Where(IsDesktop))
                if (Run(s) && s is DesktopFolderSetting) System.Threading.Thread.Sleep(700); // Explorer relee la carpeta

            // 3) El fondo de video va AL FINAL: si se cambia la imagen de fondo después, Windows tapa el video.
            foreach (var s in Settings.Where(IsVideo))
            {
                // Windows tarda un momento en terminar de poner la imagen nueva; si el video arranca antes, queda tapado.
                if (fondoCambio) System.Threading.Thread.Sleep(1200);
                bool changed = Run(s);
                // Si solo cambió la imagen pero el video sigue activo, se le avisa para que se vuelva a poner encima.
                if (!changed && fondoCambio && s is WindhawkModSetting && s.Read().Contains("\"activo\":\"1\""))
                {
                    WindhawkModSetting.Rebump("live-video-wallpaper");
                    System.Threading.Interlocked.Increment(ref wh);
                    Log.Info("  fondo de video: recargado tras cambiar la imagen");
                }
            }
            LastWindhawkChanges = wh;
            return errores.ToList();
        }

        private static bool IsVideo(Setting s) => s.Id == "wh:live-video-wallpaper";
        // El brillo va en este grupo (después del plan de energía): Windows guarda un brillo por plan y, si se cambia
        // antes, el brillo del modo Batería quedaría grabado en el plan normal.
        private static bool IsDesktop(Setting s) => s is DesktopFolderSetting or IconSizeSetting or DesktopIconsSetting or BrightnessSetting;

        private static bool IsColor(Setting s) => s is ThemeSetting or TransparencySetting or AccentSetting or AnimationSetting;

        private static string Short(string v) => v.Length > 40 ? v[..18] + "…" + v[^14..] : v;
    }
}
