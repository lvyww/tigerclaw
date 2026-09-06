import AppKit
import InputMethodKit

// This is a compile-checked adapter for the existing IMK host.  It is not wired
// into the production input-method target; the spike bundle must link
// DummyEngine.dylib and instantiate this controller for a real TextEdit run.
@objc(TigerClawHybridSpikeInputController)
final class BridgeInputController: IMKInputController {
    private var engine: NativeAotBridge?

    override init!(server: IMKServer!, delegate: Any!, client inputClient: Any!) {
        engine = NativeAotBridge()
        super.init(server: server, delegate: delegate, client: inputClient)
    }

    override func activateServer(_ sender: Any!) {
        super.activateServer(sender)
        if engine == nil {
            engine = NativeAotBridge()
        }
    }

    override func deactivateServer(_ sender: Any!) {
        super.deactivateServer(sender)
    }

    override func inputControllerWillClose() {
        engine = nil
        super.inputControllerWillClose()
    }

    @objc(handleEvent:client:)
    override func handle(_ event: NSEvent!, client sender: Any!) -> Bool {
        guard event.type == .keyDown,
              let client = sender as? IMKTextInput,
              let key = bridgeKey(for: event),
              let engine else {
            return false
        }

        let result = engine.process(key: key)
        guard result.handled else { return false }

        if !result.commit.isEmpty {
            client.insertText(
                result.commit,
                replacementRange: NSRange(location: NSNotFound, length: 0))
        } else if !result.preedit.isEmpty {
            client.setMarkedText(
                result.preedit,
                selectionRange: NSRange(location: result.preedit.utf16.count, length: 0),
                replacementRange: NSRange(location: NSNotFound, length: 0))
        }
        return true
    }

    private func bridgeKey(for event: NSEvent) -> String? {
        if event.keyCode == 49 { return "space" }
        guard let characters = event.charactersIgnoringModifiers,
              characters.count == 1 else {
            return nil
        }
        return characters.lowercased()
    }
}
