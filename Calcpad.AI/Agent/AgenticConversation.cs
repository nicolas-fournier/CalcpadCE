namespace Calcpad.AI
{
    /// <summary>
    /// Drives the agentic loop: call the model, run any tools it requests, feed the results back,
    /// and repeat until the model answers without calling tools (or a safety cap is hit). The loop
    /// is transport-agnostic — it talks to an <see cref="IModelCaller"/> and runs <see cref="AiTool"/>s,
    /// so it is fully unit-testable with fakes.
    /// </summary>
    public sealed class AgenticConversation
    {
        private readonly IModelCaller _caller;
        private readonly IReadOnlyList<AiTool> _tools;
        private readonly Dictionary<string, AiTool> _byName;

        public int MaxIterations { get; set; } = 8;

        public AgenticConversation(IModelCaller caller, IReadOnlyList<AiTool> tools)
        {
            _caller = caller ?? throw new ArgumentNullException(nameof(caller));
            _tools = tools ?? [];
            _byName = new(StringComparer.Ordinal);
            foreach (var t in _tools)
                _byName[t.Name] = t;
        }

        /// <summary>Raised when a tool is about to run (for UI/telemetry). Optional.</summary>
        public event Action<AgentToolCall>? ToolInvoking;
        /// <summary>Raised after a tool has run, with its textual result. Optional.</summary>
        public event Action<AgentToolCall, string>? ToolInvoked;
        /// <summary>Raised for each chunk of assistant text produced on any iteration. Optional.</summary>
        public event Action<string>? AssistantText;

        /// <summary>
        /// Runs the loop starting from <paramref name="history"/> (whose last turn is the user's
        /// message) and returns the final assistant text.
        /// </summary>
        public async Task<string> RunAsync(IReadOnlyList<AgentMessage> history, CancellationToken cancellationToken = default)
        {
            var messages = new List<AgentMessage>(history);
            var finalText = string.Empty;
            for (var iteration = 0; iteration < MaxIterations; ++iteration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reply = await _caller.CallAsync(messages, _tools, cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(reply.Text))
                {
                    finalText = reply.Text;
                    AssistantText?.Invoke(reply.Text);
                }

                if (reply.ToolCalls is null || reply.ToolCalls.Count == 0)
                    return finalText;

                messages.Add(AgentMessage.Assistant(reply.Text, reply.ToolCalls));

                var results = new List<AgentToolResult>(reply.ToolCalls.Count);
                foreach (var call in reply.ToolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ToolInvoking?.Invoke(call);
                    string output;
                    bool isError = false;
                    if (_byName.TryGetValue(call.Name, out var tool))
                    {
                        try
                        {
                            output = await tool.InvokeAsync(call.Input, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            output = "Tool error: " + ex.Message;
                            isError = true;
                        }
                    }
                    else
                    {
                        output = $"Unknown tool '{call.Name}'.";
                        isError = true;
                    }
                    ToolInvoked?.Invoke(call, output);
                    results.Add(new AgentToolResult(call.Id, output, isError));
                }
                messages.Add(AgentMessage.ToolResultsTurn(results));
            }
            return finalText;
        }
    }
}
