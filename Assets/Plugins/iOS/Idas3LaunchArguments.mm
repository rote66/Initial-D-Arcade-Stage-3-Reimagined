#import <Foundation/Foundation.h>
#include <cstring>

// IL2CPP's managed Environment.GetCommandLineArgs is empty on iOS.
// Read the actual process arguments for explicitly requested diagnostics.
extern "C" int Idas3CopyProcessArgument(int index, unsigned char* output, int capacity)
{
    @autoreleasepool {
        NSArray<NSString*>* arguments = NSProcessInfo.processInfo.arguments;
        if (index < 0 || index >= (int)arguments.count) return -1;
        const char* text = arguments[index].UTF8String;
        const int length = (int)std::strlen(text);
        if (output != nullptr && capacity > length) std::memcpy(output, text, length + 1);
        return length;
    }
}
