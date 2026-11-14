using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;

namespace Okla
{
    /// <summary>
    /// Iconos de cada modo (recursos\iconos-modos\&lt;clave&gt;.png, 256 px; "panico" para el pánico).
    /// Si falta alguno, se devuelve null y quien lo usa pone el punto de color de siempre.
    /// </summary>
    internal static class IconosModos
    {
        private static readonly Dictionary<string, System.Drawing.Image?> _gdi = new();
        private static readonly Dictionary<string, BitmapImage?> _wpf = new();

        private static string Ruta(string clave) => Paths.Recurso(Path.Combine("iconos-modos", clave + ".png"));

        /// <summary>Para menús de WinForms (bandeja), ya reducido a 48 px con buena calidad.</summary>
        public static System.Drawing.Image? Gdi(string clave)
        {
            lock (_gdi)
            {
                if (_gdi.TryGetValue(clave, out var i)) return i;
                try
                {
                    var f = Ruta(clave);
                    if (File.Exists(f))
                    {
                        using var src = System.Drawing.Image.FromFile(f);
                        var b = new System.Drawing.Bitmap(48, 48);
                        using (var g = System.Drawing.Graphics.FromImage(b))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            g.DrawImage(src, 0, 0, 48, 48);
                        }
                        i = b;
                    }
                }
                catch (Exception ex) { Log.Error($"Icono de modo {clave}: {ex.Message}"); }
                return _gdi[clave] = i;
            }
        }

        /// <summary>A tamaño completo (256 px), para la pantalla de carga en pantallas 4K. "okla" = el icono de okla.</summary>
        public static BitmapImage? Grande(string clave)
        {
            var f = clave == "okla" ? Paths.Recurso(Path.Combine("icono", "okla.png")) : Ruta(clave);
            lock (_wpf)
            {
                if (_wpf.TryGetValue("grande:" + clave, out var i)) return i;
                try
                {
                    if (File.Exists(f))
                    {
                        i = new BitmapImage();
                        i.BeginInit();
                        i.CacheOption = BitmapCacheOption.OnLoad;
                        i.DecodePixelWidth = 256;
                        i.UriSource = new Uri(f);
                        i.EndInit();
                        i.Freeze();
                    }
                }
                catch (Exception ex) { Log.Error($"Icono grande {clave}: {ex.Message}"); }
                return _wpf["grande:" + clave] = i;
            }
        }

        /// <summary>Para ventanas WPF (editor, hub).</summary>
        public static BitmapImage? Wpf(string clave)
        {
            lock (_wpf)
            {
                if (_wpf.TryGetValue(clave, out var i)) return i;
                try
                {
                    var f = Ruta(clave);
                    if (File.Exists(f))
                    {
                        i = new BitmapImage();
                        i.BeginInit();
                        i.CacheOption = BitmapCacheOption.OnLoad;  // no deja el archivo bloqueado
                        i.DecodePixelWidth = 96;
                        i.UriSource = new Uri(f);
                        i.EndInit();
                        i.Freeze();
                    }
                }
                catch (Exception ex) { Log.Error($"Icono de modo {clave}: {ex.Message}"); }
                return _wpf[clave] = i;
            }
        }
    }
}
