using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Automation;
using Microsoft.Win32;

namespace Okla
{
    /// <summary>
    /// Un mod de Windhawk: activarlo/desactivarlo y cambiar algunos de sus ajustes.
    /// Valor = JSON plano, p.ej. {"activo":"1","theme":"DockLike"}. Solo se tocan las claves que aparezcan.
    /// Windhawk vigila su registro y recarga el mod al ver un SettingsChangeTime nuevo.
    /// </summary>
    internal sealed class WindhawkModSetting : Setting
    {
        private const string Root = @"SOFTWARE\Windhawk\Engine\Mods\";
        private readonly string _mod;
        private readonly string[] _keys;

        public WindhawkModSetting(string mod, string nombre, params string[] keys)
        { _mod = mod; Nombre = "Windhawk · " + nombre; _keys = keys; }

        public override string Id => "wh:" + _mod;
        public override string Nombre { get; }
        private bool? _available;
        /// <summary>Instalado y con permiso de escritura (se comprueba una vez).</summary>
        public override bool Available => _available ??= Check();
        private bool Check()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(Root + _mod, writable: true);
                return k != null;
            }
            catch (Exception) { return false; }
        }

        public override string Read()
        {
            var d = new SortedDictionary<string, string>();
            using var k = Registry.LocalMachine.OpenSubKey(Root + _mod);
            if (k == null) return "{}";
            d["activo"] = (k.GetValue("Disabled") is int dis && dis != 0) ? "0" : "1";
            using var s = k.OpenSubKey("Settings");
            foreach (var key in _keys) d[key] = s?.GetValue(key)?.ToString() ?? "";
            return JsonSerializer.Serialize(d);
        }

        private static Dictionary<string, string> Parse(string v) =>
            JsonSerializer.Deserialize<Dictionary<string, string>>(v) ?? new();

        public override string Normalize(string v) => v.Trim();

        public override bool Matches(string target, string current)
        {
            var t = Parse(target); var c = Parse(current);
            return t.All(kv => c.TryGetValue(kv.Key, out var cv) && cv == kv.Value);
        }

        public override void Apply(string v)
        {
            var t = Parse(v);
            using var k = Registry.LocalMachine.OpenSubKey(Root + _mod, writable: true)
                          ?? throw new InvalidOperationException("mod no instalado");
            bool settingsChanged = false;
            using (var s = k.CreateSubKey("Settings"))
            {
                foreach (var (key, val) in t.Where(kv => kv.Key != "activo"))
                {
                    var old = s.GetValue(key);
                    if (old?.ToString() == val) continue;
                    // Números y sí/no van como DWORD (así los guarda Windhawk), también si el ajuste aún no existía.
                    if ((old is int || old == null) && int.TryParse(val, out var iv)) s.SetValue(key, iv, RegistryValueKind.DWord);
                    else s.SetValue(key, val, RegistryValueKind.String);
                    settingsChanged = true;
                }
            }
            if (settingsChanged) Bump(k);
            if (t.TryGetValue("activo", out var a))
                k.SetValue("Disabled", a == "1" ? 0 : 1, RegistryValueKind.DWord);
            if (settingsChanged) WindhawkDialogs.WatchAsync(_mod);
        }

        internal static void Bump(RegistryKey k)
        {
            int now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int old = k.GetValue("SettingsChangeTime") is int o ? o : 0;
            k.SetValue("SettingsChangeTime", Math.Max(now, old + 1), RegistryValueKind.DWord);
        }

        /// <summary>Para el reintento: vuelve a avisar a Windhawk sin cambiar nada.</summary>
        internal static void Rebump(string mod)
        {
            using var k = Registry.LocalMachine.OpenSubKey(Root + mod, writable: true);
            if (k != null) Bump(k);
        }
    }

    /// <summary>
    /// Algunos mods sacan un aviso "Restarting Explorer is required…" al recargarse.
    /// okla lo cierra con "No" y reintenta una vez (normalmente funciona al segundo intento).
    /// </summary>
    internal static class WindhawkDialogs
    {
        public static void WatchAsync(string mod) => ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var until = DateTime.UtcNow.AddSeconds(5);
                bool retried = false;
                while (DateTime.UtcNow < until)
                {
                    Thread.Sleep(400);
                    AutomationElementCollection dlgs;
                    // UI Automation falla a ratos mientras Explorer recarga; se reintenta en la siguiente vuelta.
                    try { dlgs = AutomationElement.RootElement.FindAll(TreeScope.Children,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)); }
                    catch (Exception) { continue; }
                    foreach (AutomationElement d in dlgs)
                    {
                        if (!d.Current.Name.EndsWith("- Windhawk", StringComparison.Ordinal)) continue;
                        var no = d.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "No"));
                        if (no?.GetCurrentPattern(InvokePattern.Pattern) is InvokePattern ip)
                        {
                            ip.Invoke();
                            Log.Info($"  Windhawk pidió reiniciar Explorer ({d.Current.Name}); respondí No");
                            if (!retried) { retried = true; Thread.Sleep(600); WindhawkModSetting.Rebump(mod); until = DateTime.UtcNow.AddSeconds(4); }
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Error("Vigilante de avisos Windhawk: " + ex.Message); }
        });
    }

    /// <summary>Rainmeter abierto o cerrado (en Gaming se cierra del todo).</summary>
    internal sealed class RainmeterSetting : Setting
    {
        private static readonly string Exe = @"C:\Program Files\Rainmeter\Rainmeter.exe";
        public override string Id => "rainmeter";
        public override string Nombre => "Widgets (Rainmeter)";
        public override string Read() => Process.GetProcessesByName("Rainmeter").Length > 0 ? "abierto" : "cerrado";
        public override string Normalize(string v) => v.Trim().ToLowerInvariant() is "1" or "abierto" or "si" or "sí" ? "abierto" : "cerrado";
        public override void Apply(string v)
        {
            if (!File.Exists(Exe)) throw new FileNotFoundException("Rainmeter no está instalado", Exe);
            if (Normalize(v) == "abierto")
            {
                Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = true });
                return;
            }
            // Cierre inmediato: Rainmeter guarda la posición de los widgets al moverlos, no al salir.
            foreach (var p in Process.GetProcessesByName("Rainmeter")) try { p.Kill(); p.WaitForExit(1000); } catch { }
        }
    }
}
