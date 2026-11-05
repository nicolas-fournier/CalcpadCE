using System.Runtime.CompilerServices;
using Calcpad.AI;

namespace Calcpad.Tests;

// Phase B1 — Calcpad.AI foundation. No network: the IAiClient contract is exercised
// through a deterministic fake, and the embedded system prompt is validated offline.
public class AiClientTests
{
    [Fact]
    public void SystemPrompt_Loads_And_TeachesMarkdownFirst()
    {
        var prompt = CalcpadSystemPrompt.Default;
        Assert.False(string.IsNullOrWhiteSpace(prompt));
        Assert.Contains("#md full", prompt);
        Assert.Contains("$", prompt); // math delimiter documented
    }

    [Fact]
    public void Options_DefaultModel_IsCurrentOpus()
    {
        var o = new AiOptions();
        Assert.Equal("claude-opus-5-5", o.Model);
        Assert.False(o.IsConfigured);
        o.ApiKey = "sk-test";
        Assert.True(o.IsConfigured);
    }

    [Fact]
    public void AnthropicClient_WithoutKey_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new AnthropicAiClient(new AiOptions()));
    }

    [Fact]
    public async Task FakeClient_StreamsReplyChunks()
    {
        IAiClient client = new EchoAiClient();
        var history = new List<ChatMessage> { ChatMessage.FromUser("beam") };

        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in client.StreamReplyAsync(history))
            sb.Append(chunk);

        Assert.Equal("echo:beam", sb.ToString());
    }

    // Minimal deterministic IAiClient used to validate the streaming consumption pattern
    // the UI relies on, without hitting the Anthropic API.
    private sealed class EchoAiClient : IAiClient
    {
        public async IAsyncEnumerable<string> StreamReplyAsync(
            IReadOnlyList<ChatMessage> history,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var last = history[^1].Text;
            foreach (var part in new[] { "echo", ":", last })
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return part;
            }
        }
    }
}
