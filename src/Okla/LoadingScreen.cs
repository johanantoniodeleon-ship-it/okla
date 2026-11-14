using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Okla
{
    /// <summary>
    /// Pantalla completa que tapa el cambio de modo mientras Windows y Windhawk se reconfiguran por detrás.
    /// Cada modo tiene su propio estilo. No roba el foco (para no sacar a nadie de un juego).
    /// </summary>
    internal sealed class LoadingScreen
    {
        private sealed record Style(TimeSpan In, TimeSpan Out, TimeSpan Min, bool Zoom);

        private readonly Window _w;
        private readonly TextBlock _status;
        private readonly FrameworkElement _content;
        private readonly Style _style;
        private readonly Stopwatch _shown = Stopwatch.StartNew();
        private bool _closing;
        private readonly System.Threading.Tasks.TaskCompletionSource _covered = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Se completa cuando la pantalla ya tapa todo: solo entonces empieza el cambio de modo.</summary>
        public System.Threading.Tasks.Task Covered => _covered.Task;
        private static LoadingScreen? _current;

        private static SolidColorBrush B(string hex, byte a = 255)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); b.Freeze(); return b;
        }
        private static FontFamily F(string f) => new(f);

        public static LoadingScreen Show(string key, string nombre, string color, string? fondo)
        {
            _current?.CloseNow();
            var ls = new LoadingScreen(key, nombre, color, fondo);
            _current = ls;
            return ls;
        }

        private LoadingScreen(string key, string nombre, string color, string? fondo)
        {
            _status = new TextBlock { FontSize = 13, Opacity = 0.75, TextAlignment = TextAlignment.Center };
            var root = new Grid { ClipToBounds = true };
            (_content, _style) = key switch
            {
                "J" => BuildGaming(root),
                "A" => BuildFedora(root, fondo),
                "G" => BuildPotencia(root, fondo, color),
                "P" => BuildPresentacion(root),
                "B" => BuildBateria(root),
                _ => BuildPanico(root, nombre),
            };

            _w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                Topmost = true, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
                Left = 0, Top = 0, Width = SystemParameters.PrimaryScreenWidth, Height = SystemParameters.PrimaryScreenHeight,
                Content = root, Opacity = _style.In == TimeSpan.Zero ? 1 : 0, Title = "okla"
            };
            _w.SourceInitialized += (_, _) => { Toast.MakeClickThrough(_w); Encima(); };
            // La barra de tareas también es "siempre encima": sin esto puede quedar por delante de la pantalla de carga.
            var encima = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            encima.Tick += (_, _) => { if (_closing) encima.Stop(); else Encima(); };
            encima.Start();
            _w.Show();
            if (_style.In > TimeSpan.Zero)
                _w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, _style.In) { EasingFunction = new QuadraticEase() });
            if (_style.Zoom)
            {
                var st = new ScaleTransform(0.96, 0.96);
                _content.RenderTransformOrigin = new Point(0.5, 0.5);
                _content.RenderTransform = st;
                var a = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(500)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
            }
            // Red de seguridad: nunca más de 20 s tapando la pantalla.
            var guard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            guard.Tick += (_, _) => { guard.Stop(); Finish(TimeSpan.Zero); };
            guard.Start();
            var cover = new DispatcherTimer { Interval = _style.In + TimeSpan.FromMilliseconds(300) };
            cover.Tick += (_, _) => { cover.Stop(); _covered.TrySetResult(); };
            cover.Start();
        }

        private void Encima()
        {
            var h = new System.Windows.Interop.WindowInteropHelper(_w).Handle;
            if (h != IntPtr.Zero) SetWindowPos(h, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* NOSIZE NOMOVE NOACTIVATE */);
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);

        /// <summary>Texto de estado ("Barra de tareas…"). Se puede llamar desde cualquier hilo.</summary>
        public void Report(string texto) =>
            _w.Dispatcher.BeginInvoke(() => _status.Text = texto.Replace("Windhawk · ", "") + "…");

        /// <summary>Cierra cuando haya pasado el mínimo del modo + <paramref name="asentar"/> (lo que tarda Explorer en redibujar).</summary>
        public void Finish(TimeSpan asentar) => _w.Dispatcher.BeginInvoke(() =>
        {
            if (_closing) return;
            var wait = _style.Min - _shown.Elapsed;
            if (asentar > wait) wait = asentar;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            _status.Text = "Listo";
            var t = new DispatcherTimer { Interval = wait };
            t.Tick += (_, _) => { t.Stop(); AnimateOut(); };
            t.Start();
        });

        private void AnimateOut()
        {
            if (_closing) return;
            _closing = true;
            if (_style.Out == TimeSpan.Zero) { CloseNow(); return; }
            if (_style.Zoom && _content.RenderTransform is ScaleTransform st)
            {
                var z = new DoubleAnimation(1, 1.05, _style.Out) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, z);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, z);
            }
            var a = new DoubleAnimation(_w.Opacity, 0, _style.Out) { EasingFunction = new QuadraticEase() };
            a.Completed += (_, _) => CloseNow();
            _w.BeginAnimation(UIElement.OpacityProperty, a);
        }

        private void CloseNow()
        {
            _closing = true;
            _covered.TrySetResult();   // si otro cambio la reemplaza, que nadie se quede esperando
            try { _w.Close(); } catch { }
            if (_current == this) _current = null;
        }

        // ---------- piezas comunes ----------

        private static Image? Backdrop(string? fondo, double blur, double opacity)
        {
            var path = fondo;
            if (path == null || !File.Exists(path)) return null;
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(path);
                bi.DecodePixelWidth = 960;              // pequeño: el desenfoque lo disimula y apenas gasta GPU
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit(); bi.Freeze();
                return new Image { Source = bi, Stretch = Stretch.UniformToFill, Opacity = opacity, Margin = new Thickness(-60),
                                   Effect = blur > 0 ? new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian } : null };
            }
            catch { return null; }
        }

        private static void Forever(Animatable target, DependencyProperty p, double from, double to, double ms, double delayMs = 0, bool autoReverse = false, IEasingFunction? ease = null)
        {
            var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
            { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = autoReverse, BeginTime = TimeSpan.FromMilliseconds(delayMs), EasingFunction = ease };
            target.BeginAnimation(p, a);
        }

        private static TextBlock Text(string s, double size, Brush fg, string font, FontWeight w) =>
            new() { Text = s, FontSize = size, Foreground = fg, FontFamily = F(font), FontWeight = w,
                    HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };

        /// <summary>Icono del modo (null si falta: la pantalla queda como antes).</summary>
        private static Image? Icono(string clave, double tam, Thickness margen, double opacidad = 1)
        {
            if (IconosModos.Grande(clave) is not { } src) return null;
            var img = new Image { Source = src, Width = tam, Height = tam, Margin = margen, Opacity = opacidad, HorizontalAlignment = HorizontalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }

        /// <summary>Firma pequeña de okla abajo a la derecha.</summary>
        private static void Firma(Grid root, Brush texto)
        {
            var fila = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                                        Margin = new Thickness(0, 0, 40, 34), Opacity = 0.7 };
            if (Icono("okla", 26, new Thickness(0, 0, 8, 0)) is { } i) fila.Children.Add(i);
            var t = new TextBlock { Text = "okla", FontSize = 14, Foreground = texto, VerticalAlignment = VerticalAlignment.Center, FontFamily = F("Segoe UI Variable Display"), FontWeight = FontWeights.SemiBold };
            fila.Children.Add(t);
            root.Children.Add(fila);
        }

        private static string Spaced(string s) => string.Join("  ", s.ToCharArray());

        // ---------- J · Gaming: negro, técnico, corte seco ----------
        private (FrameworkElement, Style) BuildGaming(Grid root)
        {
            root.Background = B("#050506");
            var fg = B("#d8d8de"); var dim = B("#6e6e76");

            // Esquinas tipo HUD
            foreach (var (h, v) in new[] { (HorizontalAlignment.Left, VerticalAlignment.Top), (HorizontalAlignment.Right, VerticalAlignment.Top),
                                           (HorizontalAlignment.Left, VerticalAlignment.Bottom), (HorizontalAlignment.Right, VerticalAlignment.Bottom) })
            {
                var c = new Border { Width = 46, Height = 46, Margin = new Thickness(48), HorizontalAlignment = h, VerticalAlignment = v, BorderBrush = dim,
                    BorderThickness = new Thickness(h == HorizontalAlignment.Left ? 2 : 0, v == VerticalAlignment.Top ? 2 : 0,
                                                    h == HorizontalAlignment.Right ? 2 : 0, v == VerticalAlignment.Bottom ? 2 : 0) };
                root.Children.Add(c);
            }

            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (Icono("J", 96, new Thickness(0, 0, 0, 22)) is { } ij) stack.Children.Add(ij);
            stack.Children.Add(Text(Spaced("OKLA // MODO J"), 13, dim, "Bahnschrift", FontWeights.Normal));
            stack.Children.Add(Text(Spaced("GAMING"), 84, fg, "Bahnschrift SemiBold", FontWeights.SemiBold));

            // Barra: un bloque brillante que recorre la pista
            var track = new Grid { Width = 460, Height = 3, Background = B("#1c1c20"), ClipToBounds = true, Margin = new Thickness(0, 18, 0, 14) };
            var tt = new TranslateTransform();
            var blk = new Rectangle { Width = 110, Fill = fg, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = tt };
            track.Children.Add(blk);
            Forever(tt, TranslateTransform.XProperty, -110, 460, 650);
            stack.Children.Add(track);

            _status.Foreground = dim; _status.FontFamily = F("Bahnschrift");
            stack.Children.Add(_status);
            root.Children.Add(stack);

            var info = Text("ALTO RENDIMIENTO · MEMORIA LIBRE · SIN NOTIFICACIONES", 11, dim, "Bahnschrift", FontWeights.Normal);
            info.VerticalAlignment = VerticalAlignment.Bottom; info.Margin = new Thickness(0, 0, 0, 64);
            root.Children.Add(info);
            Firma(root, dim);
            return (stack, new Style(TimeSpan.Zero, TimeSpan.FromMilliseconds(140), TimeSpan.FromMilliseconds(2000), false));
        }

        // ---------- A · Fedora: Nord, cristal y un anillo que gira ----------
        private (FrameworkElement, Style) BuildFedora(Grid root, string? fondo)
        {
            root.Background = B("#2e3440");
            if (Backdrop(fondo, 40, 0.55) is { } img) root.Children.Add(img);
            root.Children.Add(new Rectangle { Fill = B("#2e3440", 0x99) });

            var frost = B("#88c0d0"); var snow = B("#eceff4");
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

            var ring = new Grid { Width = 132, Height = 132, Margin = new Thickness(0, 0, 0, 26) };
            ring.Children.Add(new Ellipse { Stroke = B("#eceff4", 0x26), StrokeThickness = 5 });
            var rot = new RotateTransform();
            var arc = new System.Windows.Shapes.Path
            {
                Stroke = frost, StrokeThickness = 5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M 66,2.5 A 63.5,63.5 0 0 1 129.5,66"),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = rot
            };
            ring.Children.Add(arc);
            if (Icono("A", 84, new Thickness(0)) is { } ia) { ia.VerticalAlignment = VerticalAlignment.Center; ring.Children.Add(ia); }
            Forever(rot, RotateTransform.AngleProperty, 0, 360, 900);
            stack.Children.Add(ring);

            stack.Children.Add(Text("Fedora", 46, snow, "Segoe UI Variable Display", FontWeights.Light));
            stack.Children.Add(Text("Preparando tu escritorio", 15, B("#d8dee9", 0xCC), "Segoe UI Variable Text", FontWeights.Normal));
            _status.Foreground = B("#d8dee9"); _status.Margin = new Thickness(0, 18, 0, 0); _status.FontFamily = F("Segoe UI Variable Text");
            stack.Children.Add(_status);
            root.Children.Add(stack);
            Firma(root, snow);
            return (stack, new Style(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(420), TimeSpan.FromMilliseconds(3500), false));
        }

        // ---------- G · Potencia: negro OLED, destello que barre, título enorme ----------
        private (FrameworkElement, Style) BuildPotencia(Grid root, string? fondo, string color)
        {
            root.Background = Brushes.Black;
            if (Backdrop(fondo, 28, 0.42) is { } img) root.Children.Add(img);
            root.Children.Add(new Rectangle { Fill = new RadialGradientBrush(Color.FromArgb(0x10, 0, 0, 0), Color.FromArgb(0xE0, 0, 0, 0)) { RadiusX = 0.75, RadiusY = 0.75 } });

            // Destello diagonal que cruza la pantalla
            var sweepT = new TranslateTransform();
            var sweep = new Rectangle
            {
                Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, -400, 0, -400),
                Fill = new LinearGradientBrush(new GradientStopCollection {
                    new(Color.FromArgb(0, 255, 255, 255), 0), new(Color.FromArgb(0x22, 0xb8, 0xc0, 0xff), 0.5), new(Color.FromArgb(0, 255, 255, 255), 1) }, 0),
                RenderTransform = new TransformGroup { Children = { new RotateTransform(18), sweepT } }
            };
            root.Children.Add(sweep);
            Forever(sweepT, TranslateTransform.XProperty, -600, SystemParameters.PrimaryScreenWidth + 200, 1400, ease: new SineEase());

            var a = (Color)ColorConverter.ConvertFromString(color);
            var comp = Color.FromRgb(0xff, 0x7c, 0xd8); // complementario cálido del azul-violeta
            var grad = new LinearGradientBrush(a, comp, 0);
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (Icono("G", 110, new Thickness(0, 0, 0, 14)) is { } ig) { ig.Effect = new DropShadowEffect { Color = a, BlurRadius = 36, ShadowDepth = 0, Opacity = 0.6 }; stack.Children.Add(ig); }
            stack.Children.Add(Text(Spaced("MODO"), 14, B("#ffffff", 0x99), "Segoe UI Variable Display", FontWeights.SemiBold));
            var title = Text("POTENCIA", 132, grad, "Segoe UI Variable Display", FontWeights.Black);
            title.Effect = new DropShadowEffect { Color = a, BlurRadius = 40, ShadowDepth = 0, Opacity = 0.55 };
            stack.Children.Add(title);

            var track = new Grid { Width = 520, Height = 4, Background = B("#ffffff", 0x18), ClipToBounds = true, Margin = new Thickness(0, 8, 0, 16) };
            var tt = new TranslateTransform();
            track.Children.Add(new Rectangle { Width = 180, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = tt, RadiusX = 2, RadiusY = 2,
                                               Fill = new LinearGradientBrush(Color.FromArgb(0, a.R, a.G, a.B), comp, 0) });
            Forever(tt, TranslateTransform.XProperty, -180, 520, 800, ease: new QuadraticEase { EasingMode = EasingMode.EaseInOut });
            stack.Children.Add(track);

            _status.Foreground = B("#ffffff", 0xAA); _status.FontFamily = F("Segoe UI Variable Text");
            stack.Children.Add(_status);
            root.Children.Add(stack);
            Firma(root, B("#ffffff"));
            return (stack, new Style(TimeSpan.FromMilliseconds(140), TimeSpan.FromMilliseconds(340), TimeSpan.FromMilliseconds(3200), true));
        }

        // ---------- P · Presentación: Everforest, tres puntos triádicos, calma ----------
        private (FrameworkElement, Style) BuildPresentacion(Grid root)
        {
            root.Background = B("#2d353b");
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (Icono("P", 96, new Thickness(0, 0, 0, 26)) is { } ip) stack.Children.Add(ip);
            var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 30) };
            var cols = new[] { "#a7c080", "#e69875", "#7fbbb3" };
            for (int i = 0; i < 3; i++)
            {
                var st = new ScaleTransform(0.6, 0.6);
                var e = new Ellipse { Width = 16, Height = 16, Fill = B(cols[i]), Margin = new Thickness(9, 0, 9, 0),
                                      RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = st };
                var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
                Forever(st, ScaleTransform.ScaleXProperty, 0.6, 1.15, 520, i * 170, true, ease);
                Forever(st, ScaleTransform.ScaleYProperty, 0.6, 1.15, 520, i * 170, true, ease);
                dots.Children.Add(e);
            }
            stack.Children.Add(dots);
            stack.Children.Add(Text("Presentación", 52, B("#d3c6aa"), "Segoe UI Variable Display", FontWeights.Light));
            stack.Children.Add(Text("Preparando la escena", 15, B("#9da9a0"), "Segoe UI Variable Text", FontWeights.Normal));
            _status.Foreground = B("#859289"); _status.Margin = new Thickness(0, 18, 0, 0); _status.FontFamily = F("Segoe UI Variable Text");
            stack.Children.Add(_status);
            root.Children.Add(stack);
            Firma(root, B("#d3c6aa"));
            return (stack, new Style(TimeSpan.FromMilliseconds(380), TimeSpan.FromMilliseconds(650), TimeSpan.FromMilliseconds(3200), false));
        }

        // ---------- Pánico: neutro ----------
        /// <summary>Batería: negro puro y sin animaciones (lo que menos gasta).</summary>
        private (FrameworkElement, Style) BuildBateria(Grid root)
        {
            root.Background = B("#000000");
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (Icono("B", 72, new Thickness(0, 0, 0, 18), 0.8) is { } ib) stack.Children.Add(ib);
            stack.Children.Add(Text("BATERÍA", 44, B("#a3be8c"), "Segoe UI Variable Display", FontWeights.Light));
            stack.Children.Add(Text("Todo al mínimo para que dure más", 15, B("#7c8a70"), "Segoe UI Variable Text", FontWeights.Normal));
            _status.Foreground = B("#5d6658"); _status.Margin = new Thickness(0, 16, 0, 0);
            stack.Children.Add(_status);
            root.Children.Add(stack);
            return (stack, new Style(TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(1500), false));
        }

        private (FrameworkElement, Style) BuildPanico(Grid root, string nombre)
        {
            root.Background = B("#1b1b1f");
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (Icono("panico", 64, new Thickness(0, 0, 0, 20), 0.85) is { } ix) stack.Children.Add(ix);
            stack.Children.Add(Text(nombre, 40, B("#eeeeee"), "Segoe UI Variable Display", FontWeights.Light));
            stack.Children.Add(Text("Volviendo a Windows normal", 15, B("#e06c75"), "Segoe UI Variable Text", FontWeights.Normal));
            _status.Foreground = B("#888888"); _status.Margin = new Thickness(0, 16, 0, 0);
            stack.Children.Add(_status);
            root.Children.Add(stack);
            return (stack, new Style(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(800), false));
        }
    }
}
