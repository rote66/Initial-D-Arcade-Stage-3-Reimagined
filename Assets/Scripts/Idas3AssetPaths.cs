using System.IO;
using UnityEngine;

// One resolver for native assets, imported courses, music and replay viewing.
public static class Idas3AssetPaths
{
    public const string DataFolder = "IDAS3GameData";
    public const int DataVersion = 3;
    public static bool HasBundledData => Application.platform == RuntimePlatform.IPhonePlayer
        && File.Exists(Path.Combine(Application.streamingAssetsPath, DataFolder, "game-data.json"));
    // Packaging changes the data location, never the audio support in the core.
    internal static string ResolveRoot(bool ios, string documents, string streamingAssets) {
        if (!ios) return streamingAssets;
        string bundled = Path.Combine(streamingAssets, DataFolder);
        return File.Exists(Path.Combine(bundled, "game-data.json")) ? bundled : Path.Combine(documents, DataFolder);
    }
    public static string Root => ResolveRoot(Application.platform == RuntimePlatform.IPhonePlayer,
        Application.persistentDataPath, Application.streamingAssetsPath);
    public static string NativeRoot => Application.isEditor
        ? Path.GetFullPath(Path.Combine(Application.dataPath, "../Native")) : Path.Combine(Root, "IDAS3");
}
