// Gutter Script Generator MCP
// Lets an AI app you already use (Claude Desktop, Claude Code, or any MCP client) write the narration
// for the project open in Gutter: it reads the panels as images and writes one line per panel back.
// No API key: the AI app does the writing with its own model.
//
//   ScriptGeneratorMcp.exe              MCP over stdio (Claude Desktop, Claude Code)
//   ScriptGeneratorMcp.exe --http [port] MCP over Streamable HTTP at http://127.0.0.1:<port>/mcp (default 7331)
//
// Gutter must be running with a project open; it listens on http://127.0.0.1:7330 (override with GUTTER_BRIDGE_URL).

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

const string Version = "1.1.0";
string[] protocols = { "2025-06-18", "2025-03-26", "2024-11-05" };
var bridgeBase = (Environment.GetEnvironmentVariable("GUTTER_BRIDGE_URL") ?? "http://127.0.0.1:7330/").TrimEnd('/') + "/";
var http = new HttpClient { BaseAddress = new Uri(bridgeBase), Timeout = TimeSpan.FromSeconds(60) };

const string Instructions = """
    This server connects you to Gutter, a desktop editor for manhwa/manga narration videos.
    To write the narration for the project open in Gutter:
    1. Call get_script_guide once. Follow its rules, tone and story notes exactly.
    2. Call gutter_status to see how many panels there are and where the first empty one is.
    3. Work through the panels in order with get_panels (4 at a time). Look at each image and write one
       narration line per panel that tells the story, including panels with no text.
    4. After each batch, call write_lines with those lines. They appear in Gutter immediately.
    5. Keep going until the last panel, carrying names and plot forward. Don't skip panels.

    To check and fix the panels themselves (do this before writing, if asked or if panels look wrong):
    1. Call check_panels. It lists panels that look like bad cuts (empty strips, lone speech bubbles,
       several panels stuck together, big empty margins) with a suggested fix.
    2. Look at the flagged panels (and their neighbours) with get_panels before changing anything,
       and also watch for credits pages, content warnings, and characters cut off at an edge.
    3. Fix with remove_panels, merge_panels, split_panel (exact heights, or auto), crop_panel, trim_panel.
       restore_panel undoes edits to one panel's image, and the user can press Ctrl+Z in Gutter.
       add_panel inserts a new image (file path, image link or base64).
    4. Panel numbers shift after removing, merging or splitting: work from the last panel backwards,
       or call check_panels / gutter_status again before the next edit.
    """;

if (args.Length > 0 && args[0] == "--http")
{
    int port = args.Length > 1 && int.TryParse(args[1], out var pp) ? pp : 7331;
    await ServeHttp(port);
}
else
{
    await ServeStdio();
}

// ───────────────────────── transports ─────────────────────────

async Task ServeStdio()
{
    Console.InputEncoding = new UTF8Encoding(false);
    var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
    Log($"Gutter Script Generator MCP {Version} (stdio). Bridge: {bridgeBase}");
    string? line;
    while ((line = await Console.In.ReadLineAsync()) != null)
    {
        line = line.TrimStart((char)0xFEFF);   // some shells prepend a byte-order mark
        if (string.IsNullOrWhiteSpace(line)) continue;
        JsonNode? reply;
        try { reply = await HandleMessage(JsonNode.Parse(line)); }
        catch (JsonException) { reply = Error(null, -32700, "Parse error"); }
        if (reply != null) await stdout.WriteLineAsync(reply.ToJsonString());
    }
}

