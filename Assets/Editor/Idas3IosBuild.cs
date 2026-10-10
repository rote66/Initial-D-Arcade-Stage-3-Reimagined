using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.iOS.Xcode;
using UnityEngine;
using UnityEngine.Rendering;

public static class Idas3IosBuild
{
    [MenuItem("Initial D/Build iOS 15 Test Player")]
    public static void Build()
    {
        BuildPlayer(false);
    }

    [MenuItem("Initial D/Build iOS Simulator Test Player")]
    public static void BuildSimulator()
    {
        BuildPlayer(true);
    }

    private static void BuildPlayer(bool simulator)
    {
        string project=Path.GetFullPath(Path.Combine(Application.dataPath,".."));
        string pluginPath="Assets/Plugins/iOS/libIdas3Unity.a";
        if(!File.Exists(pluginPath))throw new FileNotFoundException("Build the iOS native plugin first.",pluginPath);
        PlayerSettings.companyName="IDAS3";
        PlayerSettings.productName="Initial D iOS";
        PlayerSettings.bundleVersion="0.3.95";
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS,"org.idas3.reimagined.ios");
        string buildNumber=Environment.GetEnvironmentVariable("IDAS3_BUILD_NUMBER")??"13";
        if(!int.TryParse(buildNumber,out int number)||number<1)throw new ArgumentException("IDAS3_BUILD_NUMBER must be a positive integer.");
        PlayerSettings.iOS.buildNumber=buildNumber;
        PlayerSettings.enableFrameTimingStats=true;
        PlayerSettings.iOS.targetOSVersionString="15.0";
        var previousSdk=PlayerSettings.iOS.sdkVersion;
        PlayerSettings.iOS.sdkVersion=simulator?iOSSdkVersion.SimulatorSDK:iOSSdkVersion.DeviceSDK;
        PlayerSettings.iOS.simulatorSdkArchitecture=AppleMobileArchitectureSimulator.ARM64;
        PlayerSettings.iOS.targetDevice=iOSTargetDevice.iPhoneAndiPad;
        PlayerSettings.iOS.appleEnableAutomaticSigning=false;
        PlayerSettings.allowUnsafeCode=true;
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.iOS,ScriptingImplementation.IL2CPP);
        PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.iOS,ManagedStrippingLevel.Low);
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.iOS,false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.iOS,new[]{GraphicsDeviceType.Metal});
        var defines=PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.iOS).Split(';').Where(x=>!string.IsNullOrWhiteSpace(x)).ToList();
        // Opt back into the official dump check with IDAS3_VERIFY_ROM=1.
        defines.Remove("IDAS3_DISABLE_ROM_VALIDATION");
        if(Environment.GetEnvironmentVariable("IDAS3_VERIFY_ROM")!="1")defines.Add("IDAS3_DISABLE_ROM_VALIDATION");
        PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.iOS,string.Join(";",defines));
        PlayerSettings.defaultInterfaceOrientation=UIOrientation.AutoRotation;
        PlayerSettings.allowedAutorotateToPortrait=false;
        PlayerSettings.allowedAutorotateToPortraitUpsideDown=false;
        PlayerSettings.allowedAutorotateToLandscapeLeft=true;
        PlayerSettings.allowedAutorotateToLandscapeRight=true;
        PlayerSettings.runInBackground=false;
        PlayerSettings.colorSpace=ColorSpace.Gamma;
        QualitySettings.antiAliasing=2;
        GraphicsSettings.defaultRenderPipeline=null;
        var plugin=AssetImporter.GetAtPath(pluginPath) as PluginImporter;
        if(plugin==null)throw new InvalidOperationException("Unity did not import the native archive.");
        plugin.SetCompatibleWithAnyPlatform(false);plugin.SetCompatibleWithEditor(false);
        plugin.SetCompatibleWithPlatform(BuildTarget.iOS,true);plugin.SetPlatformData(BuildTarget.iOS,"CPU","ARM64");
        plugin.SaveAndReimport();
        Idas3LanBuildIdentity.Generate();
        AssetDatabase.SaveAssets();
        string output=Path.Combine(project,simulator?"Builds/iOS/Simulator/Xcode":"Builds/iOS/Xcode");
        try {
            var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions{
                scenes=new[]{"Assets/Scenes/InitialDUnityScene.unity"},locationPathName=output,
                target=BuildTarget.iOS,options=simulator?BuildOptions.Development:BuildOptions.None});
            if(report.summary.result!=BuildResult.Succeeded)throw new InvalidOperationException("iOS export failed: "+report.summary.result);
            Debug.Log("iOS Xcode export ready: "+output);
        } finally {
            PlayerSettings.iOS.sdkVersion=previousSdk;
            AssetDatabase.SaveAssets();
        }
    }

    [UnityEditor.Callbacks.PostProcessBuild(200)]
    public static void Stage(BuildTarget target,string output)
    {
        if(target!=BuildTarget.iOS)return;
        string root=Path.GetFullPath(Path.Combine(Application.dataPath,".."));
        // iOS core contains engine/UI resources. Large content is installed in Documents.
        foreach(string folder in new[]{"IDAS3","SoundRoom","SteamInput"}.Concat(Idas3CourseCatalog.Packs)){
            string staged=Path.Combine(output,"Data/Raw",folder);
            if(Directory.Exists(staged))Directory.Delete(staged,true);
        }
        var plist=new PlistDocument();string info=Path.Combine(output,"Info.plist");plist.ReadFromFile(info);
        plist.root.SetString("MinimumOSVersion","15.0");
        plist.root.SetString("IDAS3UpstreamRelease","0.3.95-community-replays.46");
        plist.root.SetString("CFBundleDevelopmentRegion","en");
        var localizations=plist.root.CreateArray("CFBundleLocalizations");
        foreach(string language in new[]{"en","ja","zh-Hans"})localizations.AddString(language);
        plist.root.SetBoolean("UIFileSharingEnabled",true);
        plist.root.SetBoolean("LSSupportsOpeningDocumentsInPlace",true);
        plist.root.SetString("NSMotionUsageDescription","Use device tilt for optional gravity steering.");
        plist.root.SetString("NSLocalNetworkUsageDescription","Connect to another driver on your local Wi-Fi for a two-player LAN battle. 用于连接同一 Wi-Fi 内的另一位车手，进行双人局域网对战。");
        plist.root.SetBoolean("IDAS3LanMultiplayerEnabled",true);
        plist.root.SetBoolean("IDAS3SteamEnabled",false);
        plist.root.SetBoolean("IDAS3DiscordEnabled",false);
        plist.root.SetBoolean("IDAS3UpdatesEnabled",false);
        plist.root.SetInteger("IDAS3GameDataVersion",Idas3AssetPaths.DataVersion);
        plist.root.SetBoolean("IDAS3RomValidationEnabled",!PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.iOS).Split(';').Contains("IDAS3_DISABLE_ROM_VALIDATION"));
        plist.WriteToFile(info);
        var pbx=new PBXProject();string pbxPath=PBXProject.GetPBXProjectPath(output);pbx.ReadFromFile(pbxPath);
        pbx.AddFrameworkToProject(pbx.GetUnityFrameworkTargetGuid(),"CoreMotion.framework",false);
        pbx.AddFrameworkToProject(pbx.GetUnityFrameworkTargetGuid(),"UniformTypeIdentifiers.framework",false);
        if(PlayerSettings.iOS.sdkVersion==iOSSdkVersion.SimulatorSDK){
            // Unity's ARM64 plugin filter omits archives from Simulator exports.
            // The build script supplies an archive compiled for iphonesimulator.
            const string library="Libraries/libIdas3Unity.a";
            string staged=Path.Combine(output,library);Directory.CreateDirectory(Path.GetDirectoryName(staged));
            File.Copy(Path.Combine(root,"Assets/Plugins/iOS/libIdas3Unity.a"),staged,true);
            string file=pbx.AddFile(library,library,PBXSourceTree.Source);
            pbx.AddFileToBuild(pbx.GetUnityFrameworkTargetGuid(),file);
        }
        foreach(string guid in new[]{pbx.GetUnityMainTargetGuid(),pbx.GetUnityFrameworkTargetGuid()}){
            pbx.SetBuildProperty(guid,"IPHONEOS_DEPLOYMENT_TARGET","15.0");
            pbx.SetBuildProperty(guid,"ENABLE_BITCODE","NO");
            pbx.SetBuildProperty(guid,"CODE_SIGNING_ALLOWED","NO");
        }
        pbx.WriteToFile(pbxPath);
    }
    private static void CopyTree(string source,string destination)
    {
        if(!Directory.Exists(source))throw new DirectoryNotFoundException(source);
        foreach(string file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories)){
            if(Path.GetFileName(file)==".DS_Store"||file.EndsWith(".meta",StringComparison.Ordinal))continue;
            string output=Path.Combine(destination,Path.GetRelativePath(source,file));
            Directory.CreateDirectory(Path.GetDirectoryName(output));File.Copy(file,output,true);
        }
    }
}
