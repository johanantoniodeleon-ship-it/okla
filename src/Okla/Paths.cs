using System;
using System.IO;

namespace Okla
{
    /// <summary>Rutas de okla. Los datos viven en %USERPROFILE%\okla (config, recursos, logs); el programa, donde se instale.</summary>
    internal static class Paths
    {
        public static readonly string Root =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "okla");

        public static string Recursos => Path.Combine(Root, "recursos");
        public static string Sonidos => Path.Combine(Recursos, "sonidos");

        /// <summary>Un recurso que viene con okla: primero en la carpeta del usuario (por si lo cambió), si no, junto al exe.</summary>
        public static string Recurso(string rel)
        {
            var propio = Path.Combine(Recursos, rel);
            if (File.Exists(propio)) return propio;
            return Path.Combine(AppContext.BaseDirectory, "recursos", rel);
        }
        public static string Config => Path.Combine(Root, "config");
        public static string Logs => Path.Combine(Root, "logs");
        public static string ModesFile => Path.Combine(Config, "modos.json");
        public static string StateFile => Path.Combine(Config, "estado.json");

        public static void Ensure()
        {
            Directory.CreateDirectory(Config);
            Directory.CreateDirectory(Logs);
            Directory.CreateDirectory(Sonidos);
        }
    }

    internal static class Log
    {
        private static readonly object Gate = new();

        public static void Info(string msg) => Write("INFO", msg);
        public static void Error(string msg) => Write("ERROR", msg);

        private static void Write(string level, string msg)
        {
            try
            {
                lock (Gate)
                {
                    var file = Path.Combine(Paths.Logs, $"okla-{DateTime.Now:yyyy-MM-dd}.log");
                    File.AppendAllText(file, $"[{DateTime.Now:HH:mm:ss}] [{level}] {msg}{Environment.NewLine}");
                }
            }
            catch { /* el log nunca debe tumbar la app */ }
        }
    }
}
