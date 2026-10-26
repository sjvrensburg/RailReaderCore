using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>Host-supplied document key for sidecar naming (#153).</summary>
public class AnnotationDocumentKeyTests : IDisposable
{
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly List<string> _cleanup = [];

    private string PdfPath(string name) => Path.Combine(Path.GetTempPath(), $"{_id}-{name}.pdf");

    private static AnnotationFile Sample()
    {
        var f = new AnnotationFile();
        f.Pages[0] = [new HighlightAnnotation { Rects = [new HighlightRect(0, 0, 10, 10)] }];
        return f;
    }

    public void Dispose()
    {
        foreach (var p in _cleanup) if (File.Exists(p)) File.Delete(p);
        foreach (var f in Directory.EnumerateFiles(AnnotationService.AnnotationDir, $"{_id}*.json")) File.Delete(f);
    }

    [Fact]
    public void KeyedSidecar_SurvivesMove()
    {
        var svc = new AnnotationService { DocumentKeyResolver = _ => $"{_id}-doc" };
        var before = PdfPath("a");
        var after = PdfPath("moved");
        Assert.True(svc.Save(before, Sample()));
        _cleanup.Add(svc.GetStoragePath(before));

        var loaded = svc.Load(after);
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Pages[0]);
    }

    [Fact]
    public void LegacyPathHashFile_IsReadAndKept_ThenKeyedFileWritten()
    {
        var pdf = PdfPath("legacy");
        var plain = new AnnotationService();
        plain.Save(pdf, Sample());
        var legacy = AnnotationService.GetInternalPath(pdf);
        _cleanup.Add(legacy);

        var svc = new AnnotationService { DocumentKeyResolver = _ => $"{_id}-key" };
        var loaded = svc.Load(pdf);
        Assert.NotNull(loaded);
        Assert.False(File.Exists(svc.GetStoragePath(pdf)));

        svc.Save(pdf, loaded!);
        _cleanup.Add(svc.GetStoragePath(pdf));
        Assert.True(File.Exists(svc.GetStoragePath(pdf)));
        Assert.True(File.Exists(legacy));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a/b")]
    public void UnusableKey_FallsBackToPathHash(string? key)
    {
        var svc = new AnnotationService { DocumentKeyResolver = _ => key };
        var pdf = PdfPath("fallback");
        Assert.Equal(AnnotationService.GetInternalPath(pdf), svc.GetStoragePath(pdf));
    }

    [Fact]
    public void Delete_RemovesKeyedAndLegacy()
    {
        var pdf = PdfPath("del");
        new AnnotationService().Save(pdf, Sample());
        var svc = new AnnotationService { DocumentKeyResolver = _ => $"{_id}-del" };
        svc.Save(pdf, Sample());

        Assert.True(svc.Delete(pdf));
        Assert.Null(svc.Load(pdf));
    }
}
