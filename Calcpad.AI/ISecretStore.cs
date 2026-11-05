namespace Calcpad.AI
{
    /// <summary>
    /// Persists the user's Anthropic API key. Implementations encrypt at rest
    /// (e.g. Windows DPAPI, per current user). The key is never logged.
    /// </summary>
    public interface ISecretStore
    {
        /// <summary>Returns the stored API key, or null/empty if none is saved.</summary>
        string? Load();

        /// <summary>Saves the API key (encrypted at rest). A null/empty value clears it.</summary>
        void Save(string? apiKey);

        /// <summary>True when a non-empty key is stored.</summary>
        bool HasKey { get; }
    }
}
