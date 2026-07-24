using System.Text.Json.Nodes;
using Synopsis.Analysis;
using Synopsis.Analysis.Graph;
using Synopsis.Mcp;

namespace Synopsis.Tests;

public sealed class McpIndexingTests
{
    [Fact]
    public async Task GraphTool_WhileIndexingEmptyGraph_ReportsIndexingInProgress()
    {
        var combined = new CombinedGraph();
        var indexing = IndexingState.Started();
        var tools = new McpTools(combined, ScannerBuilder.Create(), null, indexing);

        var result = await tools.InvokeAsync("list_nodes", null, default);

        var obj = Assert.IsType<JsonObject>(result);
        Assert.True((bool)obj["indexing"]!);
        Assert.Contains("scan in progress", (string)obj["message"]!);
    }

    [Fact]
    public async Task ScanStats_WhileIndexing_StaysAvailableAndReportsStatus()
    {
        var combined = new CombinedGraph();
        var indexing = IndexingState.Started();
        var tools = new McpTools(combined, ScannerBuilder.Create(), null, indexing);

        var result = await tools.InvokeAsync("scan_stats", null, default);

        var status = Assert.IsType<JsonObject>(result["indexing"]);
        Assert.True((bool)status["inProgress"]!);
        Assert.NotNull(status["startedAtUtc"]);
    }

    [Fact]
    public async Task ListRepositories_WhileIndexing_StaysAvailable()
    {
        var combined = new CombinedGraph();
        var tools = new McpTools(combined, ScannerBuilder.Create(), null, IndexingState.Started());

        var result = await tools.InvokeAsync("list_repositories", null, default);

        Assert.NotNull(result["repositories"]);
    }

    [Fact]
    public async Task GraphTool_AfterIndexingCompletes_ServesNormally()
    {
        var combined = new CombinedGraph();
        var indexing = IndexingState.Started();
        var tools = new McpTools(combined, ScannerBuilder.Create(), null, indexing);

        indexing.Complete();
        var result = await tools.InvokeAsync("list_nodes", null, default);

        Assert.False(IsIndexingBusyResponse(result));
    }

    [Fact]
    public async Task GraphTool_AfterIndexingFails_ServesNormallyAndStatsCarryError()
    {
        var combined = new CombinedGraph();
        var indexing = IndexingState.Started();
        var tools = new McpTools(combined, ScannerBuilder.Create(), null, indexing);

        indexing.Fail("boom");
        var nodes = await tools.InvokeAsync("list_nodes", null, default);
        var stats = await tools.InvokeAsync("scan_stats", null, default);

        Assert.False(IsIndexingBusyResponse(nodes));
        var status = Assert.IsType<JsonObject>(stats["indexing"]);
        Assert.False((bool)status["inProgress"]!);
        Assert.Equal("boom", (string)status["error"]!);
    }

    [Fact]
    public async Task GraphTool_WithoutIndexingState_ServesNormally()
    {
        var combined = new CombinedGraph();
        var tools = new McpTools(combined, ScannerBuilder.Create());

        var result = await tools.InvokeAsync("list_nodes", null, default);

        Assert.False(IsIndexingBusyResponse(result));
    }

    [Fact]
    public async Task RunLockedScanAsync_SerializesConcurrentScanBodies()
    {
        var combined = new CombinedGraph();
        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var secondEntered = false;

        var first = combined.RunLockedScanAsync(async _ =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
        }, default);

        await firstEntered.Task;
        var second = combined.RunLockedScanAsync(_ =>
        {
            secondEntered = true;
            return Task.CompletedTask;
        }, default);

        await Task.Delay(50);
        Assert.False(secondEntered);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second);
        Assert.True(secondEntered);
    }

    private static bool IsIndexingBusyResponse(JsonNode result) =>
        result is JsonObject obj && obj.ContainsKey("indexing") && obj.ContainsKey("message");
}
