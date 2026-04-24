using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Synopsis.Analysis.Model;

namespace Synopsis.Mcp;

internal sealed class McpServer
{
    private readonly McpTools _tools;

    public McpServer(ScanResult graph)
    {
        _tools = new McpTools(graph);
    }

    /// <summary>
    /// Run the MCP server against the given <paramref name="transport"/>.
    /// Each accepted connection runs its own request/response loop on a
    /// separate task; one misbehaving client cannot stall another. The
    /// shared <see cref="McpTools"/> is safe for concurrent reads — its
    /// backing <see cref="ScanResult"/> is immutable.
    /// </summary>
    public async Task RunAsync(IMcpTransport transport, CancellationToken ct)
    {
        Console.Error.WriteLine($"[mcp] Synopsis MCP server ready on {transport.Name}.");

        // ConcurrentDictionary + continuation self-removal: O(1) tracking and
        // automatic cleanup as connections complete, instead of scanning a
        // growing List<Task> per-accept.
        var inFlight = new ConcurrentDictionary<Task, byte>();
        try
        {
            await foreach (var connection in transport.AcceptAsync(ct))
            {
                var task = Task.Run(() => HandleConnectionAsync(connection, ct), ct);
                inFlight[task] = 0;
                _ = task.ContinueWith(done => inFlight.TryRemove(done, out _),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested; await in-flight connections below.
        }

        try
        {
            await Task.WhenAll(inFlight.Keys);
        }
        catch { /* per-connection errors already logged inside HandleConnectionAsync */ }

        Console.Error.WriteLine("[mcp] Server stopped.");
    }

    private async Task HandleConnectionAsync(IMcpConnection connection, CancellationToken ct)
    {
        await using var _ = connection;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await connection.ReadLineAsync(ct);
                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                McpResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize(line, McpJsonContext.Default.McpRequest);
                    response = request is null
                        ? MakeError(null, McpErrorCodes.ParseError, "Failed to parse request")
                        : Dispatch(request);
                }
                catch (JsonException ex)
                {
                    response = MakeError(null, McpErrorCodes.ParseError, ex.Message);
                }

                var json = JsonSerializer.Serialize(response, McpJsonContext.Default.McpResponse);
                await connection.WriteLineAsync(json, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown; nothing to report.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mcp] Connection error: {ex.Message}");
        }
    }

    private McpResponse Dispatch(McpRequest request)
    {
        try
        {
            return request.Method switch
            {
                "initialize" => HandleInitialize(request),
                "initialized" or "notifications/initialized" => Ok(request.Id, new JsonObject()),
                "tools/list" => HandleToolsList(request),
                "tools/call" => HandleToolCall(request),
                "ping" => Ok(request.Id, new JsonObject { ["status"] = "ok" }),
                _ => MakeError(request.Id, McpErrorCodes.MethodNotFound, $"Unknown method: {request.Method}")
            };
        }
        catch (Exception ex)
        {
            return MakeError(request.Id, McpErrorCodes.InternalError, ex.Message);
        }
    }

    private static McpResponse HandleInitialize(McpRequest request) =>
        Ok(request.Id, new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false }
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "synopsis",
                ["version"] = "1.0.0"
            }
        });

    private static McpResponse HandleToolsList(McpRequest request)
    {
        var definitions = McpTools.GetDefinitions();
        var tools = new JsonArray();
        foreach (var def in definitions)
        {
            // JsonObject initialiser handles escaping of " / \ / control chars
            // in descriptions; hand-rolled string interpolation did not.
            // Explicit JsonNode cast forces the non-generic Add overload
            // (the generic Add<T> trips the trim analyzer).
            JsonNode entry = new JsonObject
            {
                ["name"] = def.Name,
                ["description"] = def.Description,
                ["inputSchema"] = def.InputSchema.DeepClone(),
            };
            tools.Add(entry);
        }

        return Ok(request.Id, new JsonObject { ["tools"] = tools });
    }

    private McpResponse HandleToolCall(McpRequest request)
    {
        if (request.Params is null)
            return MakeError(request.Id, McpErrorCodes.InvalidParams, "params is required");

        string? toolName = null;
        JsonElement? arguments = null;

        if (request.Params.Value.TryGetProperty("name", out var nameEl))
            toolName = nameEl.GetString();
        if (request.Params.Value.TryGetProperty("arguments", out var argsEl))
            arguments = argsEl;

        if (string.IsNullOrWhiteSpace(toolName))
            return MakeError(request.Id, McpErrorCodes.InvalidParams, "params.name is required");

        if (!_tools.CanHandle(toolName))
            return MakeError(request.Id, McpErrorCodes.InvalidParams, $"Unknown tool: {toolName}");

        try
        {
            var result = _tools.Invoke(toolName, arguments);
            var text = result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            return Ok(request.Id, ToolContent(text, isError: false));
        }
        catch (Exception ex)
        {
            return Ok(request.Id, ToolContent($"Error: {ex.Message}", isError: true));
        }
    }

    /// <summary>
    /// Builds a <c>tools/call</c> result envelope via the JsonObject
    /// initialiser so the string value is escaped by the serializer — the
    /// previous hand-rolled <c>JsonEncode</c> missed <c>\b</c>, <c>\f</c>,
    /// and <c>\uXXXX</c> for control chars.
    /// </summary>
    private static JsonObject ToolContent(string text, bool isError)
    {
        JsonNode entry = new JsonObject
        {
            ["type"] = "text",
            ["text"] = text,
        };
        var content = new JsonArray(entry);
        var result = new JsonObject { ["content"] = content };
        if (isError) result["isError"] = true;
        return result;
    }

    private static McpResponse Ok(JsonElement? id, JsonNode result) =>
        new() { Id = id, Result = result };

    private static McpResponse MakeError(JsonElement? id, int code, string message) =>
        new() { Id = id, Error = new McpError(code, message) };
}
