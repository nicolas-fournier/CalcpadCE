using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace Calcpad.AI
{
    /// <summary>
    /// <see cref="IModelCaller"/> backed by the Anthropic C# SDK: one non-streaming round-trip that
    /// sends the conversation plus the tool definitions and parses the reply (text + tool calls).
    /// Isolated so the agentic loop (<see cref="AgenticConversation"/>) stays provider-agnostic.
    /// </summary>
    public sealed class AnthropicModelCaller : IModelCaller
    {
        private readonly AiOptions _options;
        private readonly AnthropicClient _client;

        public AnthropicModelCaller(AiOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (!_options.IsConfigured)
                throw new InvalidOperationException("No Anthropic API key configured.");

            _client = new AnthropicClient { ApiKey = _options.ApiKey };
        }

        public async Task<ModelReply> CallAsync(
            IReadOnlyList<AgentMessage> messages,
            IReadOnlyList<AiTool> tools,
            CancellationToken cancellationToken = default)
        {
            var parameters = new MessageCreateParams
            {
                Model = _options.Model,
                MaxTokens = _options.MaxTokens,
                System = _options.SystemPrompt ?? CalcpadSystemPrompt.Default,
                Messages = MapMessages(messages),
                Tools = MapTools(tools)
            };

            var message = await _client.Messages.Create(parameters, cancellationToken).ConfigureAwait(false);

            var text = new System.Text.StringBuilder();
            var calls = new List<AgentToolCall>();
            foreach (var block in message.Content)
            {
                if (block.TryPickText(out var textBlock))
                    text.Append(textBlock.Text);
                else if (block.TryPickToolUse(out var toolUse))
                    calls.Add(new AgentToolCall(toolUse.ID, toolUse.Name, toolUse.Input));
            }
            return new ModelReply(text.ToString(), calls);
        }

        private static List<MessageParam> MapMessages(IReadOnlyList<AgentMessage> messages)
        {
            var result = new List<MessageParam>(messages.Count);
            foreach (var m in messages)
            {
                if (m.Role == AiRole.Assistant)
                {
                    if (m.ToolCalls.Count == 0)
                    {
                        result.Add(new AssistantMessageParam { Content = m.Text });
                        continue;
                    }
                    var blocks = new List<ContentBlockParam>();
                    if (!string.IsNullOrEmpty(m.Text))
                        blocks.Add(new ContentBlockParam(new TextBlockParam { Text = m.Text }, null));
                    foreach (var call in m.ToolCalls)
                        blocks.Add(new ContentBlockParam(
                            new ToolUseBlockParam { ID = call.Id, Name = call.Name, Input = call.Input }, null));

                    result.Add(new AssistantMessageParam { Content = new MessageContentParam(blocks, null) });
                }
                else if (m.ToolResults.Count > 0)
                {
                    var blocks = new List<ContentBlockParam>(m.ToolResults.Count);
                    foreach (var r in m.ToolResults)
                        blocks.Add(new ContentBlockParam(
                            new ToolResultBlockParam(r.Id)
                            {
                                Content = new ToolResultBlockParamContent(r.Content, null),
                                IsError = r.IsError
                            }, null));

                    result.Add(new UserMessageParam { Content = new MessageContentParam(blocks, null) });
                }
                else
                {
                    result.Add(new UserMessageParam { Content = m.Text });
                }
            }
            return result;
        }

        private static List<ToolUnion> MapTools(IReadOnlyList<AiTool> tools)
        {
            var result = new List<ToolUnion>(tools.Count);
            foreach (var t in tools)
            {
                var tool = new Tool
                {
                    Name = t.Name,
                    Description = t.Description,
                    InputSchema = new InputSchema
                    {
                        Type = JsonSerializer.SerializeToElement("object"),
                        Properties = t.Properties,
                        Required = t.Required
                    }
                };
                result.Add(new ToolUnion(tool, null));
            }
            return result;
        }
    }
}
