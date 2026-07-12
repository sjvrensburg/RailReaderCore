using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Regression tests for the 2026-07 code-audit fixes in RailReader.Core.Pdfium:
/// AppConfig.Load no longer clobbers an unreadable config, PdfOutlineService
/// initialises PDFium defensively, ConsoleLogger is safe under concurrent writes,
/// and the text/link services' cached document handle stays correct across
/// per-page call bursts and document switches.
/// </summary>
public class PdfiumAuditFixTests
{
    // ---- Finding 26: AppConfig.Load must not overwrite the user's config ----

    [Fact]
    public void Load_CorruptConfig_PreservesFileAndBacksUp()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"railreader_cfg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        AppConfig.OverrideConfigDirForTesting(dir);
        try
        {
            const string corrupt = "{ this is not json";
            File.WriteAllText(AppConfig.ConfigPath, corrupt);

            var config = AppConfig.Load();

            // Defaults returned in memory, but the on-disk file is untouched…
            Assert.Equal(3.0, config.RailZoomThreshold);
            Assert.Equal(corrupt, File.ReadAllText(AppConfig.ConfigPath));
            // …and a forensic backup was made.
            Assert.Equal(corrupt, File.ReadAllText(AppConfig.ConfigPath + ".bad"));
        }
        finally
        {
            AppConfig.OverrideConfigDirForTesting(null);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Load_MissingConfig_CreatesDefaultsOnDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"railreader_cfg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        AppConfig.OverrideConfigDirForTesting(dir);
        try
        {
            var config = AppConfig.Load();
            Assert.True(File.Exists(AppConfig.ConfigPath)); // first-run behaviour unchanged
            Assert.Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);
        }
        finally
        {
            AppConfig.OverrideConfigDirForTesting(null);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Load_ValidConfig_StillLoadsNormally()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"railreader_cfg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        AppConfig.OverrideConfigDirForTesting(dir);
        try
        {
            var original = new AppConfig { RailZoomThreshold = 7.5 };
            original.Save();

            var loaded = AppConfig.Load();
            Assert.Equal(7.5, loaded.RailZoomThreshold);
            Assert.False(File.Exists(AppConfig.ConfigPath + ".bad"));
        }
        finally
        {
            AppConfig.OverrideConfigDirForTesting(null);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ---- Finding 22: outline extraction as a (potential) first PDFium touch ----

    [Fact]
    public void OutlineExtract_OnPlainPdf_ReturnsEmptyWithoutCrashing()
    {
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var outline = new PdfOutlineService().Extract(bytes);
        Assert.NotNull(outline);
        Assert.Empty(outline); // synthetic fixture has no bookmarks
    }

    // ---- Finding 25: ConsoleLogger under concurrent writers ----

    [Fact]
    public void ConsoleLogger_ConcurrentWrites_DoNotCorruptWriter()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"railreader_log_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        AppConfig.OverrideConfigDirForTesting(dir);
        try
        {
            using var logger = new ConsoleLogger();
            Assert.NotNull(logger.LogFilePath);

            Parallel.For(0, 8, t =>
            {
                for (int i = 0; i < 200; i++)
                    logger.Debug($"thread {t} message {i} {new string('x', 64)}");
            });

            // The writer must still be functional after the concurrent burst.
            logger.Info("post-burst sentinel");
        }
        finally
        {
            AppConfig.OverrideConfigDirForTesting(null);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ---- Findings 21/24: init paths are idempotent and safe under concurrency ----

    [Fact]
    public async Task EnsureLibraryInitialized_ConcurrentFirstTouches_DoNotThrowOrDeadlock()
    {
        // Mixes the two historical acquisition orders: bare EnsureLibraryInitialized
        // (PdfAnnotationReader-style) racing callers that already hold the gate
        // (PdfTextService-style). With the single-lock design this must neither
        // deadlock nor throw from a duplicate SetDllImportResolver registration.
        var tasks = new List<Task>();
        for (int i = 0; i < 8; i++)
        {
            tasks.Add(Task.Run(() => PdfiumResolver.EnsureLibraryInitialized()));
            tasks.Add(Task.Run(() =>
            {
                lock (PdfiumGate.Lock)
                {
                    PdfiumResolver.EnsureLibraryInitialized();
                }
            }));
            tasks.Add(Task.Run(() => PdfiumResolver.Initialize()));
        }
        // Throws TimeoutException if the initialisation paths deadlock.
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
    }

    // ---- Finding 28: cached document handle in text/link services ----

    [Fact]
    public void PdfTextService_RepeatedCalls_SameBytes_ReuseIsTransparent()
    {
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var svc = new PdfTextService();

        var first = svc.ExtractPageText(bytes, 0);
        var again = svc.ExtractPageText(bytes, 0);   // cache hit
        var page1 = svc.ExtractPageText(bytes, 1);   // cache hit, different page

        Assert.Equal(first.Text, again.Text);
        Assert.Equal(first.CharBoxes.Count, again.CharBoxes.Count);
        Assert.NotEqual(first.Text, page1.Text);
    }

    [Fact]
    public void PdfTextService_SwitchingDocuments_EvictsAndReloads()
    {
        var bytesA = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var bytesB = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var svc = new PdfTextService();

        var a1 = svc.ExtractPageText(bytesA, 0);
        var b = svc.ExtractPageText(bytesB, 0);  // different array instance → reload
        var a2 = svc.ExtractPageText(bytesA, 0); // back again → reload

        Assert.Equal(a1.Text, a2.Text);
        Assert.NotNull(b.Text);
    }

    [Fact]
    public void PdfTextService_InvalidBytes_StillFailSoft()
    {
        var svc = new PdfTextService();
        var garbage = new byte[] { 1, 2, 3, 4, 5 };
        var result = svc.ExtractPageText(garbage, 0);
        Assert.Equal("", result.Text);

        // A failed load must not poison the cache for a subsequent good document.
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var ok = svc.ExtractPageText(bytes, 0);
        Assert.NotEqual("", ok.Text);
    }

    [Fact]
    public void PdfLinkService_RepeatedCalls_SameBytes_ReuseIsTransparent()
    {
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var svc = new PdfLinkService();

        var first = svc.ExtractPageLinks(bytes, 0);
        var again = svc.ExtractPageLinks(bytes, 0);
        Assert.Equal(first.Count, again.Count);
    }

    [Fact]
    public void PdfTextService_ConcurrentCallers_AreSerializedSafely()
    {
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var svc = new PdfTextService();
        var baseline = svc.ExtractPageText(bytes, 0).Text;

        Parallel.For(0, 16, i =>
        {
            var text = svc.ExtractPageText(bytes, i % 3).Text;
            if (i % 3 == 0) Assert.Equal(baseline, text);
            Assert.NotNull(text);
        });
    }
}
