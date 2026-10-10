#import <CoreMotion/CoreMotion.h>
#import <UIKit/UIKit.h>

static CMMotionManager *idas3Motion;
static int Idas3InterfaceOrientation() {
    for (UIScene *scene in UIApplication.sharedApplication.connectedScenes)
        if ([scene isKindOfClass:UIWindowScene.class] && scene.activationState == UISceneActivationStateForegroundActive)
            return (int)((UIWindowScene *)scene).interfaceOrientation;
    return (int)UIInterfaceOrientationLandscapeRight;
}
extern "C" int Idas3MotionEnable(int enabled) {
    if (!enabled) { [idas3Motion stopDeviceMotionUpdates]; return 0; }
    if (!idas3Motion) { idas3Motion = [[CMMotionManager alloc] init]; idas3Motion.deviceMotionUpdateInterval = 1.0/60.0; }
    if (!idas3Motion.deviceMotionAvailable) return 0;
    if (!idas3Motion.deviceMotionActive) [idas3Motion startDeviceMotionUpdates];
    return 1;
}
extern "C" int Idas3MotionRead(double *x, double *y, double *timestamp, int *orientation) {
    CMDeviceMotion *sample = idas3Motion.deviceMotion;
    if (!sample || !idas3Motion.deviceMotionActive) return 0;
    *x = sample.gravity.x; *y = sample.gravity.y; *timestamp = sample.timestamp;
    *orientation = Idas3InterfaceOrientation(); return 1;
}
