using System.Text.Json;
using Synopsis.Output;

namespace Synopsis.Tests;

public sealed class JsonOutputTests
{
    [Fact]
    public void BuildEnvelope_IsValidJson_WithSchemaVersion()
    {
        var line = JsonOutput.BuildEnvelope("scan", """{"nodeCount":3}""", ms: 42);

        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        Assert.Equal("scan", root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(3, root.GetProperty("result").GetProperty("nodeCount").GetInt32());
        Assert.Equal(42, root.GetProperty("ms").GetInt64());
        Assert.Equal(SynopsisSchema.EnvelopeVersion, root.GetProperty("schemaVersion").GetInt32());
    }

    [Fact] // backslashes, quotes, and newlines in string values must stay valid JSON
    public void BuildError_EscapesControlCharsAndQuotes()
    {
        const string message = "line1\nline2 \"q\" C:\\repo\\graph.json";
        var line = JsonOutput.BuildError("scan", message, ms: 7);

        using var doc = JsonDocument.Parse(line); // throws on invalid JSON if escaping is wrong
        var root = doc.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal(message, root.GetProperty("error").GetString());
        Assert.Equal(SynopsisSchema.EnvelopeVersion, root.GetProperty("schemaVersion").GetInt32());
    }
}
