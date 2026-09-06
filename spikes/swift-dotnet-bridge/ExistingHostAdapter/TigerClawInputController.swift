import AppKit
import Foundation
import InputMethodKit

final class TigerClawInputController: IMKInputController {
    private var engine: NativeAotBridge?

    override init!(server: IMKServer!, delegate: Any!, client inputClient: Any!) {
        engine = NativeAotBridge()
        super.init(server: server, delegate: delegate, client: inputClient)
        trace("controller init engine=\(engine != nil)")
    }

    override func activateServer(_ sender: Any!) {
        super.activateServer(sender)
        if engine == nil { engine = NativeAotBridge() }
        trace("activate engine=\(engine != nil)")
    }

    override func inputControllerWillClose() {
        trace("controller close")
        engine = nil
        super.inputControllerWillClose()
    }

    @objc(handleEvent:client:)
    override func handle(_ event: NSEvent!, client sender: Any!) -> Bool {
        trace("handle type=\(event.type.rawValue) client=\(sender is IMKTextInput) engine=\(engine != nil)")
        guard event.type == .keyDown,
              let client = sender as? IMKTextInput,
              let engine else { return false }

        let key: String
        if event.keyCode == 49 {
            key = "space"
        } else if let characters = event.charactersIgnoringModifiers, characters.count == 1 {
            key = characters.lowercased()
        } else {
            return false
        }

        let result = engine.process(key: key)
        trace("key=\(key) handled=\(result.handled) preedit=\(result.preedit) commit=\(result.commit)")
        guard result.handled else { return false }
        if !result.commit.isEmpty {
            client.insertText(result.commit, replacementRange: NSRange(location: NSNotFound, length: 0))
        } else if !result.preedit.isEmpty {
            client.setMarkedText(result.preedit,
                                 selectionRange: NSRange(location: result.preedit.utf16.count, length: 0),
                                 replacementRange: NSRange(location: NSNotFound, length: 0))
        }
        return true
    }

    private func trace(_ message: String) {
        let url = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Logs/TigerClaw/hybrid-spike.log")
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        if !FileManager.default.fileExists(atPath: url.path) { FileManager.default.createFile(atPath: url.path, contents: nil) }
        if let handle = try? FileHandle(forWritingTo: url) {
            try? handle.seekToEnd()
            try? handle.write(contentsOf: Data((message + "\n").utf8))
            try? handle.close()
        }
    }
}
