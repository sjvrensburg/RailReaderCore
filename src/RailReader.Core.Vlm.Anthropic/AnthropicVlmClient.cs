using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using RailReader.Core.Services;

namespace RailReader.Core.Vlm.Anthropic;

/// <summary>
/// <see cref="IVlmService"/> implementation that calls Claude through the Anthropic
/// Messages API, using the official Anthropic .NET SDK.
///
/// <see cref="VlmEndpointConfig.Endpoint"/> is an optional base URL (blank means
/// <c>https://api.anthropic.com</c>; set it for a gateway or proxy that speaks the
/// Messages API). <see cref="VlmEndpointConfig.ApiKey"/> may be blank, in which case the
/// SDK resolves credentials itself (<c>ANTHROPIC_API_KEY</c>, <c>ANTHROPIC_AUTH_TOKEN</c>,
/// or a profile from <c>ant auth login</c>). <see cref="VlmEndpointConfig.Model"/> is a
/// Claude model id such as <c>claude-opus-5-5</c>.
///
/// Stateless — safe to construct per call or hold as a singleton.
/// </summary>
public sealed class AnthropicVlmClient : IVlmService
{
    /// <summary>
    /// Output cap per block. Generous because on current models (thinking always on) thinking
    /// tokens count against it, and a too-small cap truncates the answer itself; only tokens
    /// actually generated are billed.
    /// </summary>
    internal const int MaxOutputTokens = 16000;

    public async Task<VlmService.VlmResult> DescribeBlockAsync(
        byte[] pngBytes,
        VlmService.BlockAction action,
        VlmEndpointConfig endpoint,
        VlmService.PromptStyle style = VlmService.PromptStyle.Instruction,
        bool structuredOutput = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Model))
            return new VlmService.VlmResult(null, "VLM model not configured — check Settings");

        try
        {
            var client = CreateClient(endpoint);
            var parameters = BuildDescribeParams(pngBytes, action, endpoint.Model!, style, structuredOutput);

            var response = await client.Messages.Create(parameters, ct);

            if (response.StopReason == "refusal")
                return new VlmService.VlmResult(null, "Claude declined to transcribe this block");

            var text = ExtractText(response);
            if (string.IsNullOrWhiteSpace(text))
            {
                return new VlmService.VlmResult(null, response.StopReason == "max_tokens"
                    ? "VLM response was cut off before any text (max_tokens)"
                    : "VLM returned empty response");
            }

            if (structuredOutput)
            {
                var (fieldName, _) = VlmService.GetSchema(action);
                var (parsed, parseError) = VlmService.ExtractSchemaField(text, fieldName);
                if (parsed != null) return new VlmService.VlmResult(parsed, null);
                // Parse failure: return raw text so the user can recover, but flag it.
                return new VlmService.VlmResult(text, $"Structured parse failed: {parseError}");
            }

            return new VlmService.VlmResult(text, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new VlmService.VlmResult(null, "Request cancelled");
        }
        catch (Exception ex)
        {
            return new VlmService.VlmResult(null, DescribeError(ex, forTest: false));
        }
    }

    public async Task<string?> TestConnectionAsync(VlmEndpointConfig endpoint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Model))
            return "Enter a model name first";

        try
        {
            var client = CreateClient(endpoint);
            // A tiny real request, not a Models API lookup: it exercises the same endpoint
            // DescribeBlockAsync uses, which a Messages-only gateway may be all that exists.
            await client.Messages.Create(new MessageCreateParams
            {
                Model = endpoint.Model!,
                MaxTokens = 64,
                Messages = [new UserMessageParam { Content = "Reply with OK" }],
            }, ct);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-initiated cancellation (e.g. the settings dialog closed) — not a timeout.
            return "Request cancelled";
        }
        catch (Exception ex)
        {
            return DescribeError(ex, forTest: true);
        }
    }

    /// <summary>The request for one block crop: the image, then the instruction.</summary>
    internal static MessageCreateParams BuildDescribeParams(byte[] pngBytes, VlmService.BlockAction action,
        string model, VlmService.PromptStyle style, bool structuredOutput)
    {
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = MaxOutputTokens,
            Messages =
            [
                new UserMessageParam
                {
                    Content = new List<ContentBlockParam>
                    {
                        new ImageBlockParam
                        {
                            Source = new Base64ImageSource
                            {
                                Data = Convert.ToBase64String(pngBytes),
                                MediaType = MediaType.ImagePng,
                            },
                        },
                        new TextBlockParam { Text = VlmService.GetPrompt(action, style, structuredOutput) },
                    },
                },
            ],
        };

        if (structuredOutput)
        {
            var (_, schema) = VlmService.GetSchema(action);
            parameters = parameters with
            {
                OutputConfig = new OutputConfig
                {
                    Format = new JsonOutputFormat { Schema = ParseSchema(schema) },
                },
            };
        }

        return parameters;
    }

    /// <summary>A JSON-schema string as the SDK's property map.</summary>
    internal static Dictionary<string, JsonElement> ParseSchema(string schema)
    {
        using var doc = JsonDocument.Parse(schema);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    /// <summary>All text blocks of the reply, joined; thinking blocks are skipped.</summary>
    private static string? ExtractText(Message response)
    {
        var sb = new StringBuilder();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? text))
                sb.Append(text.Text);
        }
        return sb.ToString().Trim();
    }

    private static string DescribeError(Exception ex, bool forTest) => ex switch
    {
        // Also what a missing key looks like: with none configured the SDK still sends the request.
        AnthropicUnauthorizedException => "Invalid or missing API key",
        AnthropicForbiddenException => "API key lacks permission for this model",
        AnthropicNotFoundException => "Model not found — check the model name",
        AnthropicRateLimitException => "Rate limited — try again shortly",
        AnthropicApiException api => $"API error: {api.Message}",
        // The SDK's own message is a bare "I/O exception"; the cause is on the inner exception.
        AnthropicIOException io => (forTest ? "Cannot reach endpoint: " : "Cannot reach VLM endpoint: ")
            + (io.InnerException?.Message ?? io.Message),
        HttpRequestException http => (forTest ? "Cannot reach endpoint: " : "Cannot reach VLM endpoint: ") + http.Message,
        TaskCanceledException or OperationCanceledException => "Connection timed out",
        _ => (forTest ? "Error: " : "VLM error: ") + ex.Message,
    };

    private static AnthropicClient CreateClient(VlmEndpointConfig endpoint)
    {
        // Only override what the config supplies: leaving ApiKey / BaseUrl unset keeps the
        // SDK's own resolution (environment, `ant auth login` profile, default URL).
        bool hasKey = !string.IsNullOrWhiteSpace(endpoint.ApiKey);
        bool hasUrl = !string.IsNullOrWhiteSpace(endpoint.Endpoint);
        return (hasKey, hasUrl) switch
        {
            (true, true) => new AnthropicClient { ApiKey = endpoint.ApiKey!, BaseUrl = endpoint.Endpoint! },
            (true, false) => new AnthropicClient { ApiKey = endpoint.ApiKey! },
            (false, true) => new AnthropicClient { BaseUrl = endpoint.Endpoint! },
            _ => new AnthropicClient(),
        };
    }
}
