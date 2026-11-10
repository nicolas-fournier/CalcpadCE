using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Calcpad.Avalonia
{
    // A self-contained Calcpad package (.cpdz = a zip):
    //   manifest.json   { version, entry }
    //   worksheet.cpd   the main worksheet, references rewritten to internal paths
    //   config.json     the editor options (optional)
    //   Images/…        every referenced image (src="…")
    //   lib/…           every #include'd library (recursively), its own refs rewritten too
    // References inside the package are relative, so the extracted folder is portable and the same
    // Save() path is reused to UPDATE an existing package (it re-collects newly added files).
    internal static class CpdPackage
    {
        public const string Extension = ".cpdz";

        private static readonly Regex IncludeRx =
            new(@"^(\s*#include\s+)(.+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SrcRx =
            new("src\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Writes a self-contained .cpdz at <paramref name="destZip"/> from the worksheet text and
        /// its folder. Returns the rewritten main text (internal paths). When <paramref name="syncDir"/>
        /// is given, the collected images/libraries are also mirrored into it (used on update so the
        /// working copy stays in sync with the package).
        /// </summary>
        public static string Save(string worksheetText, string mainDir, string? configJson, string destZip,
            string? syncDir = null, string? templateLocalPath = null, string? templateEntryName = null)
        {
            var libNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // full path -> lib entry name
            var imgNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // full path -> image entry name
            var usedLib = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usedImg = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var libContents = new Dictionary<string, string>(StringComparer.Ordinal); // lib entry name -> processed text

            string? Resolve(string refPath, string baseDir)
            {
                refPath = refPath.Trim().Trim('"');
                if (refPath.Length == 0 || refPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    || refPath.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
                try { return Path.IsPathRooted(refPath) ? Path.GetFullPath(refPath) : Path.GetFullPath(Path.Combine(baseDir, refPath)); }
                catch { return null; }
            }
            string Unique(string fullPath, HashSet<string> used)
            {
                var baseName = Path.GetFileName(fullPath);
                var n = baseName; var i = 1;
                while (used.Contains(n))
                    n = Path.GetFileNameWithoutExtension(baseName) + "_" + (++i) + Path.GetExtension(baseName);
                used.Add(n);
                return n;
            }
            string AddImage(string full)
            {
                if (imgNames.TryGetValue(full, out var n)) return n;
                n = Unique(full, usedImg); imgNames[full] = n; return n;
            }
            string AddLib(string full)
            {
                if (libNames.TryGetValue(full, out var n)) return n;
                n = Unique(full, usedLib); libNames[full] = n;
                libContents[n] = Process(ReadStripped(full), Path.GetDirectoryName(full) ?? mainDir, isMain: false);
                return n;
            }
            string Process(string text, string baseDir, bool isMain)
            {
                var sb = new StringBuilder();
                foreach (var raw in text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
                {
                    var m = IncludeRx.Match(raw);
                    if (m.Success)
                    {
                        var full = Resolve(m.Groups[2].Value, baseDir);
                        if (full is not null && File.Exists(full))
                        {
                            var name = AddLib(full);
                            sb.Append(m.Groups[1].Value).Append(isMain ? "lib/" + name : name).Append('\n');
                            continue;
                        }
                    }
                    sb.Append(raw).Append('\n');
                }
                return SrcRx.Replace(sb.ToString(), mm =>
                {
                    var full = Resolve(mm.Groups[1].Value, baseDir);
                    return full is not null && File.Exists(full) ? $"src=\"Images/{AddImage(full)}\"" : mm.Value;
                });
            }

            var mainOut = Process(worksheetText, mainDir, isMain: true);

            // Build the archive atomically (write to a temp file, then move over the destination).
            var tmp = destZip + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            using (var archive = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                WriteText(archive, "worksheet.cpd", mainOut);
                foreach (var kv in libContents) WriteText(archive, "lib/" + kv.Key, kv.Value);
                foreach (var kv in imgNames) archive.CreateEntryFromFile(kv.Key, "Images/" + kv.Value);
                if (templateLocalPath is not null && templateEntryName is not null && File.Exists(templateLocalPath))
                    archive.CreateEntryFromFile(templateLocalPath, "template/" + templateEntryName);
                if (!string.IsNullOrEmpty(configJson)) WriteText(archive, "config.json", configJson);
                WriteText(archive, "manifest.json",
                    JsonSerializer.Serialize(new Manifest { version = 1, entry = "worksheet.cpd" }));
            }
            if (File.Exists(destZip)) File.Delete(destZip);
            File.Move(tmp, destZip);

            // Mirror collected files into the working copy so it stays consistent with the package.
            if (syncDir is not null)
            {
                try
                {
                    foreach (var kv in libContents)
                    {
                        var p = Path.Combine(syncDir, "lib", kv.Key);
                        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                        File.WriteAllText(p, kv.Value, new UTF8Encoding(false));
                    }
                    foreach (var kv in imgNames)
                    {
                        var p = Path.Combine(syncDir, "Images", kv.Value);
                        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                        if (!string.Equals(Path.GetFullPath(kv.Key), Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase))
                            File.Copy(kv.Key, p, true);
                    }
                    if (templateLocalPath is not null && templateEntryName is not null && File.Exists(templateLocalPath))
                    {
                        var p = Path.Combine(syncDir, "template", templateEntryName);
                        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                        if (!string.Equals(Path.GetFullPath(templateLocalPath), Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase))
                            File.Copy(templateLocalPath, p, true);
                    }
                }
                catch { /* best effort */ }
            }

            return mainOut;
        }

        /// <summary>
        /// Extracts a .cpdz into a per-package working folder and returns the main worksheet's path,
        /// the stored config JSON (if any) and the working folder (reused for updates).
        /// </summary>
        public static (string worksheetPath, string? configJson, string workDir) Open(string zipPath)
        {
            var key = Convert.ToHexString(
                System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(zipPath))))[..16];
            var workDir = Path.Combine(Path.GetTempPath(), "CalcpadPackages", key);
            if (Directory.Exists(workDir)) { try { Directory.Delete(workDir, true); } catch { /* ignore */ } }
            Directory.CreateDirectory(workDir);
            ZipFile.ExtractToDirectory(zipPath, workDir, overwriteFiles: true);

            var entry = "worksheet.cpd";
            var manifestPath = Path.Combine(workDir, "manifest.json");
            if (File.Exists(manifestPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                    if (doc.RootElement.TryGetProperty("entry", out var e) && e.GetString() is string s && s.Length > 0)
                        entry = s;
                }
                catch { /* use default entry */ }
            }

            string? cfg = null;
            var cfgPath = Path.Combine(workDir, "config.json");
            if (File.Exists(cfgPath)) { try { cfg = File.ReadAllText(cfgPath); } catch { /* ignore */ } }

            return (Path.Combine(workDir, entry), cfg, workDir);
        }

        private static string ReadStripped(string path)
        {
            // Drop the '\v' input-form field data part (matches the include resolver).
            var content = File.ReadAllText(path);
            var vt = content.IndexOf('\v');
            return vt > 0 ? content.Substring(0, vt) : content;
        }

        private static void WriteText(ZipArchive archive, string entry, string text)
        {
            var e = archive.CreateEntry(entry);
            using var s = e.Open();
            using var w = new StreamWriter(s, new UTF8Encoding(false));
            w.Write(text);
        }

        private sealed class Manifest { public int version { get; set; } public string entry { get; set; } = "worksheet.cpd"; }
    }
}
