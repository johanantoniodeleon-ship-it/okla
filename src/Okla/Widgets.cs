using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Okla
{
    /// <summary>
    /// Widgets del escritorio por modo (punto 16). Una ventana transparente que no se puede pulsar
    /// (los clics pasan al escritorio), que vive justo encima del fondo y debajo de todas las ventanas.
    /// Con "Mostrar escritorio" (Win+D) se vuelve a poner encima del escritorio.
    ///   reloj  → hora grande y fecha al centro (estilo GNOME).
    ///   stats  → hora + procesador, gráfica, memoria y lo que suena en Spotify.
    ///   0      → nada.
    /// Ajuste por modo: Ajustes["widgets"]. Sin ajuste: Fedora reloj, Potencia stats, el resto nada.
    /// </summary>
    internal static class Widgets
    {
        public static string PorDefecto(string modo) => modo switch { "A" => "reloj", "G" => "stats", _ => "0" };

        private static Window? _w;
        private static DispatcherTimer? _reloj, _pila;
        private static TextBlock? _hora, _fecha, _stats;
        private static string _tipo = "0";
        private static readonly CultureInfo Es = new("es-ES");

        public static void Poner(string? tipo, string colorAcento)
        {
            tipo = string.IsNullOrEmpty(tipo) ? "0" : tipo;
            if (tipo == _tipo && _w != null) return;
            Quitar();
            _tipo = tipo;
            if (tipo == "0") return;
            try { Crear(tipo, colorAcento); Log.Info("Widgets: " + tipo); }
            catch (Exception ex) { Log.Error("Widgets: " + ex.Message); Quitar(); }
        }

        public static void Quitar()
        {
            _reloj?.Stop(); _pila?.Stop(); _reloj = _pila = null;
            try { _w?.Close(); } catch { }
            _w = null; _hora = _fecha = _stats = null; _tipo = "0";
        }

        private static void Crear(string tipo, string colorAcento)
        {
            var area = SystemParameters.WorkArea;
            var root = new Grid();
            var sombra = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 0, Opacity = 0.55, Color = Colors.Black };
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Effect = sombra,
                                      Margin = new Thickness(0, 0, 0, area.Height * 0.12) };
            var blanco = new SolidColorBrush(Color.FromArgb(0xee, 0xff, 0xff, 0xff));
            var suave = new SolidColorBrush(Color.FromArgb(0xc0, 0xff, 0xff, 0xff));

            if (tipo == "stats")
            {
                var acento = (Color)ColorConverter.ConvertFromString(colorAcento);
                _hora = new TextBlock { FontSize = 124, FontFamily = new FontFamily("Bahnschrift SemiLight"), Foreground = blanco, HorizontalAlignment = HorizontalAlignment.Center };
                _fecha = new TextBlock { FontSize = 18, FontFamily = new FontFamily("Bahnschrift"), Foreground = new SolidColorBrush(acento), HorizontalAlignment = HorizontalAlignment.Center,
                                         Margin = new Thickness(0, -6, 0, 14) };
                _stats = new TextBlock { FontSize = 15, FontFamily = new FontFamily("Bahnschrift"), Foreground = suave, HorizontalAlignment = HorizontalAlignment.Center };
                sp.Children.Add(_hora); sp.Children.Add(_fecha); sp.Children.Add(_stats);
            }
            else
            {
                _hora = new TextBlock { FontSize = 150, FontFamily = new FontFamily("Segoe UI Variable Display"), FontWeight = FontWeights.Light, Foreground = blanco,
                                        HorizontalAlignment = HorizontalAlignment.Center };
                _fecha = new TextBlock { FontSize = 24, FontFamily = new FontFamily("Segoe UI Variable Display"), Foreground = suave, HorizontalAlignment = HorizontalAlignment.Center,
                                         Margin = new Thickness(0, -10, 0, 0) };
                sp.Children.Add(_hora); sp.Children.Add(_fecha);
            }
            root.Children.Add(sp);

            _w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false,
                ResizeMode = ResizeMode.NoResize, Left = area.Left, Top = area.Top, Width = area.Width, Height = area.Height, Content = root, Title = "okla widgets", Focusable = false,
            };
            _w.SourceInitialized += (_, _) => { Toast.MakeClickThrough(_w); AlFondo(); };
            _w.Show();

            Actualizar();
            _reloj = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _reloj.Tick += (_, _) => { Actualizar(); Pila(); };
            _reloj.Start();
        }

        private static string _ultimaHora = "";
        private static int _n;
        private static void Actualizar()
        {
            if (_hora == null) return;
            var ahora = DateTime.Now;
            var h = ahora.ToString("HH:mm");
            if (h != _ultimaHora)
            {
                _ultimaHora = h; _hora.Text = h;
                var f = ahora.ToString(_tipo == "stats" ? "dddd d MMMM" : "dddd, d 'de' MMMM", Es);
                _fecha!.Text = _tipo == "stats" ? f.ToUpper(Es) : char.ToUpper(f[0], Es) + f[1..];
            }
            if (_stats != null && _n++ % 2 == 0)
            {
                var partes = new System.Collections.Generic.List<string> { $"CPU {Regulador.UltimaCpu:0} %" };
                if (Regulador.UltimaGpu > 0) partes.Add($"GPU {Regulador.UltimaGpu:0} %");
                var m = new MEM { Len = (uint)Marshal.SizeOf<MEM>() };
                if (GlobalMemoryStatusEx(ref m)) partes.Add($"RAM {(m.Total - m.Avail) / 1073741824.0:0.0} / {m.Total / 1073741824.0:0} GB");
                if (GameHub.Spotify() is { } musica) partes.Add("♪ " + musica);
                _stats.Text = string.Join("    ·    ", partes);
            }
        }

        // ---------- capa: encima del fondo, debajo de las ventanas ----------
        /// <summary>Con el escritorio en primer plano (clic en el escritorio, Win+D) se sube; si no, se queda justo encima del escritorio.</summary>
        private static void Pila()
        {
            if (_w == null) return;
            var fg = GetForegroundWindow();
            var c = Clase(fg);
            if (c is "Progman" or "WorkerW")
                SetWindowPos(Handle(), HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        private static void AlFondo()
        {
            var prog = FindWindow("Progman", null);
            var encima = prog != IntPtr.Zero ? GetWindow(prog, GW_HWNDPREV) : IntPtr.Zero;
            // Nos colocamos debajo de la ventana que está justo encima del escritorio = justo encima del escritorio.
            SetWindowPos(Handle(), encima != IntPtr.Zero ? encima : HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        private static IntPtr Handle() => _w == null ? IntPtr.Zero : new WindowInteropHelper(_w).Handle;
        private static string Clase(IntPtr h) { var sb = new StringBuilder(64); GetClassName(h, sb, 64); return sb.ToString(); }

        private static readonly IntPtr HWND_TOP = IntPtr.Zero, HWND_BOTTOM = new(1);
        private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        private const uint GW_HWNDPREV = 3;
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string c, string? t);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEM { public uint Len, Load; public ulong Total, Avail, TP, AP, TV, AV, AE; }
        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEM m);
    }
}
