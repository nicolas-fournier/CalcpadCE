namespace Calcpad.AI
{
    /// <summary>
    /// Abstraction over the chat backend. Implemented by <see cref="AnthropicAiClient"/> in
    /// production and by fakes in tests, so the UI and unit tests never touch the network.
    /// </summary>
    public interface IAiClient
    {
        /// <summary>
        /// Streams the assistant reply to <paramref name="history"/> token-by-token.
        /// The last entry in <paramref name="history"/> is expected to be the user turn.
        /// </summary>
        IAsyncEnumerable<string> StreamReplyAsync(
            IReadOnlyList<ChatMessage> history,
            CancellationToken cancellationToken = default);
    }
}
