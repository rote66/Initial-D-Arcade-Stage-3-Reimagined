using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

[DefaultExecutionOrder(-1000)]
public sealed class Idas3IosDataGate : MonoBehaviour
{
    private static bool installed;
    public static bool Ready => Application.platform != RuntimePlatform.IPhonePlayer || installed;
    private string root, message = "Checking installed game data…";
    private Task operation;
    private CancellationTokenSource cancellation;
    private volatile float progress;
    private bool importing, selecting;
    private Camera background;
    private GUIStyle title, body, button;
    private Rect importButton, retryButton;
#if UNITY_IOS && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern void Idas3PickDataArchive();
    [DllImport("__Internal")] private static extern void Idas3ReleaseDataArchive();
#endif
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetState() { installed = false; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap() {
        if (Application.platform != RuntimePlatform.IPhonePlayer) return;
        Idas3MenuLocalization.SetLanguage(Idas3MenuLocalization.LoadLanguage(Path.Combine(Application.persistentDataPath,"userdata-unity-scene")));
        PrepareDocuments();
        // A signed full IPA reads its resources in place, without duplicating
        // several GB into Documents or offering an importer for a read-only bundle.
        if (Idas3AssetPaths.HasBundledData) {
            installed = true; Debug.Log("IDAS3 bundled game data ready: " + Idas3AssetPaths.Root); return;
        }
        var go = new GameObject("IDAS3 game data"); DontDestroyOnLoad(go);
        var gate = go.AddComponent<Idas3IosDataGate>(); gate.root = Idas3AssetPaths.Root;
        gate.background = go.AddComponent<Camera>(); gate.background.clearFlags = CameraClearFlags.SolidColor;
        gate.background.backgroundColor = new Color(.025f,.035f,.05f); gate.background.cullingMask = 0; gate.background.depth = 10000;
        var args = Idas3LaunchArguments.GetArguments(); int index = Array.IndexOf(args, "-idas3-data-import");
        if (index >= 0 && index + 1 < args.Length) gate.SelectedArchive(args[index + 1]); else gate.CheckAgain();
    }
    private void CheckAgain() {
        if (operation != null || selecting) return;
        cancellation?.Dispose(); cancellation = new CancellationTokenSource(); var token = cancellation.Token;
        importing = false; message = "Checking installed game data…";
        Idas3MenuLocalization.SetLanguage(Idas3MenuLocalization.LoadLanguage(Path.Combine(Application.persistentDataPath,"userdata-unity-scene")));
        PrepareDocuments();
        operation = Task.Run(() => { Idas3DataPackage.Recover(root); Idas3DataPackage.ValidateDirectory(root, token); }, token);
    }
    private static void PrepareDocuments() {
        // Populate the shared Documents container before the game-data gate opens.
        // Files must be usable even on a fresh install without the data package.
        string documents = Application.persistentDataPath;
        Directory.CreateDirectory(documents);
        Directory.CreateDirectory(Path.Combine(documents, "Custom Music"));
        Directory.CreateDirectory(Path.Combine(documents, "Touch Layouts"));
    }
    private void PickArchive() {
        if (operation != null || selecting) return;
        selecting = true; message = "Choose the Initial D game data ZIP in Files.";
#if UNITY_IOS && !UNITY_EDITOR
        Idas3PickDataArchive();
#endif
    }
    // Called by the iOS document picker on Unity's main thread.
    public void SelectedArchive(string path) {
        selecting = false; if (operation != null) return;
        if (string.IsNullOrEmpty(path)) { message = "Import cancelled. Your saved games are kept."; return; }
        cancellation?.Dispose(); cancellation = new CancellationTokenSource(); var token = cancellation.Token;
        progress = 0; importing = true; message = "Installing game data…";
        Idas3MenuLocalization.SetLanguage(Idas3MenuLocalization.LoadLanguage(Path.Combine(Application.persistentDataPath,"userdata-unity-scene")));
        PrepareDocuments();
        operation = Task.Run(() => { Idas3DataPackage.Import(path, root, value => progress = value, token); }, token);
    }
    private void Update() {
        if (installed) return;
        if (operation != null && operation.IsCompleted) {
            try { operation.GetAwaiter().GetResult(); installed = true; background.enabled = false; Debug.Log("IDAS3 external game data ready: " + root); }
            catch (Exception error) { message = error is OperationCanceledException ? "Import cancelled." : error.Message; Debug.LogWarning("Game data: " + message); }
            operation = null;
#if UNITY_IOS && !UNITY_EDITOR
            if (importing) Idas3ReleaseDataArchive();
#endif
            importing = false;
        }
        if (installed) return;
        if (Input.touchCount == 0) return;
        for (int i = 0; i < Input.touchCount; ++i) {
            var touch = Input.GetTouch(i); if (touch.phase != TouchPhase.Began) continue;
            var point = new Vector2(touch.position.x, Screen.height - touch.position.y);
            if (importButton.Contains(point)) { if (importing) cancellation?.Cancel(); else PickArchive(); }
            else if (retryButton.Contains(point)) CheckAgain();
        }
    }
    private static void Fill(Rect rect, Color color) { GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, color, 0, 0); }
    private void OnGUI() {
        if (installed) return;
        var matrix = GUI.matrix; var color = GUI.color; GUI.matrix = Matrix4x4.identity; GUI.color = Color.white; GUI.depth = -30000;
        float scale = Mathf.Min(Screen.width / 1280f, Screen.height / 720f); float x = (Screen.width - 1020 * scale) / 2, y = (Screen.height - 440 * scale) / 2;
        if (title == null) { title = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold }; body = new GUIStyle(GUI.skin.label) { wordWrap = true }; button = new GUIStyle(title) { alignment = TextAnchor.MiddleCenter }; }
        title.fontSize = Mathf.RoundToInt(36 * scale); body.fontSize = Mathf.RoundToInt(21 * scale); button.fontSize = Mathf.RoundToInt(20 * scale);
        title.normal.textColor = body.normal.textColor = button.normal.textColor = Color.white;
        Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(.025f,.035f,.05f)); Fill(new Rect(x,y,1020*scale,440*scale),new Color(.055f,.075f,.105f));
        Idas3MenuLocalization.Label(new Rect(x+36*scale,y+25*scale,948*scale,55*scale), importing ? "INSTALLING GAME DATA" : "GAME DATA", title);
        Idas3MenuLocalization.Label(new Rect(x+36*scale,y+102*scale,948*scale,66*scale), message, body);
        Idas3MenuLocalization.Label(new Rect(x+36*scale,y+181*scale,948*scale,102*scale), "Install the data package once. App updates keep your data and saves.\n\nOr unzip IDAS3GameData into this app's Documents folder, then tap CHECK AGAIN.", body);
        importButton = new Rect(x+36*scale,y+351*scale,455*scale,58*scale); retryButton = new Rect(x+529*scale,y+351*scale,455*scale,58*scale);
        if (importing) { Fill(new Rect(x+36*scale,y+312*scale,948*scale,10*scale),new Color(.2f,.25f,.33f)); Fill(new Rect(x+36*scale,y+312*scale,948*scale*progress,10*scale),new Color(1,.66f,.3f)); }
        Fill(importButton,new Color(.16f,.21f,.29f)); Fill(retryButton,new Color(.16f,.21f,.29f));
        Idas3MenuLocalization.Label(importButton, importing ? "CANCEL IMPORT  " + Mathf.RoundToInt(progress*100) + "%" : "IMPORT DATA ZIP", button);
        Idas3MenuLocalization.Label(retryButton,"CHECK AGAIN",button);
        GUI.matrix = matrix; GUI.color = color;
    }
    private void OnDestroy() { cancellation?.Cancel(); cancellation?.Dispose(); }
}
