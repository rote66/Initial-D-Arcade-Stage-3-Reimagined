using System;
using System.Runtime.InteropServices;
using UnityEngine;

// NFS3's four-line, non-interactive upper-left overlay. CPU/memory are process
// measurements; GPU comes from Unity's measured frame timings, never FPS guesses.
public sealed class Idas3PerformanceOsd : MonoBehaviour
{
    private Idas3GameOptions options;
    private readonly FrameTiming[] timing=new FrameTiming[1];
    private double began,cpu,gpuMilliseconds;
    private Idas3FrameStatistics frameStatistics;
    private ulong lastGpuTimestamp;
    private bool sampling,gpuAvailable;
    private GUIStyle style;
    private string measurements="CPU --\nGPU --\nRAM --";
    internal string Text=>"FPS "+(frameStatistics!=null&&frameStatistics.FramesPerSecond>0?frameStatistics.RoundedFps.ToString():"--")+"\n"+measurements;
    internal bool Visible=>options?.Current.showOsd==true;
    internal Rect DisplayRect {get;private set;}
    internal float LineHeight {get;private set;}
    public void Initialize(Idas3GameOptions owner){options=owner;frameStatistics=GetComponent<Idas3FrameStatistics>();}
#if UNITY_IOS && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern double Idas3ProcessCpuSeconds();
    [DllImport("__Internal")] private static extern double Idas3ProcessMemoryMiB();
#else
    private static double Idas3ProcessCpuSeconds()=>-1;
    private static double Idas3ProcessMemoryMiB()=>-1;
#endif
    private void LateUpdate(){
        if(!Visible||!Application.isFocused){sampling=false;return;}
        double now=Time.realtimeSinceStartupAsDouble;
        if(!sampling){began=now;cpu=Idas3ProcessCpuSeconds();gpuMilliseconds=0;gpuAvailable=false;lastGpuTimestamp=0;sampling=true;}
        if(FrameTimingManager.IsFeatureEnabled()){
            FrameTimingManager.CaptureFrameTimings();
            if(FrameTimingManager.GetLatestTimings(1,timing)>0&&timing[0].frameStartTimestamp!=lastGpuTimestamp){
                lastGpuTimestamp=timing[0].frameStartTimestamp;
                double ms=timing[0].gpuFrameTime;
                if(ms>0&&!double.IsNaN(ms)&&!double.IsInfinity(ms)){gpuMilliseconds+=ms;gpuAvailable=true;}
            }
        }
        double elapsed=now-began;
        if(elapsed<.5)return;
        double nextCpu=Idas3ProcessCpuSeconds(),memory=Idas3ProcessMemoryMiB();
        string cpuText=cpu>=0&&nextCpu>=cpu?"CPU "+Math.Round((nextCpu-cpu)*100/elapsed)+"%":"CPU --";
        string gpuText=gpuAvailable?"GPU "+Math.Round(Math.Min(100,gpuMilliseconds/(elapsed*10)))+"%":"GPU --";
        measurements=cpuText+"\n"+gpuText+"\n"+(memory>=0?"RAM "+Math.Round(memory)+" MiB":"RAM --");
        began=now;cpu=nextCpu;gpuMilliseconds=0;gpuAvailable=false;
    }
    private void OnGUI(){
        if(!Visible||GetComponent<Idas3PauseMenu>()?.TouchLayoutEditing==true)return;
        var safe=Screen.safeArea;float unit=Mathf.Clamp(Mathf.Min(safe.width/1280f,safe.height/720f),.45f,3f);
        if(style==null)style=new GUIStyle(GUI.skin.label){padding=new RectOffset(0,0,0,0),fontStyle=FontStyle.Bold};
        style.fontSize=Mathf.RoundToInt(18*unit);
        var matrix=GUI.matrix;var color=GUI.color;GUI.matrix=Matrix4x4.identity;GUI.depth=-21000;
        var lines=Array.ConvertAll(Text.Split('\n'),Idas3MenuLocalization.T);
        var localizedStyle=Idas3MenuLocalization.Style(style);
        float width=0,height=0;
        foreach(string line in lines){var size=localizedStyle.CalcSize(new GUIContent(line));width=Mathf.Max(width,size.x);height=Mathf.Max(height,size.y);}
        // A CJK font has a taller line box than the English font. Measure the
        // font actually being drawn and give every line its complete box.
        LineHeight=Mathf.Ceil(height+2*unit);
        DisplayRect=new Rect(safe.x+12*unit,Screen.height-safe.yMax+8*unit,Mathf.Ceil(width+4*unit),LineHeight*lines.Length);
        GUI.color=Color.white;
        for(int i=0;i<lines.Length;++i){
            var rect=new Rect(DisplayRect.x,DisplayRect.y+i*LineHeight,DisplayRect.width,LineHeight);
            localizedStyle.normal.textColor=new Color(0,0,0,.6f);
            GUI.Label(new Rect(rect.x+unit,rect.y+unit,rect.width,rect.height),lines[i],localizedStyle);
            localizedStyle.normal.textColor=new Color(1,1,1,.7f);GUI.Label(rect,lines[i],localizedStyle);
        }
        GUI.matrix=matrix;GUI.color=color;
    }
}
