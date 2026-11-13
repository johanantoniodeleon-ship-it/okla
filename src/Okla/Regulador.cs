using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Okla
{
    internal enum Nivel { Bajo, Medio, Alto }

    /// <summary>Lo que okla sabe de la máquina (se guarda en config\maquina.json; servirá también para el registro de equipos).</summary>
    internal sealed class PerfilMaquina
    {
        public string Cpu { get; set; } = "";
        public int Hilos { get; set; }
        public int RamGB { get; set; }
        public List<string> Graficas { get; set; } = new();
        public string GraficaPrincipal { get; set; } = "";
        public int VramGB { get; set; }
        public bool Dedicada { get; set; }
        public bool Portatil { get; set; }
        public string Pantalla { get; set; } = "";
        public int Puntos { get; set; }
        public Nivel Detectado { get; set; }
        public DateTime Medido { get; set; }
    }

    /// <summary>
    /// Regulador automático de rendimiento.
    /// 1) Al arrancar mide la máquina y le da un nivel (alto / medio / bajo), que el usuario puede forzar desde la bandeja.
    ///    - Alto: fondos a la resolución nativa, video de fondo.
    ///    - Medio: fondos como mucho a 1440p.
    ///    - Bajo: fondos como mucho a 1080p, sin video de fondo y sin ventanas translúcidas.
    /// 2) Mientras okla corre, si con el video de fondo puesto el procesador o la gráfica van al tope un rato,
    ///    pausa el video ("presión") y lo devuelve cuando la carga baja. Si vuelve a pasar enseguida, lo deja
    ///    pausado hasta el próximo cambio de modo.
    /// </summary>
    internal static class Regulador
    {
        private static string ConfigFile => Path.Combine(Paths.Config, "rendimiento.json");
        private static string PerfilFile => Path.Combine(Paths.Config, "maquina.json");

        public static PerfilMaquina Perfil { get; private set; } = new();
        /// <summary>null = automático.</summary>
        public static Nivel? Forzado { get; private set; }
        public static Nivel Actual => Forzado ?? Perfil.Detectado;

        public static bool Presion { get; private set; }
        private static bool _bloqueadoHastaCambio;
        private static readonly List<DateTime> _activaciones = new();

        /// <summary>Cambió el nivel o la presión: hay que volver a aplicar el modo actual.</summary>
        public static event Action<string>? Cambio;
        /// <summary>Program dice si el modo actual tiene video de fondo.</summary>
        public static Func<bool>? HayVideo;

        public static int AlturaMax => Actual switch { Nivel.Bajo => 1080, Nivel.Medio => 1440, _ => int.MaxValue };

        // ---------- 1) medir ----------
        public static void Iniciar()
        {
            try
            {
                if (File.Exists(ConfigFile))
                {
                    var n = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ConfigFile))?.GetValueOrDefault("nivel");
                    if (Enum.TryParse<Nivel>(n, true, out var f)) Forzado = f;
                }
            }
            catch { }

            try { Perfil = Medir(); }
            catch (Exception ex) { Log.Error("No pude medir la máquina: " + ex.Message); Perfil = new PerfilMaquina { Detectado = Nivel.Medio }; }

            PerfilMaquina? antes = null;
            try { if (File.Exists(PerfilFile)) antes = JsonSerializer.Deserialize<PerfilMaquina>(File.ReadAllText(PerfilFile)); } catch { }
            try { File.WriteAllText(PerfilFile, JsonSerializer.Serialize(Perfil, new JsonSerializerOptions { WriteIndented = true })); } catch { }

            Log.Info($"Máquina: {Perfil.Cpu} ({Perfil.Hilos} hilos), {Perfil.RamGB} GB, {Perfil.GraficaPrincipal} ({Perfil.VramGB} GB{(Perfil.Dedicada ? ", dedicada" : "")}), " +
                     $"pantalla {Perfil.Pantalla} -> {Perfil.Puntos} puntos, nivel {Perfil.Detectado}" + (Forzado != null ? $" (forzado: {Forzado})" : ""));
            if (antes != null && antes.Detectado != Perfil.Detectado) Log.Info($"El nivel detectado cambió: {antes.Detectado} -> {Perfil.Detectado}");

            _timer = new System.Threading.Timer(_ => Muestrear(), null, 15000, 5000);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        private static PerfilMaquina Medir()
        {
            var p = new PerfilMaquina { Medido = DateTime.Now, Hilos = Environment.ProcessorCount };
            p.Cpu = (Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "") as string ?? "").Trim();
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref m)) p.RamGB = (int)Math.Round(m.ullTotalPhys / 1073741824.0);
            p.Portatil = System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery;
            var (w, h, hz) = Okla.Pantalla.Actual();
            p.Pantalla = $"{w}x{h}@{hz}";

            // Gráficas: del registro (rápido y con la memoria real; WMI se queda en 4 GB como máximo).
            using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
            {
                foreach (var sub in cls?.GetSubKeyNames().Where(s => s.All(char.IsDigit)) ?? Enumerable.Empty<string>())
                {
                    try
                    {
                        using var k = cls!.OpenSubKey(sub);
                        var nombre = k?.GetValue("DriverDesc") as string;
                        if (string.IsNullOrWhiteSpace(nombre) || nombre.Contains("Basic", StringComparison.OrdinalIgnoreCase)) continue;
                        long bytes = k!.GetValue("HardwareInformation.qwMemorySize") switch
                        {
                            long l => l, byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0), _ => 0
                        };
                        if (bytes == 0) bytes = k.GetValue("HardwareInformation.MemorySize") switch
                        {
                            int i => (uint)i, byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0), _ => 0
                        };
                        int gb = (int)Math.Round(bytes / 1073741824.0);
                        bool integrada = (nombre.Contains("Intel", StringComparison.OrdinalIgnoreCase) && !nombre.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                                         || nombre.Equals("AMD Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) || gb < 2;
                        p.Graficas.Add($"{nombre} ({gb} GB)");
                        if (!integrada && gb >= p.VramGB) { p.GraficaPrincipal = nombre; p.VramGB = gb; p.Dedicada = true; }
                        else if (!p.Dedicada && p.GraficaPrincipal == "") p.GraficaPrincipal = nombre;
                    }
                    catch { }
                }
            }

            // Puntos: procesador (0-2) + RAM (0-2) + gráfica (0-2), y -1 si una integrada tiene que mover una pantalla 4K.
            int pts = p.Hilos >= 12 ? 2 : p.Hilos >= 8 ? 1 : 0;
            pts += p.RamGB >= 16 ? 2 : p.RamGB >= 8 ? 1 : 0;
            bool igpuBuena = new[] { "Iris Xe", "Arc", "680M", "780M", "880M", "890M" }.Any(x => p.GraficaPrincipal.Contains(x, StringComparison.OrdinalIgnoreCase));
            pts += p.Dedicada && p.VramGB >= 4 ? 2 : p.Dedicada || igpuBuena ? 1 : 0;
            if (!p.Dedicada && (long)w * h >= 3840L * 2160) pts -= 1;
            p.Puntos = pts;
            p.Detectado = pts >= 5 ? Nivel.Alto : pts >= 3 ? Nivel.Medio : Nivel.Bajo;
            return p;
        }

        /// <summary>Forzar un nivel (null = automático). Lo guarda y avisa para reaplicar.</summary>
        public static void Forzar(Nivel? n)
        {
            if (n == Forzado) return;
            var antes = Actual;
            Forzado = n;
            try { File.WriteAllText(ConfigFile, JsonSerializer.Serialize(new Dictionary<string, string> { ["nivel"] = n?.ToString().ToLowerInvariant() ?? "auto" })); } catch { }
            Log.Info($"Rendimiento: {(n == null ? "automático" : "forzado")} -> nivel {Actual}");
            if (Actual != antes) Cambio?.Invoke($"nivel {Nombre(Actual)}");
        }

        public static string Nombre(Nivel n) => n switch { Nivel.Alto => "alto", Nivel.Medio => "medio", _ => "bajo" };

        /// <summary>Al cambiar de modo a mano, se da otra oportunidad al video.</summary>
        public static void NuevoModo()
        {
            _bloqueadoHastaCambio = false;
            _activaciones.Clear();
            Presion = false;
            _altas = _bajas = 0;
        }

        // ---------- ajustar lo que aplica el motor ----------
        public static string Ajustar(string id, string valor)
        {
            bool sinVideo = Actual == Nivel.Bajo || Presion;
            bool sinTranslucidas = Actual == Nivel.Bajo;
            if ((id == "wh:live-video-wallpaper" && sinVideo) || (id == "wh:translucent-windows" && sinTranslucidas))
            {
                try
                {
                    var d = JsonSerializer.Deserialize<Dictionary<string, string>>(valor);
                    if (d != null && d.GetValueOrDefault("activo") == "1") { d["activo"] = "0"; return JsonSerializer.Serialize(d); }
                }
                catch { }
            }
            return valor;
        }

        // ---------- 2) vigilar la carga ----------
        private static System.Threading.Timer? _timer;
        private static int _altas, _bajas;
        private static (ulong idle, ulong total)? _cpuAntes;
        private static readonly Dictionary<string, CounterSample> _gpuAntes = new();
        public static double UltimaCpu { get; private set; }
        public static double UltimaGpu { get; private set; }

        [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        private static int _muestreando;
        private static void Muestrear()
        {
            if (System.Threading.Interlocked.Exchange(ref _muestreando, 1) == 1) return;
            try
            {
                double cpu = Cpu(), gpu = Gpu();
                UltimaCpu = cpu; UltimaGpu = gpu;
                // Mientras se convierten fondos la carga es de okla, no del usuario.
                if (Adaptador.Trabajando || Actual == Nivel.Bajo) { _altas = _bajas = 0; return; }

                if (!Presion)
                {
                    bool video = HayVideo?.Invoke() == true;
                    _altas = video && (cpu > 85 || gpu > 90) ? _altas + 1 : 0;
                    if (_altas >= 6)   // 30 s seguidos
                    {
                        _altas = 0;
                        _activaciones.RemoveAll(t => DateTime.Now - t > TimeSpan.FromMinutes(15));
                        _activaciones.Add(DateTime.Now);
                        if (_activaciones.Count >= 2) _bloqueadoHastaCambio = true;
                        Presion = true;
                        Log.Info($"Rendimiento: carga alta (CPU {cpu:0} %, gráfica {gpu:0} %) -> video de fondo en pausa" + (_bloqueadoHastaCambio ? " hasta el próximo cambio de modo" : ""));
                        Cambio?.Invoke("presion");
                    }
                }
                else if (!_bloqueadoHastaCambio)
                {
                    _bajas = cpu < 50 && gpu < 50 ? _bajas + 1 : 0;
                    if (_bajas >= 12)  // 60 s tranquilos
                    {
                        _bajas = 0;
                        Presion = false;
                        Log.Info($"Rendimiento: carga normal (CPU {cpu:0} %, gráfica {gpu:0} %) -> vuelve el video de fondo");
                        Cambio?.Invoke("normal");
                    }
                }
            }
            catch (Exception ex) { Log.Error("Regulador: " + ex.Message); }
            finally { System.Threading.Interlocked.Exchange(ref _muestreando, 0); }
        }

        private static double Cpu()
        {
            if (!GetSystemTimes(out var idle, out var k, out var u)) return 0;
            ulong total = (ulong)(k + u), id = (ulong)idle;   // kernel ya incluye idle
            double r = 0;
            if (_cpuAntes is { } a && total > a.total) r = 100.0 * (1 - (double)(id - a.idle) / (total - a.total));
            _cpuAntes = (id, total);
            return Math.Clamp(r, 0, 100);
        }

        /// <summary>Uso del motor 3D más ocupado de cualquier gráfica (como el Administrador de tareas).</summary>
        private static double Gpu()
        {
            try
            {
                var cat = new PerformanceCounterCategory("GPU Engine");
                var datos = cat.ReadCategory()["Utilization Percentage"];
                if (datos == null) return 0;
                var porMotor = new Dictionary<string, double>();
                var vistos = new HashSet<string>();
                foreach (InstanceData d in datos.Values)
                {
                    var inst = d.InstanceName;
                    if (!inst.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;
                    vistos.Add(inst);
                    var s = d.Sample;
                    if (_gpuAntes.TryGetValue(inst, out var a))
                    {
                        var v = CounterSample.Calculate(a, s);
                        int i = inst.IndexOf("luid_", StringComparison.Ordinal);
                        var motor = i >= 0 ? inst[i..] : inst;   // misma gráfica y motor, distinto proceso
                        porMotor[motor] = porMotor.GetValueOrDefault(motor) + v;
                    }
                    _gpuAntes[inst] = s;
                }
                foreach (var viejo in _gpuAntes.Keys.Where(k => !vistos.Contains(k)).ToList()) _gpuAntes.Remove(viejo);
                return porMotor.Count == 0 ? 0 : Math.Clamp(porMotor.Values.Max(), 0, 100);
            }
            catch { return 0; }
        }
    }
}
