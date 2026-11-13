using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Okla
{
    /// <summary>Estilo visual del mini hub según el modo.</summary>
    internal sealed record HubTheme(Color Bg, Color Fg, Color Dim, Color Line, string Font, double Radius)
    {
        public static HubTheme For(string key) => key switch
        {
            "J" => new(C("#0c0c0e"), C("#e6e6ea"), C("#6e6e76"), C("#24242a"), "Bahnschrift", 4),
            "A" => new(C("#2e3440"), C("#eceff4"), C("#a3adbf"), C("#434c5e"), "Segoe UI Variable Text", 14),
            "G" => new(C("#000000"), C("#ffffff"), C("#8a8aa0"), C("#22223a"), "Segoe UI Variable Display", 10),
            "P" => new(C("#2d353b"), C("#d3c6aa"), C("#9da9a0"), C("#475258"), "Segoe UI Variable Text", 18),
            "B" => new(C("#000000"), C("#d8dee9"), C("#7c8a70"), C("#1e2420"), "Segoe UI Variable Text", 8),
            _ => new(C("#1b1b1f"), C("#eeeeee"), C("#888888"), C("#333333"), "Segoe UI", 10),
        };
        public static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        public SolidColorBrush B(Color c, byte a = 255) { var b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); b.Freeze(); return b; }
    }

    /// <summary>Un fondo disponible para un modo: imagen fija o video (con su póster .jpg).</summary>
    internal sealed record Fondo(string Archivo, string Miniatura, bool EsVideo, string Nombre);

    /// <summary>
    /// Pastilla fija en la esquina inferior izquierda (siempre detrás de las ventanas) con el fondo actual.
    /// Clic o Ctrl+Alt+H abre el panel: fondos e videos del modo + colores sacados del fondo + brillo del color.
    /// </summary>
    internal sealed class MiniHub
    {
        private readonly Func<ModeDef?> _mode;
        private readonly Action<ModeDef> _changed;
        private Window? _pill, _panel;

        public MiniHub(Func<ModeDef?> currentMode, Action<ModeDef> onChanged) { _mode = currentMode; _changed = onChanged; }

        // ---------------- catálogo de fondos ----------------
        public static List<Fondo> Fondos(string key)
        {
            var list = new List<Fondo>();
            var c = ModeStore.Carpeta(key);
            if (c == "") return list;
            var vd = System.IO.Path.Combine(Paths.Recursos, "videos", c);
            if (Directory.Exists(vd))
                foreach (var v in Directory.GetFiles(vd, "*.mp4").OrderBy(x => x))
                {
                    var jpg = System.IO.Path.ChangeExtension(v, ".jpg");
                    if (File.Exists(jpg)) list.Add(new Fondo(v, jpg, true, Pretty(v)));
                }
            var fd = System.IO.Path.Combine(Paths.Recursos, "fondos", c);
            if (Directory.Exists(fd))
                foreach (var f in Directory.GetFiles(fd).Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x))
                    list.Add(new Fondo(f, f, false, Pretty(f)));
            return list;
        }

        private static string Pretty(string f)
        {
            var n = System.IO.Path.GetFileNameWithoutExtension(f).Replace('-', ' ');
            n = n.Length > 26 ? n[..25] + "…" : n;
            return char.ToUpper(n[0]) + n[1..];
        }

        /// <summary>Fondo que el modo tiene puesto ahora (video si está activo, si no la imagen).</summary>
        public static string? Actual(ModeDef m)
        {
            var a = m.Ajustes;
            if (a == null) return null;
            if (a.TryGetValue("wh:live-video-wallpaper", out var v))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(v);
                if (d != null && d.GetValueOrDefault("activo") == "1" && d.TryGetValue("videoPath", out var vp)) return vp;
            }
            return a.TryGetValue("fondo", out var f) && f.StartsWith("img:") ? f[4..] : null;
        }

        // ---------------- colores sacados de la imagen ----------------
        /// <summary>Hasta 5 colores vivos y distintos del fondo (por tono), ordenados por presencia.</summary>
        private static readonly Dictionary<string, List<Color>> _paletas = new(StringComparer.OrdinalIgnoreCase);

        public static List<Color> Paleta(string imagen)
        {
            if (_paletas.TryGetValue(imagen, out var hecha)) return hecha;
            var res = CalcularPaleta(imagen);
            _paletas[imagen] = res;
            return res;
        }

        private static List<Color> CalcularPaleta(string imagen)
        {
            var res = new List<Color>();
            try
            {
                var bi = new BitmapImage(); bi.BeginInit(); bi.UriSource = new Uri(imagen); bi.DecodePixelWidth = 96;
                bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
                var fc = new FormatConvertedBitmap(bi, PixelFormats.Bgra32, null, 0);
                int w = fc.PixelWidth, h = fc.PixelHeight; var px = new byte[w * h * 4];
                fc.CopyPixels(px, w * 4, 0);
                const int Bins = 24;
                var peso = new double[Bins]; var sr = new double[Bins]; var sg = new double[Bins]; var sb = new double[Bins];
                for (int i = 0; i < px.Length; i += 4)
                {
                    double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                    double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
                    double s = max == 0 ? 0 : d / max;
                    if (max < 0.22 || s < 0.25) continue;                       // ignora negros, grises y blancos
                    double hue = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
                    if (hue < 0) hue += 360;
                    int k = (int)(hue / (360.0 / Bins)) % Bins;
                    double wgt = s * max;                                         // pesa más lo vivo y luminoso
                    peso[k] += wgt; sr[k] += r * wgt; sg[k] += g * wgt; sb[k] += b * wgt;
                }
                var orden = Enumerable.Range(0, Bins).Where(k => peso[k] > 0).OrderByDescending(k => peso[k]).ToList();
                var usados = new List<int>();
                foreach (var k in orden)
                {
                    if (usados.Any(u => Math.Min(Math.Abs(u - k), Bins - Math.Abs(u - k)) < 2)) continue; // tonos demasiado parecidos
                    usados.Add(k);
                    res.Add(Color.FromRgb((byte)(sr[k] / peso[k] * 255), (byte)(sg[k] / peso[k] * 255), (byte)(sb[k] / peso[k] * 255)));
                    if (res.Count == 5) break;
                }
            }
            catch (Exception ex) { Log.Error("Paleta: " + ex.Message); }
            return res;
        }

        /// <summary>Ajusta el brillo (V de HSV) a un nivel 0..1 manteniendo tono y saturación.</summary>
        public static Color ConBrillo(Color c, double nivel)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, max = Math.Max(r, Math.Max(g, b));
            if (max == 0) return c;
            double f = Math.Clamp(nivel, 0.15, 1.0) / max;
            byte S(double x) => (byte)Math.Clamp(Math.Round(x * f * 255), 0, 255);
            return Color.FromRgb(S(r), S(g), S(b));
        }

        private static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

        // ---------------- pastilla fija ----------------
        public void ShowPill()
        {
            ClosePill();
            var m = _mode();
            if (m == null || m.Key is "J" or "B") return;   // en Gaming manda el hub de juegos; en Batería, nada que gaste
            var t = HubTheme.For(m.Key);
            var actual = Actual(m);
            var thumbFile = actual == null ? null : (actual.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.ChangeExtension(actual, ".jpg") : actual);

            var thumb = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(Math.Min(t.Radius, 10)), Background = t.B(t.Line) };
            if (thumbFile != null && File.Exists(thumbFile)) thumb.Background = new ImageBrush(Load(thumbFile, 120)) { Stretch = Stretch.UniformToFill };
            var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) };
            txt.Children.Add(new TextBlock { Text = "Fondos", FontSize = 13, Foreground = t.B(t.Fg), FontFamily = new FontFamily(t.Font), FontWeight = FontWeights.SemiBold });
            txt.Children.Add(new TextBlock { Text = "Ctrl+Alt+H", FontSize = 11, Foreground = t.B(t.Dim), FontFamily = new FontFamily(t.Font) });
            var dot = new Ellipse { Width = 8, Height = 8, Fill = t.B(HubTheme.C(m.Color)), Margin = new Thickness(4, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(thumb); row.Children.Add(txt); row.Children.Add(dot);
            var card = new Border { Child = row, Padding = new Thickness(6, 6, 14, 6), CornerRadius = new CornerRadius(t.Radius + 4),
                                    Background = t.B(t.Bg, 0xD8), BorderBrush = t.B(t.Line), BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
            card.MouseLeftButtonUp += (_, _) => TogglePanel();

            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false,
                ShowActivated = false, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight, Content = card, Title = "okla · fondos"
            };
            w.Loaded += (_, _) =>
            {
                var wa = SystemParameters.WorkArea;
                w.Left = wa.Left + 22; w.Top = wa.Bottom - w.ActualHeight - 22;
                SendToBack(w);
            };
            w.SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(w).Handle;
                SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x80 | 0x08000000);   // TOOLWINDOW | NOACTIVATE
                HwndSource.FromHwnd(h).AddHook(KeepBottom);
            };
            w.Show();
            _pill = w;
        }

        public void ClosePill() { _pill?.Close(); _pill = null; }

        // ---------------- panel ----------------
        public void TogglePanel()
        {
            if (_panel != null) { _panel.Close(); return; }
            var m = _mode();
            if (m == null) return;
            var t = HubTheme.For(m.Key);
            var fg = t.B(t.Fg); var dim = t.B(t.Dim); var font = new FontFamily(t.Font);

            var root = new StackPanel { Margin = new Thickness(22) };
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            var close = new TextBlock { Text = "✕", FontSize = 14, Foreground = dim, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(close, Dock.Right); head.Children.Add(close);
            var ht = new StackPanel();
            ht.Children.Add(new TextBlock { Text = $"Fondos · {m.Nombre}", FontSize = 20, Foreground = fg, FontFamily = font, FontWeight = FontWeights.SemiBold });
            ht.Children.Add(new TextBlock { Text = "Elige un fondo; los colores del modo se adaptan a él", FontSize = 12, Foreground = dim, FontFamily = font });
            head.Children.Add(ht);
            root.Children.Add(head);

            // Galería
            var actual = Actual(m);
            var grid = new WrapPanel { Width = 3 * 186 };
            foreach (var f in Fondos(m.Key)) grid.Children.Add(Tile(f, t, string.Equals(f.Archivo, actual, StringComparison.OrdinalIgnoreCase), m));
            root.Children.Add(new ScrollViewer { Content = grid, MaxHeight = 3 * 118 + 10, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            // Tema de Windows de este fondo: se calcula solo y se puede cambiar aquí (se recuerda por fondo).
            var opciones = new StackPanel();
            root.Children.Add(opciones);
            FrameworkElement Chip(string texto, bool activo, Action click, Color? relleno = null)
            {
                var b = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(relleno == null ? 12 : 0, 5, relleno == null ? 12 : 0, 5),
                                     Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, BorderThickness = new Thickness(activo ? 2 : 1),
                                     BorderBrush = activo ? fg : t.B(t.Line), Background = relleno is { } rc ? new SolidColorBrush(rc) : t.B(activo ? t.Line : t.Bg) };
                if (relleno == null) b.Child = new TextBlock { Text = texto, FontSize = 12, Foreground = activo ? fg : dim, FontFamily = font };
                else { b.Width = 30; b.Height = 30; b.CornerRadius = new CornerRadius(15); b.ToolTip = texto; }
                b.MouseLeftButtonUp += (_, _) => click();
                return b;
            }
            TextBlock Titulo(string s) => new() { Text = s, FontSize = 11, Foreground = dim, FontFamily = font, Margin = new Thickness(0, 14, 0, 8) };
            WrapPanel Fila() => new() { Width = 3 * 186 };

            void Pintar()
            {
                opciones.Children.Clear();
                var cur = Actual(m);
                if (cur == null) return;
                var img = ImagenDe(cur);
                var pref = PrefDe(cur);
                var auto = File.Exists(img) ? TemaAuto(img) : "oscuro";
                void Cambiar(Action<TemaFondo> c) { c(pref); GuardarTemas(); AplicarTema(m, cur); Programar(m); Pintar(); }

                opciones.Children.Add(Titulo("TEMA DE WINDOWS"));
                var f1 = Fila();
                f1.Children.Add(Chip($"Automático ({auto})", pref.Tema == "auto", () => Cambiar(p => p.Tema = "auto")));
                f1.Children.Add(Chip("Claro", pref.Tema == "claro", () => Cambiar(p => p.Tema = "claro")));
                f1.Children.Add(Chip("Oscuro", pref.Tema == "oscuro", () => Cambiar(p => p.Tema = "oscuro")));
                opciones.Children.Add(f1);

                opciones.Children.Add(Titulo("COLOR DE ACENTO"));
                var f2 = Fila();
                var tema = pref.Tema == "auto" ? auto : pref.Tema;
                f2.Children.Add(Chip("Automático", pref.Acento == null, () => Cambiar(p => p.Acento = null)));
                foreach (var c in File.Exists(img) ? Paleta(img) : new List<Color>())
                {
                    var shown = ParaTema(c, tema);
                    var hx = Hex(shown);
                    f2.Children.Add(Chip(hx, string.Equals(pref.Acento, hx, StringComparison.OrdinalIgnoreCase), () => Cambiar(p => p.Acento = hx), shown));
                }
                opciones.Children.Add(f2);

                opciones.Children.Add(Titulo("DÓNDE SE VE EL COLOR"));
                var f3 = Fila();
                f3.Children.Add(Chip("Solo detalles", !pref.Bordes, () => Cambiar(p => p.Bordes = false)));
                f3.Children.Add(Chip("Barra, Inicio y bordes de ventanas", pref.Bordes, () => Cambiar(p => p.Bordes = true)));
                opciones.Children.Add(f3);

                opciones.Children.Add(Titulo("TRANSPARENCIAS"));
                var f4 = Fila();
                var trModo = TransparenciaDelModo(m.Key);
                f4.Children.Add(Chip($"Las del modo ({(trModo ? "sí" : "no")})", pref.Transparencia == null, () => Cambiar(p => p.Transparencia = null)));
                f4.Children.Add(Chip("Sí", pref.Transparencia == true, () => Cambiar(p => p.Transparencia = true)));
                f4.Children.Add(Chip("No", pref.Transparencia == false, () => Cambiar(p => p.Transparencia = false)));
                opciones.Children.Add(f4);
            }
            Pintar();

            var card = new Border { Child = root, CornerRadius = new CornerRadius(t.Radius + 6), Background = t.B(t.Bg, 0xF4),
                                    BorderBrush = t.B(t.Line), BorderThickness = new Thickness(1) };
            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, Topmost = true,
                ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight, Content = card, Title = "okla · fondos"
            };
            w.Loaded += (_, _) =>
            {
                var wa = SystemParameters.WorkArea;
                w.Left = wa.Left + 22; w.Top = wa.Bottom - w.ActualHeight - 22 - (_pill?.ActualHeight + 12 ?? 0);
            };
            w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
            w.Deactivated += (_, _) => { if (_panel == w) w.Close(); };
            close.MouseLeftButtonUp += (_, _) => w.Close();
            w.Closed += (_, _) => { if (_panel == w) _panel = null; };
            _panel = w;
            w.Show(); w.Activate();

            // Cambiar de fondo recarga la galería y los colores
            void Refrescar() { if (_panel != w) return; grid.Children.Clear(); var act = Actual(m); foreach (var f in Fondos(m.Key)) grid.Children.Add(Tile(f, t, string.Equals(f.Archivo, act, StringComparison.OrdinalIgnoreCase), m)); Pintar(); }
            _refresh = Refrescar;
        }

        private Action? _refresh;

        private FrameworkElement Tile(Fondo f, HubTheme t, bool selected, ModeDef m)
        {
            var img = new Border { Width = 170, Height = 96, CornerRadius = new CornerRadius(Math.Min(t.Radius, 10)),
                                   Background = new ImageBrush(Load(f.Miniatura, 340)) { Stretch = Stretch.UniformToFill } };
            var g = new Grid { Margin = new Thickness(0, 0, 16, 16), Cursor = Cursors.Hand, ToolTip = f.Nombre };
            g.Children.Add(img);
            if (f.EsVideo)
                g.Children.Add(new Border { Background = t.B(Colors.Black, 0xA0), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 2, 6, 2),
                                            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6),
                                            Child = new TextBlock { Text = "▶ video", FontSize = 10, Foreground = Brushes.White } });
            var frame = new Border { CornerRadius = new CornerRadius(Math.Min(t.Radius, 10) + 2), BorderThickness = new Thickness(selected ? 3 : 0),
                                     BorderBrush = t.B(HubTheme.C(m.Color)), Margin = new Thickness(-3) };
            g.Children.Add(frame);
            g.MouseEnter += (_, _) => { if (!selected) { frame.BorderThickness = new Thickness(2); frame.BorderBrush = t.B(t.Dim); } };
            g.MouseLeave += (_, _) => { if (!selected) frame.BorderThickness = new Thickness(0); };
            g.MouseLeftButtonUp += (_, _) => SetFondo(m, f);
            return g;
        }

        private void SetFondo(ModeDef m, Fondo f)
        {
            m.Ajustes ??= new();
            if (f.EsVideo)
            {
                // La imagen fija de debajo no se toca: el video la tapa, y cambiarla cuesta segundos y puede taparlo a él.
                m.Ajustes["wh:live-video-wallpaper"] = ModeStore.VideoSetting(f.Archivo);
            }
            else
            {
                m.Ajustes["wh:live-video-wallpaper"] = ModeStore.VideoOff();
                m.Ajustes["fondo"] = "img:" + f.Archivo;
            }
            AplicarTema(m, f.Archivo);   // tema claro/oscuro, acento y transparencias de ese fondo
            Log.Info($"Mini hub: {m.Nombre} -> {System.IO.Path.GetFileName(f.Archivo)} ({m.Ajustes["tema"]}, {m.Ajustes["acento"]})");
            Toast.PlaySound("click");
            _changed(m);
            _refresh?.Invoke();
            ShowPill();
        }

        // ---------------- tema de Windows por fondo ----------------
        /// <summary>Lo que el usuario cambió para un fondo concreto (lo demás es automático).</summary>
        internal sealed class TemaFondo
        {
            public string Tema { get; set; } = "auto";      // auto | claro | oscuro
            public string? Acento { get; set; }             // null = automático
            public bool Bordes { get; set; }                // acento también en barra, Inicio y bordes
            public bool? Transparencia { get; set; }        // null = la del modo
        }

        private static string TemasFile => System.IO.Path.Combine(Paths.Config, "temas-fondos.json");
        private static Dictionary<string, TemaFondo>? _temas;

        private static Dictionary<string, TemaFondo> Temas()
        {
            if (_temas != null) return _temas;
            try { if (File.Exists(TemasFile)) _temas = JsonSerializer.Deserialize<Dictionary<string, TemaFondo>>(File.ReadAllText(TemasFile)); }
            catch (Exception ex) { Log.Error("temas-fondos.json ilegible: " + ex.Message); }
            _temas = new Dictionary<string, TemaFondo>(_temas ?? new(), StringComparer.OrdinalIgnoreCase);
            return _temas;
        }

        private static TemaFondo PrefDe(string fondo)
        {
            if (!Temas().TryGetValue(fondo, out var p)) { p = new TemaFondo(); Temas()[fondo] = p; }
            return p;
        }

        private static void GuardarTemas()
        {
            try { File.WriteAllText(TemasFile, JsonSerializer.Serialize(Temas(), new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { Log.Error("No pude guardar temas-fondos.json: " + ex.Message); }
        }

        private static string ImagenDe(string fondo) =>
            fondo.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.ChangeExtension(fondo, ".jpg") : fondo;

        private static readonly Dictionary<string, double> _lum = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Luminosidad media del fondo (0 negro … 1 blanco).</summary>
        public static double Luminancia(string imagen)
        {
            if (_lum.TryGetValue(imagen, out var hecha)) return hecha;
            double total = 0; int n = 0;
            try
            {
                var fc = new FormatConvertedBitmap(Load(imagen, 64), PixelFormats.Bgra32, null, 0);
                var px = new byte[fc.PixelWidth * fc.PixelHeight * 4];
                fc.CopyPixels(px, fc.PixelWidth * 4, 0);
                for (int i = 0; i < px.Length; i += 4, n++)
                    total += (0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2]) / 255.0;
            }
            catch (Exception ex) { Log.Error("Luminancia: " + ex.Message); }
            return _lum[imagen] = n == 0 ? 0 : total / n;
        }

        public static string TemaAuto(string imagen) => Luminancia(imagen) > 0.55 ? "claro" : "oscuro";

        /// <summary>El acento se ajusta al tema: más oscuro sobre claro, más luminoso sobre oscuro, para que se lea.</summary>
        private static Color ParaTema(Color c, string tema) => ConBrillo(c, tema == "claro" ? 0.6 : 0.92);

        private static bool TransparenciaDelModo(string key) =>
            ModeStore.Defaults().First(x => x.Key == key).Ajustes!.GetValueOrDefault("transparencia", "1") == "1";

        /// <summary>Pone en el modo el tema, acento y transparencias que tocan para este fondo.</summary>
        private static void AplicarTema(ModeDef m, string fondo)
        {
            m.Ajustes ??= new();
            var img = ImagenDe(fondo);
            var pref = PrefDe(fondo);
            var tema = pref.Tema == "auto" ? (File.Exists(img) ? TemaAuto(img) : "oscuro") : pref.Tema;
            var pal = File.Exists(img) ? Paleta(img) : new List<Color>();
            var acc = pref.Acento != null ? HubTheme.C(pref.Acento) : pal.Count > 0 ? ParaTema(pal[0], tema) : HubTheme.C(ModeStore.Defaults().First(x => x.Key == m.Key).Color);
            m.Ajustes["tema"] = tema;
            m.Ajustes["acento"] = Hex(acc) + (pref.Bordes ? ";bordes" : "");
            m.Ajustes["transparencia"] = (pref.Transparencia ?? TransparenciaDelModo(m.Key)) ? "1" : "0";
            m.Color = Hex(acc);
        }

        /// <summary>Varios clics seguidos = un solo cambio en Windows, 400 ms después del último.</summary>
        private void Programar(ModeDef m)
        {
            _accTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _accTimer.Stop();
            _accTimer.Tick -= _accTick;
            _accTick = (_, _) => { _accTimer!.Stop(); Log.Info($"Mini hub: {m.Nombre} tema {m.Ajustes?["tema"]}, acento {m.Ajustes?["acento"]}"); _changed(m); };
            _accTimer.Tick += _accTick;
            _accTimer.Start();
        }

        private System.Windows.Threading.DispatcherTimer? _accTimer;
        private EventHandler _accTick = (_, _) => { };
        // Miniaturas ya decodificadas: abrir el panel otra vez no vuelve a leer imágenes 4K del disco.
        private static readonly Dictionary<(string, int), BitmapImage> _miniaturas = new();

        private static BitmapImage Load(string f, int w)
        {
            if (_miniaturas.TryGetValue((f, w), out var hecha)) return hecha;
            var bi = new BitmapImage(); bi.BeginInit(); bi.UriSource = new Uri(f); bi.DecodePixelWidth = w;
            bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
            _miniaturas[(f, w)] = bi;
            return bi;
        }

        // ---------------- siempre al fondo (como un widget de escritorio) ----------------
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
        private static readonly IntPtr HWND_BOTTOM = new(1);

        private static void SendToBack(Window w) =>
            SetWindowPos(new WindowInteropHelper(w).Handle, HWND_BOTTOM, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // NOSIZE|NOMOVE|NOACTIVATE

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS { public IntPtr hwnd, after; public int x, y, cx, cy; public uint flags; }

        private static IntPtr KeepBottom(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_WINDOWPOSCHANGING = 0x0046;
            if (msg == WM_WINDOWPOSCHANGING)
            {
                var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                if ((wp.flags & 0x0004) == 0) { wp.after = HWND_BOTTOM; Marshal.StructureToPtr(wp, lParam, false); } // sin NOZORDER: forzar al fondo
            }
            return IntPtr.Zero;
        }
    }
}
