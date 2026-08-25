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
    /// Defaults to <see cref="AcceleratorPreference.Cpu"/> (unchanged behaviour).
    /// <c>MarkdownExportService</c> (<c>RailReader.Export</c>) already references
    /// <c>RailReader.Core.Analysis.WebGpu</c>, so no extra reference is needed to use
    /// <see cref="AcceleratorPreference.Gpu"/> here — it enables GPU opportunistically
    /// and falls back to CPU if no GPU device is available or the model fails to load
    /// on it. A different <c>IMarkdownExportService</c> implementation is free to wire
    /// GPU differently (or not at all) — this option only names the *preference*, not
    /// how it's honoured.
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
