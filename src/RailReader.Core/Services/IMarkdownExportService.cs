using RailReader.Core.Models;

namespace RailReader.Core.Services;

public record ExportProgress(int CurrentPage, int TotalPages, string Status);

public record MarkdownExportOptions
{
    public bool EnableVlm { get; init; } = true;
    public bool IncludeAnnotations { get; init; } = true;
    public bool IncludeFigureImages { get; init; } = true;
    public bool InsertPageBreaks { get; init; } = true;
    public string? FigureOutputDir { get; init; }
    public string? PageRange { get; init; }
    public int VlmConcurrency { get; init; } = 2;
    public VlmEndpointConfig? VlmEndpoint { get; init; }
    public VlmService.PromptStyle VlmPromptStyle { get; init; }
    public bool VlmStructuredOutput { get; init; } = true;

    /// <summary>
    /// Which layout-model export to load — see <see cref="LayoutModelRegistry.Resolve"/>.
    /// Defaults to <see cref="AcceleratorPreference.Cpu"/> (unchanged behaviour). GPU
    /// inference additionally requires the caller's app to reference
    /// <c>RailReader.Core.Analysis.WebGpu</c> — <c>MarkdownExportService</c> enables
    /// it opportunistically when this is <see cref="AcceleratorPreference.Gpu"/> and
    /// falls back to CPU if no GPU device is available or the model fails to load on
    /// it.
    /// </summary>
    public AcceleratorPreference Accelerator { get; init; } = AcceleratorPreference.Cpu;
}

public interface IMarkdownExportService
{
    /// <summary>
    /// Exports the PDF at <paramref name="pdfPath"/> to Markdown. For an encrypted
    /// (password-protected) PDF — e.g. a paper distributed for moderation — pass the
    /// <paramref name="password"/>; an encrypted document opened without it throws
    /// <see cref="PdfPasswordRequiredException"/>.
    /// </summary>
    Task ExportAsync(
        string pdfPath,
        TextWriter output,
        MarkdownExportOptions options,
        string? password = null,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default);
}
