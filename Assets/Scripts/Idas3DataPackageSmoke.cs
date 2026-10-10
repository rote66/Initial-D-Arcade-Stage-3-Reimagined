using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public sealed class Idas3DataPackageSmoke : MonoBehaviour
{
    internal static bool Requested => Array.IndexOf(Idas3LaunchArguments.GetArguments(), "-idas3-data-checks") >= 0;
    [Serializable] private sealed class Report {
        public bool passed; public int checks; public string error;
        public string scope = "Isolated ZIP installation: contents, corruption, unsafe paths, cancellation, rollback, recovery and save preservation.";
    }
    private Task<Report> task; private string root;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap() {
        var args = Idas3LaunchArguments.GetArguments(); int index = Array.IndexOf(args, "-idas3-data-checks");
        if (index < 0 || index + 1 >= args.Length) return;
        var owner = new GameObject("Data package checks").AddComponent<Idas3DataPackageSmoke>(); DontDestroyOnLoad(owner.gameObject);
        owner.root = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(owner.root);
        owner.task = Task.Run(() => Run(owner.root));
    }
    private void Update() {
        if (task == null || !task.IsCompleted) return;
        var report = task.GetAwaiter().GetResult(); task = null;
        File.WriteAllText(Path.Combine(root, "report.json"), JsonUtility.ToJson(report, true));
        Debug.Log((report.passed ? "PASS" : "FAIL") + " data package checks: " + report.error); Application.Quit(report.passed ? 0 : 1);
    }
    private static string Hash(byte[] bytes) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private static Report Run(string testRoot) {
        var report = new Report();
        void Check(bool value, string reason) { ++report.checks; if (!value) throw new InvalidOperationException(reason); }
        string installed = Path.Combine(testRoot, "installed"), archive = Path.Combine(testRoot, "fixture.zip");
        byte[] content = Encoding.UTF8.GetBytes("known data"), changed = Encoding.UTF8.GetBytes("other data");
        var paths = new System.Collections.Generic.List<string> { "IDAS3/data/original_physics/tables.bin", "SoundRoom/catalog.json" };
        foreach (string pack in Idas3CourseCatalog.Packs) paths.Add(pack + "/menu.idastex");
        var manifest = new Idas3DataPackage.Manifest { schema = "idas3-game-data", version = Idas3DataPackage.Version, unpackedBytes = content.Length * paths.Count,
            files = paths.ConvertAll(path => new Idas3DataPackage.FileRecord { path = path, bytes = content.Length, sha256 = Hash(content) }).ToArray() };
        void Zip(string variant, byte[] payload = null) {
            if (File.Exists(archive)) File.Delete(archive);
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) {
                string prefix = Idas3AssetPaths.DataFolder + "/";
                using (var writer = new StreamWriter(zip.CreateEntry(prefix + Idas3DataPackage.ManifestName).Open())) writer.Write(JsonUtility.ToJson(manifest));
                zip.CreateEntry(prefix); zip.CreateEntry(prefix + "IDAS3/"); zip.CreateEntry(prefix + "IDAS3/data/");
                for (int i = 0; i < paths.Count; ++i) {
                    if (variant == "missing" && i == 0) continue;
                    var entry = zip.CreateEntry(prefix + paths[i]); if (variant == "symlink" && i == 0) entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                    using (var output = entry.Open()) { var bytes = payload ?? (variant == "corrupt" && i == 0 ? changed : content); output.Write(bytes, 0, bytes.Length); }
                }
                if (variant == "duplicate") zip.CreateEntry(prefix + paths[0]);
                if (variant == "extra") zip.CreateEntry(prefix + "../escaped.txt");
            }
        }
        void Rejected(string reason) {
            bool rejected = false; try { Idas3DataPackage.Import(archive, installed, null, CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, reason); Check(File.ReadAllBytes(Path.Combine(installed, paths[0]))[0] == content[0], "Bad import changed installed data");
            Check(!Directory.Exists(installed + ".installing"), "Bad import left a staging directory");
        }
        try {
            manifest.version = 1;
            Check(Idas3DataPackage.Parse(JsonUtility.ToJson(manifest)).version == 1, "Universal core rejects original v1 data");
            manifest.version = Idas3DataPackage.Version;
            Check(Idas3DataPackage.Parse(JsonUtility.ToJson(manifest)).version == Idas3DataPackage.Version, "Universal core rejects current full/MP3 data");
            string documents = Path.Combine(testRoot, "documents"), streaming = Path.Combine(testRoot, "bundle");
            Check(Idas3AssetPaths.ResolveRoot(true, documents, streaming) == Path.Combine(documents, Idas3AssetPaths.DataFolder), "Core did not resolve Documents data");
            string bundled = Path.Combine(streaming, Idas3AssetPaths.DataFolder); Directory.CreateDirectory(bundled);
            File.WriteAllText(Path.Combine(bundled, "game-data.json"), JsonUtility.ToJson(manifest));
            Check(Idas3AssetPaths.ResolveRoot(true, documents, streaming) == bundled, "Full IPA did not resolve bundled data");
            foreach (string path in paths) {
                string destination = Path.Combine(bundled, path); Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.WriteAllBytes(destination, content);
            }
            Idas3DataPackage.ValidateDirectory(bundled, CancellationToken.None);
            Check(!Directory.Exists(documents), "Bundled data validation copied data into Documents");
            Check(Idas3AssetPaths.ResolveRoot(false, documents, streaming) == streaming, "Desktop asset location changed");
            Directory.CreateDirectory(Path.Combine(documents, Idas3AssetPaths.DataFolder));
            File.WriteAllText(Path.Combine(documents, Idas3AssetPaths.DataFolder, "game-data.json"), "old external data");
            Check(Idas3AssetPaths.ResolveRoot(true, documents, streaming) == bundled, "Old external data overrides a full IPA");
            string saves = Path.Combine(testRoot, "saves/save.bin"); Directory.CreateDirectory(Path.GetDirectoryName(saves)); File.WriteAllText(saves, "user save");
            Zip("valid"); float progress = 0; Idas3DataPackage.Import(archive, installed, p => progress = p, CancellationToken.None);
            Check(progress == 1, "Import progress incomplete"); Idas3DataPackage.ValidateDirectory(installed, CancellationToken.None);
            foreach (string path in paths) Check(Hash(File.ReadAllBytes(Path.Combine(installed, path))) == Hash(content), "Imported file differs");
            foreach (string variant in new[] { "missing", "corrupt", "symlink", "duplicate", "extra" }) { Zip(variant); Rejected("Accepted " + variant + " archive"); }
            foreach (string path in new[] { "../outside", "/absolute", "HAKONE/../outside", "HAKONE\\outside", "HAKONE/C:/file" }) {
                string original = manifest.files[0].path; manifest.files[0].path = path;
                bool rejected = false; try { Idas3DataPackage.Parse(JsonUtility.ToJson(manifest)); } catch (InvalidDataException) { rejected = true; }
                manifest.files[0].path = original; Check(rejected, "Unsafe manifest path accepted");
            }
            manifest.version = Idas3DataPackage.Version+1; Zip("valid"); Rejected("Wrong package version accepted"); manifest.version = Idas3DataPackage.Version;
            manifest.unpackedBytes++; Zip("valid"); Rejected("Wrong total size accepted"); manifest.unpackedBytes--;
            Zip("valid"); using (var cancel = new CancellationTokenSource()) {
                cancel.Cancel(); bool cancelled = false;
                try { Idas3DataPackage.Import(archive, installed, null, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && !Directory.Exists(installed + ".installing"), "Cancelled import was committed");
            }
            Check(File.ReadAllText(saves) == "user save", "Import changed a save"); Check(!File.Exists(Path.Combine(testRoot, "escaped.txt")), "Archive escaped install directory");
            Directory.Move(installed, installed + ".previous"); Idas3DataPackage.Recover(installed);
            Check(Directory.Exists(installed) && !Directory.Exists(installed + ".previous"), "Interrupted directory switch did not recover");
            foreach (var file in manifest.files) file.sha256 = Hash(changed); Zip("valid", changed); Idas3DataPackage.Import(archive, installed, null, CancellationToken.None);
            Check(Hash(File.ReadAllBytes(Path.Combine(installed, paths[0]))) == Hash(changed), "Replacement not installed");
            Check(!Directory.Exists(installed + ".previous") && File.ReadAllText(saves) == "user save", "Replacement cleanup or save preservation failed");
            File.Delete(Path.Combine(installed, paths[0])); bool incomplete = false;
            try { Idas3DataPackage.ValidateDirectory(installed, CancellationToken.None); } catch (InvalidDataException) { incomplete = true; }
            Check(incomplete, "Manually copied incomplete data accepted"); report.passed = true;
        } catch (Exception error) { report.error = error.ToString(); }
        return report;
    }
}
