import AppKit
import Carbon
import Foundation
import InputMethodKit

@main
enum TigerClawHybridSpikeMain {
    static func main() {
        if let status = runInputSourceCommand(arguments: CommandLine.arguments) {
            exit(status)
        }
        _ = NSApplicationMain(CommandLine.argc, CommandLine.unsafeArgv)
    }
}

@objc(TigerClawHybridSpikeApplication)
final class TigerClawHybridSpikeApplication: NSApplication {
    private lazy var inputMethodDelegate = TigerClawHybridSpikeDelegate()

    override init() {
        super.init()
        delegate = inputMethodDelegate
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        delegate = inputMethodDelegate
    }
}

final class TigerClawHybridSpikeDelegate: NSObject, NSApplicationDelegate {
    private var server: IMKServer?

    func applicationDidFinishLaunching(_ notification: Notification) {
        guard let connectionName = Bundle.main.object(forInfoDictionaryKey: "InputMethodConnectionName") as? String,
              let bundleIdentifier = Bundle.main.bundleIdentifier else {
            fputs("missing InputMethodKit bundle configuration\\n", stderr)
            NSApp.terminate(nil)
            return
        }

        server = IMKServer(name: connectionName, bundleIdentifier: bundleIdentifier)
        guard server != nil else {
            fputs("IMKServer creation failed\\n", stderr)
            NSApp.terminate(nil)
            return
        }
        print("HYBRID_SPIKE_IMK_SERVER_PASS")
    }
}

private enum TisOperation: String {
    case register
    case enableParent = "enable-parent"
    case enableMode = "enable-mode"
    case select
    case dump
}

private func runInputSourceCommand(arguments: [String]) -> Int32? {
    let values = Array(arguments.dropFirst())
    guard values.count == 2,
          values[0] == "--spike-tis",
          let operation = TisOperation(rawValue: values[1]),
          let bundleIdentifier = Bundle.main.bundleIdentifier,
          let modeIdentifier = bundleIdentifier + ".Hans" as String? else {
        return nil
    }

    switch operation {
    case .register:
        return report("register", TISRegisterInputSource(Bundle.main.bundleURL as CFURL))
    case .enableParent:
        guard let source = inputSource(bundleIdentifier, includeAllInstalled: true) else { return 1 }
        return report("enable-parent", TISEnableInputSource(source))
    case .enableMode:
        guard let source = inputSource(modeIdentifier, includeAllInstalled: true) else { return 1 }
        return report("enable-mode", TISEnableInputSource(source))
    case .select:
        guard let source = inputSource(modeIdentifier, includeAllInstalled: false) else { return 1 }
        return report("select", TISSelectInputSource(source))
    case .dump:
        dumpTigerClawSources()
        return 0
    }
}

private func report(_ name: String, _ status: OSStatus) -> Int32 {
    print("\(name)=\(status)")
    return status == noErr ? 0 : 1
}

private func inputSource(_ identifier: String, includeAllInstalled: Bool) -> TISInputSource? {
    guard let unmanaged = TISCreateInputSourceList(nil, includeAllInstalled) else { return nil }
    let sources = unmanaged.takeRetainedValue()
    for index in 0..<CFArrayGetCount(sources) {
        let source = unsafeBitCast(CFArrayGetValueAtIndex(sources, index), to: TISInputSource.self)
        guard let pointer = TISGetInputSourceProperty(source, kTISPropertyInputSourceID) else { continue }
        let sourceIdentifier = Unmanaged<CFString>.fromOpaque(pointer).takeUnretainedValue() as String
        if sourceIdentifier == identifier { return source }
    }
    return nil
}

private func dumpTigerClawSources() {
    guard let unmanaged = TISCreateInputSourceList(nil, true) else { return }
    let sources = unmanaged.takeRetainedValue()
    for index in 0..<CFArrayGetCount(sources) {
        let source = unsafeBitCast(CFArrayGetValueAtIndex(sources, index), to: TISInputSource.self)
        guard let pointer = TISGetInputSourceProperty(source, kTISPropertyInputSourceID) else { continue }
        let identifier = Unmanaged<CFString>.fromOpaque(pointer).takeUnretainedValue() as String
        if identifier.localizedCaseInsensitiveContains("tigerclaw") {
            print(identifier)
        }
    }
}
