using System.Text.Json;

namespace Calcpad.AI
{
    /// <summary>A tool invocation requested by the model.</summary>
    public sealed record AgentToolCall(string Id, string Name, IReadOnlyDictionary<string, JsonElement> Input);

    /// <summary>The result of running a tool, to be fed back to the model.</summary>
    public sealed record AgentToolResult(string Id, string Content, bool IsError = false);

    /// <summary>
    /// One turn in an agentic conversation. Most turns are plain <see cref="Text"/>; an assistant
    /// turn that calls tools also carries <see cref="ToolCalls"/>; the following user turn carries
    /// the matching <see cref="ToolResults"/>. Provider-neutral so the loop is testable.
    /// </summary>
    public sealed record AgentMessage(
        AiRole Role,
        string Text,
        IReadOnlyList<AgentToolCall> ToolCalls,
        IReadOnlyList<AgentToolResult> ToolResults)
    {
        public static AgentMessage User(string text) => new(AiRole.User, text, [], []);
        public static AgentMessage Assistant(string text, IReadOnlyList<AgentToolCall> calls) =>
            new(AiRole.Assistant, text, calls ?? [], []);
        public static AgentMessage ToolResultsTurn(IReadOnlyList<AgentToolResult> results) =>
            new(AiRole.User, string.Empty, [], results ?? []);
    }

    /// <summary>One model round-trip: any assistant text plus any tool calls it requested.</summary>
    public sealed record ModelReply(string Text, IReadOnlyList<AgentToolCall> ToolCalls);

    /// <summary>
    /// A single model round-trip: given the conversation and the available tools, return the
    /// model's reply. Implemented by <see cref="AnthropicModelCaller"/> in production and by fakes
    /// in tests, so <see cref="AgenticConversation"/> can be exercised without the network.
    /// </summary>
    public interface IModelCaller
    {
        Task<ModelReply> CallAsync(
            IReadOnlyList<AgentMessage> messages,
            IReadOnlyList<AiTool> tools,
            CancellationToken cancellationToken = default);
    }
}
