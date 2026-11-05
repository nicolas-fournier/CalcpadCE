using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Models.Messages;

namespace Calcpad.AI
{
    /// <summary>
    /// <see cref="IAiClient"/> backed by the official Anthropic C# SDK, streaming the reply.
    /// </summary>
    public sealed class AnthropicAiClient : IAiClient
    {
        private readonly AiOptions _options;
        private readonly AnthropicClient _client;

        public AnthropicAiClient(AiOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (!_options.IsConfigured)
                throw new InvalidOperationException("No Anthropic API key configured.");

            _client = new AnthropicClient { ApiKey = _options.ApiKey };
        }

        public async IAsyncEnumerable<string> StreamReplyAsync(
            IReadOnlyList<ChatMessage> history,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var messages = new List<MessageParam>(history.Count);
            foreach (var m in history)
            {
                if (m.Role == AiRole.Assistant)
                {
                    messages.Add(new AssistantMessageParam { Content = m.Text });
                }
                else if (m.Attachments is { Count: > 0 })
                {
                    messages.Add(new UserMessageParam { Content = BuildMultimodalContent(m) });
                }
                else
                {
                    messages.Add(new UserMessageParam { Content = m.Text });
                }
            }

            var parameters = new MessageCreateParams
            {
                Model = _options.Model,
                MaxTokens = _options.MaxTokens,
                System = _options.SystemPrompt ?? CalcpadSystemPrompt.Default,
                Messages = messages
            };

            await foreach (var streamEvent in _client.Messages
                .CreateStreaming(parameters)
                .WithCancellation(cancellationToken))
            {
                if (streamEvent.TryPickContentBlockDelta(out var delta) &&
                    delta.Delta.TryPickText(out var text))
                {
                    yield return text.Text;
                }
            }
        }

        // Builds a user turn with image/PDF attachments followed by the text. Shared with the
        // agentic caller via AttachmentBlocks so both paths send multimodal content identically.
        internal static MessageContentParam BuildMultimodalContent(ChatMessage m)
        {
            var blocks = AttachmentBlocks(m.Attachments);
            if (!string.IsNullOrEmpty(m.Text))
                blocks.Add(new ContentBlockParam(new TextBlockParam(m.Text), null));

            return new MessageContentParam(blocks, null);
        }

        internal static List<ContentBlockParam> AttachmentBlocks(IReadOnlyList<ChatAttachment> attachments)
        {
            var blocks = new List<ContentBlockParam>((attachments?.Count ?? 0) + 1);
            if (attachments is null)
                return blocks;

            foreach (var a in attachments)
            {
                if (a.Kind == AttachmentKind.Image)
                {
                    var source = new ImageBlockParamSource(
                        new Base64ImageSource { Data = a.Base64Data, MediaType = a.MediaType }, null);
                    blocks.Add(new ContentBlockParam(new ImageBlockParam(source), null));
                }
                else // Pdf
                {
                    var source = new DocumentBlockParamSource(new Base64PdfSource(a.Base64Data), null);
                    blocks.Add(new ContentBlockParam(new DocumentBlockParam(source), null));
                }
            }
            return blocks;
        }
    }
}
