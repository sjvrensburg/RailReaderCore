namespace RailReader.Core.Services;

/// <summary>
/// Sends a rendered block crop to a vision-language model and returns the
/// transcription (LaTeX for equations, Markdown for tables, prose for figures).
///
/// Implementations live in provider-specific sibling packages — e.g.
/// <c>RailReader.Core.Vlm.OpenAI</c> for OpenAI-compatible endpoints. Core
/// never instantiates a backend itself; the platform supplies one (or none)
/// and consumers may swap providers without touching Core.
/// </summary>
public interface IVlmService
{
    /// <summary>
    /// Describe a block crop. Returns the transcribed text in
    /// <see cref="VlmService.VlmResult.Text"/>, or a user-facing diagnostic in
    /// <see cref="VlmService.VlmResult.Error"/> if the call failed. Never throws
    /// for expected failure modes (network, auth, malformed response).
    /// </summary>
    Task<VlmService.VlmResult> DescribeBlockAsync(
        byte[] pngBytes,
        VlmService.BlockAction action,
        VlmEndpointConfig endpoint,
        VlmService.PromptStyle style = VlmService.PromptStyle.Instruction,
        bool structuredOutput = false,
        CancellationToken ct = default);

    /// <summary>
    /// Smoke-test the endpoint with a tiny request. Returns null on success or
    /// a human-readable error message.
    /// </summary>
    Task<string?> TestConnectionAsync(VlmEndpointConfig endpoint, CancellationToken ct = default);
}
