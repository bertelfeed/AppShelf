using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AppShelf.Core;

/// <summary>
/// Request/response pipe to the resident AppShelf of one profile. External tools (the MCP server)
/// never write shelf.json themselves: the running app applies every change, so nothing is overwritten.
/// One JSON line per request: {"tool": "...", "arguments": {...}}; one JSON line back: {"ok": bool, "text": "..."}.
/// </summary>
public static class ShelfApiChannel
{
    public const int MaxMessage = 1024 * 1024;

    /// <summary>One pipe per user and profile folder, like the single-instance pipe.</summary>
    public static string PipeName(string dataPath)
    {
        var identity = Environment.UserName + "|" + Path.GetFullPath(dataPath).TrimEnd('\\', '/').ToUpperInvariant();
        return "AppShelf." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24] + ".api";
    }

    public sealed record Reply(bool Ok, string Text);

    public static async Task<Reply> SendAsync(string dataPath, string tool, JsonObject? arguments, int connectTimeout = 3000, CancellationToken cancellation = default)
    {
        await using var client = new NamedPipeClientStream(".", PipeName(dataPath), PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(connectTimeout, cancellation);
        var request = new JsonObject { ["tool"] = tool, ["arguments"] = arguments?.DeepClone() ?? new JsonObject() };
        using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(request.ToJsonString());
        var line = await reader.ReadLineAsync(cancellation) ?? throw new IOException("AppShelf закрыл соединение без ответа.");
        var reply = JsonNode.Parse(line)?.AsObject() ?? throw new IOException("Пустой ответ AppShelf.");
        return new(reply["ok"]?.GetValue<bool>() == true, reply["text"]?.GetValue<string>() ?? "");
    }

    public static async Task ListenAsync(string dataPath, Func<string, JsonObject, Task<Reply>> handle, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(PipeName(dataPath), PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellation);
                _ = ServeAsync(server, handle, cancellation);
                server = null;
            }
            catch (OperationCanceledException) { }
            catch (IOException) { await Task.Delay(200, cancellation).ConfigureAwait(false); }
            finally { server?.Dispose(); }
        }
    }

    private static async Task ServeAsync(NamedPipeServerStream server, Func<string, JsonObject, Task<Reply>> handle, CancellationToken cancellation)
    {
        await using (server)
        {
            try
            {
                using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                await using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var line = await reader.ReadLineAsync(deadline.Token);
                Reply reply;
                if (line is null || line.Length > MaxMessage) reply = new(false, "Слишком большой или пустой запрос.");
                else
                {
                    try
                    {
                        var request = JsonNode.Parse(line)!.AsObject();
                        var tool = request["tool"]?.GetValue<string>() ?? "";
                        reply = await handle(tool, request["arguments"] as JsonObject ?? new JsonObject());
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
                    { reply = new(false, "Некорректный запрос: " + ex.Message); }
                }
                await writer.WriteLineAsync(new JsonObject { ["ok"] = reply.Ok, ["text"] = reply.Text }.ToJsonString());
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
}
