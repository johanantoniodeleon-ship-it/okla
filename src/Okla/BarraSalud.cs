using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Okla
{
    /// <summary>
    /// Vigila que la barra de tareas responda. Con varios mods de Windhawk cambiando a la vez, el hilo de la barra
    /// del Explorador puede quedarse colgado: la barra se ve, pero sin estilo (blanca, sin la isla ni la transparencia)
    /// y todo lo que le habla se queda esperando. Si pasa, okla reinicia el Explorador (como haría el usuario a mano).
    /// </summary>
    internal static class BarraSalud
    {
        private static DateTime _ultimoReinicio = DateTime.MinValue;

        public static bool Responde(uint ms = 2000)
        {
            var h = FindWindow("Shell_TrayWnd", null);
            if (h == IntPtr.Zero) return true;   // sin barra (el Explorador está arrancando): no es un cuelgue
            return SendMessageTimeout(h, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero, 0x2 /* SMTO_ABORTIFHUNG */, ms, out _) != IntPtr.Zero;
        }

        /// <summary>Si la barra no responde durante unos segundos, reinicia el Explorador. Devuelve si lo reinició.</summary>
        public static bool Revisar()
        {
            if (Responde()) return false;
            for (int i = 0; i < 4; i++) { Thread.Sleep(1500); if (Responde()) return false; }
            if (DateTime.Now - _ultimoReinicio < TimeSpan.FromMinutes(2)) { Log.Error("La barra de tareas no responde (ya se reinició hace poco)"); return false; }
            _ultimoReinicio = DateTime.Now;
            Log.Error("La barra de tareas no responde: reinicio el Explorador");
            try
            {
                foreach (var p in Process.GetProcessesByName("explorer")) { try { p.Kill(); } catch { } }
                // Windows vuelve a abrir el Explorador solo (AutoRestartShell). Si no, se abre sin permisos de administrador.
                for (int i = 0; i < 10 && Process.GetProcessesByName("explorer").Length == 0; i++) Thread.Sleep(500);
                if (Process.GetProcessesByName("explorer").Length == 0)
                    Process.Start(new ProcessStartInfo("runas.exe", "/trustlevel:0x20000 explorer.exe") { UseShellExecute = false, CreateNoWindow = true });
                for (int i = 0; i < 20 && FindWindow("Shell_TrayWnd", null) == IntPtr.Zero; i++) Thread.Sleep(500);
                Thread.Sleep(3000);   // que Windhawk vuelva a cargar los mods de la barra
                WindhawkModSetting.Rebump("windows-11-taskbar-styler");
            }
            catch (Exception ex) { Log.Error("Reiniciar el Explorador: " + ex.Message); }
            return true;
        }

        /// <summary>Reinicia el menú Inicio y el buscador (Windows los vuelve a abrir solos).</summary>
        public static void ReiniciarInicio()
        {
            int n = 0;
            foreach (var nombre in new[] { "StartMenuExperienceHost", "SearchHost" })
                foreach (var p in Process.GetProcessesByName(nombre))
                    try { p.Kill(); p.WaitForExit(2000); n++; } catch (Exception ex) { Log.Error($"Reiniciar {nombre}: {ex.Message}"); }
            Log.Info($"  menú Inicio y buscador reiniciados ({n})");
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string c, string? t);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint f, uint t, out IntPtr r);
    }
}
