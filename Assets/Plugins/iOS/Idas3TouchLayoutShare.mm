#import <UIKit/UIKit.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#include "UnityInterface.h"

static UIViewController *Idas3TouchLayoutPresenter() {
    UIViewController *presenter = nil;
    for (UIScene *scene in UIApplication.sharedApplication.connectedScenes) {
        if (![scene isKindOfClass:UIWindowScene.class] || scene.activationState != UISceneActivationStateForegroundActive) continue;
        for (UIWindow *window in ((UIWindowScene *)scene).windows) if (window.isKeyWindow) presenter = window.rootViewController;
    }
    while (presenter.presentedViewController) presenter = presenter.presentedViewController;
    return presenter;
}

@interface Idas3TouchLayoutPicker : NSObject <UIDocumentPickerDelegate>
@property(nonatomic,copy) NSString *receiver;
@property(nonatomic,strong) NSURL *selectedURL;
@end
@implementation Idas3TouchLayoutPicker
- (void)documentPicker:(UIDocumentPickerViewController *)controller didPickDocumentsAtURLs:(NSArray<NSURL *> *)urls {
    self.selectedURL = urls.firstObject;
    [self.selectedURL startAccessingSecurityScopedResource];
    UnitySendMessage(self.receiver.UTF8String, "SelectedTouchLayout", self.selectedURL.fileSystemRepresentation ?: "");
}
- (void)documentPickerWasCancelled:(UIDocumentPickerViewController *)controller {
    UnitySendMessage(self.receiver.UTF8String, "SelectedTouchLayout", "");
}
@end

static Idas3TouchLayoutPicker *idas3TouchLayoutPicker;
extern "C" void Idas3PickTouchLayout(const char *receiver) {
    NSString *target = [NSString stringWithUTF8String:receiver];
    if (!target) return;
    dispatch_async(dispatch_get_main_queue(), ^{
        UIViewController *presenter = Idas3TouchLayoutPresenter();
        if (!presenter) { UnitySendMessage(target.UTF8String, "SelectedTouchLayout", ""); return; }
        if (!idas3TouchLayoutPicker) idas3TouchLayoutPicker = [Idas3TouchLayoutPicker new];
        idas3TouchLayoutPicker.receiver = target;
        UIDocumentPickerViewController *picker = [[UIDocumentPickerViewController alloc] initForOpeningContentTypes:@[UTTypeJSON] asCopy:YES];
        picker.delegate = idas3TouchLayoutPicker;
        picker.allowsMultipleSelection = NO;
        [presenter presentViewController:picker animated:YES completion:nil];
    });
}
extern "C" void Idas3ReleaseTouchLayout() {
    NSURL *url = idas3TouchLayoutPicker.selectedURL;
    [url stopAccessingSecurityScopedResource];
    // Delete only the app-owned picker copy, never the user's exported source.
    NSString *path = url.path.stringByStandardizingPath;
    NSString *documents = NSSearchPathForDirectoriesInDomains(NSDocumentDirectory, NSUserDomainMask, YES).firstObject;
    NSString *inbox = [documents stringByAppendingPathComponent:@"Inbox/"];
    NSString *temporary = NSTemporaryDirectory().stringByStandardizingPath;
    if (url && ([path hasPrefix:inbox] || [path hasPrefix:[temporary stringByAppendingString:@"/"]]))
        [NSFileManager.defaultManager removeItemAtURL:url error:nil];
    idas3TouchLayoutPicker.selectedURL = nil;
}

extern "C" int Idas3ShareTouchLayout(const char *path) {
    NSString *file = [NSString stringWithUTF8String:path];
    if (!file || ![NSFileManager.defaultManager fileExistsAtPath:file]) return 0;
    dispatch_async(dispatch_get_main_queue(), ^{
        UIViewController *presenter = Idas3TouchLayoutPresenter();
        if (!presenter) return;
        UIActivityViewController *share = [[UIActivityViewController alloc] initWithActivityItems:@[[NSURL fileURLWithPath:file]] applicationActivities:nil];
        if (share.popoverPresentationController) {
            share.popoverPresentationController.sourceView = presenter.view;
            share.popoverPresentationController.sourceRect = CGRectMake(CGRectGetMidX(presenter.view.bounds), CGRectGetMidY(presenter.view.bounds), 1, 1);
            share.popoverPresentationController.permittedArrowDirections = 0;
        }
        [presenter presentViewController:share animated:YES completion:nil];
    });
    return 1;
}
