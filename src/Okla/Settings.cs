using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Okla
{
    /// <summary>
    /// Un ajuste de Windows que okla puede leer, cambiar y devolver a su valor original.
    /// Los valores son texto para poder guardarlos en JSON y editarlos a mano.
    /// </summary>
    internal abstract class Setting
    {
        public abstract string Id { get; }
        public abstract string Nombre { get; }
        /// <summary>Valor actual, en formato canónico.</summary>
        public abstract string Read();
        /// <summary>Convierte un valor "humano" (p.ej. "oscuro") al formato canónico de Read().</summary>
        public virtual string Normalize(string v) => v.Trim().ToLowerInvariant();
        public abstract void Apply(string v);
        /// <summary>false si okla no puede tocar este ajuste (falta un programa o un permiso).</summary>
        public virtual bool Available => true;
        /// <summary>¿El valor actual ya cumple el objetivo? (para no reaplicar lo que ya está)</summary>
        public virtual bool Matches(string target, string current) =>
            string.Equals(Normalize(target), current, StringComparison.OrdinalIgnoreCase);
        /// <summary>Se llama al capturar la base, por si hay que guardar algo aparte (p.ej. el fondo).</summary>
        public virtual string Capture() => Read();
    }

    internal static class Native
    {
        public const uint SPIF_UPDATE = 0x1 | 0x2;
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint a, uint b, ref ANIMATIONINFO c, uint d);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint a, uint b, ref int c, uint d);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint a, uint b, IntPtr c, uint d);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(uint a, uint b, StringBuilder c, uint d);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(uint a, uint b, string c, uint d);
        [StructLayout(LayoutKind.Sequential)] public struct ANIMATIONINFO { public uint cbSize; public int iMinAnimate; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? cls, string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? name);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr res);
        [DllImport("user32.dll")] public static extern int GetSysColor(int i);
        [DllImport("user32.dll")] public static extern bool SetSysColors(int n, int[] els, int[] cols);

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA { public uint cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int l, t, r, b; }
        [DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);

        /// <summary>Avisa a Explorer y a las apps de que cambió el tema/colores.</summary>
        public static void BroadcastSettingChange(params string[] areas)
        {
            // SMTO_ABORTIFHUNG: no espero a ventanas colgadas o pausadas; 250 ms por ventana como máximo.
            var tasks = new System.Threading.Tasks.Task[areas.Length];
            for (int i = 0; i < areas.Length; i++)
            {
                var a = areas[i];
                tasks[i] = System.Threading.Tasks.Task.Run(() => SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, a, 0x0002, 250, out _));
            }
            System.Threading.Tasks.Task.WaitAll(tasks, 4000);
        }
    }

    internal static class Reg
    {
        public const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        public const string Dwm = @"Software\Microsoft\Windows\DWM";
        public const string Accent = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

        public static object? Get(string key, string name)
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            return k?.GetValue(name);
        }
        public static int GetInt(string key, string name, int def) => Get(key, name) is int i ? i : def;
        public static void SetInt(string key, string name, int v)
        {
            using var k = Registry.CurrentUser.CreateSubKey(key);
            k.SetValue(name, v, RegistryValueKind.DWord);
        }
        public static void SetBytes(string key, string name, byte[] v)
        {
            using var k = Registry.CurrentUser.CreateSubKey(key);
            k.SetValue(name, v, RegistryValueKind.Binary);
        }
    }

    // ---------------------------------------------------------------- tema claro/oscuro
    internal sealed class ThemeSetting : Setting
    {
        public override string Id => "tema";
        public override string Nombre => "Tema claro/oscuro";
        public override string Read() =>
            $"apps={Reg.GetInt(Reg.Personalize, "AppsUseLightTheme", 1)};sys={Reg.GetInt(Reg.Personalize, "SystemUsesLightTheme", 1)}";
        public override string Normalize(string v) => v.Trim().ToLowerInvariant() switch
        {
            "oscuro" => "apps=0;sys=0",
            "claro" => "apps=1;sys=1",
            var x => x
        };
        public override void Apply(string v)
        {
            var m = Regex.Match(Normalize(v), @"apps=(\d);sys=(\d)");
            if (!m.Success) throw new ArgumentException("tema: " + v);
            Reg.SetInt(Reg.Personalize, "AppsUseLightTheme", int.Parse(m.Groups[1].Value));
            Reg.SetInt(Reg.Personalize, "SystemUsesLightTheme", int.Parse(m.Groups[2].Value));
        }
    }

    // ---------------------------------------------------------------- transparencias
    internal sealed class TransparencySetting : Setting
    {
        public override string Id => "transparencia";
        public override string Nombre => "Efectos de transparencia";
        public override string Read() => Reg.GetInt(Reg.Personalize, "EnableTransparency", 1).ToString();
        public override string Normalize(string v) => v.Trim().ToLowerInvariant() is "1" or "si" or "sí" or "true" ? "1" : "0";
        public override void Apply(string v) => Reg.SetInt(Reg.Personalize, "EnableTransparency", int.Parse(Normalize(v)));
    }

    // ---------------------------------------------------------------- animaciones de ventanas
    internal sealed class AnimationSetting : Setting
    {
        private const uint SPI_GETANIMATION = 0x48, SPI_SETANIMATION = 0x49,
                           SPI_GETCLIENTAREAANIMATION = 0x1042, SPI_SETCLIENTAREAANIMATION = 0x1043;
        public override string Id => "animaciones";
        public override string Nombre => "Animaciones de Windows";
        public override string Read()
        {
            var ai = new Native.ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<Native.ANIMATIONINFO>() };
            Native.SystemParametersInfo(SPI_GETANIMATION, ai.cbSize, ref ai, 0);
            int client = 1;
            Native.SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref client, 0);
            return $"ventanas={(ai.iMinAnimate != 0 ? 1 : 0)};contenido={(client != 0 ? 1 : 0)}";
        }
        public override string Normalize(string v) => v.Trim().ToLowerInvariant() switch
        {
            "1" or "si" or "sí" => "ventanas=1;contenido=1",
            "0" or "no" => "ventanas=0;contenido=0",
            var x => x
        };
        public override void Apply(string v)
        {
            var m = Regex.Match(Normalize(v), @"ventanas=(\d);contenido=(\d)");
            if (!m.Success) throw new ArgumentException("animaciones: " + v);
            var ai = new Native.ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<Native.ANIMATIONINFO>(), iMinAnimate = int.Parse(m.Groups[1].Value) };
            // Solo guardo (sin SPIF_SENDCHANGE, que avisa ventana por ventana y es lento); el aviso va al final, en paralelo.
            Native.SystemParametersInfo(SPI_SETANIMATION, ai.cbSize, ref ai, 0x1);
            Native.SystemParametersInfo(SPI_SETCLIENTAREAANIMATION, 0, (IntPtr)int.Parse(m.Groups[2].Value), 0x1);
        }
    }

    // ---------------------------------------------------------------- color de acento
    internal sealed class AccentSetting : Setting
    {
        public override string Id => "acento";
        public override string Nombre => "Color de acento";

        // Formato canónico: hex de AccentPalette (8 colores RGBA) + flags de prevalencia.
        public override string Read()
        {
            var pal = Reg.Get(Reg.Accent, "AccentPalette") as byte[];
            return $"pal={(pal == null ? "" : Convert.ToHexString(pal))};" +
                   $"barra={Reg.GetInt(Reg.Personalize, "ColorPrevalence", 0)};" +
                   $"titulos={Reg.GetInt(Reg.Dwm, "ColorPrevalence", 0)}";
        }

        public override string Normalize(string v)
        {
            v = v.Trim();
            if (!v.StartsWith("#")) return v.ToUpperInvariant().Replace("PAL=", "pal=").Replace("BARRA=", "barra=").Replace("TITULOS=", "titulos=");
            // "#rrggbb" -> paleta generada; "#rrggbb;bordes" además tiñe la barra, Inicio, títulos y bordes de ventana
            var (hex, bordes) = SplitHex(v);
            return $"pal={Convert.ToHexString(BuildPalette(hex))};barra={(bordes ? 1 : 0)};titulos={(bordes ? 1 : 0)}";
        }

        public static (string hex, bool bordes) SplitHex(string v)
        {
            var parts = v.Trim().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return (parts[0], parts.Skip(1).Any(p => p.Equals("bordes", StringComparison.OrdinalIgnoreCase)));
        }

        [StructLayout(LayoutKind.Sequential)] private struct ColorPref { public uint Start, Accent; }
        // La misma llamada que usa Configuración > Personalización > Colores: Windows recalcula la paleta y avisa a todo.
        [DllImport("uxtheme.dll", EntryPoint = "#122")] private static extern int SetUserColorPreference(ref ColorPref p, bool force);

        /// <summary>true si el último Apply usó la API de Windows (ya avisa sola, no hace falta el aviso general).</summary>
        public bool LastUsedApi { get; private set; }

        private static uint Abgr(byte r, byte g, byte b) => 0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | r;

        public override bool Matches(string target, string current)
        {
            var t = target.Trim();
            if (!t.StartsWith("#")) return base.Matches(target, current);
            var (hex, bordes) = SplitHex(t);
            var c = System.Drawing.ColorTranslator.FromHtml(hex);
            uint now = unchecked((uint)Reg.GetInt(Reg.Accent, "AccentColorMenu", 0));
            int f = bordes ? 1 : 0;
            return now == Abgr(c.R, c.G, c.B) && current.EndsWith($";barra={f};titulos={f}");
        }

        public override void Apply(string v)
        {
            var n = Normalize(v);
            var m = Regex.Match(n, @"pal=([0-9A-Fa-f]*);barra=(\d);titulos=(\d)");
            if (!m.Success) throw new ArgumentException("acento: " + v);
            LastUsedApi = false;
            if (v.Trim().StartsWith("#"))
            {
                var (hex, bordes) = SplitHex(v);
                var c = System.Drawing.ColorTranslator.FromHtml(hex);
                int f = bordes ? 1 : 0;
                bool flagsCambian = Reg.GetInt(Reg.Personalize, "ColorPrevalence", 0) != f || Reg.GetInt(Reg.Dwm, "ColorPrevalence", 0) != f;
                // Primero los flags: el aviso que manda la API ya los recoge.
                Reg.SetInt(Reg.Personalize, "ColorPrevalence", f);
                Reg.SetInt(Reg.Dwm, "ColorPrevalence", f);
                var p = new ColorPref { Accent = Abgr(c.R, c.G, c.B), Start = Abgr((byte)(c.R * 0.7), (byte)(c.G * 0.7), (byte)(c.B * 0.7)) };
                if (SetUserColorPreference(ref p, true) == 0)
                {
                    LastUsedApi = !flagsCambian;   // si cambian barra/bordes, que también vaya el aviso general
                    return;
                }
            }
            if (m.Groups[1].Value.Length == 64)
            {
                var pal = Convert.FromHexString(m.Groups[1].Value);
                // Paleta exacta (la base de Windows): primero aviso a Windows con su color principal y luego
                // dejo la paleta original tal cual en el registro.
                var pp = new ColorPref { Accent = Abgr(pal[12], pal[13], pal[14]), Start = Abgr(pal[20], pal[21], pal[22]) };
                SetUserColorPreference(ref pp, true);
                Reg.SetBytes(Reg.Accent, "AccentPalette", pal);
                // El color "principal" de Windows es la entrada 3 de la paleta (índice de 4 bytes).
                byte r = pal[12], g = pal[13], b = pal[14];
                int abgr = unchecked((int)(0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | r));
                Reg.SetInt(Reg.Accent, "AccentColorMenu", abgr);
                Reg.SetInt(Reg.Accent, "StartColorMenu", unchecked((int)(0xFF000000u | ((uint)pal[22] << 16) | ((uint)pal[21] << 8) | pal[20])));
                Reg.SetInt(Reg.Dwm, "AccentColor", abgr);
                Reg.SetInt(Reg.Dwm, "ColorizationColor", unchecked((int)(0xC4000000u | ((uint)r << 16) | ((uint)g << 8) | b)));
                Reg.SetInt(Reg.Dwm, "ColorizationAfterglow", unchecked((int)(0xC4000000u | ((uint)r << 16) | ((uint)g << 8) | b)));
            }
            Reg.SetInt(Reg.Personalize, "ColorPrevalence", int.Parse(m.Groups[2].Value));
            Reg.SetInt(Reg.Dwm, "ColorPrevalence", int.Parse(m.Groups[3].Value));
        }

        /// <summary>Genera las 7 tonalidades que usa Windows (claras -> oscuras) a partir de un color.</summary>
        private static byte[] BuildPalette(string hex)
        {
            var c = System.Drawing.ColorTranslator.FromHtml(hex);
            double[] factors = { 0.62, 0.42, 0.22, 0.0, -0.22, -0.40, -0.58, 0.0 };
            var bytes = new byte[32];
            for (int i = 0; i < 8; i++)
            {
                double f = factors[i];
                byte Mix(byte ch) => (byte)Math.Clamp(f >= 0 ? ch + (255 - ch) * f : ch * (1 + f), 0, 255);
                bytes[i * 4] = Mix(c.R); bytes[i * 4 + 1] = Mix(c.G); bytes[i * 4 + 2] = Mix(c.B); bytes[i * 4 + 3] = 0;
            }
            // la última entrada es un gris de apoyo, como en las paletas de Windows
            bytes[28] = 0x88; bytes[29] = 0x17; bytes[30] = 0x98; bytes[31] = 0x00;
            return bytes;
        }
    }

    // ---------------------------------------------------------------- plan de energía
    internal sealed class PowerSetting : Setting
    {
        private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
        private const string High = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        public override string Id => "energia";
        public override string Nombre => "Plan de energía";
        public override string Read()
        {
            var o = Run("powercfg", "/getactivescheme");
            var m = Regex.Match(o, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            return m.Success ? m.Value.ToLowerInvariant() : "";
        }
        // Plan propio del modo Batería (copia de Equilibrado): CPU al 60 %, sin turbo, ahorro de energía siempre con batería.
        private const string Bateria = "6b0c5a1e-0b1a-4c1a-9a1e-0000000000b1";
        public override string Normalize(string v) => v.Trim().ToLowerInvariant() switch
        {
            "alto" or "alto rendimiento" => High,
            "equilibrado" => Balanced,
            "bateria" or "batería" => Bateria,
            var x => x
        };
        public override void Apply(string v)
        {
            var g = Normalize(v);
            if (g.Length != 36) throw new ArgumentException("energia: " + v);
            if (g == Bateria) CrearPlanBateria();
            Run("powercfg", "/setactive " + g);
        }

        private static void CrearPlanBateria()
        {
            if (Run("powercfg", "/list").Contains(Bateria)) return;
            Run("powercfg", $"/duplicatescheme {Balanced} {Bateria}");
            Run("powercfg", $"/changename {Bateria} \"okla Batería\" \"Modo Batería de okla: todo al mínimo\"");
            foreach (var modo in new[] { "/setdcvalueindex", "/setacvalueindex" })
            {
                bool dc = modo == "/setdcvalueindex";
                Run("powercfg", $"{modo} {Bateria} SUB_PROCESSOR PROCTHROTTLEMAX {(dc ? 60 : 80)}");
                Run("powercfg", $"{modo} {Bateria} SUB_PROCESSOR PERFBOOSTMODE 0");
                Run("powercfg", $"{modo} {Bateria} SUB_ENERGYSAVER ESBATTTHRESHOLD 100");   // ahorro de energía de Windows encendido
                Run("powercfg", $"{modo} {Bateria} SUB_VIDEO VIDEOIDLE {(dc ? 180 : 600)}");  // apagar pantalla
            }
            Log.Info("Plan de energía \"okla Batería\" creado");
        }
        private static string Run(string exe, string args)
        {
            var p = Process.Start(new ProcessStartInfo(exe, args)
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return o;
        }
    }

    // ---------------------------------------------------------------- brillo de la pantalla
    /// <summary>
    /// Brillo de la pantalla del portátil. Solo lo cambia el modo que lo pide (Batería); al salir vuelve el brillo
    /// que había antes (guardado en config\brillo-antes.txt), y si no, no se toca: las teclas de brillo mandan.
    /// </summary>
    internal sealed class BrightnessSetting : Setting
    {
        private static string Antes => Path.Combine(Paths.Config, "brillo-antes.txt");
        public override string Id => "brillo";
        public override string Nombre => "Brillo de la pantalla";

        public override bool Available
        {
            get { try { return Leer() >= 0; } catch { return false; } }
        }

        private static int Leer()
        {
            using var s = new System.Management.ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (System.Management.ManagementObject o in s.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
            return -1;
        }

        private static void Poner(int nivel)
        {
            using var s = new System.Management.ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (System.Management.ManagementObject o in s.Get()) o.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)Math.Clamp(nivel, 5, 100) });
        }

        public override string Read() => Leer().ToString();
        public override string Capture() => "";   // la base es "no tocar"
        public override string Normalize(string v) => v.Trim().TrimEnd('%');

        public override bool Matches(string target, string current)
        {
            var t = Normalize(target);
            if (t == "") return !File.Exists(Antes);          // nada que devolver
            return File.Exists(Antes) && t == current;        // ya puesto por okla
        }

        public override void Apply(string v)
        {
            var t = Normalize(v);
            if (t == "")
            {
                if (File.Exists(Antes) && int.TryParse(File.ReadAllText(Antes), out var antes)) Poner(antes);
                File.Delete(Antes);
                return;
            }
            if (!int.TryParse(t, out var nivel)) throw new ArgumentException("brillo: " + v);
            if (!File.Exists(Antes)) File.WriteAllText(Antes, Leer().ToString());
            Poner(nivel);
        }
    }

    // ---------------------------------------------------------------- barra de tareas: ocultar automáticamente
    internal sealed class TaskbarAutoHideSetting : Setting
    {
        private const uint ABM_GETSTATE = 4, ABM_SETSTATE = 10, ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2;
        public override string Id => "autoocultar";
        public override string Nombre => "Ocultar barra de tareas automáticamente";
        private static Native.APPBARDATA Data() => new()
        { cbSize = (uint)Marshal.SizeOf<Native.APPBARDATA>(), hWnd = Native.FindWindow("Shell_TrayWnd", null) };
        public override string Read()
        {
            var d = Data();
            return ((Native.SHAppBarMessage(ABM_GETSTATE, ref d).ToUInt64() & ABS_AUTOHIDE) != 0) ? "1" : "0";
        }
        public override string Normalize(string v) => v.Trim().ToLowerInvariant() is "1" or "si" or "sí" or "true" ? "1" : "0";
        public override void Apply(string v)
        {
            var d = Data();
            d.lParam = (IntPtr)(Normalize(v) == "1" ? ABS_AUTOHIDE : ABS_ALWAYSONTOP);
            Native.SHAppBarMessage(ABM_SETSTATE, ref d);
        }
    }

    // ---------------------------------------------------------------- iconos del escritorio
    internal sealed class DesktopIconsSetting : Setting
    {
        public override string Id => "iconos";
        public override string Nombre => "Iconos del escritorio";

        private static (IntPtr defView, IntPtr list) Find()
        {
            IntPtr defView = Native.FindWindowEx(Native.FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
            IntPtr w = IntPtr.Zero;
            while (defView == IntPtr.Zero && (w = Native.FindWindowEx(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero)
                defView = Native.FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null);
            var list = defView == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
            return (defView, list);
        }
        public override string Read()
        {
            var (_, list) = Find();
            if (list != IntPtr.Zero) return Native.IsWindowVisible(list) ? "1" : "0";
            return Reg.GetInt(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideIcons", 0) == 1 ? "0" : "1";
        }
        public override string Normalize(string v) => v.Trim().ToLowerInvariant() is "1" or "si" or "sí" or "true" ? "1" : "0";
        public override void Apply(string v)
        {
            if (Read() == Normalize(v)) return;
            var (defView, _) = Find();
            if (defView != IntPtr.Zero) Native.SendMessage(defView, 0x0111 /*WM_COMMAND*/, (IntPtr)0x7402, IntPtr.Zero); // "Mostrar iconos"
        }
    }

    // ---------------------------------------------------------------- fondo de escritorio (imagen o color sólido)
    internal sealed class WallpaperSetting : Setting
    {
        private const uint SPI_GETDESKWALLPAPER = 0x73, SPI_SETDESKWALLPAPER = 0x14;
        public override string Id => "fondo";
        public override string Nombre => "Fondo de escritorio";
        private static string BackupFile => Path.Combine(Paths.Config, "fondo-original.jpg");

        public override string Read()
        {
            var sb = new StringBuilder(1024);
            Native.SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0);
            var path = sb.ToString();
            if (string.IsNullOrWhiteSpace(path)) return "color:" + DesktopColorHex();
            return "img:" + path;
        }

        /// <summary>Al capturar la base guardo una copia del fondo, por si el original se borra.</summary>
        public override string Capture()
        {
            var v = Read();
            if (!v.StartsWith("img:")) return v;
            var path = v[4..];
            try
            {
                var src = File.Exists(path) ? path
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
                if (File.Exists(src))
                {
                    // Copia de respaldo; la base sigue apuntando al original mientras exista.
                    File.Copy(src, BackupFile, true);
                    return File.Exists(path) ? v : "img:" + BackupFile;
                }
            }
            catch (Exception ex) { Log.Error("No pude copiar el fondo original: " + ex.Message); }
            return v;
        }

        public override string Normalize(string v) => v.Trim();

        public override void Apply(string v)
        {
            v = Normalize(v);
            if (v.StartsWith("color:", StringComparison.OrdinalIgnoreCase))
            {
                var c = System.Drawing.ColorTranslator.FromHtml(v[6..]);
                Native.SetSysColors(1, new[] { 1 /*COLOR_DESKTOP*/ }, new[] { c.R | (c.G << 8) | (c.B << 16) });
                using (var k = Registry.CurrentUser.CreateSubKey(@"Control Panel\Colors"))
                    k.SetValue("Background", $"{c.R} {c.G} {c.B}");
                Native.SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, "", Native.SPIF_UPDATE);
            }
            else if (v.StartsWith("img:", StringComparison.OrdinalIgnoreCase))
            {
                var path = v[4..];
                if (!File.Exists(path) && File.Exists(BackupFile)) path = BackupFile; // el original se borró
                if (!File.Exists(path)) throw new FileNotFoundException("Fondo no encontrado", path);
                Native.SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, Native.SPIF_UPDATE);
            }
            else throw new ArgumentException("fondo: " + v);
        }

        private static string DesktopColorHex()
        {
            int c = Native.GetSysColor(1);
            return $"#{c & 0xFF:x2}{(c >> 8) & 0xFF:x2}{(c >> 16) & 0xFF:x2}";
        }
    }
}
