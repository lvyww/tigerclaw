import Carbon
import Foundation

enum TigerClawInputSourceInstaller {
    private enum Phase: String {
        case register
        case enableParent
        case enableMode
        case select
        case dump
    }

    static func runIfRequested(arguments: [String]) -> Int32? {
        let values = Array(arguments.dropFirst())
        guard values.first == "--tigerclaw-tis-phase" else { return nil }
        guard values.count == 2,
              let phase = Phase(rawValue: values[1]),
              let bundleIdentifier = Bundle.main.bundleIdentifier,
              let modeIdentifier = modeIdentifier(bundleIdentifier: bundleIdentifier) else {
            print("invalid TigerClaw TIS diagnostic invocation")
            return 64
        }

        switch phase {
        case .register:
            return report("register", TISRegisterInputSource(Bundle.main.bundleURL as CFURL))
        case .enableParent:
            guard let parent = source(bundleIdentifier, includeAllInstalled: true) else {
                print("TigerClaw parent source is not installed")
                return 1
            }
            return report("enable-parent", TISEnableInputSource(parent))
        case .enableMode:
            guard source(bundleIdentifier, includeAllInstalled: false) != nil else {
                print("TigerClaw parent source is not in the enabled roster")
                return 1
            }
            guard let mode = source(modeIdentifier, includeAllInstalled: true) else {
                print("TigerClaw input mode is not installed")
                return 1
            }
            return report("enable-mode", TISEnableInputSource(mode))
        case .select:
            guard let mode = source(modeIdentifier, includeAllInstalled: true) else {
                print("TigerClaw input mode is not installed")
                return 1
            }
            return report("select", TISSelectInputSource(mode))
        case .dump:
            dumpSources(matching: "TigerClaw")
            return 0
        }
    }

    private static func report(_ operation: String, _ status: OSStatus) -> Int32 {
        print("\(operation)=\(status)")
        return status == noErr ? 0 : 1
    }

    private static func modeIdentifier(bundleIdentifier: String) -> String? {
        guard let component = Bundle.main.object(forInfoDictionaryKey: "ComponentInputModeDict") as? [String: Any],
              let visibleModes = component["tsVisibleInputModeOrderedArrayKey"] as? [String],
              visibleModes.count == 1,
              let modeIdentifier = visibleModes.first,
              modeIdentifier.hasPrefix(bundleIdentifier + ".") else {
            return nil
        }
        return modeIdentifier
    }

    private static func source(_ identifier: String, includeAllInstalled: Bool) -> TISInputSource? {
        guard let unmanaged = TISCreateInputSourceList(nil, includeAllInstalled) else {
            return nil
        }
        let sources = unmanaged.takeRetainedValue()
        for index in 0 ..< CFArrayGetCount(sources) {
            let source = unsafeBitCast(CFArrayGetValueAtIndex(sources, index), to: TISInputSource.self)
            if stringProperty(source, kTISPropertyInputSourceID) == identifier {
                return source
            }
        }
        return nil
    }

    private static func stringProperty(_ source: TISInputSource, _ key: CFString) -> String? {
        guard let pointer = TISGetInputSourceProperty(source, key) else { return nil }
        return Unmanaged<CFString>.fromOpaque(pointer).takeUnretainedValue() as String
    }

    private static func boolProperty(_ source: TISInputSource, _ key: CFString) -> Bool? {
        guard let pointer = TISGetInputSourceProperty(source, key) else { return nil }
        return CFBooleanGetValue(Unmanaged<CFBoolean>.fromOpaque(pointer).takeUnretainedValue())
    }

    private static func dumpSources(matching text: String) {
        guard let unmanaged = TISCreateInputSourceList(nil, true) else {
            print("dump=unavailable")
            return
        }
        let sources = unmanaged.takeRetainedValue()
        for index in 0 ..< CFArrayGetCount(sources) {
            let source = unsafeBitCast(CFArrayGetValueAtIndex(sources, index), to: TISInputSource.self)
            guard let identifier = stringProperty(source, kTISPropertyInputSourceID),
                  identifier.localizedCaseInsensitiveContains(text) else {
                continue
            }
            let enabled = boolProperty(source, kTISPropertyInputSourceIsEnabled).map(String.init) ?? "unknown"
            let selected = boolProperty(source, kTISPropertyInputSourceIsSelected).map(String.init) ?? "unknown"
            let name = stringProperty(source, kTISPropertyLocalizedName) ?? "(unnamed)"
            print("id=\(identifier) enabled=\(enabled) selected=\(selected) name=\(name)")
        }
    }
}
