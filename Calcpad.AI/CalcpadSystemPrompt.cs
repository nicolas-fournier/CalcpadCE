using System.Reflection;

namespace Calcpad.AI
{
    /// <summary>
    /// Builds the system prompt that teaches Claude the CalcpadCE markdown-first (<c>#md full</c>)
    /// nomenclature. The text lives in an embedded resource so it stays in sync with the language.
    /// </summary>
    public static class CalcpadSystemPrompt
    {
        private const string ResourceName = "Calcpad.AI.Resources.calcpad-md-system-prompt.md";
        private static string? _cached;

        /// <summary>The embedded CalcpadCE language reference used as the default system prompt.</summary>
        public static string Default => _cached ??= Load();

        private static string Load()
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(ResourceName);
            if (stream is null)
                return "You write CalcpadCE worksheets using markdown-first (#md full) syntax.";

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
