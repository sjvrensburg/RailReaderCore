using RailReader.Core.Services;
using RailReader.Core.Vlm.OpenAI;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Regression tests for <see cref="OpenAIVlmClient"/> error mapping. No network:
/// the cancellation test uses a pre-cancelled token, so the request never leaves
/// the client pipeline.
/// </summary>
public class OpenAIVlmClientTests
{
    [Fact]
    public async Task TestConnection_CallerCancelled_ReportsCancellationNotTimeout()
    {
        var client = new OpenAIVlmClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await client.TestConnectionAsync(
            new VlmEndpointConfig("http://127.0.0.1:9/v1", "test-model", null), cts.Token);

        // Caller-initiated cancellation (e.g. closing the settings dialog) used
        // to be reported as "Connection timed out" — a spurious endpoint error.
        Assert.Equal("Request cancelled", result);
    }

    [Fact]
    public async Task TestConnection_MissingEndpoint_PromptsForConfig()
    {
        var client = new OpenAIVlmClient();
        Assert.Equal("Enter an endpoint URL first",
            await client.TestConnectionAsync(new VlmEndpointConfig("", "m", null)));
        Assert.Equal("Enter a model name first",
            await client.TestConnectionAsync(new VlmEndpointConfig("http://x/v1", "", null)));
    }
}