async Task ServeHttp(int port)
{
    var l = new HttpListener();
    l.Prefixes.Add($"http://127.0.0.1:{port}/");
    l.Start();
    Log($"Gutter Script Generator MCP {Version} listening on http://127.0.0.1:{port}/mcp. Bridge: {bridgeBase}");
    while (true)
    {
        var ctx = await l.GetContextAsync();
        _ = Task.Run(async () =>
        {
            var res = ctx.Response;
            try
            {
                var origin = ctx.Request.Headers["Origin"];
                if (origin != null && !origin.StartsWith("http://127.0.0.1") && !origin.StartsWith("http://localhost"))
                { res.StatusCode = 403; res.Close(); return; }
                if (ctx.Request.Url!.AbsolutePath.TrimEnd('/') != "/mcp") { res.StatusCode = 404; res.Close(); return; }
                if (ctx.Request.HttpMethod != "POST") { res.StatusCode = 405; res.AddHeader("Allow", "POST"); res.Close(); return; }

                using var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = JsonNode.Parse(await sr.ReadToEndAsync());
                JsonNode? reply;
                if (body is JsonArray batch)
                {
                    var outArr = new JsonArray();
                    foreach (var m in batch) { var r = await HandleMessage(m); if (r != null) outArr.Add(r); }
                    reply = outArr.Count > 0 ? outArr : null;
                }
                else reply = await HandleMessage(body);

                if (body?["method"]?.ToString() == "initialize") res.AddHeader("Mcp-Session-Id", Guid.NewGuid().ToString("N"));
                if (reply == null) { res.StatusCode = 202; res.Close(); return; }
                var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString());
                res.ContentType = "application/json";
                res.ContentLength64 = bytes.Length;
                await res.OutputStream.WriteAsync(bytes);
                res.Close();
            }
            catch (Exception ex)
            {
                Log("HTTP error: " + ex.Message);
                try { res.StatusCode = 400; res.Close(); } catch { }
            }
        });
    }
}

// ───────────────────────── JSON-RPC ─────────────────────────

async Task<JsonNode?> HandleMessage(JsonNode? msg)
{
    if (msg is not JsonObject o) return Error(null, -32600, "Invalid request");
    var id = o["id"]?.DeepClone();
    var method = o["method"]?.ToString();
    if (method == null) return null;                    // a response to us; we never send requests
    bool isNotification = !o.ContainsKey("id");
    try
    {
        JsonNode? result = method switch
        {
            "initialize" => Initialize(o["params"]),
            "ping" => new JsonObject(),
            "tools/list" => new JsonObject { ["tools"] = Tools() },
            "tools/call" => await CallTool(o["params"]),
            "prompts/list" => new JsonObject { ["prompts"] = Prompts() },
            "prompts/get" => GetPrompt(o["params"]),
            _ when method.StartsWith("notifications/") => null,
            _ => throw new RpcException(-32601, $"Method not found: {method}"),
        };
        if (isNotification) return null;
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JsonObject() };
    }
    catch (RpcException ex) { return isNotification ? null : Error(id, ex.Code, ex.Message); }
    catch (Exception ex) { return isNotification ? null : Error(id, -32603, ex.Message); }
}

JsonObject Error(JsonNode? id, int code, string message) => new()
{
    ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
};

JsonObject Initialize(JsonNode? p)
{
    var asked = p?["protocolVersion"]?.ToString();
    return new JsonObject
    {
        ["protocolVersion"] = asked != null && protocols.Contains(asked) ? asked : protocols[0],
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false }, ["prompts"] = new JsonObject { ["listChanged"] = false } },
        ["serverInfo"] = new JsonObject { ["name"] = "gutter-script-generator", ["title"] = "Gutter Script Generator", ["version"] = Version },
        ["instructions"] = Instructions,
    };
}

// ───────────────────────── tools ─────────────────────────

