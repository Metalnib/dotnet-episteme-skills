using Synopsis.Analysis.Model;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Synopsis.Analysis.Roslyn;

public sealed class WorkspaceLoader
{
    private static readonly Lock RegistrationLock = new();
    private static bool _registered;

    public async Task<LoadedWorkspace> LoadAsync(
        ScanOptions options,
        DiscoveryResult discovery,
        CancellationToken ct = default,
        IProgress<ProgressEvent>? progress = null)
    {
        EnsureMsBuildRegistered();

        var warnings = new List<ScanWarning>();
        var loadedProjects = new Dictionary<string, (Project Project, string? SolutionPath)>(StringComparer.OrdinalIgnoreCase);
        var workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
        {
            ["DesignTimeBuild"] = "true",
            ["BuildingInsideVisualStudio"] = "true",
            ["AlwaysCompileMarkupFilesInSeparateDomain"] = "false"
        });

        workspace.RegisterWorkspaceFailedHandler(args =>
            warnings.Add(new ScanWarning("workspace-failed", args.Diagnostic.Message, null, Certainty.Ambiguous)));

        var totalItems = discovery.Solutions.Length + discovery.Projects.Length;
        var processed = 0;

        progress?.Report(new ProgressEvent("roslyn-load",
            $"Opening {discovery.Solutions.Length} solution(s) and {discovery.Projects.Length} project(s).", 0, totalItems));

        foreach (var solution in discovery.Solutions)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                progress?.Report(new ProgressEvent("roslyn-load",
                    $"Opening solution: {solution.Name}", processed, totalItems));

                var opened = await workspace.OpenSolutionAsync(solution.FullPath, cancellationToken: ct);
                foreach (var project in opened.Projects.Where(p => p.Language == LanguageNames.CSharp))
                {
                    var key = Paths.Normalize(project.FilePath ?? project.Name);
                    loadedProjects[key] = (project, solution.FullPath);
                }
                processed++;
            }
            catch (Exception ex)
            {
                processed++;
                warnings.Add(new ScanWarning("solution-load-failed", ex.Message, solution.FullPath, Certainty.Ambiguous));
                progress?.Report(new ProgressEvent("roslyn-load",
                    $"Failed to open solution {solution.FullPath}: {ex.Message}", processed, totalItems));
            }
        }

        foreach (var (looseProject, idx) in discovery.Projects.Select((v, i) => (v, i + 1)))
        {
            ct.ThrowIfCancellationRequested();
            if (loadedProjects.ContainsKey(looseProject.FullPath))
            {
                processed++;
                continue;
            }

            try
            {
                if (ShouldReport(idx, discovery.Projects.Length, 25))
                    progress?.Report(new ProgressEvent("roslyn-load",
                        $"Opening project {idx}/{discovery.Projects.Length}: {looseProject.Name}", processed, totalItems));

                var project = await workspace.OpenProjectAsync(looseProject.FullPath, cancellationToken: ct);
                loadedProjects[looseProject.FullPath] = (project, null);
                processed++;
            }
            catch (Exception ex)
            {
                processed++;
                warnings.Add(new ScanWarning("project-load-failed", ex.Message, looseProject.FullPath, Certainty.Ambiguous));
                progress?.Report(new ProgressEvent("roslyn-load",
                    $"Failed to open project {looseProject.FullPath}: {ex.Message}", processed, totalItems));
            }
        }

        var workspaceRef = new WorkspaceRef();
        var contexts = new List<LoadedProject>();

        progress?.Report(new ProgressEvent("compilation",
            $"Building semantic models for {loadedProjects.Count} project(s).", 0, loadedProjects.Count));

        var compiled = 0;
        foreach (var (_, value) in loadedProjects.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var compilation = await value.Project.GetCompilationAsync(ct);
                if (compilation is null)
                {
                    warnings.Add(new ScanWarning("compilation-null",
                        $"Compilation for '{value.Project.Name}' was null.", value.Project.FilePath, Certainty.Ambiguous));
                    compiled++;
                    continue;
                }

                var repo = discovery.Repositories
                    .Where(r => value.Project.FilePath is not null && Paths.IsUnder(value.Project.FilePath, r.RootPath))
                    .OrderByDescending(r => r.RootPath.Length)
                    .FirstOrDefault();

                contexts.Add(new LoadedProject(workspaceRef, value.Project, compilation,
                    value.SolutionPath, repo?.Name, repo?.RootPath));

                compiled++;
                if (ShouldReport(compiled, loadedProjects.Count, 25))
                    progress?.Report(new ProgressEvent("compilation",
                        $"Compiled {compiled}/{loadedProjects.Count}: {value.Project.Name}", compiled, loadedProjects.Count));
            }
            catch (Exception ex)
            {
                compiled++;
                warnings.Add(new ScanWarning("compilation-failed", ex.Message, value.Project.FilePath, Certainty.Ambiguous));
                progress?.Report(new ProgressEvent("compilation",
                    $"Failed to compile {value.Project.Name}: {ex.Message}", compiled, loadedProjects.Count));
            }
        }

        var catalog = SymbolCatalogFactory.Build(contexts, options);
        var result = new LoadedWorkspace(options, discovery, contexts, catalog, warnings);
        workspaceRef.Value = result;
        return result;
    }

    private static void EnsureMsBuildRegistered()
    {
        lock (RegistrationLock)
        {
            if (_registered || MSBuildLocator.IsRegistered)
            {
                _registered = true;
                return;
            }
            MSBuildLocator.RegisterDefaults();
            _registered = true;
        }
    }

    private static bool ShouldReport(int current, int total, int interval) =>
        current <= 1 || current == total || current % interval == 0;
}
