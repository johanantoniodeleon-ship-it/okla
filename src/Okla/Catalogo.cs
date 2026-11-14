using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Okla
{
    /// <summary>
    /// Catálogo de fondos en línea (punto 13): okla lee catalogo\fondos.json del repositorio de GitHub y baja,
    /// de la web original (motionbgs), los fondos que falten de los modos activos. Así, añadir un fondo al catálogo
    /// lo hace llegar a todos sin publicar otra versión. Los fondos no se vuelven a subir a ningún sitio.
    /// Si el usuario borra un fondo que okla bajó, no se vuelve a bajar (config\catalogo.json).
    /// </summary>
    internal static class Catalogo
    {
        private sealed class Estado
        {
            public HashSet<int> Bajados { get; set; } = new();   // ids de motionbgs que okla ya tuvo en disco
            public DateTime UltimaRevision { get; set; }
        }

        private static readonly string Archivo = Path.Combine(Paths.Config, "catalogo.json");
        private static readonly SemaphoreSlim _uno = new(1, 1);

        /// <summary>Carpeta de recursos -> clave del modo.</summary>
        private static string Modo(string carpeta) => carpeta switch
        { "gaming" => "J", "fedora" => "A", "potencia" => "G", "presentacion" => "P", "bateria" => "B", _ => "" };

        private static Estado Leer()
        {
            try { if (File.Exists(Archivo)) return JsonSerializer.Deserialize<Estado>(File.ReadAllText(Archivo)) ?? new(); } catch { }
            return new();
        }
        private static void Guardar(Estado e)
        {
            try { File.WriteAllText(Archivo, JsonSerializer.Serialize(e, new JsonSerializerOptions { WriteIndented = true })); } catch { }
        }

        /// <summary>
        /// Revisa el catálogo y baja lo que falte (de uno en uno). <paramref name="puede"/> se consulta antes de cada
        /// descarga: si devuelve false (juego, grabación, batería) se para y se sigue en la próxima revisión.
        /// Devuelve cuántos fondos nuevos hay por modo.
        /// </summary>
        public static async Task<Dictionary<string, int>> Revisar(IReadOnlyCollection<string> modosActivos, Func<bool> puede, CancellationToken ct = default)
        {
            var nuevos = new Dictionary<string, int>();
            if (!await _uno.WaitAsync(0, ct)) return nuevos;   // ya hay una revisión en marcha
            try
            {
                string json;
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("okla");
                    json = await http.GetStringAsync($"https://raw.githubusercontent.com/{Instalador.Repo}/main/catalogo/fondos.json", ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { Log.Info("Catálogo en línea no disponible: " + ex.Message); return nuevos; }

                var cat = JsonNode.Parse(json)!;
                string plantilla = (string?)cat["descarga"] ?? "https://motionbgs.com/dl/4k/{id}";
                string respaldo = (string?)cat["respaldo"] ?? "https://motionbgs.com/dl/hd/{id}";
                var todos = cat["videos"]!.AsArray()
                    .Select(v => (carpeta: (string)v!["modo"]!, nombre: (string)v["nombre"]!, id: (int)v["motionbgs"]!)).ToList();

                var e = Leer();
                // Lo que ya está en disco cuenta como bajado (instalaciones viejas y esta laptop).
                foreach (var v in todos) if (File.Exists(Mp4(v.carpeta, v.nombre))) e.Bajados.Add(v.id);
                e.UltimaRevision = DateTime.Now;
                Guardar(e);

                var faltan = todos.Where(v => modosActivos.Contains(Modo(v.carpeta)) && !e.Bajados.Contains(v.id)).ToList();
                if (faltan.Count == 0) return nuevos;
                Log.Info(faltan.Count == 1 ? "Catálogo: 1 fondo nuevo para bajar" : $"Catálogo: {faltan.Count} fondos nuevos para bajar");

                var ff = Adaptador.FFmpeg();
                foreach (var v in faltan)
                {
                    if (!puede()) { Log.Info("Catálogo: descarga en pausa (juego, grabación o batería)"); break; }
                    var mp4 = Mp4(v.carpeta, v.nombre);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(mp4)!);
                        void Nada(string _, double __) { }
                        try { await Instalador.Descargar(plantilla.Replace("{id}", v.id.ToString()), mp4, Nada, v.nombre, ct); }
                        catch (HttpRequestException) { await Instalador.Descargar(respaldo.Replace("{id}", v.id.ToString()), mp4, Nada, v.nombre, ct); }
                        if (ff != null)
                        {
                            using var pr = Process.Start(new ProcessStartInfo(ff, $"-hide_banner -loglevel error -y -ss 1 -i \"{mp4}\" -frames:v 1 -q:v 2 \"{Path.ChangeExtension(mp4, ".jpg")}\"")
                            { UseShellExecute = false, CreateNoWindow = true })!;
                            await pr.WaitForExitAsync(ct);
                        }
                        e.Bajados.Add(v.id); Guardar(e);
                        var k = Modo(v.carpeta);
                        nuevos[k] = nuevos.GetValueOrDefault(k) + 1;
                        Log.Info($"Catálogo: bajado {v.carpeta}/{v.nombre}");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Log.Error($"Catálogo: {v.nombre}: {ex.Message}");
                        try { File.Delete(mp4 + ".part"); } catch { }
                    }
                }
                return nuevos;
            }
            finally { _uno.Release(); }
        }

        private static string Mp4(string carpeta, string nombre) => Path.Combine(Paths.Recursos, "videos", carpeta, nombre + ".mp4");
    }
}
