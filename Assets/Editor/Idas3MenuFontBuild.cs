using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public sealed class Idas3MenuFontBuild : IPreprocessBuildWithReport
{
    public int callbackOrder=>0;
    public void OnPreprocessBuild(BuildReport report)
    {
        const string path="Assets/Resources/Fonts/NotoSansJP-Regular.otf";
        var importer=AssetImporter.GetAtPath(path) as TrueTypeFontImporter;
        if(importer==null||!importer.includeFontData||AssetDatabase.LoadAssetAtPath<Font>(path)==null)
            throw new BuildFailedException("Sound Room requires its bundled Noto font with Include Font Data enabled.");
        foreach(string name in new[]{"NotoSansCJKsc-Regular","NotoSansCJKtc-Regular"}){
            string chinese="Assets/Resources/Fonts/"+name+".otf";
            var cjk=AssetImporter.GetAtPath(chinese) as TrueTypeFontImporter;
            if(cjk==null||!cjk.includeFontData||AssetDatabase.LoadAssetAtPath<Font>(chinese)==null)
                throw new BuildFailedException("Chinese UI requires its bundled Noto CJK font with Include Font Data enabled: "+name);
        }
    }

    // Isolated real-menu renderer. No ROM, native game initialization, personal
    // saves or installed OS fonts are needed to verify the actual text output.
    public static void BuildDiagnostic()
    {
        const string scenePath="Assets/Scenes/MenuFontDiagnostic.unity";
        if(File.Exists(scenePath))throw new IOException("Temporary font diagnostic scene already exists.");
        try{
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var camera=new GameObject("Camera").AddComponent<Camera>();
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
            new GameObject("Sound Room font check").AddComponent<Idas3MenuFontSmoke>();
            EditorSceneManager.SaveScene(scene,scenePath);
            var result=BuildPipeline.BuildPlayer(new BuildPlayerOptions{
                scenes=new[]{scenePath},locationPathName="Builds/MenuFontCheck/InitialDUnity.exe",
                target=BuildTarget.StandaloneWindows64,options=BuildOptions.None});
            if(result.summary.result!=BuildResult.Succeeded)throw new BuildFailedException("Font diagnostic build failed.");
        }finally{AssetDatabase.DeleteAsset(scenePath);}
    }
}
