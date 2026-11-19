using System.Text.Json;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Core.Vlm.Anthropic;
using RailReader.Core.Vlm.OpenAI;
using RailReader.Export;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// <see cref="AnthropicVlmClient"/> request shape and error mapping, and the provider plumbing that
/// picks it. No network: requests are inspected before sending, and the one call that reaches the
/// client pipeline uses a pre-cancelled token.
/// </summary>
public class AnthropicVlmClientTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    [Fact]
    public void Describe_request_sends_the_image_then_the_prompt()
    {
        var p = AnthropicVlmClient.BuildDescribeParams(Png, VlmService.BlockAction.LaTeX, "claude-opus-5-5",
            VlmService.PromptStyle.Instruction, structuredOutput: false);

        using var json = JsonDocument.Parse(p.ToString());
        var root = json.RootElement.GetProperty("BodyData");
        Assert.Equal("claude-opus-5-5", root.GetProperty("model").GetString());
        Assert.Equal(AnthropicVlmClient.MaxOutputTokens, root.GetProperty("max_tokens").GetInt32());

        var content = root.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("image", content[0].GetProperty("type").GetString());
        var source = content[0].GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/png", source.GetProperty("media_type").GetString());
        Assert.Equal(Convert.ToBase64String(Png), source.GetProperty("data").GetString());

        Assert.Equal("text", content[1].GetProperty("type").GetString());
        Assert.Equal(VlmService.GetPrompt(VlmService.BlockAction.LaTeX, VlmService.PromptStyle.Instruction, false),
            content[1].GetProperty("text").GetString());
        Assert.False(root.TryGetProperty("output_config", out _));
    }

    [Fact]
    public void Structured_request_constrains_output_to_the_action_schema()
    {
        var p = AnthropicVlmClient.BuildDescribeParams(Png, VlmService.BlockAction.Markdown, "claude-opus-5-5",
            VlmService.PromptStyle.Instruction, structuredOutput: true);

        using var json = JsonDocument.Parse(p.ToString());
        var format = json.RootElement.GetProperty("BodyData").GetProperty("output_config").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var schema = format.GetProperty("schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(schema.GetProperty("properties").TryGetProperty("markdown", out _));
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    [Theory]
    [InlineData("claude-opus-5-5", null, null, true)]
    [InlineData("claude-sonnet-5-5", "", null, true)]
    [InlineData("claude-fable-5-2", null, null, true)]
    [InlineData("claude-haiku-5-5", null, null, false)]          // no server-side fallback
    [InlineData("claude-opus-4-8", null, null, false)]           // older model: parameter not accepted
    [InlineData("claude-opus-5-5", "https://gateway.example", null, false)]  // configured gateway
    [InlineData("claude-opus-5-5", null, "https://gateway.example", false)]  // ANTHROPIC_BASE_URL gateway
    public void Server_fallback_only_for_supporting_models_on_anthropics_api(
        string model, string? endpoint, string? envBaseUrl, bool expected)
    {
        var config = new VlmEndpointConfig(endpoint, model, null) { Provider = VlmProvider.Anthropic };
        Assert.Equal(expected, AnthropicVlmClient.UseServerFallback(config, envBaseUrl));
    }

    [Fact]
    public void Fallback_request_opts_into_default_routing_with_the_same_content()
    {
        var p = AnthropicVlmClient.BuildFallbackDescribeParams(Png, VlmService.BlockAction.Description,
            "claude-opus-5-5", VlmService.PromptStyle.Instruction, structuredOutput: true);

        using var json = JsonDocument.Parse(p.ToString());
        var body = json.RootElement.GetProperty("BodyData");
        Assert.Equal("default", body.GetProperty("fallbacks").GetString());
        Assert.Contains(AnthropicVlmClient.ServerFallbackBeta, p.ToString());

        var content = body.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("image", content[0].GetProperty("type").GetString());
        Assert.Equal("image/png", content[0].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal("text", content[1].GetProperty("type").GetString());
        var schema = body.GetProperty("output_config").GetProperty("format").GetProperty("schema");
        Assert.True(schema.GetProperty("properties").TryGetProperty("description", out _));
    }

    [Fact]
    public async Task Missing_model_prompts_for_config()
    {
        var client = new AnthropicVlmClient();
        var config = new VlmEndpointConfig(null, "", "key") { Provider = VlmProvider.Anthropic };
        Assert.Equal("Enter a model name first", await client.TestConnectionAsync(config));
        var result = await client.DescribeBlockAsync(Png, VlmService.BlockAction.LaTeX, config);
        Assert.Null(result.Text);
        Assert.Contains("model not configured", result.Error);
    }

    [Fact]
    public async Task Caller_cancellation_is_reported_as_cancellation()
    {
        var client = new AnthropicVlmClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var config = new VlmEndpointConfig("http://127.0.0.1:9", "claude-opus-5-5", "test-key")
        {
            Provider = VlmProvider.Anthropic,
        };

        Assert.Equal("Request cancelled", await client.TestConnectionAsync(config, cts.Token));
        var result = await client.DescribeBlockAsync(Png, VlmService.BlockAction.LaTeX, config, ct: cts.Token);
        Assert.Equal("Request cancelled", result.Error);
    }

    [Fact]
    public void Endpoint_is_optional_for_anthropic_only()
    {
        Assert.True(new VlmEndpointConfig(null, "claude-opus-5-5", null) { Provider = VlmProvider.Anthropic }.IsConfigured);
        Assert.False(new VlmEndpointConfig(null, "gpt-x", null).IsConfigured);
        Assert.True(new VlmEndpointConfig("http://localhost:11434/v1", "qwen", null).IsConfigured);
        Assert.False(new VlmEndpointConfig("http://x", " ", null) { Provider = VlmProvider.Anthropic }.IsConfigured);
    }

    [Fact]
    public void Provider_flows_from_config_file_to_endpoint()
    {
        var config = new AppConfig { VlmModel = "claude-opus-5-5", VlmProvider = VlmProvider.Anthropic };
        var json = JsonSerializer.Serialize(config, RailReaderJsonContext.Default.AppConfig);
        Assert.Contains("\"vlm_provider\": \"anthropic\"", json);

        var reloaded = JsonSerializer.Deserialize(json, RailReaderJsonContext.Default.AppConfig)!;
        var endpoint = VlmEndpointConfig.FromCoreSettings(reloaded.ToCoreSettings());
        Assert.Equal(VlmProvider.Anthropic, endpoint.Provider);

        // A config written before the provider existed keeps the OpenAI-compatible client.
        var legacy = JsonSerializer.Deserialize("""{"vlm_endpoint":"http://x/v1"}""", RailReaderJsonContext.Default.AppConfig)!;
        Assert.Equal(VlmProvider.OpenAICompatible, legacy.VlmProvider);
    }

    [Theory]
    [InlineData("\"gemini\"")]
    [InlineData("\"\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{\"nested\": true}")]
    public void Unknown_provider_reads_as_openai_compatible_without_losing_the_config(string value)
    {
        var json = $$"""{"vlm_provider": {{value}}, "vlm_model": "kept", "rail_zoom_threshold": 4.5}""";
        var config = JsonSerializer.Deserialize(json, RailReaderJsonContext.Default.AppConfig)!;
        Assert.Equal(VlmProvider.OpenAICompatible, config.VlmProvider);
        Assert.Equal("kept", config.VlmModel);
        Assert.Equal(4.5, config.RailZoomThreshold);
    }

    [Theory]
    [InlineData("\"Anthropic\"")]
    [InlineData("1")]
    public void Provider_reads_case_insensitively_and_by_number(string value)
    {
        var config = JsonSerializer.Deserialize($$"""{"vlm_provider": {{value}}}""", RailReaderJsonContext.Default.AppConfig)!;
        Assert.Equal(VlmProvider.Anthropic, config.VlmProvider);
    }

    [Fact]
    public void Export_picks_the_client_for_the_provider()
    {
        Assert.IsType<AnthropicVlmClient>(MarkdownExportService.CreateVlmClient(VlmProvider.Anthropic));
        Assert.IsType<OpenAIVlmClient>(MarkdownExportService.CreateVlmClient(VlmProvider.OpenAICompatible));
    }
}
