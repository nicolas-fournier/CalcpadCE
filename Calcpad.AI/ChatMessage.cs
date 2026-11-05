namespace Calcpad.AI
{
    /// <summary>A single turn in the assistant conversation, optionally with attachments.</summary>
    public sealed record ChatMessage(AiRole Role, string Text)
    {
        /// <summary>Pasted images / attached PDFs sent with this (user) turn. Empty for plain text.</summary>
        public IReadOnlyList<ChatAttachment> Attachments { get; init; } = [];

        public static ChatMessage FromUser(string text) => new(AiRole.User, text);
        public static ChatMessage FromAssistant(string text) => new(AiRole.Assistant, text);

        public static ChatMessage FromUser(string text, IReadOnlyList<ChatAttachment> attachments) =>
            new(AiRole.User, text) { Attachments = attachments ?? [] };
    }
}
