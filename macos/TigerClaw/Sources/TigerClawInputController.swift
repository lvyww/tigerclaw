import AppKit
import InputMethodKit
import os

final class TigerClawInputController: IMKInputController {
    private let logger = Logger(subsystem: "net.tigerclaw.inputmethod.TigerClaw", category: "lifecycle")

    override init!(server: IMKServer!, delegate: Any!, client inputClient: Any!) {
        super.init(server: server, delegate: delegate, client: inputClient)
        logger.notice("Input controller created")
        LifecycleTrace.record("input controller created")
    }

    override func activateServer(_ sender: Any!) {
        super.activateServer(sender)
        logger.notice("Input controller activated")
        LifecycleTrace.record("input controller activated")
    }

    override func deactivateServer(_ sender: Any!) {
        super.deactivateServer(sender)
        logger.notice("Input controller deactivated")
        LifecycleTrace.record("input controller deactivated")
    }

    override func inputControllerWillClose() {
        logger.notice("Input controller closing")
        LifecycleTrace.record("input controller closing")
        super.inputControllerWillClose()
    }

    @objc(handleEvent:client:)
    override func handle(_ event: NSEvent!, client sender: Any!) -> Bool {
        if let inputEvent = MacKeyMapper.map(event) {
            logger.debug("Received key \(String(describing: inputEvent.key), privacy: .public) action \(String(describing: inputEvent.action), privacy: .public)")
            LifecycleTrace.record("received key \(inputEvent.key) action \(inputEvent.action) physical \(inputEvent.physicalKeyCode) repeat \(inputEvent.repeatCount)")
        } else {
            logger.debug("Received event type \(event.type.rawValue, privacy: .public)")
            LifecycleTrace.record("received event type \(event.type.rawValue)")
        }
        return false
    }
}
