using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace Okla
{
    /// <summary>Resolución real (en píxeles físicos) de la pantalla principal.</summary>
    internal static class Pantalla
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string? dev, int mode, ref DEVMODE dm);

        public static (int w, int h, int hz) Actual()
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            return EnumDisplaySettings(null, -1 /*ENUM_CURRENT_SETTINGS*/, ref dm) && dm.dmPelsWidth > 0
                ? (dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency) : (1920, 1080, 60);
        }
    }

    /// <summary>
    /// Fondos a la medida de la pantalla. Para cada imagen o video de okla\recursos crea en segundo plano una copia
    /// a la resolución exacta de la pantalla (okla\recursos\adaptados\ANCHOxALTO\...), partiendo del original de más
    /// calidad. Mientras no existe se usa el fondo normal. El pixel art se amplía sin suavizar (vecino más cercano).
    /// Convierte de uno en uno, con prioridad baja, con la NVIDIA si hay, y nunca con batería.
    /// </summary>
    internal static class Adaptador
    {
        private static readonly BlockingCollection<string> _cola = new();
        private static readonly ConcurrentDictionary<string, byte> _encolados = new(StringComparer.OrdinalIgnoreCase);
        private static Thread? _hilo;
        private static (int w, int h) _res = Objetivo();
        private static string? _ffmpeg;
        private static bool? _nvenc;

        public static string Carpeta => Path.Combine(Paths.Recursos, "adaptados", $"{_res.w}x{_res.h}");
        private static string IndiceFile => Path.Combine(Carpeta, "nativos.json");
        private static HashSet<string>? _nativos;   // ya estaban a la medida: no hace falta copia

        /// <summary>True mientras ffmpeg está convirtiendo (el regulador no cuenta esa carga como del usuario).</summary>
        public static bool Trabajando { get; private set; }

        /// <summary>Resolución objetivo: la de la pantalla, limitada por el regulador en equipos de nivel medio o bajo.</summary>
        private static (int w, int h) Objetivo()
        {
            var (w, h, _) = Pantalla.Actual();
            int max = Regulador.AlturaMax;
            if (h > max) { w = (int)Math.Round(w * (double)max / h / 2) * 2; h = max; }
            return (w, h);
        }

        /// <summary>Se llama al cambiar la pantalla. Devuelve true si cambió la resolución.</summary>
        public static bool Recalcular()
        {
            var r = Objetivo();
            if (r == _res) return false;
            Log.Info($"Fondos: {_res.w}x{_res.h} -> {r.w}x{r.h}");
            _res = r; _nativos = null; _encolados.Clear();
            return true;
        }

        /// <summary>Traduce el valor de un ajuste ("fondo" o el video) a la versión adaptada si ya existe; si no, la encola.</summary>
        public static string Adaptar(string id, string valor)
        {
            try
            {
                if (id == "fondo" && valor.StartsWith("img:"))
                    return "img:" + Ruta(valor[4..]);
                if (id == "wh:live-video-wallpaper" && valor.Contains("videoPath"))
                {
                    var d = JsonSerializer.Deserialize<Dictionary<string, string>>(valor);
                    if (d != null && d.GetValueOrDefault("activo") == "1" && d.TryGetValue("videoPath", out var vp))
                    {
                        var a = Ruta(vp);
                        if (a != vp) { d["videoPath"] = a; return JsonSerializer.Serialize(d); }
                    }
                }
            }
            catch (Exception ex) { Log.Error("Adaptar: " + ex.Message); }
            return valor;
        }

        /// <summary>Ruta a usar para un archivo: la adaptada si existe; si no, el original (y se encola la copia).</summary>
        private static string Ruta(string original)
        {
            if (!original.StartsWith(Paths.Recursos, StringComparison.OrdinalIgnoreCase) || !File.Exists(original)) return original;
            var rel = Path.GetRelativePath(Paths.Recursos, original);
            if (rel.StartsWith("adaptados", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("originales", StringComparison.OrdinalIgnoreCase)) return original;
            var dst = Path.Combine(Carpeta, rel);
            if (File.Exists(dst)) return dst;
            if (Nativos().Contains(rel)) return original;
            Encolar(original);
            return original;
        }

        public static void Encolar(string original)
        {
            if (!_encolados.TryAdd(original, 0)) return;
            _cola.Add(original);
            if (_hilo == null)
            {
                _hilo = new Thread(Trabajar) { IsBackground = true, Name = "okla adaptador", Priority = ThreadPriority.BelowNormal };
                _hilo.Start();
            }
        }

        /// <summary>Encola los fondos que tienen puestos los modos (los primeros que se verán).</summary>
        public static void Preparar(IEnumerable<ModeDef> modos)
        {
            foreach (var m in modos)
            {
                if (m.Ajustes == null) continue;
                if (m.Ajustes.TryGetValue("wh:live-video-wallpaper", out var v)) Adaptar("wh:live-video-wallpaper", v);
                if (m.Ajustes.TryGetValue("fondo", out var f)) Adaptar("fondo", f);
            }
            // Después, el resto de fondos de cada modo (para que el mini hub ya los tenga a la medida).
            foreach (var m in modos)
                foreach (var fondo in MiniHub.Fondos(m.Key)) Ruta(fondo.Archivo);
        }

        private static void Trabajar()
        {
            foreach (var original in _cola.GetConsumingEnumerable())
            {
                try
                {
                    // Con batería no se convierte nada: se espera al cargador.
                    while (System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline)
                        Thread.Sleep(30000);
                    Trabajando = true;
                    Convertir(original);
                }
                catch (Exception ex) { Log.Error($"Adaptar {Path.GetFileName(original)}: {ex.Message}"); }
                finally { _encolados.TryRemove(original, out _); if (_cola.Count == 0) Trabajando = false; }
            }
        }

        private static void Convertir(string original)
        {
            var (w, h) = _res;
            var rel = Path.GetRelativePath(Paths.Recursos, original);
            var dst = Path.Combine(Carpeta, rel);
            if (File.Exists(dst)) return;
            var ff = FFmpeg() ?? throw new InvalidOperationException("no encuentro ffmpeg");
            bool video = original.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
            var nombre = Path.GetFileName(original);
            bool pixel = nombre.StartsWith("pixel-", StringComparison.OrdinalIgnoreCase);

            // Original de más calidad, si se guardó (los videos de motionbgs están en recursos\originales\videos).
            var fuente = original;
            if (video)
            {
                var alt = Original(Path.GetFileNameWithoutExtension(nombre));
                if (alt != null) fuente = alt;
            }

            // ¿Ya está a la medida? Entonces no hace falta copia.
            var (sw, sh) = Medidas(ff, original);
            if (sw == w && sh == h)
            {
                lock (Gate) { Nativos().Add(rel); GuardarNativos(); }
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            var tmp = dst + ".tmp" + Path.GetExtension(dst);
            var escala = $"scale={w}:{h}:flags={(pixel ? "neighbor" : "lanczos")}:force_original_aspect_ratio=increase,crop={w}:{h}";
            var t = Stopwatch.StartNew();
            bool ok;
            if (video)
            {
                string Enc(bool nv) => nv
                    ? "-c:v h264_nvenc -preset p5 -rc vbr -cq " + (pixel ? "19" : "22") + " -b:v 0 -profile:v high -pix_fmt yuv420p"
                    : "-c:v libx264 -preset medium -crf " + (pixel ? "18" : "21") + (pixel ? " -tune animation" : "") + " -pix_fmt yuv420p";
                bool nv = _nvenc != false;
                ok = Run(ff, $"-hide_banner -loglevel error -y -i \"{fuente}\" -an -vf \"{escala},fps=30\" {Enc(nv)} -movflags +faststart \"{tmp}\"");
                if (!ok && nv) { _nvenc = false; ok = Run(ff, $"-hide_banner -loglevel error -y -i \"{fuente}\" -an -vf \"{escala},fps=30\" {Enc(false)} -movflags +faststart \"{tmp}\""); }
                else if (ok && nv) _nvenc = true;
            }
            else ok = Run(ff, $"-hide_banner -loglevel error -y -i \"{fuente}\" -vf \"{escala}\" -q:v 2 \"{tmp}\"");

            if (ok && File.Exists(tmp)) { File.Move(tmp, dst, true); Log.Info($"Adaptado a {w}x{h}: {rel} ({t.Elapsed.TotalSeconds:0} s)"); }
            else { try { File.Delete(tmp); } catch { } throw new InvalidOperationException("ffmpeg falló"); }
        }

        /// <summary>
        /// El original descargado de un video. Los nombres varían ("fedora-street-japan", "gojo" como "potencia-gojo",
        /// "celestial-veil.3840x2160", "pixel-silksong-4k"): se comparan sin prefijo de modo ni sufijo de tamaño y,
        /// si hay varios, gana el más grande (más calidad).
        /// </summary>
        private static string? Original(string nombre)
        {
            var dir = Path.Combine(Paths.Recursos, "originales", "videos");
            if (!Directory.Exists(dir)) return null;
            static string Limpio(string n)
            {
                n = Regex.Replace(n, @"\.\d{3,4}x\d{3,4}$", "");
                n = Regex.Replace(n, @"-4k$", "", RegexOptions.IgnoreCase);
                return Regex.Replace(n, @"^(fedora|gaming|potencia|pres|presentacion)-", "", RegexOptions.IgnoreCase);
            }
            var buscado = Limpio(nombre);
            return Directory.GetFiles(dir, "*.mp4")
                .Where(f => string.Equals(Limpio(Path.GetFileNameWithoutExtension(f)), buscado, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
        }

        private static readonly object Gate = new();

        private static HashSet<string> Nativos()
        {
            if (_nativos != null) return _nativos;
            try { if (File.Exists(IndiceFile)) _nativos = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(IndiceFile)); } catch { }
            return _nativos = new HashSet<string>(_nativos ?? new(), StringComparer.OrdinalIgnoreCase);
        }

        private static void GuardarNativos()
        {
            Directory.CreateDirectory(Carpeta);
            File.WriteAllText(IndiceFile, JsonSerializer.Serialize(_nativos));
        }

        private static (int, int) Medidas(string ff, string archivo)
        {
            var psi = new ProcessStartInfo(ff, $"-hide_banner -i \"{archivo}\"") { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var o = p.StandardError.ReadToEnd(); p.WaitForExit(10000);
            var m = Regex.Match(o, @"(Video|Stream).*?, (\d{2,5})x(\d{2,5})");
            return m.Success ? (int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : (0, 0);
        }

        private static bool Run(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            var err = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30 * 60 * 1000)) { try { p.Kill(); } catch { } return false; }
            if (p.ExitCode != 0) Log.Error("ffmpeg: " + err.Result.Trim().Split('\n').LastOrDefault());
            return p.ExitCode == 0;
        }

        /// <summary>Tras instalar ffmpeg, para que se vuelva a buscar.</summary>
        public static void OlvidarFFmpeg() => _ffmpeg = null;

        public static string? FFmpeg()
        {
            if (_ffmpeg != null) return _ffmpeg;
            var propio = Path.Combine(Paths.Root, "tools", "ffmpeg.exe");
            if (File.Exists(propio)) return _ffmpeg = propio;
            try
            {
                var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Packages");
                if (Directory.Exists(winget)) _ffmpeg = Directory.GetFiles(winget, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            }
            catch { }
            return _ffmpeg;
        }
    }
}
