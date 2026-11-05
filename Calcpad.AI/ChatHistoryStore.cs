using System.Text.Json;

namespace Calcpad.AI
{
    /// <summary>Persists the chat transcript so the assistant keeps context across app restarts.</summary>
    public interface IChatHistoryStore
    {
        IReadOnlyList<ChatMessage> Load();
        void Save(IReadOnlyList<ChatMessage> history);
        void Clear();
    }

    /// <summary>
    /// JSON-file transcript store. Only the role and text are persisted — attachments (images/PDFs)
    /// are transient and intentionally dropped to keep the file small.
    /// </summary>
    public sealed class JsonChatHistoryStore : IChatHistoryStore
    {
        private sealed record Entry(AiRole Role, string Text);

        private readonly string _path;
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

        public JsonChatHistoryStore(string path) =>
            _path = path ?? throw new ArgumentNullException(nameof(path));

        public IReadOnlyList<ChatMessage> Load()
        {
            try
            {
                if (!File.Exists(_path))
                    return [];

                var json = File.ReadAllText(_path);
                var entries = JsonSerializer.Deserialize<List<Entry>>(json, Options);
                if (entries is null)
                    return [];

                var result = new List<ChatMessage>(entries.Count);
                foreach (var e in entries)
                    result.Add(new ChatMessage(e.Role, e.Text ?? string.Empty));
                return result;
            }
            catch
            {
                return [];
            }
        }

        public void Save(IReadOnlyList<ChatMessage> history)
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var entries = new List<Entry>(history.Count);
                foreach (var m in history)
                    entries.Add(new Entry(m.Role, m.Text));

                File.WriteAllText(_path, JsonSerializer.Serialize(entries, Options));
            }
            catch
            {
                // Persistence is best-effort; never crash the UI over a failed save.
            }
        }

        public void Clear()
        {
            try
            {
                if (File.Exists(_path))
                    File.Delete(_path);
            }
            catch
            {
                // ignore
            }
        }
    }
}
