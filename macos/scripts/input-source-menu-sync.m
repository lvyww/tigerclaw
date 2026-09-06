#import <AppKit/AppKit.h>
#import <Foundation/Foundation.h>
#import <dlfcn.h>
#import <objc/message.h>

static id SendID(id target, SEL selector)
{
    return ((id (*)(id, SEL))objc_msgSend)(target, selector);
}

static id SendIDWithID(id target, SEL selector, id value)
{
    return ((id (*)(id, SEL, id))objc_msgSend)(target, selector, value);
}

static id SendIDWithTwoIDs(id target, SEL selector, id first, id second)
{
    return ((id (*)(id, SEL, id, id))objc_msgSend)(target, selector, first, second);
}

static BOOL SendBoolWithID(id target, SEL selector, id value)
{
    return ((BOOL (*)(id, SEL, id))objc_msgSend)(target, selector, value);
}

static void SendVoidWithBool(id target, SEL selector, BOOL value)
{
    ((void (*)(id, SEL, BOOL))objc_msgSend)(target, selector, value);
}

static id FindSettingsSource(Class inputSourceClass, NSString *modeIdentifier)
{
    NSArray *sources = SendID(inputSourceClass, NSSelectorFromString(@"inputSources"));
    for (id source in sources) {
        NSString *identifier = SendID(source, NSSelectorFromString(@"inputSourceID"));
        if ([identifier isEqualToString:modeIdentifier]) {
            return source;
        }
    }
    return nil;
}

static id OptionalProperty(id target, NSString *selectorName)
{
    SEL selector = NSSelectorFromString(selectorName);
    return target != nil && [target respondsToSelector:selector] ? SendID(target, selector) : nil;
}

static BOOL ListSettingsSources(NSString *filter)
{
    if (dlopen("/System/Library/PrivateFrameworks/IntlPreferences.framework/IntlPreferences", RTLD_NOW) == NULL) {
        return NO;
    }

    Class inputSourceClass = NSClassFromString(@"IPInputSource");
    NSArray *sources = SendID(inputSourceClass, NSSelectorFromString(@"inputSources"));
    for (id source in sources) {
        NSString *identifier = OptionalProperty(source, @"inputSourceID");
        NSString *description = [source description];
        if (filter.length > 0 &&
            [identifier rangeOfString:filter options:NSCaseInsensitiveSearch].location == NSNotFound &&
            [description rangeOfString:filter options:NSCaseInsensitiveSearch].location == NSNotFound) {
            continue;
        }
        id parent = OptionalProperty(source, @"parentInputSource");
        printf(
            "settings-source id=%s parent=%s localized=%s bundle=%s url=%s description=%s\n",
            identifier.UTF8String ?: "(nil)",
            [OptionalProperty(parent, @"inputSourceID") description].UTF8String ?: "(nil)",
            [OptionalProperty(source, @"localizedName") description].UTF8String ?: "(nil)",
            [OptionalProperty(source, @"bundleIdentifier") description].UTF8String ?: "(nil)",
            [OptionalProperty(source, @"bundleURL") description].UTF8String ?: "(nil)",
            description.UTF8String ?: "(nil)"
        );
    }
    return YES;
}

static BOOL RepairMenuRoster(NSString *bundleIdentifier, NSString *modeIdentifier)
{
    if (dlopen("/System/Library/PrivateFrameworks/IntlPreferences.framework/IntlPreferences", RTLD_NOW) == NULL) {
        return NO;
    }

    Class inputSourceClass = NSClassFromString(@"IPInputSource");
    id mode = FindSettingsSource(inputSourceClass, modeIdentifier);
    id parent = SendID(mode, NSSelectorFromString(@"parentInputSource"));
    if (inputSourceClass == Nil || mode == nil || parent == nil) {
        return NO;
    }
    NSString *parentIdentifier = SendID(parent, NSSelectorFromString(@"inputSourceID"));
    if (![parentIdentifier isEqualToString:bundleIdentifier]) {
        fprintf(
            stderr,
            "refusing mismatched input-source parent: expected=%s actual=%s mode=%s\n",
            bundleIdentifier.UTF8String,
            parentIdentifier.UTF8String,
            modeIdentifier.UTF8String
        );
        return NO;
    }

    // TISEnableInputSource alone can leave an input mode enabled in Carbon's
    // roster while TextInputMenuCore still omits it. Toggle through the same
    // model used by Keyboard Settings so both rosters are synchronized.
    SendVoidWithBool(mode, NSSelectorFromString(@"setEnabled:"), NO);
    SendVoidWithBool(parent, NSSelectorFromString(@"setEnabled:"), NO);

    mode = FindSettingsSource(inputSourceClass, modeIdentifier);
    parent = SendID(mode, NSSelectorFromString(@"parentInputSource"));
    if (mode == nil || parent == nil) {
        return NO;
    }
    parentIdentifier = SendID(parent, NSSelectorFromString(@"inputSourceID"));
    if (![parentIdentifier isEqualToString:bundleIdentifier]) {
        return NO;
    }
    SendVoidWithBool(mode, NSSelectorFromString(@"setEnabled:"), YES);
    SendVoidWithBool(parent, NSSelectorFromString(@"setEnabled:"), YES);
    return YES;
}