JsonArray Tools() => new()
{
    Tool("gutter_status", "Gutter status",
        "Shows whether Gutter is running, which project is open, how many panels it has, how many already have narration, and the first panel without a line.",
        new JsonObject()),
    Tool("get_script_guide", "Get the script guide",
        "Returns the narration rules, tone and the project's story notes (main character, cast, world, notes). Read this before writing any lines.",
        new JsonObject()),
    Tool("get_panels", "Get panels",
        "Returns panels from the open project as images, in reading order, with any narration they already have. Panels are numbered from 1.",
        new JsonObject
        {
            ["start"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["description"] = "First panel number (1-based)." },
            ["count"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 6, ["default"] = 4, ["description"] = "How many panels (1-6)." },
        }, new[] { "start" }),
    Tool("write_lines", "Write narration lines",
        "Writes narration lines into Gutter for the given panel numbers. They show up in the editor right away and can be undone there with Ctrl+Z.",
        new JsonObject
        {
            ["lines"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "One entry per panel.",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["panel"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                        ["line"] = new JsonObject { ["type"] = "string", ["description"] = "What the narrator says over this panel." },
                    },
                    ["required"] = new JsonArray("panel", "line"),
                },
            },
        }, new[] { "lines" }),
    Tool("check_panels", "Check panels for bad cuts",
        "Scans every panel of the open project and lists the ones that look like bad cuts: empty background strips, a speech bubble cut away from its picture, several panels stuck together in one tall image, thin strips, and panels that are mostly empty space. Each comes with a suggested fix. Look at them with get_panels before fixing.",
        new JsonObject()),
    Tool("remove_panels", "Remove panels",
        "Removes panels from the timeline (e.g. empty strips, credits, content warnings). Their images stay in the project's media.",
        new JsonObject { ["panels"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 }, ["description"] = "Panel numbers to remove." } },
        new[] { "panels" }),
    Tool("merge_panels", "Merge panels",
        "Stacks panels from..to (next to each other) into one image, top to bottom, e.g. a speech bubble with the picture it belongs to, or a character cut in half across two panels. Their narration lines are joined.",
        new JsonObject
        {
            ["from"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["description"] = "First panel." },
            ["to"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["description"] = "Last panel (inclusive)." },
        }, new[] { "from", "to" }),
    Tool("split_panel", "Split a panel",
        "Cuts one panel into several at the given heights (shares of its height, 0 = top, 1 = bottom), e.g. [0.5] cuts it in half. Use auto: true to cut at the gaps Gutter finds. The first piece keeps the narration.",
        new JsonObject
        {
            ["panel"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
            ["at"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 }, ["description"] = "Where to cut, top to bottom, as shares of the height." },
            ["auto"] = new JsonObject { ["type"] = "boolean", ["description"] = "Find the gaps automatically instead of 'at'." },
        }, new[] { "panel" }),
    Tool("crop_panel", "Crop a panel",
        "Crops one panel's image to a box given as shares of the image (0-1): left, top, right, bottom. E.g. top 0.2 bottom 0.8 keeps the middle 60%. restore_panel undoes it.",
        new JsonObject
        {
            ["panel"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
            ["left"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1, ["default"] = 0 },
            ["top"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1, ["default"] = 0 },
            ["right"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1, ["default"] = 1 },
            ["bottom"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1, ["default"] = 1 },
        }, new[] { "panel" }),
    Tool("trim_panel", "Trim empty edges",
        "Crops away the empty space around the drawing in one panel, keeping a little margin.",
        new JsonObject { ["panel"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 } }, new[] { "panel" }),
    Tool("restore_panel", "Restore a panel's original image",
        "Puts back the image the panel had when it was imported, undoing crops, splits and merges on it.",
        new JsonObject { ["panel"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 } }, new[] { "panel" }),
    Tool("add_panel", "Add a panel",
        "Inserts a new image as a panel: a file path on this PC, an http(s) image link, or base64 JPEG/PNG data.",
        new JsonObject
        {
            ["source"] = new JsonObject { ["type"] = "string", ["description"] = "File path, image URL, or base64 image data." },
            ["after"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "Insert after this panel number (0 = at the start). Default: at the end." },
        }, new[] { "source" }),
    Tool("get_lines", "Get current lines",
        "Returns every panel's current narration line (empty string if none). Useful to continue where the script stopped.",
        new JsonObject()),
};

