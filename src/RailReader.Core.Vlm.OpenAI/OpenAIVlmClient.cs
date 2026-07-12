using System.ClientModel;
using OpenAI;
using OpenAI.Chat;
using RailReader.Core.Services;

namespace RailReader.Core.Vlm.OpenAI;

/// <summary>
/// <see cref="IVlmService"/> implementation that calls an OpenAI-compatible
/// chat-completions endpoint. Works against OpenAI proper, Ollama, vLLM,
/// LightOnOCR, and other servers that speak the same protocol.
///
/// Stateless — safe to construct per call or hold as a singleton. All endpoint
/// configuration is passed per request via <see cref="VlmEndpointConfig"/>.
/// </summary>
public sealed class OpenAIVlmClient : IVlmService
{
    public async Task<VlmService.VlmResult> DescribeBlockAsync(
        byte[] pngBytes,
        VlmService.BlockAction action,
        VlmEndpointConfig endpoint,
        VlmService.PromptStyle style = VlmService.PromptStyle.Instruction,
        bool structuredOutput = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Endpoint))
            return new VlmService.VlmResult(null, "VLM not configured — check Settings");

        if (string.IsNullOrWhiteSpace(endpoint.Model))
            return new VlmService.VlmResult(null, "VLM model not configured — check Settings");

        try
        {
            var client = CreateClient(endpoint);

            var imageContent = ChatMessageContentPart.CreateImagePart(
                BinaryData.FromBytes(pngBytes), "image/png");
            var textContent = ChatMessageContentPart.CreateTextPart(
                VlmService.GetPrompt(action, style, structuredOutput));

            var messages = new List<ChatMessage>
            {
                new UserChatMessage(imageContent, textContent),
            };

            var chatOptions = new ChatCompletionOptions { MaxOutputTokenCount = 1024 };
            if (structuredOutput)
            {
                var (_, schema) = VlmService.GetSchema(action);
                chatOptions.ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: $"railreader_{action.ToString().ToLowerInvariant()}",
                    jsonSchema: BinaryData.FromString(schema),
                    jsonSchemaIsStrict: true);
            }

            var completion = await client.CompleteChatAsync(messages, chatOptions, ct);
            var text = completion.Value.Content[0].Text?.Trim();

            if (string.IsNullOrWhiteSpace(text))
                return new VlmService.VlmResult(null, "VLM returned empty response");

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
        catch (OperationCanceledException)
        {
            return new VlmService.VlmResult(null, "Request cancelled");
        }
        catch (ClientResultException ex) when (ex.Status == 401)
        {
            return new VlmService.VlmResult(null, "Invalid API key");
        }
        catch (ClientResultException ex)
        {
            return new VlmService.VlmResult(null, $"API error ({ex.Status}): {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return new VlmService.VlmResult(null, $"Cannot reach VLM endpoint: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new VlmService.VlmResult(null, $"VLM error: {ex.Message}");
        }
    }

    public async Task<string?> TestConnectionAsync(VlmEndpointConfig endpoint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Endpoint))
            return "Enter an endpoint URL first";

        if (string.IsNullOrWhiteSpace(endpoint.Model))
            return "Enter a model name first";

        try
        {
            var client = CreateClient(endpoint);

            var messages = new List<ChatMessage>
            {
                new UserChatMessage("Reply with OK"),
            };

            var chatOptions = new ChatCompletionOptions { MaxOutputTokenCount = 8 };
            await client.CompleteChatAsync(messages, chatOptions, ct);
            return null;
        }
        catch (ClientResultException ex) when (ex.Status == 401)
        {
            return "Invalid API key";
        }
        catch (ClientResultException ex)
        {
            return $"API error ({ex.Status}): {ex.Message}";
        }
        catch (HttpRequestException ex)
        {
            return $"Cannot reach endpoint: {ex.Message}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-initiated cancellation (e.g. the settings dialog closed) —
            // not a timeout; don't report a spurious endpoint failure.
            return "Request cancelled";
        }
        catch (TaskCanceledException)
        {
            return "Connection timed out";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static ChatClient CreateClient(VlmEndpointConfig endpoint)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint.Endpoint!),
        };
        // Local endpoints like Ollama don't require an API key
        var credential = new ApiKeyCredential(
            string.IsNullOrWhiteSpace(endpoint.ApiKey) ? "not-required" : endpoint.ApiKey);
        return new ChatClient(endpoint.Model!, credential, options);
    }
}
