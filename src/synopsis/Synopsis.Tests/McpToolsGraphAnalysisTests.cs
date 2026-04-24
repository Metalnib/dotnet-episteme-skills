using System.Text.Json;
using System.Text.Json.Nodes;
using Synopsis.Analysis;
using Synopsis.Analysis.Graph;
using Synopsis.Analysis.Model;
using Synopsis.Mcp;

namespace Synopsis.Tests;

public sealed class McpToolsGraphAnalysisTests
{
    // ── endpoint_callers ────────────────────────────────────────────────────

    [Fact]
    public async Task EndpointCallers_MatchingRoute_ReturnsEndpointAndCaller()
    {
        var combined = new CombinedGraph();
        await combined.ReplaceRepositoryAsync("/repo",
            BuildRepo(b =>
            {
                b.AddNode("ep:orders", NodeType.Endpoint, "POST /api/orders",
                    repositoryName: "orders-svc", projectName: "Orders.Api",
                    metadata: new Dictionary<string, string?> { ["verb"] = "POST", ["route"] = "/api/orders", ["handler"] = "h" });
                b.AddNode("ext:orders", NodeType.ExternalEndpoint, "POST /api/orders",
                    repositoryName: "gateway", projectName: "Gateway.Api",
                    metadata: new Dictionary<string, string?> { ["verb"] = "POST", ["path"] = "/api/orders" });
                b.AddEdge("ext:orders", "ep:orders", EdgeType.ResolvesToService, "resolved",
                    certainty: Certainty.Inferred);
            }),
            CancellationToken.None);

        var tools = new McpTools(combined, ScannerBuilder.Create());
        var result = (await tools.InvokeAsync("endpoint_callers",
            ArgumentsFor(new JsonObject { ["route"] = "/api/orders" }), ct: default)).AsObject();

        Assert.Equal(1, result["totalEndpoints"]!.GetValue<int>());
        Assert.Equal(1, result["totalCallers"]!.GetValue<int>());
        Assert.Equal(1, result["resolvedCallers"]!.GetValue<int>());

        var caller = result["callers"]!.AsArray().Single()!.AsObject();
        Assert.True(caller["resolved"]!.GetValue<bool>());
        Assert.Equal("ep:orders", caller["resolvedToId"]!.GetValue<string>());
    }

