using System.Text.Json;

namespace Calcpad.AI
{
    /// <summary>
    /// A host-provided tool the assistant may call (agentic mode). The schema is expressed as
    /// JSON-Schema <c>properties</c> + <c>required</c>; the handler runs the tool and returns a
    /// plain-text result that is fed back to the model. Pure and transport-agnostic, so the
    /// agentic loop and these tools are unit-testable without any network.
    /// </summary>
    public sealed class AiTool
    {
        private readonly Func<IReadOnlyDictionary<string, JsonElement>, CancellationToken, Task<string>> _handler;

        public string Name { get; }
        public string Description { get; }
        public IReadOnlyDictionary<string, JsonElement> Properties { get; }
        public IReadOnlyList<string> Required { get; }

        public AiTool(
            string name,
            string description,
            IReadOnlyDictionary<string, JsonElement> properties,
            IReadOnlyList<string> required,
            Func<IReadOnlyDictionary<string, JsonElement>, CancellationToken, Task<string>> handler)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Description = description ?? string.Empty;
            Properties = properties ?? new Dictionary<string, JsonElement>();
            Required = required ?? [];
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public Task<string> InvokeAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct = default) =>
            _handler(input ?? new Dictionary<string, JsonElement>(), ct);

        /// <summary>Builds a single JSON-Schema property, e.g. <c>Prop("string", "The code to run")</c>.</summary>
        public static JsonElement Prop(string type, string description) =>
            JsonSerializer.SerializeToElement(new { type, description });

        /// <summary>Reads a string argument from a tool-call input, or <paramref name="fallback"/>.</summary>
        public static string GetString(IReadOnlyDictionary<string, JsonElement> input, string key, string fallback = "")
        {
            if (input is not null && input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? fallback;
            return fallback;
        }
    }
}