static JsonObject Tool(string name, string title, string description, JsonObject props, string[]? required = null)
{
    var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
    if (required != null) schema["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
    return new JsonObject { ["name"] = name, ["title"] = title, ["description"] = description, ["inputSchema"] = schema };
}

async Task<JsonNode> CallTool(JsonNode? p)
{
    var name = p?["name"]?.ToString() ?? throw new RpcException(-32602, "Missing tool name");
    var a = p?["arguments"] as JsonObject ?? new JsonObject();
    try
    {
        return name switch
        {
            "gutter_status" => Text(DescribeStatus(await Get("status"))),
            "get_script_guide" => Text((await Get("guide"))["guide"]?.ToString() ?? ""),
            "get_panels" => await Panels(a),
            "write_lines" => await WriteLines(a),
            "get_lines" => Text(DescribeLines(await Get("lines"))),
            "check_panels" => Text(DescribeCheck(await Get("check"))),
            "remove_panels" => await EditPanels(new JsonObject { ["action"] = "remove", ["panels"] = a["panels"]?.DeepClone() }),
            "merge_panels" => await EditPanels(new JsonObject { ["action"] = "merge", ["from"] = a["from"]?.DeepClone(), ["to"] = a["to"]?.DeepClone() }),
            "split_panel" => await EditPanels(a["auto"]?.ToString()?.ToLowerInvariant() == "true" || a["at"] == null
                ? new JsonObject { ["action"] = "auto_split", ["panel"] = a["panel"]?.DeepClone() }
                : new JsonObject { ["action"] = "split", ["panel"] = a["panel"]?.DeepClone(), ["at"] = a["at"]?.DeepClone() }),
            "crop_panel" => await EditPanels(new JsonObject { ["action"] = "crop", ["panel"] = a["panel"]?.DeepClone(), ["left"] = a["left"]?.DeepClone(), ["top"] = a["top"]?.DeepClone(), ["right"] = a["right"]?.DeepClone(), ["bottom"] = a["bottom"]?.DeepClone() }),
            "trim_panel" => await EditPanels(new JsonObject { ["action"] = "trim", ["panel"] = a["panel"]?.DeepClone() }),
            "restore_panel" => await EditPanels(new JsonObject { ["action"] = "restore", ["panel"] = a["panel"]?.DeepClone() }),
            "add_panel" => await EditPanels(new JsonObject { ["action"] = "add", ["source"] = a["source"]?.DeepClone(), ["after"] = a["after"]?.DeepClone() }),
            _ => throw new RpcException(-32602, $"Unknown tool: {name}"),
        };
    }
    catch (HttpRequestException ex) when (ex.StatusCode == null)
    {
        return Text("Gutter isn't running, or its AI bridge is turned off. Open Gutter (Settings → AI apps must be on) and open a project in the editor, then try again.", isError: true);
    }
    catch (BridgeException ex) { return Text(ex.Message, isError: true); }
}

async Task<JsonObject> Get(string path)
{
    using var res = await http.GetAsync(path);
    var json = JsonNode.Parse(await res.Content.ReadAsStringAsync()) as JsonObject ?? new JsonObject();
    if (!res.IsSuccessStatusCode) throw new BridgeException(json["error"]?.ToString() ?? $"Gutter answered {(int)res.StatusCode}.");
    return json;
}

static string DescribeStatus(JsonObject s)
{
    var p = s["project"];
    if (p == null) return $"Gutter {s["version"]} is running, but no project is open. Ask the user to open one in the editor.";
    var empty = p["first_empty_panel"];
    return $"Gutter {s["version"]} · project \"{p["title"]}\" ({p["source"]})\n" +
           $"{p["panels"]} panels, {p["panels_with_lines"]} already have narration." +
           (empty != null ? $" First panel without a line: {empty}." : " Every panel has a line.");
}

static string DescribeLines(JsonObject l)
{
    var sb = new StringBuilder();
    foreach (var x in l["lines"]!.AsArray()) sb.AppendLine($"{x!["panel"]}: {x["line"]}");
    return sb.ToString();
}

async Task<JsonNode> Panels(JsonObject a)
{
    int start = Int(a["start"]) ?? 1;
    int count = Math.Clamp(Int(a["count"]) ?? 4, 1, 6);
    var data = await Get($"panels?start={start}&count={count}");
    var panels = data["panels"]!.AsArray();
    int total = data["total"]?.GetValue<int>() ?? 0;
    var content = new JsonArray();
    if (panels.Count == 0)
    {
        content.Add(new JsonObject { ["type"] = "text", ["text"] = $"No panels from {start}; the project has {total}." });
        return new JsonObject { ["content"] = content, ["isError"] = false };
    }
    int last = panels[^1]!["panel"]!.GetValue<int>();
    content.Add(new JsonObject { ["type"] = "text", ["text"] = $"Panels {start}–{last} of {total}." + (last < total ? $" Next: get_panels start={last + 1}." : " This is the end of the chapter.") });
    foreach (var pnl in panels)
    {
        var existing = pnl!["line"]?.ToString();
        content.Add(new JsonObject { ["type"] = "text", ["text"] = $"Panel {pnl["panel"]}" + (string.IsNullOrWhiteSpace(existing) ? "" : $" (current line: \"{existing}\")") });
        if (pnl["jpeg_base64"] is JsonNode img)
            content.Add(new JsonObject { ["type"] = "image", ["data"] = img.ToString(), ["mimeType"] = "image/jpeg" });
    }
    return new JsonObject { ["content"] = content, ["isError"] = false };
}

async Task<JsonNode> WriteLines(JsonObject a)
{
    if (a["lines"] is not JsonArray lines || lines.Count == 0) return Text("Pass lines: [{ panel, line }, ...].", isError: true);
    using var res = await http.PostAsync("lines", new StringContent(new JsonObject { ["lines"] = lines.DeepClone() }.ToJsonString(), Encoding.UTF8, "application/json"));
    var json = JsonNode.Parse(await res.Content.ReadAsStringAsync()) as JsonObject ?? new JsonObject();
    if (!res.IsSuccessStatusCode) return Text(json["error"]?.ToString() ?? $"Gutter answered {(int)res.StatusCode}.", isError: true);
    return Text($"Wrote {json["applied"]} line(s) into Gutter (project has {json["total"]} panels).");
}

static string DescribeCheck(JsonObject c)
{
    var issues = c["issues"]!.AsArray();
    if (issues.Count == 0) return $"All {c["total"]} panels look fine. (Still worth a glance with get_panels for credits pages or cut-off characters.)";
    var sb = new StringBuilder($"{issues.Count} of {c["total"]} panels look like bad cuts:\n");
    foreach (var i in issues) sb.AppendLine($"- Panel {i!["panel"]}: {i["problem"]} Suggested fix: {i["suggested_fix"]}.");
    sb.Append("Look at them with get_panels first. Panel numbers shift after removing/merging/splitting, so fix from the last panel backwards.");
    return sb.ToString();
}

async Task<JsonNode> EditPanels(JsonObject body)
{
    using var res = await http.PostAsync("panels/edit", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
    var json = JsonNode.Parse(await res.Content.ReadAsStringAsync()) as JsonObject ?? new JsonObject();
    if (!res.IsSuccessStatusCode) return Text(json["error"]?.ToString() ?? $"Gutter answered {(int)res.StatusCode}.", isError: true);
    return Text($"{json["result"]} The project now has {json["total"]} panels.");
}

static JsonObject Text(string text, bool isError = false) => new()
{
    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
    ["isError"] = isError,
};

// ───────────────────────── prompts ─────────────────────────

JsonArray Prompts() => new()
{
    new JsonObject
    {
        ["name"] = "review_panels",
        ["title"] = "Check and fix the Gutter panels",
        ["description"] = "Find bad cuts in the open project (empty strips, lone bubbles, stuck-together panels, credits) and fix them.",
        ["arguments"] = new JsonArray(),
    },
    new JsonObject
    {
        ["name"] = "write_script",
        ["title"] = "Write the Gutter script",
        ["description"] = "Write narration for every panel of the project open in Gutter.",
        ["arguments"] = new JsonArray(new JsonObject { ["name"] = "from_panel", ["description"] = "Start from this panel (default: the first one without a line).", ["required"] = false }),
    },
};

JsonObject GetPrompt(JsonNode? p)
{
    if (p?["name"]?.ToString() == "review_panels")
        return new JsonObject
        {
            ["description"] = "Check and fix the Gutter panels",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new JsonObject { ["type"] = "text", ["text"] = Instructions + "\nCheck the panels of my open Gutter project now and fix the bad cuts. Tell me what you changed." } }),
        };
    if (p?["name"]?.ToString() != "write_script") throw new RpcException(-32602, "Unknown prompt");
    var from = p?["arguments"]?["from_panel"]?.ToString();
    var text = Instructions + "\n" + (string.IsNullOrWhiteSpace(from)
        ? "Start at the first panel without a line (see gutter_status)."
        : $"Start at panel {from}.") + " Write the whole chapter now.";
    return new JsonObject
    {
        ["description"] = "Write the Gutter script",
        ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new JsonObject { ["type"] = "text", ["text"] = text } }),
    };
}

// models sometimes send numbers as strings
static int? Int(JsonNode? n) => n == null ? null : int.TryParse(n.ToString(), out var v) ? v : null;

static void Log(string s) => Console.Error.WriteLine(s);

class RpcException(int code, string message) : Exception(message) { public int Code { get; } = code; }
class BridgeException(string message) : Exception(message);
