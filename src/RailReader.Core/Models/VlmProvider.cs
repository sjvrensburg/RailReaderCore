using System.Text.Json;
using System.Text.Json.Serialization;

namespace RailReader.Core.Models;

/// <summary>
/// Which wire protocol a VLM endpoint speaks. Picks the <see cref="Services.IVlmService"/>
/// implementation a host wires in; Core itself never calls a provider.
/// </summary>
[JsonConverter(typeof(VlmProviderJsonConverter))]
public enum VlmProvider
{
    /// <summary>
    /// OpenAI chat-completions (OpenAI, Ollama, vLLM, LightOnOCR, …) —
    /// <c>RailReader.Core.Vlm.OpenAI</c>. The default, so existing configs keep their behaviour.
    /// </summary>
    OpenAICompatible,

    /// <summary>
    /// Anthropic Messages API (Claude) — <c>RailReader.Core.Vlm.Anthropic</c>. The endpoint is
    /// optional (blank means <c>https://api.anthropic.com</c>), and a blank API key falls back
    /// to the <c>ANTHROPIC_API_KEY</c> environment variable.
    /// </summary>
    Anthropic,
}

/// <summary>
/// Reads and writes <see cref="VlmProvider"/> as a camelCase string, like the other config enums —
/// but an unknown value reads as <see cref="VlmProvider.OpenAICompatible"/> instead of throwing.
/// More providers are likely, and a config written by a newer app naming one this build lacks
/// must not fail the whole config load (which falls back to defaults, and the next save would
/// overwrite every setting).
/// </summary>
internal sealed class VlmProviderJsonConverter : JsonConverter<VlmProvider>
{
    public override VlmProvider Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && Enum.TryParse(reader.GetString(), ignoreCase: true, out VlmProvider named)
            && Enum.IsDefined(named))
            return named;
        if (reader.TokenType == JsonTokenType.Number
            && reader.TryGetInt32(out int number) && Enum.IsDefined((VlmProvider)number))
            return (VlmProvider)number;
        reader.Skip();
        return VlmProvider.OpenAICompatible;
    }

    public override void Write(Utf8JsonWriter writer, VlmProvider value, JsonSerializerOptions options) =>
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}
