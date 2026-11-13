using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Okla
{
    /// <summary>
    /// Actualizaciones automáticas con Velopack desde las Releases de GitHub del repositorio de okla.
    /// Solo funciona en okla instalado con el Setup.exe (en la copia de desarrollo de esta laptop no hace nada).
    /// </summary>
    internal static class Actualizaciones
    {
        private static UpdateManager? _mgr;
        private static UpdateInfo? _lista;   // descargada y pendiente de aplicar

        private static UpdateManager Mgr => _mgr ??= new UpdateManager(
            new GithubSource($"https://github.com/{Instalador.Repo}", null, false));

        public static bool Instalado { get { try { return Mgr.IsInstalled; } catch { return false; } } }
        public static string Version { get { try { return Mgr.CurrentVersion?.ToString() ?? "desarrollo"; } catch { return "desarrollo"; } } }

        /// <summary>Busca y descarga una versión nueva. Devuelve su número si quedó lista para aplicar.</summary>
        public static async Task<string?> Buscar()
        {
            if (!Instalado) return null;
            if (_lista != null) return _lista.TargetFullRelease.Version.ToString();
            try
            {
                var info = await Mgr.CheckForUpdatesAsync();
                if (info == null) return null;
                Log.Info($"Actualización disponible: {Version} -> {info.TargetFullRelease.Version}");
                await Mgr.DownloadUpdatesAsync(info);
                _lista = info;
                return info.TargetFullRelease.Version.ToString();
            }
            catch (Exception ex) { Log.Info("Sin actualizaciones (" + ex.Message + ")"); return null; }
        }

        /// <summary>Aplica la versión descargada: Velopack espera a que okla se cierre, la instala y lo vuelve a abrir.</summary>
        public static bool Aplicar()
        {
            if (_lista == null) return false;
            try
            {
                Log.Info("Aplicando actualización " + _lista.TargetFullRelease.Version);
                Mgr.WaitExitThenApplyUpdates(_lista.TargetFullRelease, true, true);
                return true;
            }
            catch (Exception ex) { Log.Error("No se pudo aplicar la actualización: " + ex.Message); return false; }
        }
    }
}
