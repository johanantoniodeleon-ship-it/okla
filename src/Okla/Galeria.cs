using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Okla
{
    /// <summary>
    /// Galería de fondos en línea: busca en webs gratuitas y abiertas, y baja el elegido a la carpeta del modo.
    ///   Wallhaven   → fondos de pantalla (solo los aptos para todo público). Derechos de sus autores: uso personal.
    ///   Openverse   → imágenes con licencia Creative Commons (proyecto de código abierto de WordPress).
    ///   Wikimedia   → Wikimedia Commons, imágenes libres de 1900 px o más.
    ///   NASA        → archivo de imágenes de la NASA (dominio público).
    ///   Videos      → fondos animados de motionbgs (derechos de sus autores: uso personal).
    /// Cada fondo bajado se apunta con su autor, licencia y página en recursos\creditos.json.
    /// </summary>
    internal sealed class Galeria : Window
    {
        internal sealed record Resultado(string Titulo, string Miniatura, string Descarga, string Pagina, string Autor, string Licencia, bool Video, string Id, string Fuente);

        private static readonly HttpClient Http = Crear();
        private static HttpClient Crear()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) okla-desktop/0.4");
            return h;
        }

        private static readonly Color Bg = C("#16181d"), Panel = C("#1f2229"), Line = C("#2e323b"), Fg = C("#e6e6ea"), Dim = C("#8a8f9a"), Acento = C("#88c0d0");
        private static Color C(string h) => (Color)ColorConverter.ConvertFromString(h);
        private static SolidColorBrush B(Color c) => new(c);

        private readonly List<ModeDef> _modos;
        private readonly Func<string?> _modoActual;
        private readonly Action<ModeDef, string, bool, bool> _poner;   // modo, archivo, es video, ponerlo ya
        private string _fuente = "wallhaven", _modo;
        private int _pagina = 1;
        private readonly TextBox _buscar;
        private readonly WrapPanel _rejilla = new() { HorizontalAlignment = HorizontalAlignment.Center };
        private readonly WrapPanel _fuentes = new(), _destinos = new();
        private readonly TextBlock _estado, _detalle;
        private readonly Button _ponerBtn, _masBtn;
        private readonly CheckBox _ya;
        private Resultado? _sel;
        private Border? _selBorde;
        private CancellationTokenSource? _cts;
        private readonly SemaphoreSlim _mini = new(6);

        public Galeria(List<ModeDef> modos, Func<string?> modoActual, Action<ModeDef, string, bool, bool> poner)
        {
            _modos = modos.Where(m => m.Activo && m.Key != "B" && ModeStore.Carpeta(m.Key) != "").ToList();
            _modoActual = modoActual; _poner = poner;
            _modo = _modos.Any(m => m.Key == modoActual()) ? modoActual()! : _modos.FirstOrDefault()?.Key ?? "A";

            Title = "okla · Explorar fondos"; Width = 1180; Height = 800; MinWidth = 760; MinHeight = 520;
            Background = B(Bg); Foreground = B(Fg); FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            try { Icon = IconosModos.Wpf("okla"); } catch { }

            var raiz = new DockPanel { Margin = new Thickness(22) };

            // --- cabecera: título, buscador, fuentes ---
            var cab = new StackPanel();
            cab.Children.Add(new TextBlock { Text = "Explorar fondos", FontSize = 24, FontWeight = FontWeights.SemiBold });
            cab.Children.Add(new TextBlock { Text = "Busca en webs gratuitas y abiertas. El fondo se guarda en la carpeta del modo y queda en el mini hub (Ctrl+Alt+H).",
                                             Foreground = B(Dim), Margin = new Thickness(0, 2, 0, 12), TextWrapping = TextWrapping.Wrap });
            var fila = new DockPanel();
            var ir = Boton("Buscar", true); ir.Click += (_, _) => Buscar(true);
            DockPanel.SetDock(ir, Dock.Right); fila.Children.Add(ir);
            _buscar = new TextBox { FontSize = 14, Padding = new Thickness(10, 7, 10, 7), Background = B(Panel), Foreground = B(Fg), BorderBrush = B(Line), CaretBrush = B(Fg),
                                    Margin = new Thickness(0, 0, 10, 0) };
            _buscar.KeyDown += (_, e) => { if (e.Key == Key.Enter) Buscar(true); };
            fila.Children.Add(_buscar);
            cab.Children.Add(fila);
            _fuentes.Margin = new Thickness(0, 12, 0, 4);
            cab.Children.Add(_fuentes);
            DockPanel.SetDock(cab, Dock.Top); raiz.Children.Add(cab);

            // --- pie: destino y botón de poner ---
            var pie = new Border { Background = B(Panel), CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 12, 0, 0) };
            var pd = new DockPanel();
            _ponerBtn = Boton("Bajar y poner", true); _ponerBtn.IsEnabled = false; _ponerBtn.Click += async (_, _) => await Poner();
            DockPanel.SetDock(_ponerBtn, Dock.Right); pd.Children.Add(_ponerBtn);
            _ya = new CheckBox { Content = "Ponerlo ya", IsChecked = true, Foreground = B(Fg), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 14, 0) };
            DockPanel.SetDock(_ya, Dock.Right); pd.Children.Add(_ya);
            var izq = new StackPanel();
            var dest = new StackPanel { Orientation = Orientation.Horizontal };
            dest.Children.Add(new TextBlock { Text = "Para el modo:", Foreground = B(Dim), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            dest.Children.Add(_destinos);
            izq.Children.Add(dest);
            _detalle = new TextBlock { Foreground = B(Dim), FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            izq.Children.Add(_detalle);
            pd.Children.Add(izq);
            pie.Child = pd;
            DockPanel.SetDock(pie, Dock.Bottom); raiz.Children.Add(pie);

            // --- estado y rejilla ---
            _estado = new TextBlock { Foreground = B(Dim), Margin = new Thickness(0, 6, 0, 8) };
            DockPanel.SetDock(_estado, Dock.Top); raiz.Children.Add(_estado);
            var cont = new StackPanel();
            cont.Children.Add(_rejilla);
            _masBtn = Boton("Cargar más", false); _masBtn.HorizontalAlignment = HorizontalAlignment.Center; _masBtn.Margin = new Thickness(0, 10, 0, 10);
            _masBtn.Visibility = Visibility.Collapsed; _masBtn.Click += (_, _) => Buscar(false);
            cont.Children.Add(_masBtn);
            raiz.Children.Add(new ScrollViewer { Content = cont, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            Content = raiz;
            PintarFuentes(); PintarDestinos();
            Loaded += (_, _) => Buscar(true);
            Closed += (_, _) => _cts?.Cancel();
        }

        private static Button Boton(string texto, bool principal)
        {
            var b = new Button { Content = texto, Padding = new Thickness(18, 7, 18, 7), FontSize = 13 };
            InstaladorWindow.Estilo(b, !principal);
            return b;
        }

        private static readonly (string id, string nombre, string ayuda)[] Fuentes =
        {
            ("wallhaven", "Wallhaven", "Fondos de pantalla de la comunidad (solo aptos para todo público). Derechos de sus autores: uso personal."),
            ("openverse", "Openverse", "Imágenes con licencia Creative Commons (proyecto abierto de WordPress)."),
            ("commons", "Wikimedia", "Wikimedia Commons: imágenes libres de 1900 px o más."),
            ("nasa", "NASA", "Archivo de imágenes de la NASA, dominio público."),
            ("videos", "Videos", "Fondos animados de motionbgs. Derechos de sus autores: uso personal."),
        };

        private void PintarFuentes()
        {
            _fuentes.Children.Clear();
            foreach (var (id, nombre, ayuda) in Fuentes)
                _fuentes.Children.Add(Chip(nombre, id == _fuente, () => { _fuente = id; PintarFuentes(); Buscar(true); }, ayuda));
        }

        /// <summary>Para pruebas: abre la galería con una fuente elegida ("videos", "nasa"…).</summary>
        internal void ProbarFuente(string fuente) { _fuente = fuente; PintarFuentes(); Buscar(true); }

        private void PintarDestinos()
        {
            _destinos.Children.Clear();
            foreach (var m in _modos)
                _destinos.Children.Add(Chip(m.Nombre, m.Key == _modo, () => { _modo = m.Key; PintarDestinos(); }));
        }

        private FrameworkElement Chip(string texto, bool on, Action click, string? ayuda = null)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(15), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 6), Cursor = Cursors.Hand,
                Background = on ? B(Acento) : B(Panel), BorderBrush = B(on ? Acento : Line), BorderThickness = new Thickness(1), ToolTip = ayuda,
                Child = new TextBlock { Text = texto, FontSize = 12, Foreground = on ? B(C("#10141a")) : B(Dim), FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal },
            };
            b.MouseLeftButtonUp += (_, _) => click();
            return b;
        }

        // ---------------- búsqueda ----------------
        private async void Buscar(bool nueva)
        {
            _cts?.Cancel(); _cts = new CancellationTokenSource(); var ct = _cts.Token;
            if (nueva) { _pagina = 1; _rejilla.Children.Clear(); _sel = null; _selBorde = null; _ponerBtn.IsEnabled = false; _detalle.Text = ""; }
            else _pagina++;
            var ayuda = Fuentes.First(f => f.id == _fuente).ayuda;
            _estado.Text = "Buscando…  " + ayuda;
            _masBtn.Visibility = Visibility.Collapsed;
            List<Resultado> r;
            try { r = await Pedir(_fuente, _buscar.Text.Trim(), _pagina, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }   // otra búsqueda la reemplazó
            catch (Exception ex)
            {
                Log.Error($"Galería ({_fuente}): {ex.Message}");
                var nombre = Fuentes.First(f => f.id == _fuente).nombre;
                _estado.Text = ex switch
                {
                    HttpRequestException { StatusCode: { } c } when (int)c >= 500 => $"{nombre} no responde ahora mismo (error {(int)c} de su servidor). Prueba otra fuente o más tarde.",
                    HttpRequestException { StatusCode: { } c } => $"{nombre} rechazó la búsqueda (error {(int)c}). Prueba otra palabra u otra fuente.",
                    TaskCanceledException => $"{nombre} tarda demasiado en responder. Prueba otra fuente o más tarde.",
                    _ => "No se pudo buscar (¿sin internet?): " + ex.Message,
                };
                return;
            }
            if (ct.IsCancellationRequested) return;
            foreach (var x in r) _rejilla.Children.Add(Tarjeta(x, ct));
            _estado.Text = _rejilla.Children.Count == 0 ? "Nada por aquí. Prueba otra palabra (mejor en inglés: mountains, city night, anime…)." : ayuda;
            _masBtn.Visibility = r.Count >= 12 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static async Task<List<Resultado>> Pedir(string fuente, string q, int pagina, CancellationToken ct)
        {
            var list = new List<Resultado>();
            string E(string s) => Uri.EscapeDataString(s);
            switch (fuente)
            {
                case "wallhaven":
                {
                    var url = $"https://wallhaven.cc/api/v1/search?purity=100&categories=111&atleast=1920x1080&ratios=16x9,16x10,21x9&page={pagina}" +
                              (q == "" ? "&sorting=toplist&topRange=1M" : "&sorting=relevance&q=" + E(q));
                    var j = JsonNode.Parse(await Http.GetStringAsync(url, ct))!;
                    foreach (var d in j["data"]!.AsArray())
                        list.Add(new((string)d!["resolution"]! + " · " + (string)d["category"]!, (string)d["thumbs"]!["large"]!, (string)d["path"]!, (string)d["url"]!,
                                     "", "Derechos de su autor · uso personal", false, (string)d["id"]!, "Wallhaven"));
                    break;
                }
                case "openverse":
                {
                    var url = $"https://api.openverse.org/v1/images/?q={E(q == "" ? "landscape" : q)}&aspect_ratio=wide&size=large&page_size=20&page={pagina}";
                    var j = JsonNode.Parse(await Http.GetStringAsync(url, ct))!;
                    foreach (var d in j["results"]!.AsArray())
                    {
                        var lic = ((string?)d!["license"] ?? "").ToUpperInvariant();
                        var ver = (string?)d["license_version"];
                        list.Add(new((string?)d["title"] ?? "", (string?)d["thumbnail"] ?? (string)d["url"]!, (string)d["url"]!, (string?)d["foreign_landing_url"] ?? "",
                                     (string?)d["creator"] ?? "", lic == "CC0" || lic == "PDM" ? lic : $"CC {lic} {ver}".Trim(), false, (string)d["id"]!, "Openverse"));
                    }
                    break;
                }
                case "commons":
                {
                    var busca = (q == "" ? "landscape" : q) + " filew:>1900 filetype:bitmap";
                    var url = "https://commons.wikimedia.org/w/api.php?action=query&format=json&generator=search&gsrnamespace=6&gsrlimit=24" +
                              $"&gsroffset={(pagina - 1) * 24}&gsrsearch={E(busca)}&prop=imageinfo&iiprop=url|size|extmetadata&iiurlwidth=3840&iiextmetadatafilter=Artist|LicenseShortName";
                    var j = JsonNode.Parse(await Http.GetStringAsync(url, ct))!;
                    var pages = j["query"]?["pages"]?.AsObject();
                    if (pages == null) break;
                    foreach (var (_, p) in pages.OrderBy(kv => (int?)kv.Value!["index"] ?? 0))
                    {
                        var ii = p!["imageinfo"]?[0];
                        if (ii == null || (int)ii["width"]! < (int)ii["height"]!) continue;   // solo apaisadas
                        var grande = (string?)ii["thumburl"] ?? (string)ii["url"]!;
                        var mini = grande.Contains("/3840px-") ? grande.Replace("/3840px-", "/500px-") : grande;
                        var meta = ii["extmetadata"];
                        var autor = Regex.Replace((string?)meta?["Artist"]?["value"] ?? "", "<.*?>", "").Trim();
                        var titulo = ((string)p["title"]!).Replace("File:", "");
                        list.Add(new(Path.GetFileNameWithoutExtension(titulo), mini, grande, (string?)ii["descriptionurl"] ?? "", autor,
                                     (string?)meta?["LicenseShortName"]?["value"] ?? "", false, ((int)p["pageid"]!).ToString(), "Wikimedia Commons"));
                    }
                    break;
                }
                case "nasa":
                {
                    var url = $"https://images-api.nasa.gov/search?media_type=image&page_size=24&page={pagina}&q={E(q == "" ? "galaxy nebula" : q)}";
                    var j = JsonNode.Parse(await Http.GetStringAsync(url, ct))!;
                    foreach (var it in j["collection"]!["items"]!.AsArray())
                    {
                        var d = it!["data"]?[0]; var thumb = (string?)it["links"]?[0]?["href"];
                        if (d == null || thumb == null) continue;
                        var id = (string)d["nasa_id"]!;
                        list.Add(new((string?)d["title"] ?? id, thumb, thumb.Replace("~thumb", "~orig"), "https://images.nasa.gov/details/" + id,
                                     (string?)d["center"] ?? "NASA", "Dominio público (NASA)", false, id, "NASA"));
                    }
                    break;
                }
                case "videos":
                {
                    if (pagina > 1 && q == "") break;
                    var url = q == "" ? "https://motionbgs.com/" : $"https://motionbgs.com/search?q={E(q)}";
                    if (pagina > 1) url = (await Http.GetAsync(url, ct)).RequestMessage!.RequestUri!.ToString().TrimEnd('/') + $"/{pagina}/";
                    var html = await Http.GetStringAsync(url, ct);
                    var vistos = new HashSet<string>();
                    foreach (Match m in Regex.Matches(html, "<img alt=\"([^\"]*?)(?: live wallpaper)?\"[^>]*?src=(/i/c/\\d+x\\d+/media/(\\d+)/[^\\s>]+)"))
                    {
                        var id = m.Groups[3].Value;
                        if (!vistos.Add(id)) continue;
                        var nombre = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                        list.Add(new(nombre, "https://motionbgs.com" + m.Groups[2].Value, id, "https://motionbgs.com/search?q=" + E(nombre), "", "Derechos de su autor · uso personal", true, id, "motionbgs"));
                    }
                    break;
                }
            }
            return list;
        }

        private FrameworkElement Tarjeta(Resultado r, CancellationToken ct)
        {
            var img = new Image { Stretch = Stretch.UniformToFill, Width = 262, Height = 148 };
            var sp = new StackPanel();
            sp.Children.Add(new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = img, Background = B(Panel) });
            var pie = string.IsNullOrEmpty(r.Autor) ? r.Licencia : $"{r.Autor} · {r.Licencia}";
            sp.Children.Add(new TextBlock { Text = (r.Video ? "▶ " : "") + r.Titulo, FontSize = 12, Margin = new Thickness(2, 6, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis, Width = 258 });
            sp.Children.Add(new TextBlock { Text = pie, FontSize = 11, Foreground = B(Dim), Margin = new Thickness(2, 1, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis, Width = 258 });
            var borde = new Border { Child = sp, Padding = new Thickness(6), Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(14), Cursor = Cursors.Hand,
                                     BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, ToolTip = r.Titulo };
            borde.MouseLeftButtonUp += (_, _) =>
            {
                if (_selBorde != null) _selBorde.BorderBrush = Brushes.Transparent;
                _sel = r; _selBorde = borde; borde.BorderBrush = B(Acento);
                _ponerBtn.IsEnabled = true;
                _detalle.Text = $"{r.Fuente} · {r.Titulo}" + (r.Autor != "" ? $" · {r.Autor}" : "") + $" · {r.Licencia}";
            };
            borde.MouseRightButtonUp += (_, _) => { if (r.Pagina != "") try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.Pagina) { UseShellExecute = true }); } catch { } };
            _ = CargarMiniatura(img, r.Miniatura, ct);
            return borde;
        }

        private async Task CargarMiniatura(Image img, string url, CancellationToken ct)
        {
            await _mini.WaitAsync(ct);
            try
            {
                var bytes = await Http.GetByteArrayAsync(url, ct);
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.DecodePixelWidth = 420; bmp.StreamSource = new MemoryStream(bytes); bmp.EndInit(); bmp.Freeze();
                img.Source = bmp;
            }
            catch { }
            finally { _mini.Release(); }
        }

        // ---------------- bajar y poner ----------------
        private async Task Poner()
        {
            if (_sel is not { } r) return;
            var m = _modos.FirstOrDefault(x => x.Key == _modo);
            if (m == null) return;
            _ponerBtn.IsEnabled = false;
            var carpeta = ModeStore.Carpeta(m.Key);
            try
            {
                string destino;
                if (r.Video)
                {
                    var dir = Path.Combine(Paths.Recursos, "videos", carpeta); Directory.CreateDirectory(dir);
                    destino = Path.Combine(dir, Nombre(r.Titulo, r.Id) + ".mp4");
                    if (!File.Exists(destino))
                    {
                        void Prog(string t, double _) => Dispatcher.Invoke(() => _estado.Text = t);
                        try { await Instalador.Descargar($"https://motionbgs.com/dl/4k/{r.Id}", destino, Prog, "Bajando video", CancellationToken.None); }
                        catch (HttpRequestException) { await Instalador.Descargar($"https://motionbgs.com/dl/hd/{r.Id}", destino, Prog, "Bajando video", CancellationToken.None); }
                        _estado.Text = "Preparando la imagen fija del video…";
                        await Poster(destino);
                    }
                }
                else
                {
                    var dir = Path.Combine(Paths.Recursos, "fondos", carpeta); Directory.CreateDirectory(dir);
                    var ext = Path.GetExtension(new Uri(r.Descarga).AbsolutePath).ToLowerInvariant();
                    if (ext is not (".jpg" or ".jpeg" or ".png")) ext = ".jpg";
                    destino = Path.Combine(dir, Nombre(r.Titulo, r.Id) + (ext == ".jpeg" ? ".jpg" : ext));
                    if (!File.Exists(destino))
                        await Instalador.Descargar(r.Descarga, destino, (t, _) => Dispatcher.Invoke(() => _estado.Text = t), "Bajando", CancellationToken.None);
                }
                Creditos(destino, r);
                Log.Info($"Galería: {r.Fuente} {r.Id} -> {destino}");
                bool ya = _ya.IsChecked == true;
                _poner(m, destino, r.Video, ya);
                _estado.Text = ya ? $"Listo: puesto en {m.Nombre}." : $"Listo: guardado en {m.Nombre} (elígelo cuando quieras en el mini hub, Ctrl+Alt+H).";
            }
            catch (Exception ex)
            {
                Log.Error($"Galería, bajar {r.Fuente} {r.Id}: {ex.Message}");
                _estado.Text = "No se pudo bajar: " + ex.Message;
            }
            finally { _ponerBtn.IsEnabled = true; }
        }

        private static async Task Poster(string mp4)
        {
            var ff = Adaptador.FFmpeg();
            if (ff == null) return;
            var jpg = Path.ChangeExtension(mp4, ".jpg");
            using var pr = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ff, $"-hide_banner -loglevel error -y -ss 1 -i \"{mp4}\" -frames:v 1 -q:v 2 \"{jpg}\"")
            { UseShellExecute = false, CreateNoWindow = true })!;
            await pr.WaitForExitAsync();
        }

        /// <summary>Nombre de archivo legible y sin caracteres raros: "titulo-del-fondo-id".</summary>
        private static string Nombre(string titulo, string id)
        {
            var s = titulo.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var ch in s)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                sb.Append(char.IsLetterOrDigit(ch) && ch < 128 ? char.ToLowerInvariant(ch) : '-');
            }
            var n = Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
            if (n.Length > 40) n = n[..40].TrimEnd('-');
            var limpio = Regex.Replace(id, "[^A-Za-z0-9]", "");
            if (limpio.Length > 12) limpio = limpio[..12];
            return (n == "" ? "fondo" : n) + "-" + limpio;
        }

        private static void Creditos(string archivo, Resultado r)
        {
            try
            {
                var f = Path.Combine(Paths.Recursos, "creditos.json");
                var lista = File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f))?.AsArray() ?? new JsonArray() : new JsonArray();
                lista.Add(new JsonObject
                {
                    ["archivo"] = Path.GetRelativePath(Paths.Recursos, archivo), ["fuente"] = r.Fuente, ["titulo"] = r.Titulo,
                    ["autor"] = r.Autor, ["licencia"] = r.Licencia, ["pagina"] = r.Pagina, ["fecha"] = DateTime.Now.ToString("yyyy-MM-dd"),
                });
                File.WriteAllText(f, lista.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
            catch (Exception ex) { Log.Error("Créditos: " + ex.Message); }
        }
    }
}
