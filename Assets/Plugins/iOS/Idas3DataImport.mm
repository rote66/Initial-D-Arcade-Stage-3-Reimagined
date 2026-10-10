#import <UIKit/UIKit.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#include "UnityInterface.h"

@interface Idas3DataPicker : NSObject <UIDocumentPickerDelegate>
@property(nonatomic,strong) NSURL *selectedURL;
@end
@implementation Idas3DataPicker
- (void)documentPicker:(UIDocumentPickerViewController *)controller didPickDocumentsAtURLs:(NSArray<NSURL *> *)urls {
    self.selectedURL = urls.firstObject;
    [self.selectedURL startAccessingSecurityScopedResource];
    UnitySendMessage("IDAS3 game data", "SelectedArchive", self.selectedURL.fileSystemRepresentation ?: "");
}
- (void)documentPickerWasCancelled:(UIDocumentPickerViewController *)controller {
    UnitySendMessage("IDAS3 game data", "SelectedArchive", "");
}
@end

static Idas3DataPicker *idas3DataPicker;
extern "C" void Idas3PickDataArchive() {
    dispatch_async(dispatch_get_main_queue(), ^{
        UIViewController *presenter = nil;
        for (UIScene *scene in UIApplication.sharedApplication.connectedScenes)
            if ([scene isKindOfClass:UIWindowScene.class] && scene.activationState == UISceneActivationStateForegroundActive)
                for (UIWindow *window in ((UIWindowScene *)scene).windows) if (window.isKeyWindow) presenter = window.rootViewController;
        while (presenter.presentedViewController) presenter = presenter.presentedViewController;
        if (!presenter) { UnitySendMessage("IDAS3 game data", "SelectedArchive", ""); return; }
        if (!idas3DataPicker) idas3DataPicker = [Idas3DataPicker new];
        UIDocumentPickerViewController *picker = [[UIDocumentPickerViewController alloc] initForOpeningContentTypes:@[UTTypeZIP] asCopy:YES];
        picker.delegate = idas3DataPicker; picker.allowsMultipleSelection = NO;
        [presenter presentViewController:picker animated:YES completion:nil];
    });
}
extern "C" void Idas3ReleaseDataArchive() {
    NSURL *url = idas3DataPicker.selectedURL; [url stopAccessingSecurityScopedResource];
    // asCopy:YES gives an app-owned copy; remove only an Inbox/tmp copy.
    NSString *path = url.path;
    if ([path hasPrefix:NSHomeDirectory()] && ([path containsString:@"/Inbox/"] || [path containsString:@"/tmp/"]))
        [NSFileManager.defaultManager removeItemAtURL:url error:nil];
    idas3DataPicker.selectedURL = nil;
}
