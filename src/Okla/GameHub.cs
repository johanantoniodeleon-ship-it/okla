using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Okla
{
    public sealed class Game
    {
        public string Id { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string Origen { get; set; } = "manual";   // steam, epic, tlauncher, manual
        public string Lanzar { get; set; } = "";         // exe o URL (steam://, com.epicgames.launcher://)
        /// <summary>Proceso del juego en marcha (sin .exe). Para Minecraft: javaw con ventana "Minecraft…".</summary>
        public string? Proceso { get; set; }
        public string? TituloVentana { get; set; }
        public string? SteamAppId { get; set; }
    }

    /// <summary>Encuentra juegos instalados (Steam, Epic, TLauncher) + los añadidos a mano (config\juegos.json).</summary>
    internal static class GameLibrary
    {
        private static string ManualFile => System.IO.Path.Combine(Paths.Config, "juegos.json");
        public static string Portadas => System.IO.Path.Combine(Paths.Recursos, "portadas");
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

        public static List<Game> Detect()
        {
            var list = new List<Game>();
            try { list.AddRange(Steam()); } catch (Exception ex) { Log.Error("Steam: " + ex.Message); }
            try { list.AddRange(Epic()); } catch (Exception ex) { Log.Error("Epic: " + ex.Message); }
            try { list.AddRange(TLauncher()); } catch (Exception ex) { Log.Error("TLauncher: " + ex.Message); }
            list.AddRange(LoadManual());
            return list.GroupBy(g => g.Id).Select(g => g.First()).ToList();
        }

        private static IEnumerable<Game> Steam()
        {
            var sp = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
            if (sp == null || !Directory.Exists(sp)) yield break;
            var libs = new List<string> { System.IO.Path.Combine(sp, "steamapps") };
            var vdf = System.IO.Path.Combine(sp, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libs.Add(System.IO.Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps"));
            foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
                foreach (var acf in Directory.GetFiles(lib, "appmanifest_*.acf"))
                {
                    var t = File.ReadAllText(acf);
                    var id = Regex.Match(t, "\"appid\"\\s+\"(\\d+)\"").Groups[1].Value;
                    var name = Regex.Match(t, "\"name\"\\s+\"([^\"]+)\"").Groups[1].Value;
                    if (id == "" || id == "228980" || name.Contains("Redistributable")) continue;
                    yield return new Game { Id = "steam-" + id, Nombre = name, Origen = "steam", Lanzar = "steam://rungameid/" + id, SteamAppId = id };
                }
        }

        private static IEnumerable<Game> Epic()
        {
            var dir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
            if (!Directory.Exists(dir)) yield break;
            foreach (var f in Directory.GetFiles(dir, "*.item"))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var r = doc.RootElement;
                string S(string k) => r.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
                var app = S("AppName"); var exe = S("LaunchExecutable");
                yield return new Game
                {
                    Id = "epic-" + app, Nombre = S("DisplayName"), Origen = "epic",
                    Lanzar = $"com.epicgames.launcher://apps/{app}?action=launch&silent=true",
                    Proceso = System.IO.Path.GetFileNameWithoutExtension(exe)
                };
            }
        }

        private static IEnumerable<Game> TLauncher()
        {
            var exe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "TLauncher.exe");
            if (File.Exists(exe))
                yield return new Game { Id = "minecraft", Nombre = "Minecraft", Origen = "tlauncher", Lanzar = exe, Proceso = "javaw", TituloVentana = "Minecraft" };
        }

        public static List<Game> LoadManual()
        {
            try { if (File.Exists(ManualFile)) return JsonSerializer.Deserialize<List<Game>>(File.ReadAllText(ManualFile)) ?? new(); }
            catch (Exception ex) { Log.Error("juegos.json: " + ex.Message); }
            return new();
        }

        public static void SaveManual(List<Game> g) =>
            File.WriteAllText(ManualFile, JsonSerializer.Serialize(g, new JsonSerializerOptions { WriteIndented = true }));

        public static Game AddManual(string exe)
        {
            var fvi = FileVersionInfo.GetVersionInfo(exe);
            var name = !string.IsNullOrWhiteSpace(fvi.ProductName) ? fvi.ProductName!.Trim() : System.IO.Path.GetFileNameWithoutExtension(exe);
            var g = new Game { Id = "manual-" + Math.Abs(exe.ToLowerInvariant().GetHashCode()), Nombre = name, Origen = "manual",
                               Lanzar = exe, Proceso = System.IO.Path.GetFileNameWithoutExtension(exe) };
            var all = LoadManual(); all.RemoveAll(x => x.Id == g.Id); all.Add(g); SaveManual(all);
            return g;
        }

        public static void RemoveManual(string id) { var all = LoadManual(); all.RemoveAll(x => x.Id == id); SaveManual(all); }

        /// <summary>¿Está el juego abierto ahora mismo?</summary>
        public static bool IsRunning(Game g)
        {
            if (string.IsNullOrEmpty(g.Proceso)) return false;
            foreach (var p in Process.GetProcessesByName(g.Proceso))
            {
                using (p)
                {
                    if (g.TituloVentana == null) return true;
                    try { if (p.MainWindowTitle.StartsWith(g.TituloVentana, StringComparison.OrdinalIgnoreCase)) return true; } catch { }
                }
            }
            return false;
        }

        /// <summary>Portada en caché (recursos\portadas\id.jpg); si falta, la busca en Steam (gratis, sin clave).</summary>
        public static async Task<string?> CoverAsync(Game g)
        {
            Directory.CreateDirectory(Portadas);
            var file = System.IO.Path.Combine(Portadas, g.Id + ".jpg");
            if (File.Exists(file)) return file;
            if (g.Id == "minecraft")
            {
                var poster = System.IO.Path.Combine(Paths.Recursos, "videos", "gaming", "minecraft-overgrown-cabin.jpg");
                return File.Exists(poster) ? poster : null;
            }
            try
            {
                var appid = g.SteamAppId;
                if (appid == null)
                {
                    var json = await Http.GetStringAsync("https://store.steampowered.com/api/storesearch/?cc=us&l=spanish&term=" + Uri.EscapeDataString(g.Nombre));
                    using var doc = JsonDocument.Parse(json);
                    var items = doc.RootElement.GetProperty("items");
                    if (items.GetArrayLength() == 0) return null;
                    appid = items[0].GetProperty("id").GetInt32().ToString();
                }
                var bytes = await Http.GetByteArrayAsync($"https://cdn.cloudflare.steamstatic.com/steam/apps/{appid}/library_600x900.jpg");
                await File.WriteAllBytesAsync(file, bytes);
                return file;
            }
            catch (Exception ex) { Log.Info($"Sin portada para {g.Nombre}: {ex.Message}"); return null; }
        }
    }

    /// <summary>Hub de juegos del modo Gaming: biblioteca con portadas + datos del equipo. Se oculta al jugar.</summary>
    internal sealed class GameHub : Window
    {
        private static readonly Brush Bg = Frozen("#0a0a0c"), Fg = Frozen("#e6e6ea"), Dim = Frozen("#6e6e76"), Line = Frozen("#1e1e23");
        private static Brush Frozen(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
        private static readonly FontFamily Font = new("Bahnschrift");

        private readonly WrapPanel _grid = new() { HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBlock _clock = new() { FontSize = 64, Foreground = Fg, FontFamily = new FontFamily("Bahnschrift Light") };
        private readonly TextBlock _date = new() { FontSize = 15, Foreground = Dim, FontFamily = Font, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly TextBlock _cpu = Stat(), _ram = Stat(), _gpu = Stat(), _temp = Stat(), _music = Stat();
        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(2) };
        private readonly Action<Game> _onLaunch;
        private int _tickN;

        private static TextBlock Stat() => new() { FontSize = 22, Foreground = Fg, FontFamily = Font };

        public GameHub(string? fondo, Action<Game> onLaunch)
        {
            _onLaunch = onLaunch;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Title = "okla · Gaming";
            Left = 0; Top = 0; Width = SystemParameters.PrimaryScreenWidth; Height = SystemParameters.PrimaryScreenHeight;
            Background = Bg;

            var root = new Grid();
            if (fondo != null && File.Exists(fondo))
            {
                var bi = new BitmapImage(); bi.BeginInit(); bi.UriSource = new Uri(fondo); bi.DecodePixelWidth = 1600;
                bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
                root.Children.Add(new Image { Source = bi, Stretch = Stretch.UniformToFill, Opacity = 0.22 });
            }
            root.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Color.FromArgb(0x40, 10, 10, 12), Color.FromArgb(0xF2, 10, 10, 12), 90) });

            var page = new Grid { Margin = new Thickness(80, 60, 80, 50) };
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.RowDefinitions.Add(new RowDefinition());
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Cabecera: título + reloj
            var head = new Grid();
            var title = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            title.Children.Add(new TextBlock { Text = "O K L A   //   M O D O   J", FontSize = 13, Foreground = Dim, FontFamily = Font });
            title.Children.Add(new TextBlock { Text = "Biblioteca", FontSize = 46, Foreground = Fg, FontFamily = new FontFamily("Bahnschrift SemiBold") });
            head.Children.Add(title);
            var clock = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            clock.Children.Add(_clock); clock.Children.Add(_date);
            head.Children.Add(clock);
            page.Children.Add(head);

            // Datos del equipo
            var stats = new UniformGrid5();
            stats.Add("CPU", _cpu); stats.Add("MEMORIA", _ram); stats.Add("GPU NVIDIA", _gpu); stats.Add("TEMPERATURA", _temp); stats.Add("SONANDO", _music);
            var statsBorder = new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 1), Padding = new Thickness(0, 18, 0, 18),
                                           Margin = new Thickness(0, 30, 0, 34), Child = stats.Panel };
            Grid.SetRow(statsBorder, 1); page.Children.Add(statsBorder);

            var scroll = new ScrollViewer { Content = _grid, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
            Grid.SetRow(scroll, 2); page.Children.Add(scroll);

            var foot = new TextBlock { Text = "CLIC: JUGAR   ·   CLIC DERECHO: QUITAR   ·   ESC: OCULTAR HUB   ·   CTRL+ALT+2: SALIR DE GAMING",
                                       FontSize = 12, Foreground = Dim, FontFamily = Font, Margin = new Thickness(0, 20, 0, 0) };
            Grid.SetRow(foot, 3); page.Children.Add(foot);
            root.Children.Add(page);
            Content = root;

            KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
            _tick.Tick += (_, _) => UpdateStats();
            IsVisibleChanged += (_, _) => { if (IsVisible) { UpdateStats(); _tick.Start(); } else _tick.Stop(); };
            Reload();
        }

        public void Reload()
        {
            _grid.Children.Clear();
            foreach (var g in GameLibrary.Detect()) _grid.Children.Add(Tile(g));
            _grid.Children.Add(AddTile());
        }

        private FrameworkElement Tile(Game g)
        {
            var img = new Border { CornerRadius = new CornerRadius(8), Background = Frozen("#16161a") };
            var name = new TextBlock { Text = g.Nombre.ToUpperInvariant(), Foreground = Fg, FontFamily = new FontFamily("Bahnschrift SemiBold"), FontSize = 18,
                                       TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16), VerticalAlignment = VerticalAlignment.Bottom };
            var origin = new TextBlock { Text = g.Origen.ToUpperInvariant(), Foreground = Dim, FontFamily = Font, FontSize = 11,
                                         Margin = new Thickness(16, 14, 16, 0), VerticalAlignment = VerticalAlignment.Top };
            var shade = new Border { CornerRadius = new CornerRadius(8),
                                     Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0xD0, 0, 0, 0), 90) };
            var inner = new Grid(); inner.Children.Add(img); inner.Children.Add(shade); inner.Children.Add(origin); inner.Children.Add(name);
            var st = new ScaleTransform(1, 1);
            var card = new Border
            {
                Width = 230, Height = 345, Margin = new Thickness(0, 0, 22, 22), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent, Child = inner, Cursor = Cursors.Hand, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = st
            };
            card.MouseEnter += (_, _) => { card.BorderBrush = Fg; Zoom(st, 1.04); };
            card.MouseLeave += (_, _) => { card.BorderBrush = Brushes.Transparent; Zoom(st, 1); };
            card.MouseLeftButtonUp += (_, _) => Launch(g);
            if (g.Origen == "manual")
                card.MouseRightButtonUp += (_, _) => { GameLibrary.RemoveManual(g.Id); Reload(); };

            _ = SetCoverAsync(g, img);
            return card;
        }

        private static async Task SetCoverAsync(Game g, Border target)
        {
            var file = await GameLibrary.CoverAsync(g);
            if (file == null) return;
            try
            {
                var bi = new BitmapImage(); bi.BeginInit(); bi.UriSource = new Uri(file); bi.DecodePixelHeight = 520;
                bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
                target.Background = new ImageBrush(bi) { Stretch = Stretch.UniformToFill };
            }
            catch { }
        }

        private FrameworkElement AddTile()
        {
            var t = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            t.Children.Add(new TextBlock { Text = "+", FontSize = 54, Foreground = Dim, FontFamily = new FontFamily("Bahnschrift Light"), HorizontalAlignment = HorizontalAlignment.Center });
            t.Children.Add(new TextBlock { Text = "AGREGAR JUEGO", FontSize = 13, Foreground = Dim, FontFamily = Font, HorizontalAlignment = HorizontalAlignment.Center });
            var r = new Rectangle { Stroke = Dim, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 4 }, RadiusX = 9, RadiusY = 9 };
            var g = new Grid { Width = 230, Height = 345, Margin = new Thickness(0, 0, 22, 22), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            g.Children.Add(r); g.Children.Add(t);
            g.MouseEnter += (_, _) => r.Stroke = Fg; g.MouseLeave += (_, _) => r.Stroke = Dim;
            g.MouseLeftButtonUp += (_, _) =>
            {
                var dlg = new OpenFileDialog { Title = "Elige el .exe del juego", Filter = "Juegos (*.exe)|*.exe" };
                if (dlg.ShowDialog(this) == true) { GameLibrary.AddManual(dlg.FileName); Reload(); }
            };
            return g;
        }

        private static void Zoom(ScaleTransform st, double to)
        {
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(120));
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a); st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        private void Launch(Game g)
        {
            try
            {
                Process.Start(new ProcessStartInfo(g.Lanzar) { UseShellExecute = true,
                    WorkingDirectory = File.Exists(g.Lanzar) ? System.IO.Path.GetDirectoryName(g.Lanzar)! : "" });
                Log.Info("Hub: lanzando " + g.Nombre);
                Toast.PlaySound("hub");
                Hide();
                _onLaunch(g);
            }
            catch (Exception ex) { MessageBox.Show(this, "No pude abrir " + g.Nombre + ":\n" + ex.Message, "okla"); }
        }

        // ---------- datos del equipo (ligeros: nada se consulta si el hub está oculto) ----------
        [StructLayout(LayoutKind.Sequential)] private struct FT { public uint Lo, Hi; public ulong V => ((ulong)Hi << 32) | Lo; }
        [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FT idle, out FT kernel, out FT user);
        [StructLayout(LayoutKind.Sequential)]
        private struct MEM { public uint Len, Load; public ulong Total, Avail, TP, AP, TV, AV, AE; }
        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEM m);
        private ulong _pIdle, _pTotal;

        private void UpdateStats()
        {
            var now = DateTime.Now;
            _clock.Text = now.ToString("HH:mm");
            _date.Text = now.ToString("dddd d 'de' MMMM").ToUpperInvariant();

            if (GetSystemTimes(out var i, out var k, out var u))
            {
                ulong idle = i.V, total = k.V + u.V;
                if (_pTotal > 0 && total > _pTotal) _cpu.Text = $"{100.0 * (1 - (double)(idle - _pIdle) / (total - _pTotal)):0} %";
                _pIdle = idle; _pTotal = total;
            }
            var m = new MEM { Len = (uint)Marshal.SizeOf<MEM>() };
            if (GlobalMemoryStatusEx(ref m))
                _ram.Text = $"{(m.Total - m.Avail) / 1073741824.0:0.0} / {m.Total / 1073741824.0:0} GB";

            _music.Text = Spotify() ?? "—";
            if (_tickN++ % 3 == 0) _ = SlowStatsAsync();   // GPU y temperatura cada 6 s
        }

        internal static string? Spotify()
        {
            foreach (var p in Process.GetProcessesByName("Spotify"))
                using (p)
                {
                    var t = p.MainWindowTitle;
                    if (t.Contains(" - ")) return t.Length > 34 ? t[..33] + "…" : t;
                }
            return null;
        }

        private async Task SlowStatsAsync()
        {
            var (gpu, temp) = await Task.Run(() =>
            {
                string g = "—", c = "—";
                try
                {
                    var psi = new ProcessStartInfo(@"C:\Windows\System32\nvidia-smi.exe", "--query-gpu=temperature.gpu,utilization.gpu --format=csv,noheader,nounits")
                    { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                    using var p = Process.Start(psi)!;
                    var o = p.StandardOutput.ReadToEnd().Trim().Split(',');
                    p.WaitForExit(2000);
                    if (o.Length >= 2) g = $"{o[1].Trim()} %  ·  {double.Parse(o[0].Trim(), System.Globalization.CultureInfo.InvariantCulture):0} °C";
                }
                catch { }
                try
                {
                    using var s = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                    foreach (var o in s.Get()) { c = $"{(Convert.ToDouble(o["CurrentTemperature"]) / 10 - 273.15):0} °C"; break; }
                }
                catch { }
                return (g, c);
            });
            _gpu.Text = gpu; _temp.Text = temp;
        }

        /// <summary>Fila de 5 datos con etiqueta pequeña encima.</summary>
        private sealed class UniformGrid5
        {
            public readonly System.Windows.Controls.Primitives.UniformGrid Panel = new() { Columns = 5 };
            public void Add(string label, TextBlock value)
            {
                var s = new StackPanel();
                s.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Dim, FontFamily = Font, Margin = new Thickness(0, 0, 0, 6) });
                s.Children.Add(value);
                value.TextTrimming = TextTrimming.CharacterEllipsis;
                Panel.Children.Add(s);
            }
        }
    }
}
