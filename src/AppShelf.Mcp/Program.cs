using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppShelf.Core;
using L = AppShelf.Core.Localization;

// MCP server over stdio: JSON-RPC 2.0, one message per line. It holds no state of its own;
// every tool call goes to the resident AppShelf of the profile, which is started in the background when needed.
// stdout carries protocol messages only.

var dataPath = Argument(args, "--data-dir") ?? DefaultDataPath();
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
RefreshLanguage();
var knownTools = ShelfApiTools.List().Select(x => x!["name"]!.GetValue<string>()).ToHashSet();
using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
await using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

while (await input.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    JsonObject? message;
    try { message = JsonNode.Parse(line) as JsonObject; }
    catch (JsonException) { await output.WriteLineAsync(Error(null, -32700, "Parse error").ToJsonString()); continue; }
    // Notifications (no id) and responses to our own requests (no method) need no answer.
    if (message?["id"] is not { } id || message["method"]?.GetValue<string>() is not { } method) continue;
    JsonObject response;
    try
    {
        response = method switch
        {
            "initialize" => Result(id, Initialize(message["params"] as JsonObject)),
            "ping" => Result(id, new JsonObject()),
            "tools/list" => Result(id, new JsonObject { ["tools"] = ShelfApiTools.List() }),
            "tools/call" => Result(id, await CallAsync(message["params"] as JsonObject)),
            _ => Error(id, -32601, "Method not found: " + method)
        };
    }
    catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { response = Error(id, -32602, ex.Message); }
    await output.WriteLineAsync(response.ToJsonString());
}

JsonObject Initialize(JsonObject? parameters) => new()
{
    // Tools and text results are the same in every protocol revision, so the client's version is accepted.
    ["protocolVersion"] = parameters?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
    ["serverInfo"] = new JsonObject { ["name"] = "appshelf", ["title"] = "AppShelf", ["version"] = version },
    ["instructions"] = ShelfApiTools.Instructions
};

async Task<JsonObject> CallAsync(JsonObject? parameters)
{
    RefreshLanguage();
    var name = parameters?["name"]?.GetValue<string>() ?? "";
    if (!knownTools.Contains(name)) return ToolResult(L.F("UnknownTool", name), true);
    var arguments = parameters?["arguments"] as JsonObject;
    try
    {
        var reply = await SendAsync(name, arguments);
        return ToolResult(reply.Text, !reply.Ok);
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
    { return ToolResult(L.F("AppNotResponding", ex.Message), true); }
}

async Task<ShelfApiChannel.Reply> SendAsync(string tool, JsonObject? arguments)
{
    try { return await ShelfApiChannel.SendAsync(dataPath, tool, arguments, 1500); }
    catch (TimeoutException) { }
    // AppShelf is not running for this profile: start it in the background and wait for its pipe.
    var executable = Path.Combine(AppContext.BaseDirectory, "AppShelf.exe");
    if (!File.Exists(executable)) throw new IOException(L.Get("ResidentExecutableMissing"));
    // Shell execution: the resident app must not inherit this server's stdio pipes, or the client would never see them close.
    Process.Start(new ProcessStartInfo(executable, $"--background --data-dir \"{dataPath.TrimEnd('\\')}\"") { UseShellExecute = true })?.Dispose();
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (true)
    {
        try { return await ShelfApiChannel.SendAsync(dataPath, tool, arguments, 1000); }
        catch (TimeoutException) when (DateTime.UtcNow < deadline) { }
    }
}

void RefreshLanguage()
{
    // The MCP process may outlive a language change in the editor. Read only; never rewrite the profile.
    try
    {
        var path = Path.Combine(dataPath, "shelf.json");
        L.SetLanguage(File.Exists(path) ? ConfigurationStore.Read(path).Language : L.DefaultLanguage);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
    { L.SetLanguage(L.DefaultLanguage); }
}

static JsonObject ToolResult(string text, bool isError) => new()
{
    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
    ["isError"] = isError
};
static JsonObject Result(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };
static JsonObject Error(JsonNode? id, int code, string text) =>
    new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = text } };
static string? Argument(string[] args, string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

// Same rule as AppShelf.exe: a portable copy keeps data next to the program.
static string DefaultDataPath() => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"))
    ? Path.Combine(AppContext.BaseDirectory, "data")
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppShelf");
