using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Synopsis.Analysis.Model;

namespace Synopsis.Analysis.Graph;

/// <summary>
/// <see cref="IGraphStateStore"/> that persists per-repository scan
/// results to a local directory. Layout:
/// <code>
/// &lt;state-dir&gt;/
///   index.json               ← path → slug map + metadata
///   repos/
///     &lt;slug&gt;.json       ← one ScanResult per repository
/// </code>
/// Slugs are SHA-256 prefixes of the canonical repo path, so they are
/// stable across restarts and don't carry filesystem-unsafe characters.
/// </summary>
/// <remarks>
/// <para>
/// Saves are atomic <b>under process kill</b>: write to a per-write
/// randomised <c>*.tmp.&lt;guid&gt;</c> then rename. A crash leaves either
/// the pre-write or the post-write state, never a half-write. Power-loss
/// safety depends on the underlying filesystem — no explicit
/// <c>fsync</c>/directory flush is performed. Acceptable for this use
/// case because the graph is reconstructible from source.
/// </para>
/// <para>
/// A <see cref="SemaphoreSlim"/> serialises index-file writes so
/// concurrent <see cref="SaveRepositoryAsync"/> calls can't race each
/// other on the index. Per-repo file writes use random tmp suffixes so
/// two concurrent writes to the same repo slug don't collide on the
/// tmp file — last rename wins, both callers receive a successful save.
/// </para>
/// </remarks>
public sealed class JsonFileStateStore : IGraphStateStore
{
    private readonly string _indexPath;
    private readonly string _reposDir;
    private readonly SemaphoreSlim _indexLock = new(1, 1);

    public JsonFileStateStore(string stateDir)
    {
        Directory.CreateDirectory(stateDir);
        _indexPath = Path.Combine(stateDir, "index.json");
        _reposDir = Path.Combine(stateDir, "repos");
        Directory.CreateDirectory(_reposDir);
    }

    public async Task<CombinedGraphSnapshot?> LoadAsync(CancellationToken ct)
    {
        var index = await ReadIndexAsync(ct);
        if (index is null || index.Repos.Count == 0)
            return new CombinedGraphSnapshot(new Dictionary<string, ScanResult>());

        var perRepo = new Dictionary<string, ScanResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in index.Repos)
        {
            var file = Path.Combine(_reposDir, $"{entry.Slug}.json");
            if (!File.Exists(file)) continue;

            try
            {
                await using var stream = File.OpenRead(file);
                var result = await JsonSerializer.DeserializeAsync(
                    stream, StateStoreJsonContext.Default.ScanResult, ct);
                if (result is not null)
                    perRepo[entry.Path] = result.WithAdjacency();
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Console.Error.WriteLine(
                    $"[state-store] Skipping corrupt repo file '{file}': {ex.Message}");
            }
        }

        return new CombinedGraphSnapshot(perRepo);
    }

    public async Task SaveRepositoryAsync(string repoPath, ScanResult result, CancellationToken ct)
    {
        var slug = Slug(repoPath);
        var targetFile = Path.Combine(_reposDir, $"{slug}.json");

        await WriteAtomicAsync(targetFile,
            s => JsonSerializer.SerializeAsync(s, result, StateStoreJsonContext.Default.ScanResult, ct), ct);

        await _indexLock.WaitAsync(ct);
        try
        {
            var index = await ReadIndexAsync(ct) ?? new StateIndex(Version: 1, Repos: []);
            var updated = index.Repos
                .Where(e => !string.Equals(e.Path, repoPath, StringComparison.OrdinalIgnoreCase))
                .Append(new RepoIndexEntry(
                    Path: repoPath,
                    Slug: slug,
                    LastScannedAtUtc: DateTimeOffset.UtcNow,
                    NodeCount: result.Nodes.Length,
                    EdgeCount: result.Edges.Length))
                .OrderBy(e => e.Path, StringComparer.Ordinal)
                .ToList();

            var newIndex = new StateIndex(Version: 1, Repos: updated);
            await WriteAtomicAsync(_indexPath,
                s => JsonSerializer.SerializeAsync(s, newIndex, StateStoreJsonContext.Default.StateIndex, ct), ct);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async Task<IReadOnlyList<RepositoryRecord>> ListRepositoriesAsync(CancellationToken ct)
    {
        var index = await ReadIndexAsync(ct);
        if (index is null) return [];
        return index.Repos
            .Select(e => new RepositoryRecord(e.Path, e.LastScannedAtUtc, e.NodeCount, e.EdgeCount))
            .ToArray();
    }

    private async Task<StateIndex?> ReadIndexAsync(CancellationToken ct)
    {
        if (!File.Exists(_indexPath)) return null;
        try
        {
            await using var stream = File.OpenRead(_indexPath);
            return await JsonSerializer.DeserializeAsync(stream, StateStoreJsonContext.Default.StateIndex, ct);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Console.Error.WriteLine($"[state-store] Index unreadable: {ex.Message}; starting empty.");
            return null;
        }
    }

    private static async Task WriteAtomicAsync(string finalPath, Func<Stream, Task> writeAsync, CancellationToken ct)
    {
        // Per-write random suffix so two concurrent writers for the same
        // final path don't collide on File.Create. File.Move(overwrite:true)
        // handles the racy rename — last writer wins on the final name.
        var tmp = finalPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = File.Create(tmp))
            {
                await writeAsync(stream);
                await stream.FlushAsync(ct);
            }
            File.Move(tmp, finalPath, overwrite: true);
        }
        catch
        {
            // Best-effort cleanup so a failed write doesn't litter tmp files.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>SHA-256 of the canonical repo path; first 16 hex chars.</summary>
    private static string Slug(string repoPath)
    {
        var canonical = Path.GetFullPath(repoPath).ToLowerInvariant();
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(canonical), hash);
        return Convert.ToHexStringLower(hash[..8]);
    }
}


internal sealed record StateIndex(int Version, List<RepoIndexEntry> Repos);

internal sealed record RepoIndexEntry(
    string Path,
    string Slug,
    DateTimeOffset LastScannedAtUtc,
    int NodeCount,
    int EdgeCount);

[JsonSerializable(typeof(ScanResult))]
[JsonSerializable(typeof(StateIndex))]
[JsonSerializable(typeof(RepoIndexEntry))]
[JsonSerializable(typeof(ImmutableArray<ScanWarning>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string?>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true)]
internal partial class StateStoreJsonContext : JsonSerializerContext;
