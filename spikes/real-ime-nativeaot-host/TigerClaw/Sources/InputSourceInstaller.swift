import Carbon
import Foundation

enum InputSourceInstaller {
    private static let stableBundleIdentifier = "net.tigerclaw.inputmethod.NativeAotImkSpike"
    private static let stableModeIdentifier = "net.tigerclaw.inputmethod.NativeAotImkSpike.Hans"

    private enum Phase: String {
        case register
        case enableParent
        case enableMode
        case select
        case status
        case repair
    }

    static func runIfRequested(arguments: [String]) -> Int32? {
        let values = Array(arguments.dropFirst())
        guard values.first == "--tigerclaw-tis-phase" else { return nil }
        guard values.count == 2,
              let phase = Phase(rawValue: values[1]),
              let bundleIdentifier = Bundle.main.bundleIdentifier,
              let modeIdentifier = modeIdentifier(bundleIdentifier: bundleIdentifier),
              bundleIdentifier == stableBundleIdentifier,
              modeIdentifier == stableModeIdentifier else {
            print("invalid TIS diagnostic invocation")
            return 64
        }

        switch phase {
        case .register:
            return report("register", TISRegisterInputSource(Bundle.main.bundleURL as CFURL))
        case .enableParent:
            guard let parent = source(bundleIdentifier, includeAllInstalled: true) else { return 1 }
            return report("enable-parent", TISEnableInputSource(parent))
        case .enableMode:
            guard source(bundleIdentifier, includeAllInstalled: false) != nil,
                  let mode = source(modeIdentifier, includeAllInstalled: true) else { return 1 }
            return report("enable-mode", TISEnableInputSource(mode))
        case .select:
            guard let mode = source(modeIdentifier, includeAllInstalled: false) else { return 1 }
            return report("select", TISSelectInputSource(mode))
        case .status:
            return reportStatus(bundleIdentifier: bundleIdentifier, modeIdentifier: modeIdentifier)
        case .repair:
            return repair(bundleIdentifier: bundleIdentifier, modeIdentifier: modeIdentifier)
        }
    }

    static func repairIfNeeded() {
        guard let bundleIdentifier = Bundle.main.bundleIdentifier,
              let modeIdentifier = modeIdentifier(bundleIdentifier: bundleIdentifier),
              bundleIdentifier == stableBundleIdentifier,
              modeIdentifier == stableModeIdentifier,
              reportStatus(bundleIdentifier: bundleIdentifier, modeIdentifier: modeIdentifier, printResult: false) != 0 else {
            return
        }
        _ = repair(bundleIdentifier: bundleIdentifier, modeIdentifier: modeIdentifier)
    }

    private static func repair(bundleIdentifier: String, modeIdentifier: String) -> Int32 {
        if source(bundleIdentifier, includeAllInstalled: true) == nil ||
            source(modeIdentifier, includeAllInstalled: true) == nil {
            let registerStatus = TISRegisterInputSource(Bundle.main.bundleURL as CFURL)
            guard registerStatus == noErr else { return report("repair-register", registerStatus) }
        }

        guard let parent = source(bundleIdentifier, includeAllInstalled: true) else {
            print("repair-parent=missing")
            return 1
        }
        let parentStatus = TISEnableInputSource(parent)
        guard parentStatus == noErr else { return report("repair-enable-parent", parentStatus) }

        guard let mode = source(modeIdentifier, includeAllInstalled: true) else {
            print("repair-mode=missing")
            return 1
        }
        let modeStatus = TISEnableInputSource(mode)
        guard modeStatus == noErr else { return report("repair-enable-mode", modeStatus) }

        let status = reportStatus(bundleIdentifier: bundleIdentifier, modeIdentifier: modeIdentifier)
        print("repair=\(status == 0 ? "ok" : "failed")")
        return status
    }

    private static func reportStatus(
        bundleIdentifier: String,
        modeIdentifier: String,
        printResult: Bool = true
    ) -> Int32 {
        let parentInstalled = source(bundleIdentifier, includeAllInstalled: true) != nil
        let parentEnabled = source(bundleIdentifier, includeAllInstalled: false) != nil
        let modeInstalled = source(modeIdentifier, includeAllInstalled: true) != nil
        let modeEnabled = source(modeIdentifier, includeAllInstalled: false) != nil
        let selectedIdentifier = currentInputSourceIdentifier()
        if printResult {
            print("parent-installed=\(parentInstalled)")
            print("parent-enabled=\(parentEnabled)")
            print("mode-installed=\(modeInstalled)")
            print("mode-enabled=\(modeEnabled)")
            print("mode-selected=\(selectedIdentifier == modeIdentifier)")
            print("selected-source=\(selectedIdentifier ?? "")")
        }
        return parentInstalled && parentEnabled && modeInstalled && modeEnabled ? 0 : 1
    }

    private static func report(_ name: String, _ status: OSStatus) -> Int32 {
        print("\(name)=\(status)")
        return status == noErr ? 0 : 1
    }

    private static func modeIdentifier(bundleIdentifier: String) -> String? {
        guard let component = Bundle.main.object(forInfoDictionaryKey: "ComponentInputModeDict") as? [String: Any],
              let modes = component["tsVisibleInputModeOrderedArrayKey"] as? [String],
              modes.count == 1,
              let mode = modes.first,
              mode.hasPrefix(bundleIdentifier + ".") else { return nil }
        return mode
    }

    private static func source(_ identifier: String, includeAllInstalled: Bool) -> TISInputSource? {
        guard let unmanaged = TISCreateInputSourceList(nil, includeAllInstalled) else { return nil }
        let sources = unmanaged.takeRetainedValue()
        for index in 0 ..< CFArrayGetCount(sources) {
            let source = unsafeBitCast(CFArrayGetValueAtIndex(sources, index), to: TISInputSource.self)
            if stringProperty(source, kTISPropertyInputSourceID) == identifier { return source }
        }
        return nil
    }

    private static func stringProperty(_ source: TISInputSource, _ key: CFString) -> String? {
        guard let pointer = TISGetInputSourceProperty(source, key) else { return nil }
        return Unmanaged<CFString>.fromOpaque(pointer).takeUnretainedValue() as String
    }

    private static func currentInputSourceIdentifier() -> String? {
        guard let unmanaged = TISCopyCurrentKeyboardInputSource() else { return nil }
        return stringProperty(unmanaged.takeRetainedValue(), kTISPropertyInputSourceID)
    }
}