static BOOL VerifyMenuRoster(NSString *modeIdentifier)
{
    if (dlopen("/System/Library/PrivateFrameworks/TextInputMenuUI.framework/TextInputMenuUI", RTLD_NOW) == NULL) {
        return NO;
    }
    NSBundle *menuCore = [NSBundle bundleWithPath:@"/System/Library/CoreServices/TextInputMenuCore.bundle"];
    if (![menuCore load]) {
        return NO;
    }

    Class inputSourceClass = NSClassFromString(@"InputSource");
    NSArray *sources = SendIDWithID(
        inputSourceClass,
        NSSelectorFromString(@"inputSourceArrayForOwner:"),
        nil
    );
    for (id source in sources) {
        NSString *identifier = SendID(source, NSSelectorFromString(@"uniqueIdentifier"));
        NSString *title = SendID(source, NSSelectorFromString(@"menuTitleName"));
        printf("menu-item=%s\t%s\n", identifier.UTF8String, title.UTF8String);
        if ([identifier isEqualToString:modeIdentifier]) {
            return YES;
        }
    }
    return NO;
}

static BOOL ActivateMenuSource(NSString *modeIdentifier)
{
    if (dlopen("/System/Library/PrivateFrameworks/TextInputMenuUI.framework/TextInputMenuUI", RTLD_NOW) == NULL) {
        return NO;
    }
    NSBundle *menuCore = [NSBundle bundleWithPath:@"/System/Library/CoreServices/TextInputMenuCore.bundle"];
    if (![menuCore load]) {
        return NO;
    }

    Class inputSourceClass = NSClassFromString(@"InputSource");
    id owner = SendID(inputSourceClass, NSSelectorFromString(@"currentInputSourceOwner"));
    id source = SendIDWithTwoIDs(
        inputSourceClass,
        NSSelectorFromString(@"inputSourceWithInputSourceID:andOwner:"),
        modeIdentifier,
        owner
    );
    if (source == nil || ![source respondsToSelector:NSSelectorFromString(@"activateForcibly:")]) {
        return NO;
    }

    BOOL activated = SendBoolWithID(source, NSSelectorFromString(@"activateForcibly:"), owner);
    BOOL current = [source respondsToSelector:NSSelectorFromString(@"isCurrentSource")]
        ? ((BOOL (*)(id, SEL))objc_msgSend)(source, NSSelectorFromString(@"isCurrentSource"))
        : NO;
    printf("menu-source-activated=%s current=%s\n", activated ? "true" : "false", current ? "true" : "false");
    return activated || current;
}

int main(int argc, const char *argv[])
{
    @autoreleasepool {
        if (argc < 3 || argc > 4) {
            fputs("usage: input-source-menu-sync <repair|verify|activate> <mode-id> [bundle-id]\n", stderr);
            return 64;
        }

        NSString *operation = [NSString stringWithUTF8String:argv[1]];
        NSString *modeIdentifier = [NSString stringWithUTF8String:argv[2]];
        if ([operation isEqualToString:@"list"]) {
            return ListSettingsSources(modeIdentifier) ? 0 : 1;
        }
        if ([operation isEqualToString:@"repair"]) {
            if (argc != 4) {
                fputs("repair requires the expected bundle id\n", stderr);
                return 64;
            }
            NSString *bundleIdentifier = [NSString stringWithUTF8String:argv[3]];
            BOOL repaired = RepairMenuRoster(bundleIdentifier, modeIdentifier);
            printf("menu-roster-repair=%s\n", repaired ? "ok" : "failed");
            return repaired ? 0 : 1;
        }
        if ([operation isEqualToString:@"verify"]) {
            BOOL visible = VerifyMenuRoster(modeIdentifier);
            printf("menu-roster-visible=%s\n", visible ? "true" : "false");
            return visible ? 0 : 1;
        }
        if ([operation isEqualToString:@"activate"]) {
            BOOL activated = ActivateMenuSource(modeIdentifier);
            return activated ? 0 : 1;
        }

        fputs("unknown operation\n", stderr);
        return 64;
    }
}
