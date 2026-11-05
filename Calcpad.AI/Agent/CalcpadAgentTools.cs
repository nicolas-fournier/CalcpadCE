using System.Text.Json;

namespace Calcpad.AI
{
    /// <summary>
    /// Builds the standard Calcpad assistant tools from host-provided callbacks. The host is
    /// responsible for thread-marshalling (e.g. the WPF app runs the callbacks on the UI thread).
    /// </summary>
    public static class CalcpadAgentTools
    {
        /// <param name="getWorksheet">Returns the current worksheet source.</param>
        /// <param name="insertText">Inserts text into the worksheet at the caret.</param>
        /// <param name="evaluate">Evaluates Calcpad source and returns a plain-text result (or errors).</param>
        /// <param name="getResult">Optional: returns the current computed output of the whole worksheet as plain text.</param>
        public static IReadOnlyList<AiTool> Create(
            Func<string> getWorksheet,
            Func<string, string> insertText,
            Func<string, string> evaluate,
            Func<string> getResult = null)
        {
            ArgumentNullException.ThrowIfNull(getWorksheet);
            ArgumentNullException.ThrowIfNull(insertText);
            ArgumentNullException.ThrowIfNull(evaluate);

            var noProps = new Dictionary<string, JsonElement>();
            var textProp = new Dictionary<string, JsonElement>
            {
                ["text"] = AiTool.Prop("string", "The Calcpad/markdown text to insert into the worksheet.")
            };
            var sourceProp = new Dictionary<string, JsonElement>
            {
                ["source"] = AiTool.Prop("string", "Calcpad source to evaluate (use $…$ math; a trailing '=' prints a value).")
            };

            var tools = new List<AiTool>
            {
                new("get_worksheet",
                    "Returns the full current worksheet source so you can read what the user already has.",
                    noProps, [],
                    (_, _) => Task.FromResult(getWorksheet() ?? string.Empty)),

                new("insert_calcpad",
                    "Proposes Calcpad/markdown text to insert into the worksheet at the caret (the user may need to approve it). Prefer #md full nomenclature.",
                    textProp, ["text"],
                    (input, _) =>
                    {
                        var text = AiTool.GetString(input, "text");
                        if (string.IsNullOrEmpty(text))
                            return Task.FromResult("Nothing to insert: 'text' was empty.");
                        return Task.FromResult(insertText(text) ?? "Inserted.");
                    }),

                new("evaluate_calcpad",
                    "Evaluates a small Calcpad snippet and returns the computed result as plain text. Use this to check a formula before inserting it.",
                    sourceProp, ["source"],
                    (input, _) =>
                    {
                        var source = AiTool.GetString(input, "source");
                        if (string.IsNullOrWhiteSpace(source))
                            return Task.FromResult("Nothing to evaluate: 'source' was empty.");
                        return Task.FromResult(evaluate(source) ?? string.Empty);
                    }),
            };

            if (getResult is not null)
                tools.Add(new AiTool("get_result",
                    "Returns the current computed output (results/values) of the whole worksheet as plain text.",
                    noProps, [],
                    (_, _) => Task.FromResult(getResult() ?? string.Empty)));

            return tools;
        }
    }
}
