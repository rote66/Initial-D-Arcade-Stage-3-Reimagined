using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Temporary native macOS peer for testing the iOS session on one Simulator.
// It is not a distributed desktop edition or a Steam compatibility promise.
public static class Idas3LanPeerBuild
{
    public static void Build()
    {
        const string archive="Assets/Plugins/macOS/libIdas3Unity.dylib";
        var plugin=AssetImporter.GetAtPath(archive) as PluginImporter;
        if(plugin==null)throw new FileNotFoundException("Build the portable macOS LAN peer library first.",archive);
        plugin.SetCompatibleWithAnyPlatform(false);plugin.SetCompatibleWithEditor(false);
        plugin.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX,true);plugin.SaveAndReimport();
        var previous=PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone);
        var previousBackend=PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
        int previousArchitecture=PlayerSettings.GetArchitecture(NamedBuildTarget.Standalone);
        PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.Standalone,previous+";IDAS3_IOS_LAN_PEER;IDAS3_DISABLE_ROM_VALIDATION");
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone,ScriptingImplementation.Mono2x);
        PlayerSettings.SetArchitecture(NamedBuildTarget.Standalone,1); // macOS ARM64 test peer
        Idas3LanBuildIdentity.Generate();
        try{
            var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions{
                scenes=new[]{"Assets/Scenes/InitialDUnityScene.unity"},
                locationPathName="Builds/iOS/LanPeer/InitialDLanPeer.app",
                target=BuildTarget.StandaloneOSX,options=BuildOptions.Development});
            if(report.summary.result!=BuildResult.Succeeded)throw new Exception("LAN peer export failed: "+report.summary.result);
        }finally{
            PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.Standalone,previous);
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone,previousBackend);
            PlayerSettings.SetArchitecture(NamedBuildTarget.Standalone,previousArchitecture);
            AssetDatabase.SaveAssets();
        }
    }
}
