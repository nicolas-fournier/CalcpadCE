using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Calcpad.AI;

namespace Calcpad.Wpf.Ai
{
    /// <summary>
    /// Stores the Anthropic API key encrypted with Windows DPAPI (CurrentUser scope),
    /// under the per-user application data folder. The key never leaves the machine
    /// except in requests the user explicitly sends to Anthropic.
    /// </summary>
    internal sealed class DpapiSecretStore : ISecretStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CalcpadCE.AI.v1");
        private readonly string _path;

        public DpapiSecretStore()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CalcpadCE");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "anthropic.key");
        }

        public bool HasKey => File.Exists(_path) && new FileInfo(_path).Length > 0;

        public string? Load()
        {
            try
            {
                if (!File.Exists(_path))
                    return null;

                var cipher = File.ReadAllBytes(_path);
                if (cipher.Length == 0)
                    return null;

                var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                return null;
            }
        }

        public void Save(string? apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                if (File.Exists(_path))
                    File.Delete(_path);
                return;
            }

            var plain = Encoding.UTF8.GetBytes(apiKey);
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_path, cipher);
        }
    }
}
