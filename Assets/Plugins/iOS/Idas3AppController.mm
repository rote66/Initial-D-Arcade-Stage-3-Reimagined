#include "UnityAppController.h"
#include "UnityAppController+Thermal.h"
#include "UnityInterface.h"
#include <UnityRuntime/UnityInternalInterface.h>
#include <TargetConditionals.h>

static bool thermalAdjustmentEnabled = true;
#if TARGET_OS_SIMULATOR
static int diagnosticThermalState = -1;
#endif

@interface Idas3AppController : UnityAppController
@end

@implementation Idas3AppController
- (int)adjustFrameRateForThermalState:(int)targetFPS
{
    if (!thermalAdjustmentEnabled) return targetFPS;
#if TARGET_OS_SIMULATOR
    // Exercise the real display-link update without spoofing a device's sensors.
    if (diagnosticThermalState >= 0)
    {
        if (diagnosticThermalState == NSProcessInfoThermalStateSerious)
            return MIN(targetFPS, UnityGetThermalStateSeriousFPS());
        if (diagnosticThermalState == NSProcessInfoThermalStateCritical)
            return MIN(targetFPS, UnityGetThermalStateCriticalFPS());
        return targetFPS;
    }
#endif
    return [super adjustFrameRateForThermalState:targetFPS];
}
@end

IMPL_APP_CONTROLLER_SUBCLASS(Idas3AppController)

static void OnMainThread(dispatch_block_t work)
{
    if (NSThread.isMainThread) work();
    else dispatch_sync(dispatch_get_main_queue(), work);
}

extern "C" void Idas3SetThermalFrameRateAdjustment(int enabled)
{
    OnMainThread(^{
        thermalAdjustmentEnabled = enabled != 0;
        UnityFramerateChangeCallback(-1);
    });
}

extern "C" int Idas3ThermalFrameRateAdjustmentEnabled()
{
    __block int enabled;
    OnMainThread(^{ enabled = thermalAdjustmentEnabled ? 1 : 0; });
    return enabled;
}

extern "C" double Idas3DisplayLinkPreferredFps()
{
    __block double fps = -1;
    OnMainThread(^{
        UnityAppController* app = GetAppController();
#if UNITY_USES_METAL_DISPLAY_LINK
        if (@available(iOS 17.0, *))
        {
            if (app.unityUsesMetalDisplayLink)
            {
                fps = app.unityMetalDisplayLink.preferredFrameRateRange.preferred;
                return;
            }
        }
#endif
        if (app.unityDisplayLink)
            fps = app.unityDisplayLink.preferredFrameRateRange.preferred;
    });
    return fps;
}

extern "C" int Idas3SetDiagnosticThermalState(int state)
{
#if TARGET_OS_SIMULATOR
    if (state < -1 || state > NSProcessInfoThermalStateCritical) return 0;
    OnMainThread(^{
        diagnosticThermalState = state;
        UnityFramerateChangeCallback(-1);
    });
    return 1;
#else
    return 0;
#endif
}
