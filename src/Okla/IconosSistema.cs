using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Okla
{
    /// <summary>
    /// Iconos del sistema por modo (punto 17, tipo Resource Redirect): carpetas, papelera y Este equipo.
    /// Los ICO se preparan una sola vez (tools\crear-iconos.ps1 → recursos\iconos\&lt;tema&gt;\sistema); al cambiar
    /// de modo solo se tocan unas claves del registro y se refresca la caché de iconos (≈1 s, dentro de la
    /// pantalla de carga). Si el tema es el mismo que ya está puesto, no se hace nada.
    /// Lo que Windows tenía se guarda la primera vez (config\iconos-sistema-original.json) y vuelve con
    /// "Windows" o con el pánico.
    /// Ajuste por modo: Ajustes["iconos-sistema"] = fedora | gaming | potencia | 0. Sin ajuste: el tema del propio modo.
    /// </summary>
    internal static class IconosSistema
    {
        public static string PorDefecto(string modo) => modo switch { "A" => "fedora", "J" => "gaming", "G" => "potencia", _ => "0" };

        private const string Papelera = @"Software\Microsoft\Windows\CurrentVersion\Explorer\CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\DefaultIcon";
        private const string Equipo = @"Software\Microsoft\Windows\CurrentVersion\Explorer\CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\DefaultIcon";
        private const string ShellIcons = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons";   // HKLM: okla corre elevado
        private static string ArchivoOriginal => Path.Combine(Paths.Config, "iconos-sistema-original.json");
        private static string ArchivoActual => Path.Combine(Paths.Config, "iconos-sistema.txt");

        private sealed class Original
        {
            public string? PapeleraDef { get; set; }
            public string? PapeleraVacia { get; set; }
            public string? PapeleraLlena { get; set; }
            public string? Equipo { get; set; }
            public string? Carpeta3 { get; set; }
            public string? Carpeta4 { get; set; }
        }

        /// <summary>Pone el tema (o "0" = lo de Windows). Devuelve si cambió algo. Nunca lanza.</summary>
        public static bool Aplicar(string? tema)
        {
            tema = string.IsNullOrEmpty(tema) ? "0" : tema;
            try
            {
                string actual = File.Exists(ArchivoActual) ? File.ReadAllText(ArchivoActual).Trim() : "0";
                var dir = Path.Combine(Paths.Recursos, "iconos", tema, "sistema");
                if (tema != "0" && !File.Exists(Path.Combine(dir, "carpeta.ico")))
                {
                    Log.Info($"Iconos del sistema: el tema {tema} no está preparado (crear-iconos.ps1); se deja como está");
                    return false;
                }
                if (tema == actual) return false;
                var sw = Stopwatch.StartNew();
                GuardarOriginal();

                if (tema == "0") Restaurar();
                else
                {
                    string Ico(string n) => Path.Combine(dir, n + ".ico") + ",0";
                    using (var k = Registry.CurrentUser.CreateSubKey(Papelera))
                    {
                        k.SetValue("empty", Ico("papelera"));
                        k.SetValue("full", Ico("papelera-llena"));
                        k.SetValue("", PapeleraLlena() ? Ico("papelera-llena") : Ico("papelera"));
                    }
                    using (var k = Registry.CurrentUser.CreateSubKey(Equipo)) k.SetValue("", Ico("equipo"));
                    using (var k = Registry.LocalMachine.CreateSubKey(ShellIcons))
                    {
                        k.SetValue("3", Ico("carpeta"));
                        k.SetValue("4", Ico("carpeta"));
                    }
                }
                File.WriteAllText(ArchivoActual, tema);
                Refrescar();
                Log.Info($"  iconos del sistema ({sw.ElapsedMilliseconds} ms): {actual} -> {tema}");
                return true;
            }
            catch (Exception ex) { Log.Error("Iconos del sistema: " + ex.Message); return false; }
        }

        private static void GuardarOriginal()
        {
            if (File.Exists(ArchivoOriginal)) return;
            var o = new Original();
            using (var k = Registry.CurrentUser.OpenSubKey(Papelera))
            {
                o.PapeleraDef = k?.GetValue("") as string; o.PapeleraVacia = k?.GetValue("empty") as string; o.PapeleraLlena = k?.GetValue("full") as string;
            }
            using (var k = Registry.CurrentUser.OpenSubKey(Equipo)) o.Equipo = k?.GetValue("") as string;
            using (var k = Registry.LocalMachine.OpenSubKey(ShellIcons)) { o.Carpeta3 = k?.GetValue("3") as string; o.Carpeta4 = k?.GetValue("4") as string; }
            File.WriteAllText(ArchivoOriginal, JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static void Restaurar()
        {
            Original o;
            try { o = JsonSerializer.Deserialize<Original>(File.ReadAllText(ArchivoOriginal)) ?? new(); } catch { o = new(); }
            const string imageres = @"%SystemRoot%\System32\imageres.dll";
            using (var k = Registry.CurrentUser.CreateSubKey(Papelera))
            {
                var vacia = o.PapeleraVacia ?? imageres + ",-55";
                var llena = o.PapeleraLlena ?? imageres + ",-54";
                k.SetValue("empty", vacia, RegistryValueKind.ExpandString);
                k.SetValue("full", llena, RegistryValueKind.ExpandString);
                k.SetValue("", PapeleraLlena() ? llena : vacia, RegistryValueKind.ExpandString);
            }
            using (var k = Registry.CurrentUser.CreateSubKey(Equipo))
                k.SetValue("", o.Equipo ?? imageres + ",-109", RegistryValueKind.ExpandString);
            using (var k = Registry.LocalMachine.CreateSubKey(ShellIcons))
            {
                Poner(k, "3", o.Carpeta3);
                Poner(k, "4", o.Carpeta4);
            }
            static void Poner(RegistryKey k, string n, string? v) { if (v == null) k.DeleteValue(n, false); else k.SetValue(n, v); }
        }

        /// <summary>Avisa al Explorador y vacía la caché de iconos (sin reiniciar el Explorador).</summary>
        private static void Refrescar()
        {
            SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
            try
            {
                using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ie4uinit.exe"), "-show") { UseShellExecute = false, CreateNoWindow = true });
                p?.WaitForExit(4000);
            }
            catch { }
        }

        private static bool PapeleraLlena()
        {
            var i = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBin(null, ref i) == 0 && i.i64NumItems > 0;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);
        [DllImport("shell32.dll")] private static extern void SHChangeNotify(int e, uint f, IntPtr a, IntPtr b);
    }
}
