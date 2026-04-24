using System.Text.Json;
using System.Text.Json.Nodes;
using Synopsis.Analysis;
using Synopsis.Analysis.Graph;
using Synopsis.Analysis.Model;
using Synopsis.Mcp;

namespace Synopsis.Tests;

public sealed class McpToolsRepositoryManagementTests
{
    [Fact]
    public async Task ListRepositories_Empty_ReturnsEmptyArray()
    {
        var combined = new CombinedGraph();
        var tools = new McpTools(combined, ScannerBuilder.Create());

        var result = (await tools.InvokeAsync("list_repositories", arguments: null, ct: default)).AsObject();
        var repos = result["repositories"]!.AsArray();
        Assert.Empty(repos);
    }

    [Fact]
    public async Task ListRepositories_WithRegisteredRepos_ReturnsAllPaths()
    {
        var combined = new CombinedGraph();
        await combined.ReplaceRepositoryAsync("/repo-a",
            BuildRepo(b => b.AddNode("a", NodeType.Method, "A")), CancellationToken.None);
        await combined.ReplaceRepositoryAsync("/repo-b",
            BuildRepo(b => b.AddNode("b", NodeType.Method, "B")), CancellationToken.None);

        var tools = new McpTools(combined, ScannerBuilder.Create());
        var result = (await tools.InvokeAsync("list_repositories", arguments: null, ct: default)).AsObject();
        var repos = result["repositories"]!.AsArray();

        Assert.Equal(2, repos.Count);
        var paths = repos.Select(r => r!["path"]!.GetValue<string>()).ToHashSet();
        // Paths are normalized absolutely; just check they contain the suffix
        // rather than pinning to an absolute form that varies by cwd/tmpdir.
        Assert.Contains(paths, p => p.EndsWith("repo-a", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith("repo-b", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReindexAll_EmptyGraph_ReturnsEmptyResultsWithoutError()
    {
        // No repos tracked yet — reindex_all must succeed with an empty
        // result array rather than throwing.
        var combined = new CombinedGraph();
        var tools = new McpTools(combined, ScannerBuilder.Create());

        var result = (await tools.InvokeAsync("reindex_all", arguments: null, ct: default)).AsObject();
        var items = result["repositories"]!.AsArray();
        Assert.Empty(items);
    }

    [Fact]
    public void ToolDefinitions_IncludeM3Tools()
    {
        var names = McpTools.GetDefinitions().Select(d => d.Name).ToHashSet();
        Assert.Contains("list_repositories", names);
        Assert.Contains("reindex_repository", names);
        Assert.Contains("reindex_all", names);
    }

    [Fact]
    public async Task ReindexRepository_MissingPathArgument_Throws()
    {
        var combined = new CombinedGraph();
        var tools = new McpTools(combined, ScannerBuilder.Create());

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await tools.InvokeAsync("reindex_repository", ArgumentsFor(new JsonObject()), ct: default));
    }


    [Fact]
    public async Task ListRepositories_ReturnsMetadataFields()
    {
        // tool's schema advertised lastScannedAtUtc /
        // nodeCount / edgeCount but the impl only returned "path".
        var combined = new CombinedGraph();
        await combined.ReplaceRepositoryAsync("/repo-a",
            BuildRepo(b =>
            {
                b.AddNode("n1", NodeType.Method, "A");
                b.AddEdge("n1", "n1", EdgeType.Calls, "self");
            }), CancellationToken.None);

        var tools = new McpTools(combined, ScannerBuilder.Create());
        var repos = (await tools.InvokeAsync("list_repositories", arguments: null, ct: default))
            .AsObject()["repositories"]!.AsArray();

        var entry = Assert.Single(repos)!.AsObject();
        Assert.NotNull(entry["path"]);
        Assert.NotNull(entry["lastScannedAtUtc"]);
        Assert.NotNull(entry["nodeCount"]);
        Assert.NotNull(entry["edgeCount"]);
        Assert.Equal(1, entry["nodeCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task ReindexRepository_UnknownPath_NoWorkspaceRoot_Rejected()
    {
        // an MCP client pointing the scanner at an
        // arbitrary path used to work. With no workspace root configured
        // and the path not in KnownRepositories, the tool refuses.
        var combined = new CombinedGraph();
        var tools = new McpTools(combined, ScannerBuilder.Create(), workspaceRoot: null);

        var args = ArgumentsFor(new JsonObject { ["path"] = "/etc" });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await tools.InvokeAsync("reindex_repository", args, ct: default));
    }

    [Fact]
    public async Task ReindexRepository_PathOutsideWorkspaceRoot_Rejected()
    {
        // workspace root bounds scanning even when set.
        // /etc is not under /tmp, so the tool refuses.
        var combined = new CombinedGraph();
        var tools = new McpTools(combined, ScannerBuilder.Create(), workspaceRoot: "/tmp");

        var args = ArgumentsFor(new JsonObject { ["path"] = "/etc" });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await tools.InvokeAsync("reindex_repository", args, ct: default));
    }

    private static JsonElement ArgumentsFor(JsonNode node) =>
        JsonDocument.Parse(node.ToJsonString()).RootElement;

    private static ScanResult BuildRepo(Action<GraphBuilder> setup)
    {
        var builder = new GraphBuilder();
        setup(builder);
        var now = DateTimeOffset.UtcNow;
        var info = new ScanInfo("/repo", now, now, [], new Dictionary<string, string>());
        return builder.Build(info, []);
    }
}
