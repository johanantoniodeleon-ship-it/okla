using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Okla
{
    /// <summary>
    /// Widgets del escritorio por modo (punto 16). Una ventana transparente que no se puede pulsar
    /// (los clics pasan al escritorio), que vive justo encima del fondo y debajo de todas las ventanas.
    /// Con "Mostrar escritorio" (Win+D) se vuelve a poner encima del escritorio.
    /// Ideas tomadas de escritorios personalizados (Rainmeter, Conky, Eww): reloj grande, clima, calendario,
    /// recursos con barras y lo que suena; cada modo lleva solo lo que no estorba a su propósito.
    ///   reloj  → Fedora: hora grande, fecha y clima al centro + calendario del mes a la derecha (estilo GNOME).
    ///   stats  → Potencia: hora, fecha, clima, barras de CPU/GPU/RAM y lo que suena en Spotify.
    ///   mini   → Presentación: hora, fecha y clima pequeños en la esquina (discreto para grabar).
    ///   0      → nada (Gaming tiene sus datos en el hub; Batería ahorra hasta esto).
    /// Ajuste por modo: Ajustes["widgets"]. El clima sale si hay ciudad (Clima.cs).
    /// </summary>
    internal static class Widgets
    {
        public static string PorDefecto(string modo) => modo switch { "A" => "reloj", "G" => "stats", "P" => "mini", _ => "0" };

        private static Window? _w;
        private static DispatcherTimer? _reloj;
        private static TextBlock? _hora, _fecha, _clima, _musica;
        private static (Border barra, TextBlock valor)[]? _barras;
        private static StackPanel? _calendario;
        private static string _tipo = "0";
        private static Brush _acento = Brushes.White;
        private static readonly CultureInfo Es = new("es-ES");
        private static readonly SolidColorBrush Blanco = new(Color.FromArgb(0xee, 0xff, 0xff, 0xff));
        private static readonly SolidColorBrush Suave = new(Color.FromArgb(0xc0, 0xff, 0xff, 0xff));
        private static readonly SolidColorBrush Tenue = new(Color.FromArgb(0x80, 0xff, 0xff, 0xff));
        private static readonly SolidColorBrush Tarjeta = new(Color.FromArgb(0x38, 0x10, 0x14, 0x1c));
        private const double AnchoBarra = 260;

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
            _reloj?.Stop(); _reloj = null;
            try { _w?.Close(); } catch { }
            _w = null; _hora = _fecha = _clima = _musica = null; _barras = null; _calendario = null; _tipo = "0";
            _ultimaHora = ""; _diaCalendario = -1;
        }

        private static TextBlock T(double tam, Brush color, string fuente = "Segoe UI Variable Display", FontWeight? peso = null, HorizontalAlignment al = HorizontalAlignment.Center) =>
            new() { FontSize = tam, Foreground = color, FontFamily = new FontFamily(fuente), FontWeight = peso ?? FontWeights.Normal, HorizontalAlignment = al };

        private static void Crear(string tipo, string colorAcento)
        {
            var area = SystemParameters.WorkArea;
            var root = Construir(tipo, colorAcento, area);
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

        /// <summary>Para desarrollo: dibuja los widgets sobre una imagen de fondo y guarda un PNG (sin ventanas ni escritorio).</summary>
        public static void Vista(string tipo, string color, string fondo, string salida)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var area = new Rect(0, 0, 1920, 1080);
            _tipo = tipo;
            var root = Construir(tipo, color, area);
            var img = new BitmapImage(new Uri(fondo));
            var todo = new Grid { Width = area.Width, Height = area.Height };
            todo.Children.Add(new Image { Source = img, Stretch = Stretch.UniformToFill });
            todo.Children.Add(root);
            Actualizar();
            var fin = DateTime.Now.AddSeconds(4);   // tiempo para que llegue el clima
            while (DateTime.Now < fin)
            {
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
                System.Threading.Thread.Sleep(50);
                if (_clima?.Text is { Length: > 0 }) break;
            }
            _ultimaHora = ""; Actualizar();
            for (int i = 0; i < 30; i++) { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { })); System.Threading.Thread.Sleep(50); }
            todo.Measure(area.Size); todo.Arrange(area); todo.UpdateLayout();
            var rtb = new RenderTargetBitmap((int)area.Width, (int)area.Height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(todo);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
            using var f = System.IO.File.Create(salida); enc.Save(f);
            app.Shutdown();
        }

        private static Grid Construir(string tipo, string colorAcento, Rect area)
        {
            try { _acento = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorAcento)); } catch { _acento = Blanco; }
            var root = new Grid();
            var sombra = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 0, Opacity = 0.55, Color = Colors.Black };

            if (tipo == "mini")
            {
                // Discreto: hora pequeña, fecha y clima abajo a la derecha.
                var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 36, 28), Effect = sombra };
                _hora = T(40, Blanco, peso: FontWeights.Light, al: HorizontalAlignment.Right);
                _fecha = T(14, Suave, al: HorizontalAlignment.Right); _fecha.Margin = new Thickness(0, -4, 2, 0);
                _clima = T(14, Suave, al: HorizontalAlignment.Right); _clima.Margin = new Thickness(0, 2, 2, 0);
                sp.Children.Add(_hora); sp.Children.Add(_fecha); sp.Children.Add(_clima);
                root.Children.Add(sp);
            }
            else if (tipo == "stats")
            {
                var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Effect = sombra,
                                          Margin = new Thickness(0, 0, 0, area.Height * 0.12) };
                _hora = T(124, Blanco, "Bahnschrift SemiLight");
                _fecha = T(18, _acento, "Bahnschrift"); _fecha.Margin = new Thickness(0, -6, 0, 6);
                _clima = T(16, Suave, "Bahnschrift"); _clima.Margin = new Thickness(0, 0, 0, 18);
                sp.Children.Add(_hora); sp.Children.Add(_fecha); sp.Children.Add(_clima);
                // Barras de recursos (como los medidores de Rainmeter/Conky), con el color del modo.
                var filas = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                _barras = new[] { "CPU", "GPU", "RAM" }.Select(n =>
                {
                    var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AnchoBarra) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
                    var et = T(14, Suave, "Bahnschrift", al: HorizontalAlignment.Left); et.Text = n; et.VerticalAlignment = VerticalAlignment.Center;
                    var fondo = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x40, 0xff, 0xff, 0xff)), VerticalAlignment = VerticalAlignment.Center };
                    var barra = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = _acento, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Width = 0 };
                    var capa = new Grid(); capa.Children.Add(fondo); capa.Children.Add(barra);
                    var val = T(14, Suave, "Bahnschrift", al: HorizontalAlignment.Right); val.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(capa, 1); Grid.SetColumn(val, 2);
                    g.Children.Add(et); g.Children.Add(capa); g.Children.Add(val);
                    filas.Children.Add(g);
                    return (barra, val);
                }).ToArray();
                sp.Children.Add(filas);
                _musica = T(15, Suave, "Bahnschrift"); _musica.Margin = new Thickness(0, 16, 0, 0);
                sp.Children.Add(_musica);
                root.Children.Add(sp);
            }
            else
            {
                var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Effect = sombra,
                                          Margin = new Thickness(0, 0, 0, area.Height * 0.12) };
                _hora = T(150, Blanco, peso: FontWeights.Light);
                _fecha = T(24, Suave); _fecha.Margin = new Thickness(0, -10, 0, 0);
                _clima = T(19, Suave); _clima.Margin = new Thickness(0, 10, 0, 0);
                sp.Children.Add(_hora); sp.Children.Add(_fecha); sp.Children.Add(_clima);
                root.Children.Add(sp);
                // Calendario del mes a la derecha (lo que GNOME enseña al pulsar el reloj).
                _calendario = new StackPanel();
                root.Children.Add(new Border
                {
                    Child = _calendario, Background = Tarjeta, CornerRadius = new CornerRadius(18), Padding = new Thickness(22, 18, 22, 18),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, area.Width * 0.06, area.Height * 0.12),
                });
            }
            return root;
        }

        private static string _ultimaHora = "";
        private static int _n, _diaCalendario = -1;
        private static void Actualizar()
        {
            if (_hora == null) return;
            var ahora = DateTime.Now;
            var h = ahora.ToString("HH:mm");
            if (h != _ultimaHora)
            {
                _ultimaHora = h; _hora.Text = h;
                var f = _tipo switch
                {
                    "stats" => ahora.ToString("dddd d MMMM", Es).ToUpper(Es),
                    "mini" => ahora.ToString("ddd d MMM", Es),
                    _ => ahora.ToString("dddd, d 'de' MMMM", Es),
                };
                _fecha!.Text = _tipo == "stats" ? f : char.ToUpper(f[0], Es) + f[1..];
                if (_calendario != null && ahora.Day != _diaCalendario) { _diaCalendario = ahora.Day; Calendario(ahora); }
                _ = PonerClima();
            }
            if (_barras != null && _n++ % 2 == 0)
            {
                var m = new MEM { Len = (uint)Marshal.SizeOf<MEM>() };
                bool hayRam = GlobalMemoryStatusEx(ref m) && m.Total > 0;
                Barra(0, Regulador.UltimaCpu, $"{Regulador.UltimaCpu:0} %");
                Barra(1, Regulador.UltimaGpu, Regulador.UltimaGpu > 0 ? $"{Regulador.UltimaGpu:0} %" : "—");
                Barra(2, hayRam ? 100.0 * (m.Total - m.Avail) / m.Total : 0, hayRam ? $"{(m.Total - m.Avail) / 1073741824.0:0.0} GB" : "—");
                _musica!.Text = GameHub.Spotify() is { } musica ? "♪  " + musica : "";
            }
        }

        private static void Barra(int i, double pct, string texto)
        {
            var (barra, val) = _barras![i];
            barra.Width = AnchoBarra * Math.Clamp(pct, 0, 100) / 100;
            val.Text = texto;
        }

        private static async System.Threading.Tasks.Task PonerClima()
        {
            if (_clima == null) return;
            var d = await Clima.Actual();
            if (_clima == null) return;
            if (d == null) { _clima.Visibility = Visibility.Collapsed; return; }
            var (ico, txt) = Clima.Describir(d.Codigo, d.Dia);
            _clima.Visibility = Visibility.Visible;
            _clima.Text = _tipo switch
            {
                "mini" => $"{ico}  {d.Temp:0}°",
                "stats" => $"{ico}  {d.Temp:0}°   ·   {txt.ToUpper(Es)}   ·   {d.Max:0}° / {d.Min:0}°",
                _ => $"{ico}  {d.Temp:0}°  ·  {txt}  ·  {Clima.Ubicacion()?.Ciudad.Split(',')[0]}",
            };
        }

        private static void Calendario(DateTime hoy)
        {
            var c = _calendario!;
            c.Children.Clear();
            var mes = hoy.ToString("MMMM yyyy", Es);
            var titulo = T(17, Blanco, peso: FontWeights.SemiBold, al: HorizontalAlignment.Left);
            titulo.Text = char.ToUpper(mes[0], Es) + mes[1..]; titulo.Margin = new Thickness(4, 0, 0, 10);
            c.Children.Add(titulo);
            var g = new UniformGrid { Columns = 7 };
            foreach (var d in new[] { "L", "M", "X", "J", "V", "S", "D" })
            {
                var t = T(12, Tenue); t.Text = d; t.Width = 34; t.TextAlignment = TextAlignment.Center; t.Margin = new Thickness(0, 0, 0, 6);
                g.Children.Add(t);
            }
            var primero = new DateTime(hoy.Year, hoy.Month, 1);
            int hueco = ((int)primero.DayOfWeek + 6) % 7;   // la semana empieza el lunes
            for (int i = 0; i < hueco; i++) g.Children.Add(new Border());
            for (int d = 1; d <= DateTime.DaysInMonth(hoy.Year, hoy.Month); d++)
            {
                bool esHoy = d == hoy.Day;
                var t = T(14, esHoy ? new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x20)) : Suave, peso: esHoy ? FontWeights.SemiBold : null);
                t.Text = d.ToString(); t.TextAlignment = TextAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center;
                g.Children.Add(new Border { Width = 34, Height = 30, CornerRadius = new CornerRadius(15), Child = t, Background = esHoy ? Blanco : Brushes.Transparent });
            }
            c.Children.Add(g);
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
