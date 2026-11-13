using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Okla
{
    /// <summary>
    /// Gaming: mientras hay un juego abierto, congela (no cierra) las apps de config\gaming-pausar.json y les
    /// quita la RAM (pasa a disco), para que todo vaya al juego. Al cerrar el juego, salir de Gaming o pánico,
    /// las descongela. Lo congelado se apunta en config\pausados.json: si okla se cerrara de golpe,
    /// al volver a abrirse lo descongela.
    /// </summary>
    internal static class AppPause
    {
        [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr h);
        [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr h);
        [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr h);

        private static string ListaFile => Path.Combine(Paths.Config, "gaming-pausar.json");
        private static string EstadoFile => Path.Combine(Paths.Config, "pausados.json");

        // Nunca se congelan, aunque alguien los ponga en la lista.
        private static readonly HashSet<string> Prohibidos = new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "okla", "claude", "WindowsTerminal", "OpenConsole", "conhost", "powershell", "pwsh", "dwm", "csrss",
            "svchost", "audiodg", "obs64", "javaw", "java", "steam", "steamwebhelper", "EpicGamesLauncher", "Discord",
            "Spotify", "ms-teams", "NVIDIA Overlay", "nvcontainer", "windhawk", "System", "Idle",
        };

        public static void GuardarLista(IEnumerable<string> nombres)
        {
            var l = nombres.Select(x => x.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase)).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            try { File.WriteAllText(ListaFile, JsonSerializer.Serialize(l, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { Log.Error("No pude guardar gaming-pausar.json: " + ex.Message); }
        }

        public static bool EsProhibido(string nombre) => Prohibidos.Contains(nombre);

        private sealed record Pausado(int Pid, string Nombre, long Inicio);

        public static List<string> Lista()
        {
            try
            {
                if (File.Exists(ListaFile))
                    return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ListaFile)) ?? new();
            }
            catch (Exception ex) { Log.Error("gaming-pausar.json ilegible: " + ex.Message); }
            var def = new List<string> { "Notion", "Canva", "WhatsApp", "WhatsApp.Root" };
            try { File.WriteAllText(ListaFile, JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = true })); } catch { }
            return def;
        }

        /// <summary>Congela las apps de la lista (menos el juego).</summary>
        private static readonly object Gate = new();
        public static void Pausar(string? procesoJuego) { lock (Gate) PausarImpl(procesoJuego); }
        public static void Reanudar() { lock (Gate) ReanudarImpl(); }

        private static void PausarImpl(string? procesoJuego)
        {
            var ya = Cargar();
            int n = 0; long liberado = 0;
            foreach (var nombre in Lista().Where(x => !Prohibidos.Contains(x)))
            {
                foreach (var p in Process.GetProcessesByName(nombre))
                {
                    try
                    {
                        if (string.Equals(p.ProcessName, procesoJuego, StringComparison.OrdinalIgnoreCase)) continue;
                        if (ya.Any(x => x.Pid == p.Id)) continue;
                        var antes = p.WorkingSet64;
                        if (NtSuspendProcess(p.Handle) != 0) continue;
                        EmptyWorkingSet(p.Handle);
                        ya.Add(new Pausado(p.Id, p.ProcessName, p.StartTime.ToFileTimeUtc()));
                        n++; liberado += antes;
                    }
                    catch (Exception ex) { Log.Error($"No pude congelar {nombre} ({p.Id}): {ex.Message}"); }
                    finally { p.Dispose(); }
                }
            }
            Guardar(ya);
            if (n > 0) Log.Info($"Gaming: {n} procesos congelados (~{liberado / 1048576} MB de RAM liberados)");
        }

        /// <summary>Descongela todo lo que okla congeló.</summary>
        private static void ReanudarImpl()
        {
            var lista = Cargar();
            if (lista.Count == 0) return;
            int n = 0;
            foreach (var x in lista)
            {
                try
                {
                    using var p = Process.GetProcessById(x.Pid);
                    // Mismo PID pero otro programa (Windows reutiliza los números): no se toca.
                    if (p.StartTime.ToFileTimeUtc() != x.Inicio) continue;
                    if (NtResumeProcess(p.Handle) == 0) n++;
                }
                catch (ArgumentException) { /* ya no existe */ }
                catch (Exception ex) { Log.Error($"No pude descongelar {x.Nombre} ({x.Pid}): {ex.Message}"); }
            }
            Guardar(new());
            Log.Info($"Gaming: {n} procesos descongelados");
        }

        private static List<Pausado> Cargar()
        {
            try { if (File.Exists(EstadoFile)) return JsonSerializer.Deserialize<List<Pausado>>(File.ReadAllText(EstadoFile)) ?? new(); }
            catch (Exception ex) { Log.Error("pausados.json ilegible: " + ex.Message); }
            return new();
        }

        private static void Guardar(List<Pausado> l)
        {
            try { File.WriteAllText(EstadoFile, JsonSerializer.Serialize(l)); } catch (Exception ex) { Log.Error("No pude guardar pausados.json: " + ex.Message); }
        }
    }
}
