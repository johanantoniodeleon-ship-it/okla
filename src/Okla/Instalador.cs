using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Okla
{
    /// <summary>
    /// Primer arranque de okla en una PC nueva (o "okla.exe --instalar"). Deja todo listo sin pasos a mano:
    /// ffmpeg, Windhawk y sus mods, los fondos del catálogo, los escritorios de cada modo y la tarea que abre
    /// okla con permisos al iniciar sesión. Necesita administrador (Windhawk, registro de Windhawk, tarea elevada).
    /// Cada paso se salta si ya está hecho, así que se puede repetir sin miedo.
    /// </summary>
    internal static class Instalador
    {
        public const string Repo = "johanantoniodeleon-ship-it/okla";   // usuario/repositorio en GitHub
        private static string MarcaFile => Path.Combine(Paths.Config, "instalado.json");
        public static bool Hecho => File.Exists(MarcaFile);

        /// <summary>Modos que el usuario eligió en la pantalla de personalización (null = todos).</summary>
        public static HashSet<string>? ModosElegidos;

        /// <summary>Carpeta de recursos -> modo (para bajar solo los fondos de los modos elegidos).</summary>
        private static string ModoDeCarpeta(string carpeta) => carpeta switch
        { "gaming" => "J", "fedora" => "A", "potencia" => "G", "presentacion" => "P", "bateria" => "B", _ => "" };

        private static string AppDir => AppContext.BaseDirectory;
        private static string WindhawkDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windhawk");

        public static bool EsAdmin()
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>Vuelve a abrir okla como administrador (una ventana de UAC) para instalar.</summary>
        public static bool RelanzarElevado(string args)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" });
                return true;
            }
            catch (Exception ex) { Log.Error("No se pudo pedir permisos: " + ex.Message); return false; }
        }

        /// <summary>Mods de Windhawk que usa okla (los ids de los ajustes "wh:...").</summary>
        public static IEnumerable<string> ModsNecesarios() =>
            new Engine().Settings.OfType<WindhawkModSetting>().Select(s => s.Id[3..]).Distinct();

        public static void Marcar(string version)
        {
            Directory.CreateDirectory(Paths.Config);
            File.WriteAllText(MarcaFile, JsonSerializer.Serialize(new { version, fecha = DateTime.Now, maquina = Environment.MachineName }));
        }

        // ---------------- pasos ----------------
        internal sealed record Paso(string Nombre, Func<Action<string, double>, CancellationToken, Task> Hacer);

        private static readonly HttpClient Http = CrearHttp();
        private static HttpClient CrearHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("okla-instalador");
            return h;
        }

        public static List<Paso> Pasos() => new()
        {
            new("Preparar carpetas", (p, ct) =>
            {
                foreach (var d in new[] { Paths.Config, Paths.Logs, Paths.Recursos, Path.Combine(Paths.Root, "tools") })
                    Directory.CreateDirectory(d);
                return Task.CompletedTask;
            }),
            new("ffmpeg (convierte los fondos a tu pantalla)", PasoFFmpeg),
            new("Windhawk (motor de los mods de Windows)", PasoWindhawk),
            new("Mods de Windhawk", PasoMods),
            new("Fondos animados", PasoFondos),
            new("Escritorios de cada modo", PasoEscritorios),
            new("Abrir okla al iniciar sesión", PasoTarea),
        };

        private static async Task Descargar(string url, string destino, Action<string, double> p, string texto, CancellationToken ct)
        {
            using var r = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            r.EnsureSuccessStatusCode();
            long total = r.Content.Headers.ContentLength ?? 0, hecho = 0;
            var tmp = destino + ".part";
            await using (var src = await r.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[1 << 16]; int n; var t = Stopwatch.StartNew();
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    hecho += n;
                    if (t.ElapsedMilliseconds > 250) { t.Restart(); p($"{texto} · {hecho / 1048576.0:0} de {total / 1048576.0:0} MB", total > 0 ? (double)hecho / total : -1); }
                }
            }
            File.Move(tmp, destino, true);
        }

        private static async Task PasoFFmpeg(Action<string, double> p, CancellationToken ct)
        {
            if (Adaptador.FFmpeg() is string ya) { p("Ya estaba: " + ya, 1); return; }
            var zip = Path.Combine(Path.GetTempPath(), "okla-ffmpeg.zip");
            await Descargar("https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip", zip, p, "Descargando ffmpeg", ct);
            p("Descomprimiendo…", -1);
            using (var z = ZipFile.OpenRead(zip))
            {
                var e = z.Entries.FirstOrDefault(x => x.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("el zip de ffmpeg no trae ffmpeg.exe");
                e.ExtractToFile(Path.Combine(Paths.Root, "tools", "ffmpeg.exe"), true);
            }
            File.Delete(zip);
            Adaptador.OlvidarFFmpeg();
        }

        private static async Task PasoWindhawk(Action<string, double> p, CancellationToken ct)
        {
            if (File.Exists(Path.Combine(WindhawkDir, "windhawk.exe"))) { p("Ya estaba instalado", 1); return; }
            p("Buscando la última versión…", -1);
            var rel = JsonNode.Parse(await Http.GetStringAsync("https://api.github.com/repos/ramensoftware/windhawk/releases/latest", ct))!;
            var url = rel["assets"]!.AsArray().Select(a => (string?)a!["browser_download_url"])
                      .FirstOrDefault(u => u != null && u.EndsWith("_setup.exe", StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException("no encuentro el instalador de Windhawk");
            var exe = Path.Combine(Path.GetTempPath(), "windhawk_setup.exe");
            await Descargar(url, exe, p, "Descargando Windhawk " + (string?)rel["tag_name"], ct);
            p("Instalando Windhawk…", -1);
            using (var pr = Process.Start(new ProcessStartInfo(exe, "/S") { UseShellExecute = false, CreateNoWindow = true })!)
                await pr.WaitForExitAsync(ct);
            File.Delete(exe);
            if (!File.Exists(Path.Combine(WindhawkDir, "windhawk.exe"))) throw new InvalidOperationException("Windhawk no quedó instalado");
        }

        private static async Task PasoMods(Action<string, double> p, CancellationToken ct)
        {
            var mods = ModsNecesarios().Where(m => !File.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Windhawk", "ModsSource", m + ".wh.cpp"))).ToList();
            if (mods.Count == 0) { p("Ya estaban todos", 1); return; }
            var node = Path.Combine(WindhawkDir, "UI", "VSCodium.exe");
            var script = Path.Combine(AppDir, "tools", "wh-install.js");
            if (!File.Exists(node) || !File.Exists(script)) throw new InvalidOperationException("falta Windhawk o el instalador de mods");
            for (int i = 0; i < mods.Count; i++)
            {
                p($"Compilando {mods[i]} ({i + 1} de {mods.Count})", (double)i / mods.Count);
                var psi = new ProcessStartInfo(node, $"\"{script}\" {mods[i]}")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(script)! };
                psi.Environment["ELECTRON_RUN_AS_NODE"] = "1";
                using var pr = Process.Start(psi)!;
                var err = pr.StandardError.ReadToEndAsync();
                await pr.StandardOutput.ReadToEndAsync(ct);
                await pr.WaitForExitAsync(ct);
                if (pr.ExitCode != 0) Log.Error($"Mod {mods[i]}: {(await err).Trim()}");
            }
        }

        private static async Task PasoFondos(Action<string, double> p, CancellationToken ct)
        {
            // Catálogo: el de GitHub si hay internet (puede tener fondos nuevos); si no, el que viene con okla.
            string json;
            try { json = await Http.GetStringAsync($"https://raw.githubusercontent.com/{Repo}/main/catalogo/fondos.json", ct); }
            catch { json = File.ReadAllText(Path.Combine(AppDir, "catalogo", "fondos.json")); }
            var cat = JsonNode.Parse(json)!;
            string plantilla = (string?)cat["descarga"] ?? "https://motionbgs.com/dl/4k/{id}";
            string respaldo = (string?)cat["respaldo"] ?? "https://motionbgs.com/dl/hd/{id}";
            var lista = cat["videos"]!.AsArray().Select(v => (modo: (string)v!["modo"]!, nombre: (string)v["nombre"]!, id: (int)v["motionbgs"]!))
                        .Where(v => ModosElegidos == null || ModosElegidos.Contains(ModoDeCarpeta(v.modo)))
                        .Where(v => !File.Exists(Path.Combine(Paths.Recursos, "videos", v.modo, v.nombre + ".mp4"))).ToList();
            if (lista.Count == 0) { p("Ya estaban todos", 1); return; }

            int listos = 0, fallos = 0;
            var ff = Adaptador.FFmpeg();
            using var gate = new SemaphoreSlim(3);   // 3 descargas a la vez
            await Task.WhenAll(lista.Select(async v =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var dir = Path.Combine(Paths.Recursos, "videos", v.modo);
                    Directory.CreateDirectory(dir);
                    var mp4 = Path.Combine(dir, v.nombre + ".mp4");
                    void Prog(string _, double __) { }
                    try { await Descargar(plantilla.Replace("{id}", v.id.ToString()), mp4, Prog, v.nombre, ct); }
                    catch (HttpRequestException) { await Descargar(respaldo.Replace("{id}", v.id.ToString()), mp4, Prog, v.nombre, ct); }
                    // Imagen fija del video (se ve al cargar y debajo del video).
                    if (ff != null)
                    {
                        var jpg = Path.ChangeExtension(mp4, ".jpg");
                        using var pr = Process.Start(new ProcessStartInfo(ff, $"-hide_banner -loglevel error -y -ss 1 -i \"{mp4}\" -frames:v 1 -q:v 2 \"{jpg}\"")
                        { UseShellExecute = false, CreateNoWindow = true })!;
                        await pr.WaitForExitAsync(ct);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { Interlocked.Increment(ref fallos); Log.Error($"Fondo {v.nombre}: {ex.Message}"); }
                finally
                {
                    int n = Interlocked.Increment(ref listos);
                    p($"{n} de {lista.Count} fondos" + (fallos > 0 ? $" ({fallos} sin descargar)" : ""), (double)n / lista.Count);
                    gate.Release();
                }
            }));
        }

        private static async Task PasoEscritorios(Action<string, double> p, CancellationToken ct)
        {
            if (Directory.Exists(Path.Combine(Paths.Root, "escritorios", "fedora"))) { p("Ya estaban", 1); return; }
            foreach (var (script, texto) in new[] { ("crear-escritorios.ps1", "Buscando tus apps…"), ("crear-iconos.ps1", "Poniendo iconos de cada modo…") })
            {
                p(texto, -1);
                var ps1 = Path.Combine(AppDir, "tools", script);
                if (!File.Exists(ps1)) continue;
                using var pr = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" -Okla \"{Paths.Root}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
                var o = await pr.StandardOutput.ReadToEndAsync(ct);
                await pr.WaitForExitAsync(ct);
                Log.Info($"{script}: {o.Trim().Replace(Environment.NewLine, " | ")}");
            }
        }

        private static async Task PasoTarea(Action<string, double> p, CancellationToken ct)
        {
            // Tarea "okla": al iniciar sesión, con los permisos más altos (sin ventana de UAC cada vez).
            var exe = Environment.ProcessPath!;
            var args = $"/create /tn okla /tr \"\\\"{exe}\\\"\" /sc onlogon /rl highest /f /delay 0000:10";
            using var pr = Process.Start(new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
            var err = await pr.StandardError.ReadToEndAsync(ct);
            await pr.WaitForExitAsync(ct);
            if (pr.ExitCode != 0) throw new InvalidOperationException("schtasks: " + err.Trim());
            p("Listo", 1);
        }
    }

    /// <summary>Ventana del instalador: lista de pasos con su estado y una barra de progreso.</summary>
    internal sealed class InstaladorWindow : Window
    {
        private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1c));
        private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xe6, 0xe6, 0xea));
        private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x8a, 0x8f, 0x9a));
        private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x7f, 0xd8, 0xbe));
        private static readonly Brush Mal = new SolidColorBrush(Color.FromRgb(0xe0, 0x6c, 0x75));

        private readonly List<(Instalador.Paso paso, TextBlock marca, TextBlock detalle)> _filas = new();
        private readonly ProgressBar _barra = new() { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 18, 0, 0) };
        private readonly Button _boton = new() { Content = "Instalar", Padding = new Thickness(22, 8, 22, 8), Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        private readonly CancellationTokenSource _cts = new();
        public bool Terminado { get; private set; }

        public InstaladorWindow()
        {
            Title = "okla · instalación";
            Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = Bg;
            try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "okla.ico"))); } catch { }

            _pasos = ConstruirPasos();
            Content = ConstruirPersonalizar();
            Closing += (_, _) => _cts.Cancel();
        }

        // ---------------- 1) personalizar: qué modos quiere el usuario ----------------
        private readonly Dictionary<string, CheckBox> _elegir = new();
        private readonly UIElement _pasos;

        private UIElement ConstruirPersonalizar()
        {
            // Si okla ya estaba instalado (--instalar para reparar), se parte de lo que el usuario tenía.
            var actuales = new Dictionary<string, bool>();
            try
            {
                if (File.Exists(Paths.ModesFile))
                    foreach (var m in JsonSerializer.Deserialize<List<ModeDef>>(File.ReadAllText(Paths.ModesFile)) ?? new())
                        actuales[m.Key] = m.Activo;
            }
            catch { }

            var sp = new StackPanel { Margin = new Thickness(28) };
            sp.Children.Add(new TextBlock { Text = "Elige tus modos", FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Fg });
            sp.Children.Add(new TextBlock { Text = "Como elegir clase antes de la aventura: activa los que vayas a usar. Podrás cambiarlo cuando quieras en el editor (Ctrl+Alt+E).",
                                            TextWrapping = TextWrapping.Wrap, Foreground = Dim, Margin = new Thickness(0, 4, 0, 16) });
            foreach (var m in ModeStore.Resumen())
            {
                bool on = actuales.TryGetValue(m.Key, out var a) ? a : m.Activo;
                string extra = m.Key switch
                {
                    "J" => "Se pone solo al abrir un juego.",
                    "P" => "Se pone solo al grabar con OBS.",
                    "B" => "Opcional: al desconectar el cargador, okla te pregunta si lo activas.",
                    _ => "",
                };
                sp.Children.Add(Tarjeta(m.Key, m.Nombre, $"{m.Descripcion}. {extra}".Trim(), $"Ctrl+Alt+{m.Tecla}", on, true));
            }
            sp.Children.Add(Tarjeta("panico", "Pánico", "Devuelve Windows a como estaba. Siempre disponible.", "Ctrl+Alt+0", true, false));

            var sig = new Button { Content = "Siguiente", Padding = new Thickness(22, 8, 22, 8), Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            sig.Click += (_, _) =>
            {
                var elegidos = _elegir.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
                if (!elegidos.Any(k => k != "B"))
                {
                    MessageBox.Show(this, "Elige al menos un modo además de Batería.", "okla", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                Directory.CreateDirectory(Paths.Config);
                File.WriteAllText(ModeStore.ModulosFile, JsonSerializer.Serialize(elegidos));
                Instalador.ModosElegidos = new HashSet<string>(elegidos);
                Log.Info("Instalador: modos elegidos " + string.Join(", ", elegidos));
                Content = _pasos;
            };
            sp.Children.Add(sig);
            return sp;
        }

        private UIElement Tarjeta(string clave, string nombre, string texto, string atajo, bool marcado, bool editable)
        {
            var fila = new DockPanel();
            if (IconosModos.Wpf(clave) is { } src)
            {
                var img = new Image { Source = src, Width = 40, Height = 40, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                DockPanel.SetDock(img, Dock.Left); fila.Children.Add(img);
            }
            var cb = new CheckBox { IsChecked = marcado, IsEnabled = editable, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            DockPanel.SetDock(cb, Dock.Right); fila.Children.Add(cb);
            if (editable) _elegir[clave] = cb;
            var t = new StackPanel();
            var cab = new TextBlock { FontSize = 15, Foreground = Fg };
            cab.Inlines.Add(new System.Windows.Documents.Run(nombre) { FontWeight = FontWeights.SemiBold });
            cab.Inlines.Add(new System.Windows.Documents.Run("   " + atajo) { Foreground = Dim, FontSize = 12 });
            t.Children.Add(cab);
            t.Children.Add(new TextBlock { Text = texto, Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            fila.Children.Add(t);
            var borde = new Border { Child = fila, Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(10),
                                     Background = new SolidColorBrush(Color.FromRgb(0x1d, 0x20, 0x28)), Cursor = editable ? System.Windows.Input.Cursors.Hand : null };
            if (editable)
            {
                void Pintar() => borde.Opacity = cb.IsChecked == true ? 1 : 0.55;
                cb.Checked += (_, _) => Pintar(); cb.Unchecked += (_, _) => Pintar(); Pintar();
                borde.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not CheckBox) cb.IsChecked = cb.IsChecked != true; };
            }
            return borde;
        }

        // ---------------- 2) instalar ----------------
        private UIElement ConstruirPasos()
        {
            var sp = new StackPanel { Margin = new Thickness(28) };
            sp.Children.Add(new TextBlock { Text = "okla", FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = Fg });
            sp.Children.Add(new TextBlock { Text = "Vamos a dejar tu Windows listo para cambiar de modo con un atajo. Tarda unos minutos (descarga ~1 GB de fondos).",
                                            TextWrapping = TextWrapping.Wrap, Foreground = Dim, Margin = new Thickness(0, 4, 0, 18) });
            foreach (var paso in Instalador.Pasos())
            {
                var fila = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
                var marca = new TextBlock { Text = "○", Width = 26, Foreground = Dim, FontSize = 15 };
                DockPanel.SetDock(marca, Dock.Left); fila.Children.Add(marca);
                var t = new StackPanel();
                t.Children.Add(new TextBlock { Text = paso.Nombre, Foreground = Fg, FontSize = 14 });
                var det = new TextBlock { Foreground = Dim, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
                t.Children.Add(det); fila.Children.Add(t);
                sp.Children.Add(fila);
                _filas.Add((paso, marca, det));
            }
            sp.Children.Add(_barra);
            sp.Children.Add(_boton);
            _boton.Click += async (_, _) => { if (Terminado) { DialogResult = true; return; } await Correr(); };
            return sp;
        }

        private async Task Correr()
        {
            _boton.IsEnabled = false; _boton.Content = "Instalando…";
            int errores = 0;
            for (int i = 0; i < _filas.Count; i++)
            {
                var (paso, marca, det) = _filas[i];
                marca.Text = "●"; marca.Foreground = Fg;
                int idx = i;
                void P(string texto, double frac) => Dispatcher.BeginInvoke(() =>
                {
                    det.Text = texto;
                    _barra.IsIndeterminate = frac < 0;
                    if (frac >= 0) _barra.Value = (idx + frac) / _filas.Count;
                });
                try
                {
                    Log.Info("Instalación: " + paso.Nombre);
                    await Task.Run(() => paso.Hacer(P, _cts.Token));
                    marca.Text = "✓"; marca.Foreground = Ok;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    errores++;
                    marca.Text = "✕"; marca.Foreground = Mal; det.Text = ex.Message;
                    Log.Error($"Instalación, {paso.Nombre}: {ex}");
                }
            }
            _barra.IsIndeterminate = false; _barra.Value = 1;
            if (errores == 0) Instalador.Marcar(typeof(Instalador).Assembly.GetName().Version?.ToString(3) ?? "?");
            Terminado = errores == 0;
            _boton.IsEnabled = true;
            _boton.Content = errores == 0 ? "Abrir okla" : "Reintentar";   // el clic ya reintenta si no terminó
        }
    }
}
