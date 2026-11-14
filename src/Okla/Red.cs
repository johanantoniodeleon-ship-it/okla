using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Okla
{
    /// <summary>Lo que okla recuerda de la red: config\red.json.</summary>
    internal sealed class EstadoRed
    {
        public bool Compartir { get; set; } = true;
        public Guid Equipo { get; set; } = Guid.NewGuid();   // número al azar, no sale del hardware ni del usuario
        public DateTime UltimoInforme { get; set; }
        public string UltimaVersion { get; set; } = "";
        public bool Avisado { get; set; }
    }

    /// <summary>
    /// okla conectada (punto 13): manda un informe técnico anónimo del equipo para afinar el regulador
    /// y las opiniones de los usuarios (punto 15) a Supabase. El servicio solo deja AÑADIR filas:
    /// nadie puede leer lo que mandan otros con la clave pública que lleva okla.
    /// Qué se manda: procesador, RAM, gráficas, pantalla, nivel, versión de Windows, modos activos y
    /// los últimos errores del log (sin rutas con el nombre del usuario). Nunca archivos, nombres ni cuentas.
    /// </summary>
    internal static class Red
    {
        // Proyecto de Supabase. Vacío = okla no se conecta a nada.
        public const string Url = "https://aygmooqjsxbhxgwdxvqk.supabase.co";
        public const string Clave = "sb_publishable_eewcsp6AimkpsxhHYPpTAw_9TS5Nxsl";   // clave pública (publishable/anon): solo sirve para añadir filas
        public static bool Configurado => Url.Length > 0 && Clave.Length > 0;

        private static readonly string Archivo = Path.Combine(Paths.Config, "red.json");
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
        private static EstadoRed? _estado;

        public static EstadoRed Estado
        {
            get
            {
                if (_estado != null) return _estado;
                try { if (File.Exists(Archivo)) _estado = JsonSerializer.Deserialize<EstadoRed>(File.ReadAllText(Archivo)); } catch { }
                if (_estado == null) { _estado = new EstadoRed(); Guardar(); }
                return _estado;
            }
        }

        public static void Guardar()
        {
            try { File.WriteAllText(Archivo, JsonSerializer.Serialize(_estado ?? new EstadoRed(), Json)); } catch (Exception ex) { Log.Error("red.json: " + ex.Message); }
        }

        public static bool Compartir
        {
            get => Estado.Compartir;
            set { Estado.Compartir = value; Guardar(); Log.Info("Compartir datos técnicos: " + (value ? "sí" : "no")); }
        }

        /// <summary>Informe del equipo: al cambiar de versión o si pasó una semana. Nunca lanza.</summary>
        public static async Task EnviarInforme(IEnumerable<string> modosActivos)
        {
            try
            {
                if (!Configurado || !Compartir) return;
                var e = Estado;
                var version = Actualizaciones.Version;
                if (e.UltimaVersion == version && DateTime.Now - e.UltimoInforme < TimeSpan.FromDays(7)) return;

                var p = Regulador.Perfil;
                var datos = new Dictionary<string, object?>
                {
                    ["cpu"] = p.Cpu, ["hilos"] = p.Hilos, ["ramGB"] = p.RamGB,
                    ["graficas"] = p.Graficas, ["vramGB"] = p.VramGB, ["dedicada"] = p.Dedicada, ["portatil"] = p.Portatil,
                    ["pantalla"] = p.Pantalla, ["puntos"] = p.Puntos,
                    ["nivel"] = Regulador.Nombre(p.Detectado), ["nivelForzado"] = Regulador.Forzado is { } f ? Regulador.Nombre(f) : null,
                    ["windows"] = Environment.OSVersion.Version.ToString(),
                    ["modos"] = modosActivos.ToList(),
                    ["errores"] = ErroresRecientes(),
                };
                if (await Insertar("informes", new { equipo = e.Equipo, version, datos }))
                {
                    e.UltimaVersion = version; e.UltimoInforme = DateTime.Now; Guardar();
                    Log.Info("Informe técnico enviado");
                }
            }
            catch (Exception ex) { Log.Info("Informe técnico no enviado: " + ex.Message); }
        }

        /// <summary>Manda una opinión (punto 15). Devuelve si llegó.</summary>
        public static async Task<bool> EnviarOpinion(string texto, string? modo)
        {
            if (!Configurado) return false;
            texto = Anonimizar(texto.Trim());
            if (texto.Length == 0) return false;
            if (texto.Length > 2000) texto = texto[..2000];
            try { return await Insertar("opiniones", new { equipo = Estado.Equipo, version = Actualizaciones.Version, modo, texto }); }
            catch (Exception ex) { Log.Info("Opinión no enviada: " + ex.Message); return false; }
        }

        private static async Task<bool> Insertar(string tabla, object fila)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Url.TrimEnd('/')}/rest/v1/{tabla}")
            {
                Content = new StringContent(JsonSerializer.Serialize(fila), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("apikey", Clave);
            req.Headers.Add("Prefer", "return=minimal");
            using var r = await Http.SendAsync(req);
            if (!r.IsSuccessStatusCode) Log.Info($"Red ({tabla}): {(int)r.StatusCode} {Recortar(await r.Content.ReadAsStringAsync(), 200)}");
            return r.IsSuccessStatusCode;
        }

        /// <summary>Errores distintos de los logs de la última semana, sin rutas personales (máx. 20).</summary>
        private static List<string> ErroresRecientes()
        {
            var r = new List<string>();
            try
            {
                foreach (var f in new DirectoryInfo(Paths.Logs).GetFiles("okla-*.log")
                                     .Where(f => f.LastWriteTime > DateTime.Now.AddDays(-7)).OrderByDescending(f => f.LastWriteTime))
                {
                    foreach (var l in File.ReadLines(f.FullName).Reverse())
                    {
                        var i = l.IndexOf("[ERROR] ", StringComparison.Ordinal);
                        if (i < 0) continue;
                        var m = Recortar(Anonimizar(l[(i + 8)..]), 300);
                        if (!r.Contains(m)) r.Add(m);
                        if (r.Count >= 20) return r;
                    }
                }
            }
            catch { }
            return r;
        }

        /// <summary>Quita la carpeta del usuario y su nombre de cualquier texto.</summary>
        public static string Anonimizar(string s)
        {
            var perfil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (perfil.Length > 0) s = s.Replace(perfil, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            foreach (var n in new[] { Environment.UserName, Environment.MachineName })
                if (n.Length > 2) s = System.Text.RegularExpressions.Regex.Replace(s, System.Text.RegularExpressions.Regex.Escape(n), "…", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return s;
        }

        private static string Recortar(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    }
}
