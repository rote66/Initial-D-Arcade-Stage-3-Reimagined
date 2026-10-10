using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using UnityEngine;

// Package checks concern install integrity, independently of the ROM gate.
internal static class Idas3DataPackage
{
    internal const int Version = Idas3AssetPaths.DataVersion;
    internal const string ManifestName = "game-data.json";
    private const long MaxBytes = 10L * 1024 * 1024 * 1024;
    [Serializable] internal sealed class FileRecord { public string path, sha256; public long bytes; }
    [Serializable] internal sealed class Manifest {
        public string schema; public int version; public long unpackedBytes; public FileRecord[] files;
    }
    private static bool Allowed(string path) {
        if (string.IsNullOrEmpty(path) || path.Length > 1024 || path.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0 || path.IndexOf('\0') >= 0 || Path.IsPathRooted(path)) return false;
        foreach (string part in path.Split('/')) if (part == "" || part == "." || part == "..") return false;
        if (path.StartsWith("IDAS3/data/", StringComparison.Ordinal) || path.StartsWith("SoundRoom/", StringComparison.Ordinal)) return true;
        foreach (string pack in Idas3CourseCatalog.Packs) if (path.StartsWith(pack + "/", StringComparison.Ordinal)) return true;
        return false;
    }
    internal static Manifest Parse(string json) {
        if (json == null || json.Length > 8 * 1024 * 1024) throw new InvalidDataException("Invalid game data manifest.");
        var manifest = JsonUtility.FromJson<Manifest>(json);
        // v1-v3 share the same layout/audio readers; required course content must be current.
        if (manifest == null || manifest.schema != "idas3-game-data" || (manifest.version < 1 || manifest.version > Version) || manifest.files == null || manifest.files.Length < 1 || manifest.files.Length > 30000)
            throw new InvalidDataException("This data package is not compatible with this app.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var file in manifest.files) {
            if (file == null || !Allowed(file.path) || !seen.Add(file.path) || file.bytes < 0 || file.bytes > MaxBytes || file.sha256 == null || file.sha256.Length != 64)
                throw new InvalidDataException("Invalid game data entry.");
            foreach (char c in file.sha256) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) throw new InvalidDataException("Invalid data checksum.");
            total += file.bytes; if (total > MaxBytes) throw new InvalidDataException("Game data package is too large.");
        }
        if (total != manifest.unpackedBytes || !seen.Contains("IDAS3/data/original_physics/tables.bin") || !seen.Contains("SoundRoom/catalog.json"))
            throw new InvalidDataException("Game data package is incomplete.");
        foreach (string pack in Idas3CourseCatalog.Packs) if (!seen.Contains(pack + "/menu.idastex")) throw new InvalidDataException("Game data package is missing a course.");
        return manifest;
    }
    internal static void ValidateDirectory(string root, CancellationToken token) {
        string path = Path.Combine(root, ManifestName);
        if (!File.Exists(path)) throw new FileNotFoundException("Game data has not been installed.");
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Invalid game data manifest.");
        var manifest = Parse(File.ReadAllText(path));
        foreach (var file in manifest.files) {
            token.ThrowIfCancellationRequested(); var info = new FileInfo(Path.Combine(root, file.path));
            if (!info.Exists || info.Length != file.bytes) throw new InvalidDataException("Game data is missing or incomplete. Import the data package again.");
        }
    }
    internal static void Recover(string root) {
        string previous = root + ".previous";
        if (!Directory.Exists(root) && Directory.Exists(previous)) Directory.Move(previous, root);
    }
    internal static void Import(string archive, string root, Action<float> progress, CancellationToken token) {
        Recover(root);
        string staging = root + ".installing", previous = root + ".previous";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        try {
            using (var zip = ZipFile.OpenRead(archive)) {
                const string prefix = Idas3AssetPaths.DataFolder + "/";
                var entry = zip.GetEntry(prefix + ManifestName);
                if (entry == null || entry.Length > 8 * 1024 * 1024) throw new InvalidDataException("Select the Initial D game data ZIP.");
                string json; using (var reader = new StreamReader(entry.Open())) json = reader.ReadToEnd();
                var manifest = Parse(json); var expected = new Dictionary<string, FileRecord>(StringComparer.Ordinal);
                foreach (var file in manifest.files) expected.Add(prefix + file.path, file);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long written = 0; byte[] buffer = new byte[131072];
                Directory.CreateDirectory(staging);
                foreach (var item in zip.Entries) {
                    token.ThrowIfCancellationRequested();
                    if (!seen.Add(item.FullName) || ((item.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Invalid data archive entry.");
                    if (item.FullName == prefix + ManifestName) continue;
                    if (item.FullName.EndsWith("/", StringComparison.Ordinal)) {
                        bool parent = false;
                        foreach (string path in expected.Keys) if (path.StartsWith(item.FullName, StringComparison.Ordinal)) { parent = true; break; }
                        if (!parent) throw new InvalidDataException("Invalid data directory.");
                        continue;
                    }
                    if (!expected.TryGetValue(item.FullName, out var file) || item.Length != file.bytes) throw new InvalidDataException("Unexpected or incomplete game data file.");
                    string destination = Path.Combine(staging, file.path); Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    using (var input = item.Open()) using (var output = File.Create(destination)) using (var hash = SHA256.Create()) {
                        long count = 0; int bytes;
                        while ((bytes = input.Read(buffer, 0, buffer.Length)) != 0) {
                            token.ThrowIfCancellationRequested(); count += bytes;
                            if (count > file.bytes) throw new InvalidDataException("Data file exceeds declared size.");
                            output.Write(buffer, 0, bytes); hash.TransformBlock(buffer, 0, bytes, buffer, 0); written += bytes;
                            progress?.Invoke((float)((double)written / Math.Max(1, manifest.unpackedBytes)));
                        }
                        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        if (count != file.bytes || BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant() != file.sha256) throw new InvalidDataException("The data package is damaged. Copy it again and retry.");
                    }
                }
                foreach (var file in expected.Keys) if (!seen.Contains(file)) throw new InvalidDataException("The data package is incomplete.");
                File.WriteAllText(Path.Combine(staging, ManifestName), json);
            }
            token.ThrowIfCancellationRequested();
            // No game can start until the new directory has been installed.
            if (Directory.Exists(previous)) Directory.Delete(previous, true);
            if (Directory.Exists(root)) Directory.Move(root, previous);
            try { Directory.Move(staging, root); }
            catch { if (!Directory.Exists(root) && Directory.Exists(previous)) Directory.Move(previous, root); throw; }
            if (Directory.Exists(previous)) try { Directory.Delete(previous, true); } catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
            progress?.Invoke(1);
        } finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
}
