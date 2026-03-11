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

    public async Task RunAsync(CancellationToken ct)
    {
        Console.Error.WriteLine("[mcp] Synopsis MCP server ready. Reading from stdin.");

        using var reader = new StreamReader(Console.OpenStandardInput());
        using var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                break;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            McpRequest? request;
            try
            {
                request = JsonSerializer.Deserialize(line, McpJsonContext.Default.McpRequest);
                if (request is null)
                {
                    await WriteResponse(writer, MakeError(null, McpErrorCodes.ParseError, "Failed to parse request"));
                    continue;
                }
            }
            catch (JsonException ex)
            {
                await WriteResponse(writer, MakeError(null, McpErrorCodes.ParseError, ex.Message));
                continue;
            }

            var response = Dispatch(request);
            await WriteResponse(writer, response);
        }

        Console.Error.WriteLine("[mcp] Server stopped.");
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
            var toolNode = JsonNode.Parse(
                $$"""{"name":"{{def.Name}}","description":"{{def.Description}}"}""")!;
            toolNode.AsObject()["inputSchema"] = def.InputSchema.DeepClone();
            tools.Add(toolNode);
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
            return Ok(request.Id, new JsonObject
            {
                ["content"] = new JsonArray(JsonNode.Parse($$"""{"type":"text","text":{{JsonEncode(text)}}}""")!)
            });
        }
        catch (Exception ex)
        {
            return Ok(request.Id, new JsonObject
            {
                ["content"] = new JsonArray(JsonNode.Parse($$"""{"type":"text","text":{{JsonEncode($"Error: {ex.Message}")}}}""")!),
                ["isError"] = true
            });
        }
    }

    private static McpResponse Ok(JsonElement? id, JsonNode result) =>
        new() { Id = id, Result = result };

    private static McpResponse MakeError(JsonElement? id, int code, string message) =>
        new() { Id = id, Error = new McpError(code, message) };

    /// <summary>Encodes a string as a JSON string literal (with quotes).</summary>
    private static string JsonEncode(string value) =>
        "\"" + value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t") + "\"";

    private static async Task WriteResponse(StreamWriter writer, McpResponse response)
    {
        var json = JsonSerializer.Serialize(response, McpJsonContext.Default.McpResponse);
        await writer.WriteLineAsync(json);
    }
}
