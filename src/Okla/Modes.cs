using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Okla
{
    public enum Transition { Instantanea, Fundido, Barrido, FundidoLento }

    /// <summary>Definición de un modo. Se guarda en config\modos.json y se podrá editar desde el editor.</summary>
    public sealed class ModeDef
    {
        public string Key { get; set; } = "";      // J, A, G, P
        public string Nombre { get; set; } = "";
        public int Tecla { get; set; }             // 1..4 (Ctrl+Alt+N)
        public string Color { get; set; } = "#88c0d0";
        public string Descripcion { get; set; } = "";
        public Transition Transicion { get; set; } = Transition.Fundido;
        /// <summary>Si está desactivado no sale en el menú ni responde a su atajo (se elige al instalar o en el editor).</summary>
        public bool Activo { get; set; } = true;
        /// <summary>Ajustes de Windows del modo. Lo que no aparezca vuelve a la base.</summary>
        public Dictionary<string, string>? Ajustes { get; set; }
    }

    public sealed class AppState
    {
        public string ModoActual { get; set; } = "A";
    }

    internal static class ModeStore
    {
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        private static string Wh(string activo, string? theme = null, string? efecto = null)
        {
            var d = new Dictionary<string, string> { ["activo"] = activo };
            if (theme != null) d["theme"] = theme;
            if (efecto != null) d["BackgroundEffects.type"] = efecto;
            return JsonSerializer.Serialize(d);
        }

        /// <summary>Windhawk por modo. Gaming apaga todo lo visual; el resto usa Mica (ligero) en vez de acrílico.</summary>
        private static Dictionary<string, string> WhProfile(string key) => key switch
        {
            "J" or "B" => new()
            {
                ["wh:translucent-windows"] = Wh("0"),
                ["wh:taskbar-dock-animation"] = Wh("0"),
                ["wh:windows-11-taskbar-styler"] = Wh("0"),
                ["wh:windows-11-start-menu-styler"] = Wh("0"),
                ["wh:windows-11-file-explorer-styler"] = Wh("0"),
                ["wh:windows-11-notification-center-styler"] = Wh("0"),
                ["wh:windows-11-settings-styler"] = Wh("0"),
            },
            "A" => new()
            {
                ["wh:translucent-windows"] = Wh("1", efecto: "mica"),
                ["wh:taskbar-dock-animation"] = Wh("1"),
                ["wh:windows-11-taskbar-styler"] = Wh("1", "DockLike"),
                ["wh:windows-11-start-menu-styler"] = Wh("1", "SideBySideMinimal"),
                ["wh:windows-11-file-explorer-styler"] = Wh("1", "MicaBar"),
                ["wh:windows-11-notification-center-styler"] = Wh("1", "Unified"),
            },
            "G" => new()
            {
                ["wh:translucent-windows"] = Wh("1", efecto: "mica"),
                // Barra totalmente transparente sobre el video; sin el zoom del dock (descuadraba los iconos con este tema).
                ["wh:taskbar-dock-animation"] = Wh("0"),
                ["wh:windows-11-taskbar-styler"] = Wh("1", "SimplyTransparent"),
                ["wh:windows-11-start-menu-styler"] = Wh("1", "LiquidGlass"),
                ["wh:windows-11-file-explorer-styler"] = Wh("1", "LiquidGlass"),
                ["wh:windows-11-notification-center-styler"] = Wh("1", "LiquidGlass"),
            },
            "P" => new()
            {
                ["wh:translucent-windows"] = Wh("1", efecto: "mica"),
                ["wh:taskbar-dock-animation"] = Wh("0"),
                ["wh:windows-11-taskbar-styler"] = Wh("1", "Pills"),
                ["wh:windows-11-start-menu-styler"] = Wh("1", "Borderless"),
                ["wh:windows-11-file-explorer-styler"] = Wh("1", "Float"),
                ["wh:windows-11-notification-center-styler"] = Wh("1", "Borderless"),
            },
            _ => new()
        };

        private static string WhD(params (string k, string v)[] kv) =>
            JsonSerializer.Serialize(kv.ToDictionary(x => x.k, x => x.v));

        // Mods del catálogo instalados por okla (tools\wh-install.js), cada uno encendido solo en su modo.
        private static readonly Dictionary<string, string[]> ExtraMods = new()
        {
            ["J"] = new[] { "smart-process-priority-ram-optimizer", "ignore-focus-loss", "taskbar-auto-hide-keyboard-only" },
            ["A"] = new[] { "windhawk-topbar", "taskbar-icon-size", "custom-corner-radius", "taskbar-clock-customization", "slick-window-arrangement" },
            ["G"] = new[] { "live-video-wallpaper", "dynamic-island-for-windows", "macos-minimize-animation", "taskbar-system-info", "taskbar-fluent-media-player" },
            ["P"] = new[] { "taskbar-fade", "shake-to-find-cursor", "taskbar-tray-system-icon-tweaks", "zen-desktop-toggle-icons", "monitor-rounded-edges" },
            ["B"] = Array.Empty<string>(),   // Batería: ningún mod
        };

        private static string R(string rel) => Path.Combine(Paths.Recursos, rel);

        /// <summary>
        /// El archivo por defecto de un modo; si en esta PC no está (en otras PCs solo se descargan los fondos del
        /// catálogo), el primero del mismo tipo que haya en las carpetas de ese modo.
        /// </summary>
        private static string Existe(string rel)
        {
            var f = R(rel);
            if (File.Exists(f)) return f;
            var ext = Path.GetExtension(rel);
            var modo = Path.GetFileName(Path.GetDirectoryName(rel)) ?? "";
            foreach (var dir in new[] { R(Path.Combine("videos", modo)), R(Path.Combine("fondos", modo)) })
            {
                if (!Directory.Exists(dir)) continue;
                var x = Directory.GetFiles(dir, "*" + ext).OrderBy(n => n).FirstOrDefault();
                if (x != null) return x;
            }
            return f;
        }

        /// <summary>
        /// Ajuste del mod live-video-wallpaper para este video. Los videos son de 30 fps, pero el mod pide fotogramas con un
        /// temporizador de Windows (de 15,6 en 15,6 ms): con "30" (33 ms) acaba en ~21 fps a tirones. Con "60" revisa cada
        /// 15,6 ms y solo dibuja cuando hay fotograma nuevo, así que se ven los 30 fps sin gastar el doble. Con batería, ~13 fps.
        /// </summary>
        public static string VideoSetting(string fullPath) =>
            WhD(("activo", "1"), ("videoPath", fullPath), ("fitMode", "cover"), ("targetFps", "60"),
                ("filePickerHotkey", "0"), ("visibilityHotkey", "0"), ("batteryMode", "drop"));

        public static string VideoOff() => Wh("0");

        /// <summary>Carpeta de recursos de cada modo (fondos\X y videos\X).</summary>
        public static string Carpeta(string key) => key switch
        { "J" => "gaming", "A" => "fedora", "G" => "potencia", "P" => "presentacion", "B" => "bateria", _ => "" };

        public static void SaveModes(List<ModeDef> modes)
        {
            try { File.WriteAllText(Paths.ModesFile, JsonSerializer.Serialize(modes, Json)); }
            catch (Exception ex) { Log.Error("No pude guardar modos.json: " + ex.Message); }
        }

        public static List<ModeDef> Defaults()
        {
            var list = BaseDefaults();
            var all = ExtraMods.Values.SelectMany(x => x).ToList();
            foreach (var m in list)
            {
                foreach (var (k, v) in WhProfile(m.Key)) m.Ajustes![k] = v;
                var mine = ExtraMods[m.Key];
                foreach (var mod in all) m.Ajustes![$"wh:{mod}"] = Wh(mine.Contains(mod) ? "1" : "0");
            }
            var g = list.First(x => x.Key == "G").Ajustes!;
            // Videos ya convertidos a 1440p/30 fps (la Intel que dibuja el escritorio va sobrada así).
            string Video(string rel) => VideoSetting(Existe(rel));
            var p = list.First(x => x.Key == "P").Ajustes!;
            g["wh:live-video-wallpaper"] = Video(@"videos\potencia\gojo.mp4");
            p["wh:live-video-wallpaper"] = Video(@"videos\presentacion\coffee-shop.mp4");
            var a = list.First(x => x.Key == "A").Ajustes!;
            a["wh:live-video-wallpaper"] = Video(@"videos\fedora\street-japan.mp4");
            // Reloj de la barra de Fedora: hora y fecha en español, sin titulares de noticias.
            a["wh:taskbar-clock-customization"] = WhD(("activo", "1"), ("TopLine", "%time%"), ("MiddleLine", ""), ("BottomLine", "%date%"),
                                                      ("TooltipLine", ""), ("TimeFormat", "HH':'mm"), ("DateFormat", "ddd d 'de' MMM"), ("DateLocale", "es-ES"));
            // Fondo fijo de cada modo (en Potencia y Presentación queda debajo del video y se ve al cargar).
            list.First(x => x.Key == "J").Ajustes!["fondo"] = "img:" + Existe(@"videos\gaming\rog.jpg");
            list.First(x => x.Key == "A").Ajustes!["fondo"] = "img:" + Existe(@"fondos\fedora\noche-en-el-bosque-montanas-pinos-luna.jpg");
            g["fondo"] = "img:" + Existe(@"videos\potencia\gojo.jpg");
            p["fondo"] = "img:" + Existe(@"videos\presentacion\coffee-shop.jpg");
            // Escritorio propio por modo (okla\escritorios\X, ver tools\crear-escritorios.ps1) y tamaño de iconos.
            var tam = new Dictionary<string, string> { ["J"] = "48", ["A"] = "64", ["G"] = "32", ["P"] = "48", ["B"] = "48" };
            foreach (var m in list)
            {
                // Batería usa el escritorio de Fedora (los mismos accesos de diario).
                m.Ajustes!["escritorio"] = Path.Combine(Paths.Root, "escritorios", Carpeta(m.Key == "B" ? "A" : m.Key));
                m.Ajustes["tamano-iconos"] = tam[m.Key];
            }
            return list;
        }

        private static List<ModeDef> BaseDefaults() => new()
        {
            new() { Key = "J", Nombre = "Gaming", Tecla = 1, Color = "#a0a0a8",
                    Descripcion = "Consumo mínimo para jugar", Transicion = Transition.Instantanea,
                    Ajustes = new() { ["tema"] = "oscuro", ["transparencia"] = "0", ["animaciones"] = "0",
                                      ["acento"] = "#6e6e76", ["energia"] = "alto", ["fondo"] = "color:#000000",
                                      ["iconos"] = "0", ["autoocultar"] = "1" } },
            new() { Key = "A", Nombre = "Fedora", Tecla = 2, Color = "#88c0d0",
                    Descripcion = "Amigable, Nord, inspirado en GNOME", Transicion = Transition.Fundido,
                    Ajustes = new() { ["tema"] = "oscuro", ["transparencia"] = "1", ["animaciones"] = "1",
                                      ["acento"] = "#88c0d0", ["energia"] = "equilibrado",
                                      ["iconos"] = "1", ["autoocultar"] = "1" } },
            new() { Key = "G", Nombre = "Potencia", Tecla = 3, Color = "#7c8cff",
                    Descripcion = "Todo lo posible, fluido", Transicion = Transition.Barrido,
                    Ajustes = new() { ["tema"] = "oscuro", ["transparencia"] = "1", ["animaciones"] = "1",
                                      ["acento"] = "#7c8cff", ["energia"] = "alto", ["autoocultar"] = "0" } },
            new() { Key = "P", Nombre = "Presentación", Tecla = 4, Color = "#a7c080",
                    Descripcion = "Limpio para grabar", Transicion = Transition.FundidoLento,
                    Ajustes = new() { ["tema"] = "oscuro", ["transparencia"] = "1", ["animaciones"] = "1",
                                      ["acento"] = "#a7c080", ["energia"] = "alto", ["autoocultar"] = "0" } },
            new() { Key = "B", Nombre = "Batería", Tecla = 5, Color = "#a3be8c", Activo = false,   // opcional, como el pánico
                    Descripcion = "Todo al mínimo para que dure la batería", Transicion = Transition.Instantanea,
                    Ajustes = new() { ["tema"] = "oscuro", ["transparencia"] = "0", ["animaciones"] = "0",
                                      ["acento"] = "#a3be8c", ["energia"] = "bateria", ["fondo"] = "color:#000000",
                                      ["iconos"] = "1", ["autoocultar"] = "0", ["brillo"] = "40" } },
        };

        public static List<ModeDef> LoadModes()
        {
            try
            {
                if (File.Exists(Paths.ModesFile))
                {
                    var list = JsonSerializer.Deserialize<List<ModeDef>>(File.ReadAllText(Paths.ModesFile));
                    if (list != null && list.Count >= 4)
                    {
                        // modos.json de la versión 0.1 no tenía Ajustes: los completo con los de fábrica.
                        // y añado los ajustes nuevos de versiones posteriores sin pisar los que ya existan.
                        bool migrated = false;
                        var defs = Defaults();
                        // Modos nuevos de versiones posteriores (p. ej. Batería en la 0.3) se añaden tal cual.
                        foreach (var nuevo in defs.Where(d => list.All(m => m.Key != d.Key))) { list.Add(nuevo); migrated = true; }
                        foreach (var m in list)
                        {
                            var def = defs.First(x => x.Key == m.Key).Ajustes!;
                            if (m.Ajustes == null) { m.Ajustes = def; migrated = true; continue; }
                            foreach (var (k, v) in def.Where(kv => !m.Ajustes.ContainsKey(kv.Key)))
                            { m.Ajustes[k] = v; migrated = true; }
                            // v0.3: videos con temporizador de 60 (ver VideoSetting)
                            if (m.Ajustes.TryGetValue("wh:live-video-wallpaper", out var vid) && vid.Contains("\"targetFps\":\"30\""))
                            { m.Ajustes["wh:live-video-wallpaper"] = vid.Replace("\"targetFps\":\"30\"", "\"targetFps\":\"60\""); migrated = true; }
                        }
                        if (Elegidos(list) | Reparar(list)) migrated = true;
                        if (migrated) File.WriteAllText(Paths.ModesFile, JsonSerializer.Serialize(list, Json));
                        return list;
                    }
                }
            }
            catch (Exception ex) { Log.Error("modos.json ilegible, uso valores por defecto: " + ex.Message); }

            var d = Defaults();
            Elegidos(d); Reparar(d);
            File.WriteAllText(Paths.ModesFile, JsonSerializer.Serialize(d, Json));
            return d;
        }

        /// <summary>Modos elegidos en el instalador (config\modulos.json, se aplica una vez y se borra).</summary>
        public static string ModulosFile => Path.Combine(Paths.Config, "modulos.json");

        private static bool Elegidos(List<ModeDef> list)
        {
            if (!File.Exists(ModulosFile)) return false;
            try
            {
                var activos = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ModulosFile)) ?? new();
                foreach (var m in list) m.Activo = activos.Contains(m.Key);
                Log.Info("Modos elegidos al instalar: " + string.Join(", ", activos));
            }
            catch (Exception ex) { Log.Error("modulos.json: " + ex.Message); return false; }
            File.Delete(ModulosFile);
            return true;
        }

        /// <summary>
        /// Si el video o la imagen de un modo ya no existe (borrado, o en otra PC no se descargó), pone el primero
        /// que haya en las carpetas de ese modo. Así un modo nunca se queda con el fondo en negro.
        /// </summary>
        private static bool Reparar(List<ModeDef> list)
        {
            bool cambio = false;
            foreach (var m in list.Where(x => x.Ajustes != null))
            {
                var carpeta = Carpeta(m.Key);
                if (carpeta == "") continue;
                string? Primero(string ext) => new[] { R(Path.Combine("videos", carpeta)), R(Path.Combine("fondos", carpeta)) }
                    .Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*" + ext).OrderBy(n => n)).FirstOrDefault();

                if (m.Ajustes!.TryGetValue("wh:live-video-wallpaper", out var vid) && vid.Contains("\"activo\":\"1\""))
                {
                    var d = JsonSerializer.Deserialize<Dictionary<string, string>>(vid);
                    if (d != null && d.TryGetValue("videoPath", out var vp) && !File.Exists(vp) && Primero(".mp4") is string nuevo)
                    {
                        d["videoPath"] = nuevo; m.Ajustes["wh:live-video-wallpaper"] = JsonSerializer.Serialize(d); cambio = true;
                        Log.Info($"{m.Nombre}: falta {Path.GetFileName(vp)}, uso {Path.GetFileName(nuevo)}");
                    }
                }
                if (m.Ajustes.TryGetValue("fondo", out var f) && f.StartsWith("img:") && !File.Exists(f[4..]) && Primero(".jpg") is string img)
                {
                    m.Ajustes["fondo"] = "img:" + img; cambio = true;
                    Log.Info($"{m.Nombre}: falta {Path.GetFileName(f[4..])}, uso {Path.GetFileName(img)}");
                }
            }
            return cambio;
        }

        public static bool EstaActivo(IEnumerable<ModeDef> modes, string key) => Find(modes, key)?.Activo == true;

        /// <summary>Los modos de fábrica sin sus ajustes (para la pantalla de personalización del instalador).</summary>
        public static List<ModeDef> Resumen() => BaseDefaults().Select(m => { m.Ajustes = null; return m; }).ToList();

        public static AppState LoadState()
        {
            try
            {
                if (File.Exists(Paths.StateFile))
                    return JsonSerializer.Deserialize<AppState>(File.ReadAllText(Paths.StateFile)) ?? new AppState();
            }
            catch (Exception ex) { Log.Error("estado.json ilegible: " + ex.Message); }
            return new AppState();
        }

        public static void SaveState(AppState s)
        {
            try { File.WriteAllText(Paths.StateFile, JsonSerializer.Serialize(s, Json)); }
            catch (Exception ex) { Log.Error("No pude guardar estado: " + ex.Message); }
        }

        public static ModeDef? Find(IEnumerable<ModeDef> modes, string key) =>
            modes.FirstOrDefault(m => m.Key == key);
    }
}

