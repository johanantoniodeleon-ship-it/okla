using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    /// <summary>
    /// Editor de modos (Ctrl+Alt+E o bandeja). Trabaja sobre una copia: nada cambia hasta "Guardar" o "Probar".
    /// Secciones: General · Apariencia · Windhawk · Fondos · Gaming (solo en Gaming).
    /// </summary>
    internal sealed class EditorWindow : Window
    {
        private static readonly Color Bg = C("#1b1e24"), Side = C("#16181d"), Panel = C("#232730"), Line = C("#323844"),
                                      Fg = C("#e8ebf0"), Dim = C("#97a0af"), Faint = C("#5d6574");
        private static Color C(string h) => (Color)ColorConverter.ConvertFromString(h);
        private static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        private static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");

        private List<ModeDef> _modes;
        private readonly Engine _engine;
        private readonly Action<List<ModeDef>, string?> _guardar;
        private string _sel;
        private string _seccion = "General";
        private bool _sucio;

        private readonly StackPanel _lista = new() { Margin = new Thickness(14, 0, 14, 0) };
        private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(32, 0, 32, 0) };
        private readonly ContentControl _cuerpo = new();
        private readonly TextBlock _titulo = new() { FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = B(Fg), FontFamily = Font };
        private readonly TextBlock _subtitulo = new() { FontSize = 13, Foreground = B(Dim), FontFamily = Font, Margin = new Thickness(0, 4, 0, 0) };
        private readonly TextBlock _estado = new() { FontSize = 12, Foreground = B(Dim), FontFamily = Font, VerticalAlignment = VerticalAlignment.Center };

        public EditorWindow(List<ModeDef> modes, string actual, Engine engine, Action<List<ModeDef>, string?> guardar)
        {
            _modes = Clonar(modes);
            _engine = engine;
            _guardar = guardar;
            _sel = _modes.Any(m => m.Key == actual) ? actual : _modes[0].Key;

            Title = "okla · editor";
            Width = 1180; Height = 780; MinWidth = 980; MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = B(Bg); FontFamily = Font;
            SourceInitialized += (_, _) => { int on = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, 4); }; // barra de título oscura
            Closing += (_, e) =>
            {
                if (!_sucio) return;
                var r = MessageBox.Show(this, "Hay cambios sin guardar. ¿Guardarlos antes de cerrar?", "okla · editor", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) e.Cancel = true;
                else if (r == MessageBoxResult.Yes) Guardar(null);
            };
            KeyDown += (_, e) => { if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) Guardar(null); };

            // Barra lateral
            var side = new DockPanel { Background = B(Side), Width = 250 };
            var marca = new StackPanel { Margin = new Thickness(26, 26, 20, 22) };
            marca.Children.Add(new TextBlock { Text = "okla", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = B(Fg) });
            marca.Children.Add(new TextBlock { Text = "Editor de modos", FontSize = 12, Foreground = B(Dim) });
            DockPanel.SetDock(marca, Dock.Top); side.Children.Add(marca);
            var nota = new TextBlock { Text = "Los cambios se aplican al pulsar Guardar o Probar.", TextWrapping = TextWrapping.Wrap, FontSize = 11,
                                       Foreground = B(Faint), Margin = new Thickness(26, 0, 20, 22) };
            DockPanel.SetDock(nota, Dock.Bottom); side.Children.Add(nota);
            side.Children.Add(_lista);

            // Cabecera + pestañas + contenido + barra inferior
            var head = new StackPanel { Margin = new Thickness(32, 26, 32, 16) };
            head.Children.Add(_titulo); head.Children.Add(_subtitulo);
            var abajo = new DockPanel { Background = B(Side), LastChildFill = false };
            var botones = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 24, 12) };
            botones.Children.Add(Boton("Valores de fábrica", false, Fabrica));
            botones.Children.Add(Boton("Probar modo", false, () => Guardar(_sel)));
            botones.Children.Add(Boton("Guardar", true, () => Guardar(null)));
            DockPanel.SetDock(botones, Dock.Right); abajo.Children.Add(botones);
            _estado.Margin = new Thickness(32, 0, 0, 0);
            DockPanel.SetDock(_estado, Dock.Left); abajo.Children.Add(_estado);

            var main = new DockPanel();
            DockPanel.SetDock(head, Dock.Top); main.Children.Add(head);
            DockPanel.SetDock(_tabs, Dock.Top); main.Children.Add(_tabs);
            DockPanel.SetDock(abajo, Dock.Bottom); main.Children.Add(abajo);
            main.Children.Add(new ScrollViewer { Content = _cuerpo, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(32, 18, 32, 24) });

            var root = new DockPanel();
            DockPanel.SetDock(side, Dock.Left); root.Children.Add(side);
            root.Children.Add(main);
            Content = root;
            Pintar();
        }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

        private ModeDef M => _modes.First(m => m.Key == _sel);
        private Dictionary<string, string> A => M.Ajustes ??= new();
        private Color Acento => TryColor(M.Color) ?? C("#88c0d0");

        private static List<ModeDef> Clonar(List<ModeDef> l) =>
            JsonSerializer.Deserialize<List<ModeDef>>(JsonSerializer.Serialize(l))!;

        private void Cambio() { _sucio = true; _estado.Text = "● Cambios sin guardar"; _estado.Foreground = B(C("#ebcb8b")); }

        // ---------------------------------------------------------------- pintado general
        private void Pintar()
        {
            _lista.Children.Clear();
            foreach (var m in _modes)
            {
                bool sel = m.Key == _sel;
                var col = TryColor(m.Color) ?? C("#888888");
                var fila = new DockPanel { Margin = new Thickness(10, 9, 10, 9) };
                FrameworkElement dot = IconosModos.Wpf(m.Key) is { } src
                    ? new System.Windows.Controls.Image { Source = src, Width = 28, Height = 28, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }
                    : new Ellipse { Width = 12, Height = 12, Fill = B(col), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(dot, BitmapScalingMode.HighQuality);
                DockPanel.SetDock(dot, Dock.Left); fila.Children.Add(dot);
                var atajo = new TextBlock { Text = $"Ctrl+Alt+{m.Tecla}", FontSize = 11, Foreground = B(Faint), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(atajo, Dock.Right); fila.Children.Add(atajo);
                var txt = new StackPanel();
                txt.Children.Add(new TextBlock { Text = m.Nombre + (m.Activo ? "" : "  · desactivado"), FontSize = 14, FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal, Foreground = B(sel ? Fg : Dim) });
                if (!m.Activo) fila.Opacity = 0.5;
                txt.Children.Add(new TextBlock { Text = m.Descripcion, FontSize = 11, Foreground = B(Faint), TextTrimming = TextTrimming.CharacterEllipsis });
                fila.Children.Add(txt);
                var item = new Border { Child = fila, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.Hand,
                                        Background = sel ? B(Panel) : Brushes.Transparent };
                var key = m.Key;
                item.MouseLeftButtonUp += (_, _) => { _sel = key; if (_seccion == "Gaming" && key != "J") _seccion = "General"; Pintar(); };
                _lista.Children.Add(item);
            }

            _titulo.Text = M.Nombre;
            _subtitulo.Text = $"{M.Descripcion}  ·  Ctrl+Alt+{M.Tecla}";

            _tabs.Children.Clear();
            var secciones = new List<string> { "General", "Apariencia", "Windhawk", "Fondos" };
            if (M.Key == "J") secciones.Add("Gaming");
            foreach (var s in secciones)
            {
                bool on = s == _seccion;
                var t = new Border { Padding = new Thickness(2, 8, 2, 10), Margin = new Thickness(0, 0, 26, 0), Cursor = Cursors.Hand,
                                     BorderThickness = new Thickness(0, 0, 0, on ? 2 : 0), BorderBrush = B(Acento),
                                     Child = new TextBlock { Text = s, FontSize = 14, Foreground = B(on ? Fg : Dim), FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal } };
                var ss = s;
                t.MouseLeftButtonUp += (_, _) => { _seccion = ss; Pintar(); };
                _tabs.Children.Add(t);
            }

            _cuerpo.Content = _seccion switch
            {
                "Apariencia" => Apariencia(),
                "Windhawk" => Windhawk(),
                "Fondos" => FondosTab(),
                "Gaming" => Gaming(),
                _ => General(),
            };
        }

        // ---------------------------------------------------------------- controles
        private static Border Tarjeta(string titulo, string? ayuda, params UIElement[] filas)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = titulo, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = B(Fg) });
            if (ayuda != null) sp.Children.Add(new TextBlock { Text = ayuda, FontSize = 12, Foreground = B(Dim), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            foreach (var f in filas) sp.Children.Add(f);
            return new Border { Child = sp, Background = B(Panel), CornerRadius = new CornerRadius(12), Padding = new Thickness(22, 18, 22, 18), Margin = new Thickness(0, 0, 0, 14),
                                BorderBrush = B(Line), BorderThickness = new Thickness(1) };
        }

        private static UIElement Fila(string etiqueta, UIElement control, string? ayuda = null)
        {
            var g = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var l = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            l.Children.Add(new TextBlock { Text = etiqueta, FontSize = 13, Foreground = B(Fg) });
            if (ayuda != null) l.Children.Add(new TextBlock { Text = ayuda, FontSize = 11, Foreground = B(Faint), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 12, 0) });
            g.Children.Add(l);
            Grid.SetColumn(control, 1); g.Children.Add(control);
            return g;
        }

        private UIElement Opciones(IEnumerable<(string valor, string texto)> ops, string? actual, Action<string> elegir)
        {
            var wp = new WrapPanel();
            foreach (var (v, t) in ops)
            {
                bool on = string.Equals(v, actual, StringComparison.OrdinalIgnoreCase);
                var b = new Border { CornerRadius = new CornerRadius(15), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 6), Cursor = Cursors.Hand,
                                     Background = on ? B(Acento) : B(Bg), BorderBrush = B(on ? Acento : Line), BorderThickness = new Thickness(1),
                                     Child = new TextBlock { Text = t, FontSize = 12, Foreground = on ? B(Contraste(Acento)) : B(Dim), FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal } };
                var vv = v;
                b.MouseLeftButtonUp += (_, _) => { elegir(vv); Cambio(); Pintar(); };
                wp.Children.Add(b);
            }
            return wp;
        }

        private UIElement SiNo(string clave, string si = "Sí", string no = "No") =>
            Opciones(new[] { ("1", si), ("0", no), ("", "Como Windows") }, A.TryGetValue(clave, out var v) ? v : "", x => Poner(clave, x));

        private void Poner(string clave, string valor) { if (valor == "") A.Remove(clave); else A[clave] = valor; }

        private TextBox Texto(string valor, Action<string> cambio, double ancho = 360)
        {
            var tb = new TextBox { Text = valor, Width = ancho, HorizontalAlignment = HorizontalAlignment.Left, FontSize = 13, Padding = new Thickness(8, 6, 8, 6),
                                   Background = B(Bg), Foreground = B(Fg), BorderBrush = B(Line), CaretBrush = B(Fg) };
            tb.TextChanged += (_, _) => { cambio(tb.Text); Cambio(); };
            return tb;
        }

        /// <summary>Ciudad del clima (común a todos los modos): se busca y se guarda al momento, sin esperar a "Guardar".</summary>
        private UIElement CampoCiudad()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var tb = new TextBox { Text = Clima.Ubicacion()?.Ciudad ?? "", Width = 240, FontSize = 13, Padding = new Thickness(8, 6, 8, 6),
                                   Background = B(Bg), Foreground = B(Fg), BorderBrush = B(Line), CaretBrush = B(Fg) };
            var estado = new TextBlock { FontSize = 12, Foreground = B(Dim), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            async void Buscar()
            {
                estado.Text = "Buscando…";
                var r = await Clima.Buscar(tb.Text);
                if (r != null) tb.Text = r;
                estado.Text = r != null ? "Guardada" : "No la encuentro";
            }
            tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) Buscar(); };
            sp.Children.Add(tb);
            sp.Children.Add(Boton("Buscar", false, Buscar));
            sp.Children.Add(estado);
            return sp;
        }

        private static Border Boton(string texto, bool principal, Action click)
        {
            var b = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(10, 0, 0, 0), Cursor = Cursors.Hand,
                                 Background = principal ? B(C("#88c0d0")) : B(Panel), BorderBrush = B(Line), BorderThickness = new Thickness(principal ? 0 : 1),
                                 Child = new TextBlock { Text = texto, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = principal ? B(C("#1b1e24")) : B(Fg) } };
            b.MouseLeftButtonUp += (_, _) => click();
            return b;
        }

        private static Color? TryColor(string? hex)
        {
            try { return string.IsNullOrWhiteSpace(hex) ? null : C(hex.Split(';')[0].Trim()); } catch { return null; }
        }

        private static Color Contraste(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 150 ? C("#16181d") : Colors.White;

        private UIElement Muestras(string? actual, Action<string> elegir)
        {
            var wp = new WrapPanel();
            var colores = new[] { "#88c0d0", "#81a1c1", "#5e81ac", "#7c8cff", "#b48ead", "#d08770", "#ebcb8b", "#a3be8c", "#a7c080", "#e06c75", "#6e6e76", "#ffffff" };
            foreach (var h in colores)
            {
                bool on = string.Equals(actual?.Split(';')[0], h, StringComparison.OrdinalIgnoreCase);
                var e = new Ellipse { Width = 26, Height = 26, Fill = B(C(h)), Margin = new Thickness(0, 0, 8, 6), Cursor = Cursors.Hand,
                                      Stroke = B(on ? Fg : Line), StrokeThickness = on ? 3 : 1, ToolTip = h };
                var hh = h;
                e.MouseLeftButtonUp += (_, _) => { elegir(hh); Cambio(); Pintar(); };
                wp.Children.Add(e);
            }
            return wp;
        }

        // ---------------------------------------------------------------- General
        private UIElement General()
        {
            var colorBox = Texto(M.Color, v => { if (TryColor(v) != null) M.Color = v.Trim(); }, 130);
            var sp = new StackPanel();
            sp.Children.Add(Tarjeta("Disponible", M.Key == "B"
                    ? "Activado: al desconectar el cargador okla pregunta si quieres este modo, y aparece en la bandeja junto al pánico."
                    : "Desactivado: no sale en la bandeja ni responde a su atajo, y okla no cambia a él solo.",
                Fila("Modo", Opciones(new[] { ("1", "Activado"), ("0", "Desactivado") }, M.Activo ? "1" : "0", v => { M.Activo = v == "1"; Pintar(); }))));
            sp.Children.Add(Tarjeta("Identidad", "Cómo se llama el modo y su color en la bandeja, la pantalla de carga y el mini hub.",
                Fila("Nombre", Texto(M.Nombre, v => M.Nombre = v)),
                Fila("Descripción", Texto(M.Descripcion, v => M.Descripcion = v, 480)),
                Fila("Color del modo", Muestras(M.Color, h => M.Color = h)),
                Fila("Color (hex)", colorBox),
                Fila("Atajo", new TextBlock { Text = $"Ctrl+Alt+{M.Tecla}", FontSize = 13, Foreground = B(Dim), VerticalAlignment = VerticalAlignment.Center })));
            return sp;
        }

        // ---------------------------------------------------------------- Apariencia
        private UIElement Apariencia()
        {
            var acc = A.TryGetValue("acento", out var a) ? a : "";
            var (hex, bordes) = acc.StartsWith("#") ? AccentSetting.SplitHex(acc) : ("", false);
            var sp = new StackPanel();
            sp.Children.Add(Tarjeta("Colores", "Si eliges un fondo en el mini hub, el tema y el acento se adaptan a él solos.",
                Fila("Tema", Opciones(new[] { ("oscuro", "Oscuro"), ("claro", "Claro"), ("", "Como Windows") }, A.GetValueOrDefault("tema", ""), v => Poner("tema", v))),
                Fila("Color de acento", Muestras(hex, h => A["acento"] = h + (bordes ? ";bordes" : ""))),
                Fila("Acento (hex)", Texto(hex, v => { if (TryColor(v) != null) A["acento"] = v.Trim() + (bordes ? ";bordes" : ""); }, 130)),
                Fila("Dónde se ve el acento", Opciones(new[] { ("0", "Solo detalles"), ("1", "Barra, Inicio y bordes") }, bordes ? "1" : "0",
                     v => { if (hex != "") A["acento"] = hex + (v == "1" ? ";bordes" : ""); })),
                Fila("Transparencias", SiNo("transparencia")),
                Fila("Animaciones", SiNo("animaciones"))));
            sp.Children.Add(Tarjeta("Sistema", null,
                Fila("Plan de energía", Opciones(new[] { ("alto", "Alto rendimiento"), ("equilibrado", "Equilibrado"), ("", "Como Windows") }, A.GetValueOrDefault("energia", ""), v => Poner("energia", v))),
                Fila("Ocultar la barra de tareas", SiNo("autoocultar"))));
            sp.Children.Add(Tarjeta("Escritorio", "Cada modo tiene su carpeta de escritorio en okla\\escritorios.",
                Fila("Widget", Opciones(new[] { ("reloj", "Reloj, clima y calendario"), ("stats", "Reloj y estadísticas"), ("mini", "Esquina discreta"), ("0", "Ninguno") }, A.GetValueOrDefault("widgets", Widgets.PorDefecto(M.Key)), v => Poner("widgets", v))),
                Fila("Ciudad del clima", CampoCiudad(), "Para los widgets y la barra de Fedora. Común a todos los modos."),
                Fila("Iconos del sistema", Opciones(new[] { ("fedora", "Papirus"), ("gaming", "Candy (neón)"), ("potencia", "WhiteSur"), ("0", "Windows") }, A.GetValueOrDefault("iconos-sistema", IconosSistema.PorDefecto(M.Key)), v => Poner("iconos-sistema", v))),
                Fila("Iconos del escritorio", SiNo("iconos", "Visibles", "Ocultos")),
                Fila("Tamaño de los iconos", Opciones(new[] { ("32", "Pequeños"), ("48", "Medianos"), ("64", "Grandes"), ("96", "Enormes") }, A.GetValueOrDefault("tamano-iconos", ""), v => Poner("tamano-iconos", v))),
                Fila("Carpeta del escritorio", new TextBlock { Text = A.GetValueOrDefault("escritorio", "(la de Windows)"), FontSize = 12, Foreground = B(Dim), TextWrapping = TextWrapping.Wrap })));
            return sp;
        }

        // ---------------------------------------------------------------- Windhawk
        private UIElement Windhawk()
        {
            var sp = new StackPanel();
            var filas = new List<UIElement>();
            foreach (var s in _engine.Settings.OfType<WindhawkModSetting>())
            {
                if (s.Id == "wh:live-video-wallpaper") continue;   // se elige en Fondos
                var d = Leer(s.Id);
                bool on = d?.GetValueOrDefault("activo") == "1";
                var fila = new StackPanel();
                var id = s.Id;
                fila.Children.Add(Opciones(new[] { ("1", "Encendido"), ("0", "Apagado"), ("", "Como Windows") }, d == null ? "" : on ? "1" : "0", v =>
                {
                    if (v == "") { A.Remove(id); return; }
                    var dd = Leer(id) ?? new();
                    dd["activo"] = v;
                    A[id] = JsonSerializer.Serialize(dd);
                }));
                var temas = TemasDe(id[3..]);
                if (on && temas.Count > 0)
                {
                    var cb = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
                    foreach (var t in temas) cb.Items.Add(t);
                    cb.SelectedItem = d!.GetValueOrDefault("theme");
                    cb.SelectionChanged += (_, _) =>
                    {
                        var dd = Leer(id) ?? new();
                        dd["theme"] = (string)cb.SelectedItem;
                        A[id] = JsonSerializer.Serialize(dd);
                        Cambio();
                    };
                    fila.Children.Add(cb);
                }
                filas.Add(Fila(s.Nombre, fila, id[3..]));
            }
            sp.Children.Add(Tarjeta("Mods de Windhawk", "Qué mods se encienden en este modo y, en los de estilo, qué tema usan. \"Como Windows\" = como estaban antes de okla.", filas.ToArray()));
            return sp;
        }

        private Dictionary<string, string>? Leer(string id)
        {
            if (!A.TryGetValue(id, out var v)) return null;
            try { return JsonSerializer.Deserialize<Dictionary<string, string>>(v); } catch { return null; }
        }

        private static readonly Dictionary<string, List<string>> _temas = new();

        /// <summary>Temas que ofrece un mod de estilo (opciones de "theme" en su código fuente).</summary>
        private static List<string> TemasDe(string mod)
        {
            if (_temas.TryGetValue(mod, out var hecho)) return hecho;
            var res = new List<string>();
            try
            {
                var src = System.IO.Path.Combine(@"C:\ProgramData\Windhawk\ModsSource", mod + ".wh.cpp");
                if (File.Exists(src))
                {
                    var lines = File.ReadAllLines(src);
                    int i = Array.FindIndex(lines, l => l.TrimEnd() == "- theme:" || l.StartsWith("- theme: "));
                    if (i >= 0)
                        for (int j = i + 1; j < lines.Length && !Regex.IsMatch(lines[j], @"^- \w"); j++)
                        {
                            var m = Regex.Match(lines[j], @"^\s+- ""?([^"":]+)""?:");
                            if (m.Success && lines[j].StartsWith("  - ")) res.Add(m.Groups[1].Value.Trim());
                        }
                }
            }
            catch (Exception ex) { Log.Error($"Temas de {mod}: {ex.Message}"); }
            return _temas[mod] = res;
        }

        // ---------------------------------------------------------------- Fondos
        private UIElement FondosTab()
        {
            var sp = new StackPanel();
            var actual = Actual();
            var wp = new WrapPanel();
            foreach (var f in MiniHub.Fondos(M.Key))
            {
                bool sel = string.Equals(f.Archivo, actual, StringComparison.OrdinalIgnoreCase);
                var img = new Border { Width = 200, Height = 112, CornerRadius = new CornerRadius(8), Background = new ImageBrush(Mini(f.Miniatura)) { Stretch = Stretch.UniformToFill } };
                var g = new Grid { Margin = new Thickness(0, 0, 14, 14), Cursor = Cursors.Hand, ToolTip = f.Nombre };
                g.Children.Add(img);
                if (f.EsVideo)
                    g.Children.Add(new Border { Background = B(Color.FromArgb(0xA0, 0, 0, 0)), CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6),
                                                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                                                Child = new TextBlock { Text = "▶ video", FontSize = 10, Foreground = Brushes.White } });
                g.Children.Add(new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(sel ? 3 : 0), BorderBrush = B(Acento), Margin = new Thickness(-3) });
                var ff = f;
                g.MouseLeftButtonUp += (_, _) => { ElegirFondo(ff); Cambio(); Pintar(); };
                wp.Children.Add(g);
            }
            var agregar = Boton("Añadir imagen o video…", false, Agregar);
            agregar.HorizontalAlignment = HorizontalAlignment.Left; agregar.Margin = new Thickness(0, 4, 0, 0);
            sp.Children.Add(Tarjeta("Fondo por defecto", "El que pone el modo al activarse (desde el mini hub se cambia al momento). Los videos se ven a 30 fps; con batería, más lentos.",
                new Border { Margin = new Thickness(0, 16, 0, 0), Child = wp }, agregar));
            return sp;
        }

        private string? Actual() => MiniHub.Actual(M);

        private void ElegirFondo(Fondo f)
        {
            if (f.EsVideo) A["wh:live-video-wallpaper"] = ModeStore.VideoSetting(f.Archivo);
            else { A["wh:live-video-wallpaper"] = ModeStore.VideoOff(); A["fondo"] = "img:" + f.Archivo; }
        }

        private void Agregar()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Añadir fondo a " + M.Nombre, Multiselect = true,
                                                           Filter = "Imágenes y videos|*.jpg;*.jpeg;*.png;*.mp4|Imágenes|*.jpg;*.jpeg;*.png|Videos MP4|*.mp4" };
            if (dlg.ShowDialog(this) != true) return;
            var carpeta = ModeStore.Carpeta(M.Key);
            int ok = 0;
            foreach (var src in dlg.FileNames)
            {
                try
                {
                    bool video = src.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
                    var dir = System.IO.Path.Combine(Paths.Recursos, video ? "videos" : "fondos", carpeta);
                    Directory.CreateDirectory(dir);
                    var nombre = Regex.Replace(System.IO.Path.GetFileNameWithoutExtension(src).ToLowerInvariant(), @"[^a-z0-9áéíóúñ]+", "-").Trim('-');
                    var dst = System.IO.Path.Combine(dir, nombre + System.IO.Path.GetExtension(src).ToLowerInvariant());
                    File.Copy(src, dst, true);
                    // Los videos necesitan su póster .jpg (miniatura y pantalla de carga).
                    if (video && !Poster(dst)) { File.Delete(dst); throw new InvalidOperationException("no pude sacar la miniatura del video (falta ffmpeg)"); }
                    ok++;
                }
                catch (Exception ex) { MessageBox.Show(this, $"No pude añadir {System.IO.Path.GetFileName(src)}:\n{ex.Message}", "okla · editor", MessageBoxButton.OK, MessageBoxImage.Warning); }
            }
            if (ok > 0) { _estado.Text = $"{ok} fondo(s) añadidos a {M.Nombre}"; Pintar(); }
        }

        private static bool Poster(string video)
        {
            var ff = Directory.GetFiles(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Packages"),
                                        "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (ff == null) return false;
            var jpg = System.IO.Path.ChangeExtension(video, ".jpg");
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ff, $"-hide_banner -loglevel error -y -ss 1 -i \"{video}\" -frames:v 1 -q:v 2 \"{jpg}\"")
            { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(20000);
            return File.Exists(jpg);
        }

        private static readonly Dictionary<string, BitmapImage> _minis = new(StringComparer.OrdinalIgnoreCase);
        private static BitmapImage Mini(string f)
        {
            if (_minis.TryGetValue(f, out var b)) return b;
            var bi = new BitmapImage(); bi.BeginInit(); bi.UriSource = new Uri(f); bi.DecodePixelWidth = 400; bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
            return _minis[f] = bi;
        }

        // ---------------------------------------------------------------- Gaming
        private UIElement Gaming()
        {
            var sp = new StackPanel();

            // Apps que se congelan
            var apps = AppPause.Lista();
            var lista = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            foreach (var a in apps)
            {
                var chip = new Border { CornerRadius = new CornerRadius(15), Padding = new Thickness(14, 6, 10, 6), Margin = new Thickness(0, 0, 8, 8), Background = B(Bg), BorderBrush = B(Line), BorderThickness = new Thickness(1) };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock { Text = a, FontSize = 12, Foreground = B(Fg), VerticalAlignment = VerticalAlignment.Center });
                var x = new TextBlock { Text = "  ✕", FontSize = 12, Foreground = B(Dim), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
                var aa = a;
                x.MouseLeftButtonUp += (_, _) => { AppPause.GuardarLista(apps.Where(y => y != aa)); Pintar(); };
                row.Children.Add(x); chip.Child = row; lista.Children.Add(chip);
            }
            var nueva = Texto("", _ => { }, 220);
            var add = Boton("Añadir", false, () =>
            {
                var n = nueva.Text.Trim();
                if (n.Length == 0) return;
                if (AppPause.EsProhibido(n.Replace(".exe", ""))) { MessageBox.Show(this, $"{n} no se puede congelar (lo necesitan Windows, los juegos o okla).", "okla"); return; }
                AppPause.GuardarLista(apps.Append(n)); Pintar();
            });
            var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            addRow.Children.Add(nueva); addRow.Children.Add(add);
            sp.Children.Add(Tarjeta("Apps que se congelan al jugar", "Nombre del proceso (como en el Administrador de tareas, sin .exe). Se guardan al momento.", lista, addRow));

            // Juegos añadidos a mano
            var juegos = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (var g in GameLibrary.LoadManual())
            {
                var f = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var quitar = Boton("Quitar", false, () => { GameLibrary.RemoveManual(g.Id); Pintar(); });
                DockPanel.SetDock(quitar, Dock.Right); f.Children.Add(quitar);
                var t = new StackPanel();
                t.Children.Add(new TextBlock { Text = g.Nombre, FontSize = 13, Foreground = B(Fg) });
                t.Children.Add(new TextBlock { Text = g.Lanzar, FontSize = 11, Foreground = B(Faint), TextTrimming = TextTrimming.CharacterEllipsis });
                f.Children.Add(t);
                juegos.Children.Add(f);
            }
            if (juegos.Children.Count == 0) juegos.Children.Add(new TextBlock { Text = "Ninguno. Steam, Epic y TLauncher se detectan solos.", FontSize = 12, Foreground = B(Faint) });
            var addJuego = Boton("Añadir juego (.exe)…", false, () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Añadir juego", Filter = "Programas|*.exe" };
                if (dlg.ShowDialog(this) == true) { GameLibrary.AddManual(dlg.FileName); Pintar(); }
            });
            addJuego.HorizontalAlignment = HorizontalAlignment.Left; addJuego.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(Tarjeta("Juegos añadidos a mano", "Aparecen en el hub y, al abrirse, activan Gaming. Se guardan al momento.", juegos, addJuego));
            return sp;
        }

        // ---------------------------------------------------------------- acciones
        private void Fabrica()
        {
            var r = MessageBox.Show(this, $"¿Devolver {M.Nombre} a los valores de fábrica? Se pierden sus cambios (fondo, colores, mods…).", "okla · editor",
                                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) return;
            var def = ModeStore.Defaults().First(x => x.Key == _sel);
            var i = _modes.FindIndex(x => x.Key == _sel);
            _modes[i] = def;
            Cambio(); Pintar();
        }

        private void Guardar(string? probar)
        {
            _guardar(Clonar(_modes), probar);
            _sucio = false;
            _estado.Text = probar == null ? $"Guardado · {DateTime.Now:HH:mm}" : $"Guardado y probando {M.Nombre}…";
            _estado.Foreground = B(C("#a3be8c"));
        }
    }
}
