using Synopsis.Analysis;
using Synopsis.Analysis.Model;

namespace Synopsis.Tests;

/// <summary>
/// Real MSBuildWorkspace loads over on-disk fixtures — the fixes for issues
/// #8/#9/#10 live in the loader/discovery and cannot be exercised against a
/// synthetic graph. Requires a .NET SDK (present in dev + CI).
/// </summary>
public sealed class WorkspaceLoaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("synopsis-loader-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private const string Csproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static int MethodsNamed(ScanResult r, string name) =>
        r.Nodes.Count(n => n.Type == NodeType.Method
            && n.DisplayName.Contains(name, StringComparison.Ordinal));

    private static string Warnings(ScanResult r) =>
        string.Join(" | ", r.Warnings.Select(w => w.Message));

    [Fact] // issue #10: a transitive P2P dependency was dropped with "already part of the workspace"
    public async Task Scan_FilesystemMode_TransitiveDependency_IsAnalysed()
    {
        Write("Core/Core.csproj", Csproj);
        Write("Core/CoreSvc.cs", "namespace Core; public class CoreSvc { public int CoreMethodX() => 1; }");
        Write("Api/Api.csproj", Csproj.Replace("</Project>",
            "<ItemGroup><ProjectReference Include=\"../Core/Core.csproj\" /></ItemGroup></Project>"));
        Write("Api/ApiSvc.cs", "namespace Api; public class ApiSvc { public int ApiMethodY() => 1; }");

        var result = await new WorkspaceScanner().ScanAsync(_dir);

        Assert.True(MethodsNamed(result, "ApiMethodY") > 0, "Api should load. " + Warnings(result));
        Assert.True(MethodsNamed(result, "CoreMethodX") > 0,
            "Core (pulled in transitively by Api) must be reused, not dropped. " + Warnings(result));
    }

    [Fact] // issue #8: --exclude was ignored when a solution drove discovery
    public async Task Scan_SolutionMode_ExcludeFiltersSolutionProjects()
    {
        // Projects live OUTSIDE the scan root, reachable only via the .slnx, so if
        // the solution fails to load LibMethodX is 0 and the test fails loudly -
        // it can't pass vacuously through the filesystem-discovery fallback.
        Write("libs/Lib/Lib.csproj", Csproj);
        Write("libs/Lib/LibSvc.cs", "namespace Lib; public class LibSvc { public int LibMethodX() => 1; }");
        Write("libs/Tests/Tests.csproj", Csproj);
        Write("libs/Tests/TestsSvc.cs", "namespace T; public class TestsSvc { public int TestsMethodX() => 1; }");
        Write("scanroot/App.slnx", "<Solution><Project Path=\"../libs/Lib/Lib.csproj\" /><Project Path=\"../libs/Tests/Tests.csproj\" /></Solution>");

        var scanRoot = Path.Combine(_dir, "scanroot");
        var options = new ScanOptions(scanRoot, ExcludedPaths: [Path.Combine(_dir, "libs", "Tests")]);
        var result = await new WorkspaceScanner().ScanAsync(scanRoot, options);

        Assert.DoesNotContain(result.Warnings, w => w.Code == "solution-load-failed"); // the solution really drove loading
        Assert.True(MethodsNamed(result, "LibMethodX") > 0, "solution must load Lib. " + Warnings(result));
        Assert.Equal(0, MethodsNamed(result, "TestsMethodX"));
    }

    [Fact] // issue #9: a project reachable only through a .slnx must load
    public async Task Scan_SlnxSolution_LoadsReferencedProject()
    {
        Write("libs/Lib/Lib.csproj", Csproj);
        Write("libs/Lib/LibSvc.cs", "namespace Lib; public class LibSvc { public int SlnxMethodX() => 1; }");
        Write("scanroot/App.slnx", "<Solution><Project Path=\"../libs/Lib/Lib.csproj\" /></Solution>");

        var result = await new WorkspaceScanner().ScanAsync(Path.Combine(_dir, "scanroot"));

        Assert.True(MethodsNamed(result, "SlnxMethodX") > 0,
            "project referenced only by the .slnx must load. " + Warnings(result));
    }
}
