using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class Idas3IosDiagnostics : MonoBehaviour
{
    private Idas3SceneGame host;
    private Idas3FrameStatistics statistics;
    private Idas3SceneRenderer renderer;
    private Idas3DiagnosticWriter writer;
    private readonly FrameTiming[] timings=new FrameTiming[1];
    private ulong lastTiming;
    private bool configured,enabledLast,lowFps;
    private double began,cpu,nativeMs,rendererMs,uiMs,networkMs,deltaMs,maxDeltaMs;
    private double gpuMs,mainMs,renderThreadMs,presentWaitMs;
    private int frames,timedFrames,gc0,gc1,gc2;
    internal string Folder {get;private set;}
    internal string LastError {get;private set;}
    internal bool Recording=>writer!=null&&host.GameOptions.Current.diagnosticLogging;
    [Serializable] private sealed class Session {
        public string kind="session",utc,version,unity,device,os,graphics,buildGuid;
        public int build;
    }
    [Serializable] private sealed class Sample {
        public string kind,utc;
        public double uptime,fps,frameMs,maxFrameMs,nativeMs,rendererMs,uiMs,networkMs,
            cpuPercent,memoryMiB,gpuMs,mainThreadMs,renderThreadMs,presentWaitMs,refreshHz,displayLinkPreferredFps;
        public bool thermalFrameRateAdjustment;
        public int frame,targetFps,requestedFps,vSync,renderInterval,width,height,antialiasing,
            course,car,phase,frontend,thermalState,lowPowerMode,gc0,gc1,gc2,droppedLogs,
            meshes,uploadedVertices,geometryUploads,textures;
        public uint flags;
        public ulong simulationTicks,nativeRenderedFrames;
    }
#if UNITY_IOS && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern double Idas3ProcessCpuSeconds();
    [DllImport("__Internal")] private static extern double Idas3ProcessMemoryMiB();
    [DllImport("__Internal")] private static extern int Idas3ThermalState();
    [DllImport("__Internal")] private static extern int Idas3LowPowerMode();
    [DllImport("__Internal")] private static extern int Idas3IosBuildNumber();
#else
    private static double Idas3ProcessCpuSeconds()=>-1;
    private static double Idas3ProcessMemoryMiB()=>-1;
    private static int Idas3ThermalState()=>-1;
    private static int Idas3LowPowerMode()=>-1;
    private static int Idas3IosBuildNumber()=>0;
#endif
    internal void Initialize(Idas3SceneGame owner,string directory){
        host=owner;Folder=Path.GetFullPath(directory);
        statistics=GetComponent<Idas3FrameStatistics>();renderer=GetComponent<Idas3SceneRenderer>();
        Synchronize();
    }
    internal void Synchronize(){
        if(host==null)return;
        bool enabled=host.GameOptions.Current.diagnosticLogging;
        if(configured&&enabled==enabledLast)return;
        LastError=null;
        try{
            if(enabled){
                if(writer!=null)StopWriter();
                writer=new Idas3DiagnosticWriter(Folder);
                Application.logMessageReceivedThreaded+=OnLog;
                writer.Enqueue(Idas3DiagnosticWriter.Channel.Runtime,JsonUtility.ToJson(new Session{
                    utc=DateTime.UtcNow.ToString("O"),version=Application.version,unity=Application.unityVersion,
                    device=SystemInfo.deviceModel,os=SystemInfo.operatingSystem,
                    graphics=SystemInfo.graphicsDeviceName,buildGuid=Application.buildGUID,build=Idas3IosBuildNumber()}));
                ResetWindow();lowFps=false;lastTiming=0;
            }else{
                StopWriter();
                Idas3DiagnosticWriter.DeleteLogs(Folder);
            }
        }catch(Exception e){LastError=e.GetType().Name+": "+e.Message;}
        configured=true;enabledLast=enabled;
    }
    private void OnLog(string message,string stack,LogType type){
        var active=writer;if(active==null)return;
        active.Enqueue(Idas3DiagnosticWriter.Channel.Runtime,
            DateTime.UtcNow.ToString("O")+" "+type+" "+message+
            (type==LogType.Error||type==LogType.Exception||type==LogType.Assert?"\n"+stack:""));
    }
    private void ResetWindow(){
        began=Time.realtimeSinceStartupAsDouble;cpu=Idas3ProcessCpuSeconds();
        frames=timedFrames=0;nativeMs=rendererMs=uiMs=networkMs=deltaMs=maxDeltaMs=0;
        gpuMs=mainMs=renderThreadMs=presentWaitMs=0;
        gc0=GC.CollectionCount(0);gc1=GC.CollectionCount(1);gc2=GC.CollectionCount(2);
    }
    private void LateUpdate(){
        Synchronize();
        if(!Recording)return;
        if(!host.Ready||!Application.isFocused){ResetWindow();return;}
        ++frames;nativeMs+=host.NativeStepMilliseconds;rendererMs+=host.RendererMilliseconds;
        uiMs+=host.UiMilliseconds;networkMs+=host.NetworkMilliseconds;
        double dt=Time.unscaledDeltaTime*1000;deltaMs+=dt;maxDeltaMs=Math.Max(maxDeltaMs,dt);
        if(FrameTimingManager.IsFeatureEnabled()){
            FrameTimingManager.CaptureFrameTimings();
            if(FrameTimingManager.GetLatestTimings(1,timings)>0&&timings[0].frameStartTimestamp!=lastTiming){
                var timing=timings[0];lastTiming=timing.frameStartTimestamp;
                if(timing.gpuFrameTime>0&&!double.IsNaN(timing.gpuFrameTime)&&!double.IsInfinity(timing.gpuFrameTime)){
                    ++timedFrames;gpuMs+=timing.gpuFrameTime;mainMs+=timing.cpuMainThreadFrameTime;
                    renderThreadMs+=timing.cpuRenderThreadFrameTime;presentWaitMs+=timing.cpuMainThreadPresentWaitTime;
                }
            }
        }
        double now=Time.realtimeSinceStartupAsDouble,elapsed=now-began;
        if(elapsed<2)return;
        var state=host.Status;var options=host.GameOptions.Current;
        double nextCpu=Idas3ProcessCpuSeconds(),fps=statistics?.FramesPerSecond??0;
        bool slow=Application.targetFrameRate>30&&fps>0&&fps<Math.Min(45,Application.targetFrameRate*.75);
        var sample=new Sample{kind=slow!=lowFps?(slow?"low-fps":"recovered"):"sample",
            utc=DateTime.UtcNow.ToString("O"),uptime=now,frame=Time.frameCount,fps=fps,
            frameMs=deltaMs/frames,maxFrameMs=maxDeltaMs,nativeMs=nativeMs/frames,
            rendererMs=rendererMs/frames,uiMs=uiMs/frames,networkMs=networkMs/frames,
            cpuPercent=cpu>=0&&nextCpu>=cpu?(nextCpu-cpu)*100/elapsed:-1,memoryMiB=Idas3ProcessMemoryMiB(),
            gpuMs=timedFrames>0?gpuMs/timedFrames:-1,mainThreadMs=timedFrames>0?mainMs/timedFrames:-1,
            renderThreadMs=timedFrames>0?renderThreadMs/timedFrames:-1,presentWaitMs=timedFrames>0?presentWaitMs/timedFrames:-1,
            refreshHz=Screen.currentResolution.refreshRateRatio.value,targetFps=Application.targetFrameRate,
            displayLinkPreferredFps=Idas3IosThermalFrameRate.PreferredFps,thermalFrameRateAdjustment=Idas3IosThermalFrameRate.Enabled,
            requestedFps=options.frameRateLimit,vSync=QualitySettings.vSyncCount,
            renderInterval=OnDemandRendering.renderFrameInterval,width=Screen.width,height=Screen.height,
            antialiasing=QualitySettings.antiAliasing,course=state.course,car=state.car,phase=state.racePhase,
            frontend=state.frontendStage,flags=state.flags,thermalState=Idas3ThermalState(),lowPowerMode=Idas3LowPowerMode(),
            simulationTicks=state.simulationTicks,nativeRenderedFrames=state.renderedFrames,
            gc0=GC.CollectionCount(0)-gc0,gc1=GC.CollectionCount(1)-gc1,gc2=GC.CollectionCount(2)-gc2,
            droppedLogs=writer.Dropped,meshes=renderer.ActiveMeshCount,uploadedVertices=renderer.UploadedVertexCount,
            geometryUploads=renderer.GeometryUploadCount,textures=renderer.UploadedTextureCount};
        writer.Enqueue(Idas3DiagnosticWriter.Channel.Performance,JsonUtility.ToJson(sample));
        lowFps=slow;LastError=writer.Error;ResetWindow();
    }
    private void StopWriter(){
        Application.logMessageReceivedThreaded-=OnLog;
        if(writer==null)return;
        writer.Dispose();writer=null;
    }
    private void OnApplicationPause(bool paused){if(Recording)writer.Enqueue(Idas3DiagnosticWriter.Channel.Runtime,DateTime.UtcNow.ToString("O")+" pause="+paused);}
    private void Shutdown(){try{StopWriter();}catch(Exception e){LastError=e.Message;}}
    private void OnApplicationQuit(){Shutdown();}
    private void OnDestroy(){Shutdown();}
}
