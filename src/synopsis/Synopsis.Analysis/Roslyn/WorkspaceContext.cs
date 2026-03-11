using Synopsis.Analysis.Model;
using Synopsis.Analysis.Graph;
using Microsoft.CodeAnalysis;

namespace Synopsis.Analysis.Roslyn;

public interface IAnalysisPass
{
    string Name { get; }

    /// <param name="project">The project to analyze.</param>
    /// <param name="graph">Per-project builder to write nodes/edges into (lock-free).</param>
    /// <param name="mainGraph">The main accumulated graph for cross-project lookups (read-only). Null for the first pass.</param>
    /// <param name="ct">Cancellation token.</param>
    void Analyze(LoadedProject project, GraphBuilder graph, GraphBuilder? mainGraph, CancellationToken ct);
}

public sealed record TypeRef(
    string Id,
    string DisplayName,
    SourceLocation? Location,
    string? RepositoryName,
    string? ProjectName);

public sealed record MethodRef(
    string Id,
    string ContainingTypeId,
    string DisplayName,
    SourceLocation? Location,
    string? RepositoryName,
    string? ProjectName);

public sealed record ServiceBinding(
    string InterfaceId,
    string ImplementationId,
    string RepositoryName,
    string ProjectName,
    SourceLocation? Location);

public sealed class SymbolCatalog
{
    public Dictionary<string, List<TypeRef>> ImplementationsByInterfaceId { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<MethodRef>> ImplementationMethodsByInterfaceMethodId { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<ServiceBinding>> RegisteredBindingsByInterfaceId { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LoadedWorkspace
{
    private readonly Dictionary<ProjectId, LoadedProject> _projectsById;

    public LoadedWorkspace(
        ScanOptions options,
        DiscoveryResult discovery,
        IReadOnlyList<LoadedProject> projects,
        SymbolCatalog symbolCatalog,
        IReadOnlyList<ScanWarning> warnings)
    {
        Options = options;
        Discovery = discovery;
        Projects = projects;
        Catalog = symbolCatalog;
        Warnings = warnings;
        _projectsById = projects.ToDictionary(p => p.Project.Id);
    }

    public ScanOptions Options { get; }
    public DiscoveryResult Discovery { get; }
    public IReadOnlyList<LoadedProject> Projects { get; }
    public SymbolCatalog Catalog { get; }
    public IReadOnlyList<ScanWarning> Warnings { get; }

    public LoadedProject? FindProject(ProjectId id) => _projectsById.GetValueOrDefault(id);
}

public sealed class LoadedProject
{
    public LoadedProject(
        WorkspaceRef workspaceRef,
        Project project,
        Compilation compilation,
        string? solutionPath,
        string? repositoryName,
        string? repositoryPath)
    {
        WorkspaceRef = workspaceRef;
        Project = project;
        Compilation = compilation;
        SolutionPath = solutionPath;
        RepositoryName = repositoryName;
        RepositoryPath = repositoryPath;
    }

    internal WorkspaceRef WorkspaceRef { get; }
    public LoadedWorkspace Workspace => WorkspaceRef.Value;
    public Project Project { get; }
    public Compilation Compilation { get; }
    public string? SolutionPath { get; }
    public string? RepositoryName { get; }
    public string? RepositoryPath { get; }
    public string ProjectName => Project.Name;

    public IEnumerable<FoundConfig> ConfigurationValues =>
        Workspace.Discovery.ConfigurationValues.Where(v =>
            string.Equals(v.RepositoryPath, RepositoryPath, StringComparison.OrdinalIgnoreCase));
}

public sealed class WorkspaceRef
{
    public LoadedWorkspace Value { get; set; } = null!;
}
