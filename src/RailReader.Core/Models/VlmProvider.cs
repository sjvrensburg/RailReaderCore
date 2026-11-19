using System.Text.Json.Serialization;
using RailReader.Core;

namespace RailReader.Core.Models;

/// <summary>
/// Which wire protocol a VLM endpoint speaks. Picks the <see cref="Services.IVlmService"/>
/// implementation a host wires in; Core itself never calls a provider.
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter<VlmProvider>))]
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
