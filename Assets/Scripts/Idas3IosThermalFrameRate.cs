using System.Runtime.InteropServices;
using UnityEngine;

internal static class Idas3IosThermalFrameRate
{
#if UNITY_IOS && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern void Idas3SetThermalFrameRateAdjustment(int enabled);
    [DllImport("__Internal")] private static extern int Idas3ThermalFrameRateAdjustmentEnabled();
    [DllImport("__Internal")] private static extern double Idas3DisplayLinkPreferredFps();
    [DllImport("__Internal")] private static extern int Idas3SetDiagnosticThermalState(int state);
    internal static bool Enabled=>Idas3ThermalFrameRateAdjustmentEnabled()!=0;
    internal static double PreferredFps=>Idas3DisplayLinkPreferredFps();
    internal static bool DiagnosticThermalState(int state)=>Idas3SetDiagnosticThermalState(state)!=0;
    internal static void Configure(bool enabled){
        bool changed=Enabled!=enabled;
        Idas3SetThermalFrameRateAdjustment(enabled?1:0);
        if(changed)Debug.Log("iOS thermal frame rate adjustment: "+(enabled?"ON":"OFF"));
    }
#else
    internal static bool Enabled=>true;
    internal static double PreferredFps=>-1;
    internal static bool DiagnosticThermalState(int state)=>false;
    internal static void Configure(bool enabled){}
#endif
}
