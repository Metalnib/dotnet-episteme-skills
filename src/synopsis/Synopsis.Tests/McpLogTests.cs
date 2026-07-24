using Synopsis.Mcp;

namespace Synopsis.Tests;

public sealed class McpLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("synopsis-mcplog-").FullName;

    public void Dispose()
    {
        McpLog.Reset();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Write_WithConfiguredFile_AppendsTimestampedLine()
    {
        var path = Path.Combine(_dir, "synopsis.log");
        McpLog.Configure(path);

        McpLog.Write("[mcp] hello");
        McpLog.Write("[mcp] world");
        McpLog.Reset();

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("[mcp] hello", lines[0]);
        Assert.EndsWith("[mcp] world", lines[1]);
        Assert.True(DateTimeOffset.TryParse(lines[0].Split(' ')[0], out _));
    }

    [Fact]
    public void Write_WithoutConfigure_OnlyWritesStderr()
    {
        McpLog.Reset();
        McpLog.Write("[mcp] stderr only");
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Configure_CreatesMissingDirectories()
    {
        var path = Path.Combine(_dir, "nested", "deeper", "synopsis.log");
        McpLog.Configure(path);
        McpLog.Write("[mcp] nested");
        McpLog.Reset();

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Configure_AppendsAcrossReconfigure()
    {
        var path = Path.Combine(_dir, "synopsis.log");
        McpLog.Configure(path);
        McpLog.Write("[mcp] first run");
        McpLog.Configure(path);
        McpLog.Write("[mcp] second run");
        McpLog.Reset();

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void Configure_FallsBackToEnvironmentVariable()
    {
        var path = Path.Combine(_dir, "from-env.log");
        Environment.SetEnvironmentVariable("SYNOPSIS_LOG_FILE", path);
        try
        {
            McpLog.Configure(null);
            McpLog.Write("[mcp] via env");
            McpLog.Reset();

            Assert.EndsWith("[mcp] via env", File.ReadAllLines(path).Single());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SYNOPSIS_LOG_FILE", null);
        }
    }

    [Fact]
    public void Configure_UnwritablePath_DoesNotThrow()
    {
        McpLog.Configure(Path.Combine(_dir, "\0invalid"));
        McpLog.Write("[mcp] still alive");
    }
}
