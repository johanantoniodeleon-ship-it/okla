using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Okla
{
    /// <summary>
    /// Clima para los widgets y la barra de arriba de Fedora (windhawk-topbar). Fuente: Open-Meteo
    /// (gratis, sin cuenta ni clave). Solo se envían las coordenadas de la ciudad elegida.
    /// La ciudad se guarda en config\clima.json y se cambia en el editor (pestaña Apariencia > Escritorio).
    /// Sin ciudad, los widgets no muestran el clima.
    /// </summary>
    internal static class Clima
    {
        internal sealed class Lugar
        {
            public string Ciudad { get; set; } = "";
            public double Lat { get; set; }
            public double Lon { get; set; }
        }

        internal sealed record Datos(double Temp, double Max, double Min, int Codigo, bool Dia, DateTime Hora);

        private static string Archivo => Path.Combine(Paths.Config, "clima.json");
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private static Datos? _ultimo;
        private static DateTime _pedido = DateTime.MinValue;

        public static Lugar? Ubicacion()
        {
            try { return File.Exists(Archivo) ? JsonSerializer.Deserialize<Lugar>(File.ReadAllText(Archivo)) : null; }
            catch { return null; }
        }

        /// <summary>Busca la ciudad (geocoding de Open-Meteo), la guarda y la pasa a la barra de Fedora. Devuelve el nombre encontrado o null.</summary>
        public static async Task<string?> Buscar(string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return null;
            try
            {
                var url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=es&format=json&name=" + Uri.EscapeDataString(texto.Trim());
                var r = JsonNode.Parse(await Http.GetStringAsync(url))?["results"]?.AsArray();
                if (r == null || r.Count == 0) return null;
                var p = r[0]!;
                var nombre = (string?)p["name"] ?? texto;
                var pais = (string?)p["country"];
                var l = new Lugar { Ciudad = pais != null ? $"{nombre}, {pais}" : nombre, Lat = (double)p["latitude"]!, Lon = (double)p["longitude"]! };
                Guardar(l);
                return l.Ciudad;
            }
            catch (Exception ex) { Log.Error("Clima, buscar ciudad: " + ex.Message); return null; }
        }

        public static void Guardar(Lugar l)
        {
            Directory.CreateDirectory(Paths.Config);
            File.WriteAllText(Archivo, JsonSerializer.Serialize(l, new JsonSerializerOptions { WriteIndented = true }));
            _ultimo = null; _pedido = DateTime.MinValue;
            ABarra(l);
            Log.Info("Clima: ciudad " + l.Ciudad);
        }

        /// <summary>
        /// La barra de arriba (windhawk-topbar) guarda su ciudad en el almacenamiento local del mod.
        /// Se escribe ahí para que el usuario no tenga que buscarla otra vez en la barra.
        /// </summary>
        public static void ABarra(Lugar? l = null)
        {
            l ??= Ubicacion();
            if (l == null) return;
            try
            {
                using var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Windhawk\Engine\ModsWritable\windhawk-topbar\LocalStorage");
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (k.GetValue("weatherLatitude") as string == l.Lat.ToString("0.####", inv)) return;
                k.SetValue("weatherLocationName", l.Ciudad.Split(',')[0]);
                k.SetValue("weatherLatitude", l.Lat.ToString("0.####", inv));
                k.SetValue("weatherLongitude", l.Lon.ToString("0.####", inv));
            }
            catch (Exception ex) { Log.Error("Clima, barra de Fedora: " + ex.Message); }
        }

        /// <summary>Clima actual (se pide como mucho cada 30 min; si falla la red, devuelve el último que hubo).</summary>
        public static async Task<Datos?> Actual()
        {
            if (_ultimo != null && DateTime.Now - _pedido < TimeSpan.FromMinutes(30)) return _ultimo;
            if (DateTime.Now - _pedido < TimeSpan.FromMinutes(5)) return _ultimo;   // tras un fallo, no insistir cada segundo
            var l = Ubicacion();
            if (l == null) return null;
            _pedido = DateTime.Now;
            try
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var url = $"https://api.open-meteo.com/v1/forecast?latitude={l.Lat.ToString(inv)}&longitude={l.Lon.ToString(inv)}" +
                          "&current=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min&forecast_days=1&timezone=auto";
                var j = JsonNode.Parse(await Http.GetStringAsync(url))!;
                var c = j["current"]!; var d = j["daily"]!;
                _ultimo = new Datos((double)c["temperature_2m"]!, (double)d["temperature_2m_max"]![0]!, (double)d["temperature_2m_min"]![0]!,
                                    (int)c["weather_code"]!, (int)c["is_day"]! == 1, DateTime.Now);
            }
            catch (Exception ex) { Log.Error("Clima: " + ex.Message); }
            return _ultimo;
        }

        /// <summary>Código WMO → (símbolo, texto en español).</summary>
        public static (string icono, string texto) Describir(int codigo, bool dia) => codigo switch
        {
            0 => (dia ? "☀" : "☾", "Despejado"),
            1 => (dia ? "☀" : "☾", "Casi despejado"),
            2 => ("⛅", "Parcialmente nublado"),
            3 => ("☁", "Nublado"),
            45 or 48 => ("≡", "Niebla"),
            51 or 53 or 55 or 56 or 57 => ("☂", "Llovizna"),
            61 or 63 or 66 => ("☂", "Lluvia"),
            65 or 67 => ("☔", "Lluvia fuerte"),
            71 or 73 or 75 or 77 or 85 or 86 => ("❄", "Nieve"),
            80 or 81 => ("☂", "Chubascos"),
            82 => ("☔", "Chubascos fuertes"),
            95 or 96 or 99 => ("⚡", "Tormenta"),
            _ => ("·", ""),
        };
    }
}
