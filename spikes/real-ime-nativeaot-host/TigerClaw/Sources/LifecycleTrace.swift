import Foundation

enum LifecycleTrace {
    /// Verbose event traces include the complete candidate list. They are useful
    /// while debugging a specific key issue, but must never be enabled in a
    /// normal input-method session: producing and synchronously persisting one
    /// of these records for every key is visible as typing lag.
    static let isDetailedEventTraceEnabled = UserDefaults.standard.bool(forKey: "TigerClawDetailedEventTrace")

    static let fileURL: URL = {
        let libraryURL = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0]
        return libraryURL
            .appendingPathComponent("Logs", isDirectory: true)
            .appendingPathComponent("TigerClaw", isDirectory: true)
            .appendingPathComponent("nativeaot-imk-spike.log", isDirectory: false)
    }()

    private static let writeQueue = DispatchQueue(label: "net.tigerclaw.inputmethod.lifecycle-trace", qos: .utility)

    static func record(_ message: String) {
        writeQueue.async {
            let timestamp = ISO8601DateFormatter().string(from: Date())
            let line = "\(timestamp) \(message)\n"

            do {
                try FileManager.default.createDirectory(
                    at: fileURL.deletingLastPathComponent(),
                    withIntermediateDirectories: true
                )
                if !FileManager.default.fileExists(atPath: fileURL.path) {
                    FileManager.default.createFile(atPath: fileURL.path, contents: nil)
                }
                let handle = try FileHandle(forWritingTo: fileURL)
                try handle.seekToEnd()
                try handle.write(contentsOf: Data(line.utf8))
                try handle.close()
            } catch {
                // Diagnostic tracing must never affect input handling.
            }
        }
    }

    static func recordKeyEvent(_ message: @autoclosure () -> String) {
        guard isDetailedEventTraceEnabled else { return }
        record(message())
    }
}
