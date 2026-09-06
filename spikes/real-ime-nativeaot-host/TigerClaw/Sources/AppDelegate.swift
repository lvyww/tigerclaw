import AppKit
import InputMethodKit
import os

@main
@MainActor
enum TigerClawApplicationMain {
    private static var inputMethodDelegate: TigerClawApplicationDelegate?

    static func main() {
        LifecycleTrace.record("main entry arguments=\(CommandLine.arguments)")
        if let status = InputSourceInstaller.runIfRequested(arguments: CommandLine.arguments) {
            NativeAotRuntimeCache.shared.shutdown()
            exit(status)
        }
        if let status = NativeAotConfigurationCommand.runIfRequested(arguments: CommandLine.arguments) {
            NativeAotRuntimeCache.shared.shutdown()
            exit(status)
        }
        if let status = NativeAotSmoke.runIfRequested(arguments: CommandLine.arguments) {
            NativeAotRuntimeCache.shared.shutdown()
            exit(status)
        }

        let application = NSApplication.shared
        let delegate = TigerClawApplicationDelegate()
        inputMethodDelegate = delegate
        application.delegate = delegate
        LifecycleTrace.record("standard NSApplication configured")
        application.run()
    }
}

@MainActor
final class TigerClawApplicationDelegate: NSObject, NSApplicationDelegate {
    private let logger = Logger(subsystem: "net.tigerclaw.inputmethod.NativeAotImkSpike", category: "lifecycle")
    private var server: IMKServer?

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Enabling a third-party input method is a user-authorized system
        // setting on current macOS releases. TISEnableInputSource can report
        // success here without admitting the source to TextInputMenuAgent,
        // leaving a ghost source that runs under the ABC menu item. Registration
        // and repair therefore remain explicit installer diagnostics only.
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
        logger.notice("TigerClaw NativeAOT IMK spike terminating")
        LifecycleTrace.record("input-method host terminating")
    }
}