    [Fact]
    public async Task EndpointCallers_VerbFilter_ExcludesNonMatchingVerb()
    {
        var combined = new CombinedGraph();
        await combined.ReplaceRepositoryAsync("/repo",
            BuildRepo(b =>
            {
                b.AddNode("ep:get", NodeType.Endpoint, "GET /api/orders",
                    repositoryName: "svc",
                    metadata: new Dictionary<string, string?> { ["verb"] = "GET", ["route"] = "/api/orders", ["handler"] = "h" });
                b.AddNode("ext:get", NodeType.ExternalEndpoint, "GET /api/orders",
                    repositoryName: "gw",
                    metadata: new Dictionary<string, string?> { ["verb"] = "GET", ["path"] = "/api/orders" });
                b.AddNode("ext:post", NodeType.ExternalEndpoint, "POST /api/orders",
                    repositoryName: "gw",
                    metadata: new Dictionary<string, string?> { ["verb"] = "POST", ["path"] = "/api/orders" });
            }),
            CancellationToken.None);

        var tools = new McpTools(combined, ScannerBuilder.Create());
        var result = (await tools.InvokeAsync("endpoint_callers",
            ArgumentsFor(new JsonObject { ["route"] = "/api/orders", ["verb"] = "GET" }), ct: default)).AsObject();

        Assert.Equal(1, result["totalCallers"]!.GetValue<int>());
        var callerNode = result["callers"]!.AsArray().Single()!.AsObject()["caller"]!.AsObject();
        Assert.Equal("ext:get", callerNode["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task EndpointCallers_MissingRoute_Throws()
    {
        var tools = new McpTools(new CombinedGraph(), ScannerBuilder.Create());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            tools.InvokeAsync("endpoint_callers", ArgumentsFor(new JsonObject()), ct: default));
    }

    [Fact]
    public async Task EndpointCallers_NoMatch_ReturnsEmptyArrays()
    {
        var combined = new CombinedGraph();
        await combined.ReplaceRepositoryAsync("/repo",
            BuildRepo(b => b.AddNode("ep:x", NodeType.Endpoint, "GET /other",
                metadata: new Dictionary<string, string?> { ["verb"] = "GET", ["route"] = "/other", ["handler"] = "h" })),
            CancellationToken.None);

        var tools = new McpTools(combined, ScannerBuilder.Create());
        var result = (await tools.InvokeAsync("endpoint_callers",
            ArgumentsFor(new JsonObject { ["route"] = "/api/orders" }), ct: default)).AsObject();

        Assert.Equal(0, result["totalEndpoints"]!.GetValue<int>());
        Assert.Equal(0, result["totalCallers"]!.GetValue<int>());
    }

    // ── package_dependents ──────────────────────────────────────────────────

    [Fact]
    public async Task PackageDependents_MatchingPackageId_ReturnsDependentProjects()
    {
        var combined = new CombinedGraph();
        await combined.ReplaceRepositoryAsync("/repo",
            BuildRepo(b =>
            {
                b.AddNode("pkg:contracts", NodeType.Package, "Contracts@2.0",
                    repositoryName: "shared",
                    metadata: new Dictionary<string, string?> { ["packageId"] = "PrimeLabs.Contracts", ["version"] = "2.0.0" });
                b.AddNode("proj:gateway", NodeType.Project, "Gateway.Api",
                    repositoryName: "gateway", projectName: "Gateway.Api");
                b.AddEdge("proj:gateway", "pkg:contracts", EdgeType.DependsOnPackage, "depends",
                    certainty: Certainty.Exact);
            }),
            CancellationToken.None);

        var tools = new McpTools(combined, ScannerBuilder.Create());
        var result = (await tools.InvokeAsync("package_dependents",
            ArgumentsFor(new JsonObject { ["packageId"] = "PrimeLabs.Contracts" }), ct: default)).AsObject();

        Assert.Equal(1, result["matchedVersions"]!.GetValue<int>());
        Assert.Equal(1, result["dependentCount"]!.GetValue<int>());

        var dep = result["dependents"]!.AsArray().Single()!.AsObject();
        Assert.Equal("gateway", dep["repo"]!.GetValue<string>());
        Assert.Equal("2.0.0", dep["version"]!.GetValue<string>());
    }

    [Fact]
    public async Task PackageDependents_VersionFilter_ExcludesOtherVersions()
    {
        var combined = new CombinedGraph();
        await combined.ReplaceRepositoryAsync("/repo",
            BuildRepo(b =>
            {
                b.AddNode("pkg:v1", NodeType.Package, "Pkg@1.0",
                    metadata: new Dictionary<string, string?> { ["packageId"] = "Foo.Pkg", ["version"] = "1.0.0" });
                b.AddNode("pkg:v2", NodeType.Package, "Pkg@2.0",
                    metadata: new Dictionary<string, string?> { ["packageId"] = "Foo.Pkg", ["version"] = "2.0.0" });
                b.AddNode("proj:a", NodeType.Project, "A", repositoryName: "repo-a");
                b.AddNode("proj:b", NodeType.Project, "B", repositoryName: "repo-b");
                b.AddEdge("proj:a", "pkg:v1", EdgeType.DependsOnPackage, "a uses v1");
                b.AddEdge("proj:b", "pkg:v2", EdgeType.DependsOnPackage, "b uses v2");
            }),
            CancellationToken.None);

        var tools = new McpTools(combined, ScannerBuilder.Create());
        var result = (await tools.InvokeAsync("package_dependents",
            ArgumentsFor(new JsonObject { ["packageId"] = "Foo.Pkg", ["version"] = "1.0.0" }), ct: default)).AsObject();

        Assert.Equal(1, result["dependentCount"]!.GetValue<int>());
        Assert.Equal("repo-a", result["dependents"]!.AsArray().Single()!.AsObject()["repo"]!.GetValue<string>());
    }

    [Fact]
    public async Task PackageDependents_MissingPackageId_Throws()
    {
        var tools = new McpTools(new CombinedGraph(), ScannerBuilder.Create());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            tools.InvokeAsync("package_dependents", ArgumentsFor(new JsonObject()), ct: default));
    }

    [Fact]
    public async Task PackageDependents_NoMatch_ReturnsEmptyDependents()
    {
        var combined = new CombinedGraph();
        await combined.ReplaceRepositoryAsync("/repo",
            BuildRepo(b => b.AddNode("proj:a", NodeType.Project, "A", repositoryName: "repo-a")),
            CancellationToken.None);

        var tools = new McpTools(combined, ScannerBuilder.Create());
        var result = (await tools.InvokeAsync("package_dependents",
            ArgumentsFor(new JsonObject { ["packageId"] = "NonExistent" }), ct: default)).AsObject();

        Assert.Equal(0, result["dependentCount"]!.GetValue<int>());
        Assert.Equal(0, result["matchedVersions"]!.GetValue<int>());
    }

    // ── table_entry_points ──────────────────────────────────────────────────

    [Fact]
    public async Task TableEntryPoints_EndpointCallsMethodQueriesTable_ReturnsEndpoint()
    {
        var combined = new CombinedGraph();
        await combined.ReplaceRepositoryAsync("/repo",
            BuildRepo(b =>
            {
                b.AddNode("ep:create", NodeType.Endpoint, "POST /api/orders",
                    repositoryName: "orders-svc", projectName: "Orders.Api",
                    metadata: new Dictionary<string, string?> { ["verb"] = "POST", ["route"] = "/api/orders", ["handler"] = "m:handler" });
                b.AddNode("m:handler", NodeType.Method, "OrdersController.Create",
                    repositoryName: "orders-svc", projectName: "Orders.Api");
                b.AddNode("entity:order", NodeType.Entity, "Order",
                    repositoryName: "orders-svc");
                b.AddNode("table:orders", NodeType.Table, "Orders",
                    repositoryName: "orders-svc");

                b.AddEdge("ep:create", "m:handler", EdgeType.Calls, "calls handler");
                b.AddEdge("m:handler", "entity:order", EdgeType.QueriesEntity, "queries order");
                b.AddEdge("entity:order", "table:orders", EdgeType.MapsToTable, "maps to orders");
            }),
            CancellationToken.None);

        var tools = new McpTools(combined, ScannerBuilder.Create());
        var result = (await tools.InvokeAsync("table_entry_points",
            ArgumentsFor(new JsonObject { ["table"] = "Orders" }), ct: default)).AsObject();

        Assert.Equal(1, result["entryPointCount"]!.GetValue<int>());
        var ep = result["entryPoints"]!.AsArray().Single()!.AsObject();
        Assert.Equal("ep:create", ep["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task TableEntryPoints_StructuralEdgesNotTraversed_NoFalsePositives()
    {
        // A Project that Contains both an Endpoint and a Table should NOT
        // make the endpoint appear as an entry point via Contains edges.
        var combined = new CombinedGraph();
        await combined.ReplaceRepositoryAsync("/repo",
            BuildRepo(b =>
            {
                b.AddNode("proj:api", NodeType.Project, "Orders.Api");
                b.AddNode("ep:list", NodeType.Endpoint, "GET /api/orders",
                    metadata: new Dictionary<string, string?> { ["verb"] = "GET", ["route"] = "/api/orders", ["handler"] = "h" });
                b.AddNode("table:orders", NodeType.Table, "Orders");

                b.AddEdge("proj:api", "ep:list", EdgeType.Defines, "project defines endpoint");
                b.AddEdge("proj:api", "table:orders", EdgeType.Defines, "project defines table");
            }),
            CancellationToken.None);

        var tools = new McpTools(combined, ScannerBuilder.Create());
        var result = (await tools.InvokeAsync("table_entry_points",
            ArgumentsFor(new JsonObject { ["table"] = "Orders" }), ct: default)).AsObject();

        Assert.Equal(0, result["entryPointCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task TableEntryPoints_NoMatchingTable_ReturnsEmpty()
    {
        var combined = new CombinedGraph();
        await combined.ReplaceRepositoryAsync("/repo",
            BuildRepo(b => b.AddNode("table:wallets", NodeType.Table, "Wallets")),
            CancellationToken.None);

        var tools = new McpTools(combined, ScannerBuilder.Create());
        var result = (await tools.InvokeAsync("table_entry_points",
            ArgumentsFor(new JsonObject { ["table"] = "NonExistent" }), ct: default)).AsObject();

        Assert.Equal(0, result["entryPointCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task TableEntryPoints_MissingTableArg_Throws()
    {
        var tools = new McpTools(new CombinedGraph(), ScannerBuilder.Create());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            tools.InvokeAsync("table_entry_points", ArgumentsFor(new JsonObject()), ct: default));
    }

    // ── repo_dependency_matrix ──────────────────────────────────────────────

    [Fact]
    public async Task RepoDependencyMatrix_CrossRepoEdge_AppearsInResolvedDependencies()
    {
        var combined = new CombinedGraph();
        await combined.ReplaceRepositoryAsync("/repo",
            BuildRepo(b =>
            {
                b.AddNode("ext:1", NodeType.ExternalEndpoint, "GET /api/orders",
                    repositoryName: "gateway",
                    metadata: new Dictionary<string, string?> { ["verb"] = "GET", ["path"] = "/api/orders" });
                b.AddNode("ep:1", NodeType.Endpoint, "GET /api/orders",
                    repositoryName: "orders-svc",
                    metadata: new Dictionary<string, string?> { ["verb"] = "GET", ["route"] = "/api/orders", ["handler"] = "h" });
                b.AddEdge("ext:1", "ep:1", EdgeType.CrossesRepoBoundary, "cross-repo",
                    certainty: Certainty.Inferred);
            }),
            CancellationToken.None);

        var tools = new McpTools(combined, ScannerBuilder.Create());
        var result = (await tools.InvokeAsync("repo_dependency_matrix", arguments: null, ct: default)).AsObject();

        var deps = result["resolvedDependencies"]!.AsArray();
        // CrossRepoResolver may add ResolvesToService+CrossesRepoBoundary on top of the
        // manually seeded edge, so pin on pair presence + callCount >= 1, not exact count.
        var dep = deps
            .Select(d => d!.AsObject())
            .FirstOrDefault(d => d["from"]!.GetValue<string>() == "gateway"
                && d["to"]!.GetValue<string>() == "orders-svc");
        Assert.NotNull(dep);
        Assert.True(dep["callCount"]!.GetValue<int>() >= 1);
    }

    [Fact]
    public async Task RepoDependencyMatrix_CallsHttpEdges_CountedInOutbound()
    {
        var combined = new CombinedGraph();
        await combined.ReplaceRepositoryAsync("/repo",
            BuildRepo(b =>
            {
                b.AddNode("m:client", NodeType.Method, "GatewayClient.GetOrders",
                    repositoryName: "gateway");
                b.AddNode("ext:orders", NodeType.ExternalEndpoint, "GET /api/orders",
                    repositoryName: "gateway",
                    metadata: new Dictionary<string, string?> { ["verb"] = "GET", ["path"] = "/api/orders" });
                b.AddNode("ext:users", NodeType.ExternalEndpoint, "GET /api/users",
                    repositoryName: "gateway",
                    metadata: new Dictionary<string, string?> { ["verb"] = "GET", ["path"] = "/api/users" });
                b.AddEdge("m:client", "ext:orders", EdgeType.CallsHttp, "calls orders");
                b.AddEdge("m:client", "ext:users", EdgeType.CallsHttp, "calls users");
            }),
            CancellationToken.None);

        var tools = new McpTools(combined, ScannerBuilder.Create());
        var result = (await tools.InvokeAsync("repo_dependency_matrix", arguments: null, ct: default)).AsObject();

        var outbound = result["outboundByRepo"]!.AsArray();
        var gatewayEntry = outbound.Single(r => r!.AsObject()["repo"]!.GetValue<string>() == "gateway")!.AsObject();
        Assert.Equal(2, gatewayEntry["callCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task RepoDependencyMatrix_SameRepoEdges_NotIncludedInResolvedDependencies()
    {
        // ResolvesToService edges where source and target are the same repo
        // (intra-repo resolution) must not appear in the resolved matrix.
        var combined = new CombinedGraph();
        await combined.ReplaceRepositoryAsync("/repo",
            BuildRepo(b =>
            {
                b.AddNode("ext:local", NodeType.ExternalEndpoint, "GET /api/health",
                    repositoryName: "svc",
                    metadata: new Dictionary<string, string?> { ["verb"] = "GET", ["path"] = "/api/health" });
                b.AddNode("ep:health", NodeType.Endpoint, "GET /api/health",
                    repositoryName: "svc",
                    metadata: new Dictionary<string, string?> { ["verb"] = "GET", ["route"] = "/api/health", ["handler"] = "h" });
                b.AddEdge("ext:local", "ep:health", EdgeType.ResolvesToService, "intra-repo resolved",
                    certainty: Certainty.Exact);
            }),
            CancellationToken.None);

        var tools = new McpTools(combined, ScannerBuilder.Create());
        var result = (await tools.InvokeAsync("repo_dependency_matrix", arguments: null, ct: default)).AsObject();

        Assert.Empty(result["resolvedDependencies"]!.AsArray());
    }

    [Fact]
    public void ToolDefinitions_IncludeAllFourAnalysisTools()
    {
        var names = McpTools.GetDefinitions().Select(d => d.Name).ToHashSet();
        Assert.Contains("endpoint_callers", names);
        Assert.Contains("package_dependents", names);
        Assert.Contains("table_entry_points", names);
        Assert.Contains("repo_dependency_matrix", names);
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
