#import <Foundation/Foundation.h>

extern "C" int Idas3PreferredUiLanguage()
{
    // Honors device preferences and iOS's per-app language selection. Region
    // variants (including Hong Kong/Taiwan) are resolved by Foundation.
    NSArray<NSString *> *supported = @[@"en", @"ja", @"zh"];
    NSString *language = [NSBundle preferredLocalizationsFromArray:supported
                                                  forPreferences:NSLocale.preferredLanguages].firstObject;
    if ([language hasPrefix:@"ja"]) return 1;
    if ([language hasPrefix:@"zh"]) return 2;
    return 0;
}
