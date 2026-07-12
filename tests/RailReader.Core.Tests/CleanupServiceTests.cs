using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class CleanupServiceTests
{
    [Fact]
    public void FormatReport_ZeroFiles_ReturnsNothingMessage()
    {
        var result = CleanupService.FormatReport(0, 0);
        Assert.Equal("Nothing to clean up.", result);
    }

    [Fact]
    public void FormatReport_WithFiles_IncludesCountAndSize()
    {
        var result = CleanupService.FormatReport(3, 2048);

        Assert.Contains("3 file(s)", result);
        Assert.Contains("2.0 KB", result);
    }

    [Fact]
    public void FormatReport_SingleFile_IncludesFileCount()
    {
        var result = CleanupService.FormatReport(1, 512);

        Assert.Contains("1 file(s)", result);
    }
}

/// <summary>
/// RunCleanup's destructive logic — recursive cache/ wipe, *.tmp removal,
/// aged *.log removal — exercised against a throwaway temp config directory
/// (never the real ~/.config/railreader2). Orphaned-annotation cleanup is
/// excluded because it always targets the real user annotation directory.
/// </summary>
public class CleanupServiceRunCleanupTests : IDisposable
{
    private readonly string _configDir;

    public CleanupServiceRunCleanupTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), $"rr-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_configDir))
            Directory.Delete(_configDir, recursive: true);
    }

    private string Write(string relativePath, int bytes = 10)
    {
        var path = Path.Combine(_configDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private (int FilesRemoved, long BytesFreed) Run()
        => CleanupService.RunCleanup(_configDir, includeOrphanedAnnotations: false);

    [Fact]
    public void CacheDirectory_IsWipedRecursively()
    {
        var f1 = Write(Path.Combine("cache", "a.bin"), 100);
        var f2 = Write(Path.Combine("cache", "nested", "deep", "b.bin"), 50);

        var (removed, freed) = Run();

        Assert.Equal(2, removed);
        Assert.Equal(150, freed);
        Assert.False(File.Exists(f1));
        Assert.False(File.Exists(f2));
        Assert.False(Directory.Exists(Path.Combine(_configDir, "cache", "nested")));
        // The cache root itself stays (only its contents are cleaned).
        Assert.True(Directory.Exists(Path.Combine(_configDir, "cache")));
    }

    [Fact]
    public void ProtectedFiles_SurviveCacheWipe()
    {
        var cfg = Write(Path.Combine("cache", "config.json"));
        var lockFile = Write(Path.Combine("cache", "session.lock"));
        var model = Write(Path.Combine("cache", "models", "layout.onnx"), 200);
        var victim = Write(Path.Combine("cache", "models", "stale.bin"));

        Run();

        Assert.True(File.Exists(cfg));
        Assert.True(File.Exists(lockFile));
        Assert.True(File.Exists(model));
        Assert.False(File.Exists(victim));
        // The directory holding a protected file cannot be removed — and must not throw.
        Assert.True(Directory.Exists(Path.Combine(_configDir, "cache", "models")));
    }

    [Fact]
    public void ProtectionIsByExactNameOrSuffix_NotSubstring()
    {
        // A regression that protected by substring (e.g. name.Contains) would keep
        // these; a regression that matched too loosely the other way would delete
        // config.json itself. Both directions are pinned here.
        var protectedCfg = Write(Path.Combine("cache", "config.json"));
        var similar = Write(Path.Combine("cache", "config.json.bak"));
        var onnxLike = Write(Path.Combine("cache", "layout.onnx.download"));

        Run();

        Assert.True(File.Exists(protectedCfg));
        Assert.False(File.Exists(similar));   // suffix .bak — not protected
        Assert.False(File.Exists(onnxLike));  // suffix .download — not protected
    }

    [Fact]
    public void TmpFiles_InConfigRoot_AreRemoved_OthersKept()
    {
        var tmp = Write("scratch.tmp");
        var json = Write("recent.json");
        var cfg = Write("config.json");
        // *.tmp removal is non-recursive: a .tmp in a subdirectory (outside cache/) stays.
        var nestedTmp = Write(Path.Combine("annotations", "x.tmp"));

        var (removed, _) = Run();

        Assert.Equal(1, removed);
        Assert.False(File.Exists(tmp));
        Assert.True(File.Exists(json));
        Assert.True(File.Exists(cfg));
        Assert.True(File.Exists(nestedTmp));
    }

    [Fact]
    public void LogFiles_OnlyOlderThanSevenDays_AreRemoved()
    {
        var oldLog = Write("old.log", 30);
        File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow - TimeSpan.FromDays(8));
        var freshLog = Write("fresh.log");
        var notALog = Write("old.txt");
        File.SetLastWriteTimeUtc(notALog, DateTime.UtcNow - TimeSpan.FromDays(30));

        var (removed, freed) = Run();

        Assert.Equal(1, removed);
        Assert.Equal(30, freed);
        Assert.False(File.Exists(oldLog));
        Assert.True(File.Exists(freshLog));
        Assert.True(File.Exists(notALog));
    }

    [Fact]
    public void MissingCacheDirectory_IsHandledGracefully()
    {
        var (removed, freed) = Run(); // empty config dir, no cache/

        Assert.Equal(0, removed);
        Assert.Equal(0, freed);
    }
}
