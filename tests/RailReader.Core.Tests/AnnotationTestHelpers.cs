using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Shared fixture helpers for the native PDF annotation test suites
/// (writer, reconcile, fixes, reader, composite store): the synthetic plain
/// PDF, single-page <see cref="AnnotationFile"/> construction, page read-back,
/// and the optional real Acrobat-annotated fixture.
/// </summary>
internal static class AnnotationTestHelpers
{
    /// <summary>Bytes of the 3-page synthetic fixture PDF (no annotations).</summary>
    public static byte[] PlainPdfBytes() => File.ReadAllBytes(TestFixtures.GetTestPdfPath());

    /// <summary>An <see cref="AnnotationFile"/> carrying <paramref name="anns"/> on page 0.</summary>
    public static AnnotationFile OnePage(params Annotation[] anns)
    {
        var f = new AnnotationFile();
        f.Pages[0] = [.. anns];
        return f;
    }

    /// <summary>Reads the given page's annotations back out of raw PDF bytes.</summary>
    public static List<Annotation> ReadBack(byte[] bytes, int page = 0)
    {
        var file = new PdfAnnotationReader().Read(bytes);
        return file.Pages.TryGetValue(page, out var list) ? list : [];
    }

    /// <summary>
    /// Path to a genuine Acrobat-reviewed PDF (40 annotations with real /AP
    /// streams, reviewer metadata, mixed subtypes) used by the real-world
    /// preservation tests. Not checked in — override the default location with
    /// the <c>RAILREADER_ACROBAT_PDF</c> environment variable. Tests using it
    /// are declared with <see cref="RealAcrobatPdfFactAttribute"/> so they
    /// report as skipped (not vacuously green) when the file is absent.
    /// </summary>
    public static string RealAcrobatPdfPath { get; } =
        Environment.GetEnvironmentVariable("RAILREADER_ACROBAT_PDF")
        ?? "/home/stefan/Downloads/Day-ahead-photovoltaic-power-forecasting---Short.pdf";
}

/// <summary>
/// A [Fact] that is skipped (with a visible skip reason) when the real
/// Acrobat-annotated fixture PDF is not present on this machine.
/// </summary>
public sealed class RealAcrobatPdfFactAttribute : FactAttribute
{
    public RealAcrobatPdfFactAttribute()
    {
        if (!File.Exists(AnnotationTestHelpers.RealAcrobatPdfPath))
            Skip = "Real Acrobat-annotated fixture PDF not found " +
                   $"({AnnotationTestHelpers.RealAcrobatPdfPath}); set RAILREADER_ACROBAT_PDF to run.";
    }
}
