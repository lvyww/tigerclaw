import AppKit
import InputMethodKit
import os

@main
enum TigerClawApplicationMain {
    static func main() {
        LifecycleTrace.record("main entry arguments=\(CommandLine.arguments)")
        if let status = TigerClawInputSourceInstaller.runIfRequested(arguments: CommandLine.arguments) {
            exit(status)
        }
        _ = NSApplicationMain(CommandLine.argc, CommandLine.unsafeArgv)
    }
}

@MainActor
final class TigerClawApplication: NSApplication {
    private lazy var inputMethodDelegate = TigerClawApplicationDelegate()

    override init() {
        super.init()
        delegate = inputMethodDelegate
        LifecycleTrace.record("application init")
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        delegate = inputMethodDelegate
        LifecycleTrace.record("application init(coder:)")
    }
}

@MainActor
final class TigerClawApplicationDelegate: NSObject, NSApplicationDelegate {
    private let logger = Logger(subsystem: "net.tigerclaw.inputmethod.TigerClaw", category: "lifecycle")
    private var server: IMKServer?

    func applicationDidFinishLaunching(_ notification: Notification) {
        guard let connectionName = Bundle.main.object(forInfoDictionaryKey: "InputMethodConnectionName") as? String,
              let bundleIdentifier = Bundle.main.bundleIdentifier else {
            logger.fault("Missing InputMethodKit bundle configuration")
            NSApp.terminate(nil)
            return
        }

        server = IMKServer(name: connectionName, bundleIdentifier: bundleIdentifier)
        if server == nil {
            logger.fault("Unable to create IMKServer for \(connectionName, privacy: .public)")
            LifecycleTrace.record("IMKServer creation failed for \(connectionName)")
            NSApp.terminate(nil)
            return
        }

        logger.notice("IMKServer started for \(connectionName, privacy: .public)")
        LifecycleTrace.record("IMKServer started for \(connectionName)")
    }

    func applicationWillTerminate(_ notification: Notification) {
        logger.notice("TigerClaw input-method host terminating")
        LifecycleTrace.record("input-method host terminating")
    }

}
