using System;
using UnityEngine;

// Count completed main-camera frames once; both frame-rate displays read this
// exact sample. Manual diagnostic renders and mirror cameras cannot double it.
[RequireComponent(typeof(Camera))]
public sealed class Idas3FrameStatistics : MonoBehaviour
{
    private double began;
    private int frames,lastFrame=-1;
    private bool sampling;
    internal double FramesPerSecond {get;private set;}
    internal int RoundedFps=> (int)Math.Round(FramesPerSecond);
    internal string DisplayText=>FramesPerSecond>0?RoundedFps+" FPS  /  "+(1000/FramesPerSecond).ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+" ms":"-- FPS";
    private void OnPostRender(){
        if(!Application.isFocused){sampling=false;return;}
        if(lastFrame==Time.frameCount)return;
        lastFrame=Time.frameCount;
        double now=Time.realtimeSinceStartupAsDouble;
        if(!sampling){sampling=true;began=now;frames=0;return;}
        ++frames;
        double elapsed=now-began;
        if(elapsed<.5)return;
        FramesPerSecond=frames/elapsed;began=now;frames=0;
    }
    private void OnApplicationFocus(bool focused){if(!focused){sampling=false;FramesPerSecond=0;}}
}
