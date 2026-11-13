using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Okla
{
    /// <summary>
    /// Vigila OBS por obs-websocket (v5, puerto y contraseña en config\obs.json) y avisa cuando empieza o
    /// termina una grabación. Solo se conecta mientras OBS está abierto; si OBS se cierra, vuelve a esperar.
    /// </summary>
    internal sealed class ObsWatcher : IDisposable
    {
        public event Action? GrabacionEmpezo;
        public event Action? GrabacionTermino;

        private readonly Timer _timer;
        private ClientWebSocket? _ws;
        private int _conectando;
        private bool _grabando;
        private int _req;

        public ObsWatcher() => _timer = new Timer(_ => Revisar(), null, 3000, 5000);

        public bool Conectado => _ws?.State == WebSocketState.Open;
        public bool Grabando => _grabando;

        private void Revisar()
        {
            if (Conectado || System.Diagnostics.Process.GetProcessesByName("obs64").Length == 0) return;
            if (Interlocked.Exchange(ref _conectando, 1) == 1) return;
            _ = Task.Run(async () =>
            {
                try { await ConectarYEscuchar(); }
                catch (Exception ex) { Log.Info("OBS: sin conexión (" + ex.Message + ")"); }
                finally
                {
                    _ws?.Dispose(); _ws = null;
                    Interlocked.Exchange(ref _conectando, 0);
                    // OBS cerrado a mitad de grabación: se da por terminada.
                    if (_grabando) { _grabando = false; GrabacionTermino?.Invoke(); }
                }
            });
        }

        private async Task ConectarYEscuchar()
        {
            var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(Paths.Config, "obs.json")))!;
            int puerto = (int?)cfg["puerto"] ?? 4455;
            string pw = (string?)cfg["contrasena"] ?? "";

            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri($"ws://127.0.0.1:{puerto}"), CancellationToken.None);

            var hello = await Recibir();
            string? auth = null;
            if (hello?["d"]?["authentication"] is JsonNode a)
            {
                using var sha = SHA256.Create();
                var secret = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(pw + (string?)a["salt"])));
                auth = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(secret + (string?)a["challenge"])));
            }
            // eventSubscriptions 64 = Outputs (RecordStateChanged)
            await Enviar(new JsonObject { ["op"] = 1, ["d"] = new JsonObject { ["rpcVersion"] = 1, ["authentication"] = auth, ["eventSubscriptions"] = 64 } });
            var idf = await Recibir();
            if ((int?)idf?["op"] != 2) throw new InvalidOperationException("OBS rechazó la contraseña");
            Log.Info("OBS: conectado");
            await Pedir("GetRecordStatus", "estado");

            while (_ws.State == WebSocketState.Open)
            {
                var m = await Recibir();
                if (m == null) break;
                int op = (int?)m["op"] ?? -1;
                var d = m["d"];
                if (op == 5 && (string?)d?["eventType"] == "RecordStateChanged")
                {
                    var estado = (string?)d?["eventData"]?["outputState"];
                    if (estado == "OBS_WEBSOCKET_OUTPUT_STARTED" && !_grabando) { _grabando = true; Log.Info("OBS: grabación iniciada"); GrabacionEmpezo?.Invoke(); }
                    else if (estado == "OBS_WEBSOCKET_OUTPUT_STOPPED" && _grabando) { _grabando = false; Log.Info("OBS: grabación terminada"); GrabacionTermino?.Invoke(); }
                }
                else if (op == 7 && (string?)d?["requestId"] == "estado" && (bool?)d?["responseData"]?["outputActive"] == true && !_grabando)
                {
                    _grabando = true; Log.Info("OBS: ya estaba grabando"); GrabacionEmpezo?.Invoke();
                }
            }
        }

        /// <summary>Manda una petición sin esperar respuesta (PauseRecord, ResumeRecord…).</summary>
        public void Enviar(string tipo)
        {
            if (!Conectado) return;
            _ = Pedir(tipo, "okla" + Interlocked.Increment(ref _req));
        }

        private readonly SemaphoreSlim _envio = new(1, 1);

        private Task Pedir(string tipo, string id) =>
            Enviar(new JsonObject { ["op"] = 6, ["d"] = new JsonObject { ["requestType"] = tipo, ["requestId"] = id } });

        private async Task Enviar(JsonObject msg)
        {
            var b = Encoding.UTF8.GetBytes(msg.ToJsonString());
            await _envio.WaitAsync();
            try { await _ws!.SendAsync(b, WebSocketMessageType.Text, true, CancellationToken.None); }
            finally { _envio.Release(); }
        }

        private async Task<JsonNode?> Recibir()
        {
            var buf = new byte[65536];
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await _ws!.ReceiveAsync(buf, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            return JsonNode.Parse(ms.ToArray());
        }

        public void Dispose() { _timer.Dispose(); _ws?.Dispose(); }
    }
}
