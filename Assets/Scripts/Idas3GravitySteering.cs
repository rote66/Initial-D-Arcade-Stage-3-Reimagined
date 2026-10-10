using System.Runtime.InteropServices;
using UnityEngine;

// Matches the supplied ARMSX2 GravityLeftStickController: fixed horizontal
// reference, 0.35 g full scale, 0.05 deadzone, 0.28 smoothing retention at 60 Hz.
// Its screen mapping uses UIKit interface orientation, not Unity device axes.
internal sealed class Idas3GravitySteering
{
    internal float Axis {get;private set;}
    private int lastOrientation;
    private double lastSample;
    private bool running,requested;
#if UNITY_IOS && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern int Idas3MotionEnable(int enabled);
    [DllImport("__Internal")] private static extern int Idas3MotionRead(out double x,out double y,out double timestamp,out int orientation);
#endif
    internal void Tick(bool enabled,float deadzone,float sensitivity){
        if(enabled!=requested){
            requested=enabled;
            Reset();
#if UNITY_IOS && !UNITY_EDITOR
            running=Idas3MotionEnable(enabled?1:0)!=0;
#else
            running=enabled;
#endif
        }
        if(!running)return;
#if UNITY_IOS && !UNITY_EDITOR
        if(Idas3MotionRead(out double x,out double y,out double timestamp,out int orientation)!=0&&timestamp!=lastSample){
            lastSample=timestamp;Sample(x,y,orientation,deadzone,sensitivity);
        }
#endif
    }
    internal void Stop(){
#if UNITY_IOS && !UNITY_EDITOR
        Idas3MotionEnable(0);
#endif
        running=requested=false;Reset();
    }
    internal void Reset(){Axis=0;lastOrientation=0;lastSample=0;}
    internal static float Deadzone(float value)=>float.IsNaN(value)||float.IsInfinity(value) ? .05f : Mathf.Clamp(value,0,.25f);
    internal static float Sensitivity(float value)=>float.IsNaN(value)||float.IsInfinity(value) ? 1 : Mathf.Clamp(value,.25f,2);
    internal float Sample(double x,double y,int orientation,float deadzone=.05f,float sensitivity=1){
        if(lastOrientation!=orientation){lastOrientation=orientation;Axis=0;}
        // UIInterfaceOrientation (not UIDeviceOrientation): left=4, right=3.
        double screenX=orientation==4?y:orientation==3?-y:orientation==2?-x:x;
        if(double.IsNaN(screenX)||double.IsInfinity(screenX)){Axis=0;return Axis;}
        deadzone=Deadzone(deadzone);sensitivity=Sensitivity(sensitivity);
        double tilt=screenX/(.35/sensitivity),magnitude=System.Math.Abs(tilt);
        float target=magnitude<=deadzone?0:(float)(System.Math.Min((magnitude-deadzone)/(1-deadzone),1)*(tilt<0?-1:1));
        Axis+=(target-Axis)*.72f;return Axis;
    }
}
