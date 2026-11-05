namespace Calcpad.AI
{
    /// <summary>Configuration for an <see cref="IAiClient"/>.</summary>
    public sealed class AiOptions
    {
        /// <summary>Anthropic API key. Supplied by the user; never hard-coded or logged.</summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>Model id. Defaults to the current flagship Opus.</summary>
        public string Model { get; set; } = "claude-opus-5-5";

        /// <summary>Upper bound on generated tokens. Streaming is used, so a large value is safe.</summary>
        public int MaxTokens { get; set; } = 16000;

        /// <summary>
        /// System prompt. When null, <see cref="CalcpadSystemPrompt.Default"/> (the embedded
        /// CalcpadCE markdown language reference) is used so the model emits valid <c>#md full</c> syntax.
        /// </summary>
        public string? SystemPrompt { get; set; }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
    }
}
