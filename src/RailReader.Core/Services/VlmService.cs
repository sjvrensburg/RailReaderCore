using System.Text.Json;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>Minimal config needed to call a VLM endpoint.</summary>
public record VlmEndpointConfig(string? Endpoint, string? Model, string? ApiKey)
{
    public static VlmEndpointConfig FromCoreSettings(CoreSettings settings) =>
        new(settings.VlmEndpoint, settings.VlmModel, settings.VlmApiKey);
}

/// <summary>
/// Portable VLM helpers: prompt assembly, structured-output schema, layout-class
/// routing, and JSON response extraction. Pure logic — no network or SDK
/// dependencies. The actual chat-completion call lives in a sibling package
/// implementing <see cref="IVlmService"/> (e.g. <c>RailReader.Core.Vlm.OpenAI</c>).
/// </summary>
public static class VlmService
{
    public enum BlockAction { LaTeX, Markdown, Description }

    /// <summary>
    /// Prompt phrasing style. Instruction-tuned VLMs (Qwen, GPT-4, etc.) follow
    /// explicit "convert to X" directives. OCR-specialised models (LightOnOCR)
    /// respond better to short "transcribe" phrasing and tend to emit HTML for
    /// tables regardless of prompt.
    /// </summary>
    public enum PromptStyle { Instruction, Ocr }

    public record VlmResult(string? Text, string? Error);

    /// <summary>
    /// Returns the prompt text for an action under the given style. When
    /// <paramref name="structured"/> is true, the prompt restates the expected
    /// JSON shape (OpenAI's structured-output mode still benefits from the
    /// directive being in-prompt).
    /// </summary>
    public static string GetPrompt(BlockAction action, PromptStyle style, bool structured)
    {
        if (structured)
        {
            return action switch
            {
                BlockAction.Markdown =>
                    "Transcribe this table as a Markdown pipe table. Respond as JSON with a single `markdown` field.",
                BlockAction.Description =>
                    "Describe this figure in one concise sentence. Respond as JSON with a single `description` field.",
                _ =>
                    "Transcribe this equation as LaTeX (no delimiters, no $$). Respond as JSON with a single `latex` field.",
            };
        }

        return (action, style) switch
        {
            (BlockAction.Markdown, PromptStyle.Ocr) =>
                "Transcribe this table.",
            (BlockAction.Description, PromptStyle.Ocr) =>
                "Transcribe the contents of this figure.",
            (BlockAction.LaTeX, PromptStyle.Ocr) =>
                "Transcribe this equation as LaTeX.",
            (BlockAction.Markdown, _) =>
                "Convert this table to Markdown format. Return only the Markdown table, no explanation.",
            (BlockAction.Description, _) =>
                "Describe this figure briefly in one sentence.",
            _ =>
                "Convert this to LaTeX. Return only the LaTeX code, no explanation, no surrounding delimiters.",
        };
    }

    /// <summary>
    /// Per-action structured-output JSON schema. The field name is the single
    /// expected property in the response object; the schema string is a strict
    /// JSON Schema that callers hand to the LLM provider.
    /// </summary>
    public static (string FieldName, string Schema) GetSchema(BlockAction action) => action switch
    {
        BlockAction.LaTeX => ("latex",
            """{"type":"object","properties":{"latex":{"type":"string"}},"required":["latex"],"additionalProperties":false}"""),
        BlockAction.Markdown => ("markdown",
            """{"type":"object","properties":{"markdown":{"type":"string"}},"required":["markdown"],"additionalProperties":false}"""),
        BlockAction.Description => ("description",
            """{"type":"object","properties":{"description":{"type":"string"}},"required":["description"],"additionalProperties":false}"""),
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>
    /// Returns the VLM action for a layout block role, or null if the role
    /// is not a VLM-eligible type (equation/algorithm, table, or figure/chart).
    /// </summary>
    public static BlockAction? GetBlockAction(BlockRole role) => role switch
    {
        BlockRole.DisplayMath or BlockRole.InlineMath or BlockRole.Algorithm => BlockAction.LaTeX,
        BlockRole.Table => BlockAction.Markdown,
        BlockRole.Figure or BlockRole.Chart => BlockAction.Description,
        _ => null,
    };

    /// <summary>
    /// Extracts a string field from a structured-output JSON response. Returns
    /// the parsed value or an error message describing what went wrong.
    /// </summary>
    public static (string? Value, string? Error) ExtractSchemaField(string json, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, "response was not a JSON object");
            if (!doc.RootElement.TryGetProperty(field, out var valueElem))
                return (null, $"missing `{field}` field");
            if (valueElem.ValueKind != JsonValueKind.String)
                return (null, $"`{field}` was not a string");
            return (valueElem.GetString(), null);
        }
        catch (JsonException ex)
        {
            return (null, ex.Message);
        }
    }
}
