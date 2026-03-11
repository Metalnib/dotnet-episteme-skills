using System.Collections.Immutable;
using System.Diagnostics;
using Synopsis.Analysis.Model;
using Synopsis.Analysis.Graph;
using Synopsis.Analysis.Roslyn;
using Synopsis.Analysis.Scanning;

namespace Synopsis.Analysis;

public sealed class WorkspaceScanner
{
    private readonly WorkspaceLoader _loader;
    private readonly IReadOnlyList<IAnalysisPass> _passes;

    public WorkspaceScanner(WorkspaceLoader? loader = null, IEnumerable<IAnalysisPass>? passes = null)
    {
        _loader = loader ?? new WorkspaceLoader();
        _passes = [new Roslyn.Passes.StructurePass(), .. (passes ?? [])];
    }

    public async Task<ScanResult> ScanAsync(string rootPath, ScanOptions? options = null,
        CancellationToken ct = default, IProgress<ProgressEvent>? progress = null)
    {
        var startedAt = DateTimeOffset.UtcNow;
        options ??= ScanOptions.For(rootPath);
        var timings = new List<Timing>();

        progress?.Report(new ProgressEvent("scan", $"Starting scan of {options.RootPath}."));

        var discoveryWatch = Stopwatch.StartNew();
        var discovery = WorkspaceDiscovery.Discover(options.RootPath, ct, options);
        timings.Add(new Timing("filesystem-discovery", discoveryWatch.Elapsed));
        progress?.Report(new ProgressEvent("filesystem-discovery",
            $"Discovery: {discovery.Repositories.Length} repos, {discovery.Solutions.Length} solutions, {discovery.Projects.Length} projects in {discoveryWatch.Elapsed.TotalSeconds:F1}s."));

        if (options.ExcludedPaths is { Count: > 0 })
            progress?.Report(new ProgressEvent("filesystem-discovery",
                $"Excluding {options.ExcludedPaths.Count} pattern(s): {string.Join(", ", options.ExcludedPaths)}"));

        var graph = new GraphBuilder();
        AddTopology(graph, options.RootPath, discovery);

        var loadWatch = Stopwatch.StartNew();
        var workspace = await _loader.LoadAsync(options, discovery, ct, progress);
        timings.Add(new Timing("roslyn-load", loadWatch.Elapsed));
        progress?.Report(new ProgressEvent("roslyn-load",
            $"Roslyn load: {workspace.Projects.Count} project(s) in {loadWatch.Elapsed.TotalSeconds:F1}s."));

        // Run analysis passes - each pass parallelizes across projects with
        // per-project lock-free builders, then merges into the main graph.
        // This avoids lock contention: N merges instead of N*M lock acquisitions.
        foreach (var pass in _passes)
        {
            var passWatch = Stopwatch.StartNew();
            progress?.Report(new ProgressEvent(pass.Name,
                $"Running {pass.Name} across {workspace.Projects.Count} project(s)."));

            var perProjectBuilders = new GraphBuilder[workspace.Projects.Count];

            // Capture main graph snapshot for cross-project lookups (e.g. endpoint resolution)
            var mainSnapshot = graph;

            Parallel.For(0, workspace.Projects.Count,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
                i =>
                {
                    ct.ThrowIfCancellationRequested();
                    var projectGraph = new GraphBuilder();
                    pass.Analyze(workspace.Projects[i], projectGraph, mainSnapshot, ct);
                    perProjectBuilders[i] = projectGraph;
                });

            // Single-threaded merge - fast, no contention
            foreach (var projectGraph in perProjectBuilders)
                graph.Merge(projectGraph);

            timings.Add(new Timing(pass.Name, passWatch.Elapsed));
            progress?.Report(new ProgressEvent(pass.Name,
                $"{pass.Name} completed in {passWatch.Elapsed.TotalSeconds:F1}s."));
        }

        var completedAt = DateTimeOffset.UtcNow;
        var scanInfo = new ScanInfo(options.RootPath, startedAt, completedAt, [.. timings],
            new Dictionary<string, string>
            {
                ["scanner"] = nameof(WorkspaceScanner),
                ["dotnet"] = Environment.Version.ToString()
            });

        var warnings = discovery.Warnings.AddRange(workspace.Warnings);
        progress?.Report(new ProgressEvent("scan",
            $"Scan complete in {(completedAt - startedAt).TotalSeconds:F1}s with {warnings.Length} warning(s)."));

        return graph.Build(scanInfo, [.. warnings]);
    }

    public static string WorkspaceNodeId(string rootPath) => NodeId.From("workspace", rootPath);
    public static string RepositoryNodeId(string repoPath) => NodeId.From("repository", repoPath);
    public static string SolutionNodeId(string solutionPath) => NodeId.From("solution", solutionPath);
    public static string ProjectNodeId(string projectPath) => NodeId.From("project", projectPath);

    private static void AddTopology(GraphBuilder graph, string rootPath, DiscoveryResult discovery)
    {
        var workspaceId = WorkspaceNodeId(rootPath);
        graph.AddNode(workspaceId, NodeType.Workspace, Path.GetFileName(rootPath),
            new SourceLocation(rootPath), certainty: Certainty.Exact,
            metadata: new Dictionary<string, string?> { ["rootPath"] = rootPath });

        foreach (var repo in discovery.Repositories)
        {
            var repoId = RepositoryNodeId(repo.RootPath);
            graph.AddNode(repoId, NodeType.Repository, repo.Name,
                new SourceLocation(repo.RootPath), repo.Name, certainty: Certainty.Exact,
                metadata: new Dictionary<string, string?> { ["rootPath"] = repo.RootPath });

            graph.AddEdge(workspaceId, repoId, EdgeType.Contains,
                $"{Path.GetFileName(rootPath)} contains {repo.Name}");
        }

        foreach (var solution in discovery.Solutions)
        {
            var solutionId = SolutionNodeId(solution.FullPath);
            graph.AddNode(solutionId, NodeType.Solution, solution.Name,
                new SourceLocation(solution.FullPath), solution.RepositoryName, certainty: Certainty.Exact,
                metadata: new Dictionary<string, string?> { ["filePath"] = solution.FullPath });

            var ownerId = solution.RepositoryPath is not null ? RepositoryNodeId(solution.RepositoryPath) : workspaceId;
            graph.AddEdge(ownerId, solutionId, EdgeType.Contains,
                $"{Path.GetFileName(ownerId)} contains {solution.Name}");
        }

        foreach (var project in discovery.Projects)
        {
            var projectId = ProjectNodeId(project.FullPath);
            graph.AddNode(projectId, NodeType.Project, project.Name,
                new SourceLocation(project.FullPath), project.RepositoryName, project.Name,
                Certainty.Exact, new Dictionary<string, string?> { ["filePath"] = project.FullPath });

            var ownerId = project.RepositoryPath is not null ? RepositoryNodeId(project.RepositoryPath) : workspaceId;
            graph.AddEdge(ownerId, projectId, EdgeType.Contains,
                $"{project.RepositoryName ?? Path.GetFileName(rootPath)} contains {project.Name}");
        }

        foreach (var config in discovery.ConfigurationValues)
        {
            var configId = NodeId.From("config", config.FilePath, config.Key);
            graph.AddNode(configId, NodeType.ConfigurationKey, config.Key,
                new SourceLocation(config.FilePath), config.RepositoryName, certainty: Certainty.Inferred,
                metadata: new Dictionary<string, string?> { ["value"] = config.Value, ["filePath"] = config.FilePath });
        }
    }
}
