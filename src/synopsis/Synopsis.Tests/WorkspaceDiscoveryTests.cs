using Synopsis.Analysis.Scanning;

namespace Synopsis.Tests;

public sealed class WorkspaceDiscoveryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("synopsis-discovery-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Touch(string relative, string content = "")
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact] // issue #9: .slnx became the default `dotnet new sln` format on .NET 9/10
    public void Discover_SlnxSolution_IsDiscovered()
    {
        Touch("App.slnx", "<Solution />");

        var result = WorkspaceDiscovery.Discover(_dir);

        Assert.Contains(result.Solutions,
            s => s.FullPath.EndsWith("App.slnx", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] // regression guard: classic .sln discovery still works
    public void Discover_ClassicSln_StillDiscovered()
    {
        Touch("App.sln");

        var result = WorkspaceDiscovery.Discover(_dir);

        Assert.Contains(result.Solutions,
            s => s.FullPath.EndsWith("App.sln", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] // #9 safeguard: a mid-migration dir with both formats must not double-load
    public void Discover_BothSlnAndSlnx_KeepsOnlySlnx()
    {
        Touch("App.sln");
        Touch("App.slnx", "<Solution />");

        var result = WorkspaceDiscovery.Discover(_dir);

        Assert.Single(result.Solutions);
        Assert.EndsWith("App.slnx", result.Solutions[0].FullPath, StringComparison.OrdinalIgnoreCase);
    }
}
