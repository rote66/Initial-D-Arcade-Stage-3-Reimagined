using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

// Only the isolated diagnostic scene instantiates this component. It uses the
// real Unity downloader, Mono staging, cache lease, native handoff and shutdown.
public sealed class Idas3UpdaterFlowSmoke : MonoBehaviour
{
    [Serializable] private sealed class Config {public string gameRoot,url,sha256,patchUrl,patchSha256;public long bytes,patchBytes;}
    [Serializable] private sealed class Observation {public string state,message,session;public float elapsed;}
    [Serializable] private sealed class Report {public bool reachedShutdown;public string error,tempPath,cacheRoot;public Observation[] observations;}
    private readonly List<Observation> observations=new List<Observation>();
    private Idas3Updates updates;
    private string reportPath,lastMessage,error;
    private float began;
    private bool stopped;
    private static FieldInfo Field(string name)=>typeof(Idas3Updates).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
    private void Start(){StartCoroutine(Run());}
    private IEnumerator Run()
    {
        try{
            var args=Idas3LaunchArguments.GetArguments();int at=Array.IndexOf(args,"-idas3-updater-flow-smoke");
            if(at<0||at+1>=args.Length)throw new ArgumentException("Updater fixture configuration missing.");
            string configPath=Path.GetFullPath(args[at+1]);reportPath=Path.Combine(Path.GetDirectoryName(configPath),"unity-flow.json");
            if(File.Exists(reportPath))throw new IOException("Do not overwrite updater flow evidence.");
            var config=JsonUtility.FromJson<Config>(File.ReadAllText(configPath));
            if(!new Uri(config.url).IsLoopback)throw new IOException("Updater fixture server must be loopback.");
            updates=gameObject.AddComponent<Idas3Updates>();updates.Initialize(false);
            var release=JsonUtility.FromJson<Idas3Updates.Release>(Idas3UpdateChecks.Fixture("v99.0.0"));
            release.assets[0].size=config.bytes;release.assets[0].digest="sha256:"+config.sha256;
            if(!string.IsNullOrEmpty(config.patchUrl)){
                if(!new Uri(config.patchUrl).IsLoopback)throw new IOException("Patch fixture server must be loopback.");
                string name="Initial-D-Update-from-"+Application.version+"-to-99.0.0-Patch.zip";
                release.assets=new[]{release.assets[0],new Idas3Updates.Asset{name=name,state="uploaded",size=config.patchBytes,
                    digest="sha256:"+config.patchSha256,browser_download_url=Idas3Updates.RepositoryUrl+"/releases/download/v99.0.0/"+name}};
            }
            updates.ApplyResponse(200,JsonUtility.ToJson(release));
            // Only the private fixture transport is redirected. Release URL
            // validation stays unchanged for the production client.
            Field("fullUrl").SetValue(updates,config.url);
            if(!string.IsNullOrEmpty(config.patchUrl))Field("patchUrl").SetValue(updates,config.patchUrl);
            updates.DownloadOverride=patch=>updates.DownloadAndInstall(config.gameRoot);
            began=Time.realtimeSinceStartup;updates.AcceptUpdate();
        }catch(Exception e){error=e.ToString();stopped=true;Save(false);Application.Quit(1);yield break;}
        while(!stopped){
            Observe();
            if(updates.State==Idas3Updates.CheckState.Unavailable){error=updates.Message;stopped=true;Save(false);Application.Quit(1);yield break;}
            if(Time.realtimeSinceStartup-began>120){error="Updater flow exceeded two minutes.";stopped=true;Save(false);Application.Quit(1);yield break;}
            yield return null;
        }
    }
    private void Observe()
    {
        if(updates==null||updates.Message==lastMessage)return;
        lastMessage=updates.Message;
        observations.Add(new Observation{state=updates.State.ToString(),message=lastMessage,session=Field("sessionFolder").GetValue(updates) as string,elapsed=Time.realtimeSinceStartup-began});
        Save(false);
    }
    private void Save(bool shutdown)
    {
        if(reportPath==null)return;
        File.WriteAllText(reportPath,JsonUtility.ToJson(new Report{reachedShutdown=shutdown,error=error,tempPath=Path.GetTempPath(),cacheRoot=Idas3UpdateCache.Root,observations=observations.ToArray()},true));
    }
    private void OnApplicationQuit(){Observe();Save(error==null&&updates!=null&&updates.Message=="Applying update and restarting…");}
}
