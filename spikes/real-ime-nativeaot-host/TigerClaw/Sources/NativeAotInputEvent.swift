import AppKit
import Carbon
import Foundation

struct NativeAotInputEvent {
    let key: Int32
    let logicalText: String
    let modifiers: Int32
    let action: Int32
    let physicalKey: String
    let physicalScanCode: Int32
    let isExtended: Int32
    let isRepeat: Int32
    let repeatCount: Int32

    init(event: NSEvent) {
        let logicalText = NativeAotInputEvent.logicalText(for: event)
        self.key = NativeAotInputEvent.abiKey(forKeyCode: event.keyCode, logicalText: logicalText)
        self.logicalText = logicalText
        self.modifiers = NativeAotInputEvent.abiModifiers(from: event.modifierFlags)
        self.action = event.type == .keyUp
            ? Int32(TC_KEY_ACTION_KEY_UP.rawValue)
            : Int32(TC_KEY_ACTION_KEY_DOWN.rawValue)
        self.physicalKey = NativeAotInputEvent.physicalKeyName(for: event.keyCode)
        self.physicalScanCode = Int32(event.keyCode)
        self.isExtended = NativeAotInputEvent.extendedKeyCodes.contains(event.keyCode) ? 1 : 0
        self.isRepeat = event.isARepeat ? 1 : 0
        self.repeatCount = event.isARepeat ? 2 : 1
    }

    init(
        key: Int32,
        logicalText: String,
        modifiers: Int32,
        action: Int32,
        physicalKey: String,
        physicalScanCode: Int32,
        isExtended: Int32 = 0,
        isRepeat: Int32 = 0,
        repeatCount: Int32 = 1
    ) {
        self.key = key
        self.logicalText = logicalText
        self.modifiers = modifiers
        self.action = action
        self.physicalKey = physicalKey
        self.physicalScanCode = physicalScanCode
        self.isExtended = isExtended
        self.isRepeat = isRepeat
        self.repeatCount = repeatCount
    }

    func withCInputEvent<T>(_ body: (tc_input_event) -> T) -> T {
        withUtf8Slice(logicalText) { logicalSlice in
            withUtf8Slice(physicalKey) { physicalSlice in
                body(tc_input_event(
                    key: key,
                    logical_text: logicalSlice,
                    modifiers: modifiers,
                    action: action,
                    physical_key: physicalSlice,
                    physical_scan_code: physicalScanCode,
                    is_extended: isExtended,
                    is_repeat: isRepeat,
                    repeat_count: repeatCount
                ))
            }
        }
    }

    var traceDescription: String {
        "key=\(key) logical=\(logicalText.debugDescription) physical=\(physicalKey) scan=\(physicalScanCode) modifiers=\(modifiers) action=\(action) repeat=\(isRepeat)/\(repeatCount)"
    }

    var isApplicationShortcut: Bool {
        let applicationShortcutModifiers: Int32 = (1 << 1) | (1 << 2) | (1 << 3)
        return (modifiers & applicationShortcutModifiers) != 0
    }

    var isControlEqualShortcut: Bool {
        physicalScanCode == Int32(kVK_ANSI_Equal) &&
            modifiers == (1 << 1)
    }

    var isControlMShortcut: Bool {
        physicalScanCode == Int32(kVK_ANSI_M) &&
            modifiers == (1 << 1)
    }

    private static func logicalText(for event: NSEvent) -> String {
        if event.keyCode == UInt16(kVK_Space) {
            return " "
        }
        return event.charactersIgnoringModifiers ?? event.characters ?? ""
    }

    static func abiKey(forKeyCode keyCode: UInt16, logicalText: String) -> Int32 {
        switch Int(keyCode) {
        case kVK_Delete:
            return Int32(TC_INPUT_KEY_BACKSPACE.rawValue)
        case kVK_Return:
            return Int32(TC_INPUT_KEY_ENTER.rawValue)
        case kVK_Escape:
            return Int32(TC_INPUT_KEY_ESCAPE.rawValue)
        case kVK_Space:
            return Int32(TC_INPUT_KEY_SPACE.rawValue)
        case kVK_Tab:
            return Int32(TC_INPUT_KEY_TAB.rawValue)
        case kVK_UpArrow:
            return Int32(TC_INPUT_KEY_ARROW_UP.rawValue)
        case kVK_DownArrow:
            return Int32(TC_INPUT_KEY_ARROW_DOWN.rawValue)
        case kVK_LeftArrow:
            return Int32(TC_INPUT_KEY_ARROW_LEFT.rawValue)
        case kVK_RightArrow:
            return Int32(TC_INPUT_KEY_ARROW_RIGHT.rawValue)
        case kVK_ANSI_Semicolon:
            return Int32(TC_INPUT_KEY_SEMICOLON.rawValue)
        case kVK_ANSI_Quote:
            return Int32(TC_INPUT_KEY_QUOTE.rawValue)
        case kVK_ANSI_Minus:
            return Int32(TC_INPUT_KEY_PAGE_PREVIOUS.rawValue)
        case kVK_ANSI_Equal:
            return Int32(TC_INPUT_KEY_PAGE_NEXT.rawValue)
        case kVK_PageUp:
            return Int32(TC_INPUT_KEY_PAGE_PREVIOUS.rawValue)
        case kVK_PageDown:
            return Int32(TC_INPUT_KEY_PAGE_NEXT.rawValue)
        case kVK_ANSI_0,
             kVK_ANSI_1,
             kVK_ANSI_2,
             kVK_ANSI_3,
             kVK_ANSI_4,
             kVK_ANSI_5,
             kVK_ANSI_6,
             kVK_ANSI_7,
             kVK_ANSI_8,
             kVK_ANSI_9:
            return Int32(TC_INPUT_KEY_DIGIT.rawValue)
        default:
            if logicalText.count == 1, logicalText.unicodeScalars.allSatisfy({ (48 ... 57).contains($0.value) }) {
                return Int32(TC_INPUT_KEY_DIGIT.rawValue)
            }
            if logicalText.count == 1 {
                return Int32(TC_INPUT_KEY_CHARACTER.rawValue)
            }
            return Int32(TC_INPUT_KEY_UNKNOWN.rawValue)
        }
    }

    static func legacyTextEvent(_ logicalText: String) -> NativeAotInputEvent {
        let key: Int32
        let physicalKey: String
        switch logicalText {
        case " ":
            key = Int32(TC_INPUT_KEY_SPACE.rawValue)
            physicalKey = "Space"
        case "\r", "\n":
            key = Int32(TC_INPUT_KEY_ENTER.rawValue)
            physicalKey = "Enter"
        case "\t":
            key = Int32(TC_INPUT_KEY_TAB.rawValue)
            physicalKey = "Tab"
        case "\u{1b}":
            key = Int32(TC_INPUT_KEY_ESCAPE.rawValue)
            physicalKey = "Escape"
        case ";":
            key = Int32(TC_INPUT_KEY_SEMICOLON.rawValue)
            physicalKey = "Semicolon"
        case "'":
            key = Int32(TC_INPUT_KEY_QUOTE.rawValue)
            physicalKey = "Quote"
        case "-":
            key = Int32(TC_INPUT_KEY_PAGE_PREVIOUS.rawValue)
            physicalKey = "Minus"
        case "=":
            key = Int32(TC_INPUT_KEY_PAGE_NEXT.rawValue)
            physicalKey = "Equal"
        default:
            if logicalText.count == 1,
               logicalText.unicodeScalars.allSatisfy({ (48 ... 57).contains($0.value) }) {
                key = Int32(TC_INPUT_KEY_DIGIT.rawValue)
                physicalKey = "Digit\(logicalText)"
            } else if logicalText.count == 1 {
                key = Int32(TC_INPUT_KEY_CHARACTER.rawValue)
                physicalKey = "LegacyText"
            } else {
                key = Int32(TC_INPUT_KEY_UNKNOWN.rawValue)
                physicalKey = "LegacyText"
            }
        }

        return NativeAotInputEvent(
            key: key,
            logicalText: logicalText,
            modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue),
            physicalKey: physicalKey,
            physicalScanCode: -1)
    }

    static func abiModifiers(fromInputMethodKitModifiers modifiers: Int) -> Int32 {
        abiModifiers(from: NSEvent.ModifierFlags(rawValue: UInt(modifiers)))
    }

    private static func abiModifiers(from flags: NSEvent.ModifierFlags) -> Int32 {
        var result: Int32 = 0
        if flags.contains(.shift) { result |= 1 << 0 }
        if flags.contains(.control) { result |= 1 << 1 }
        if flags.contains(.option) { result |= 1 << 2 }
        if flags.contains(.command) { result |= 1 << 3 }
        if flags.contains(.capsLock) { result |= 1 << 4 }
        if flags.contains(.function) { result |= 1 << 5 }
        return result
    }

    static func physicalKeyName(for keyCode: UInt16) -> String {
        switch Int(keyCode) {
        case kVK_ANSI_A: return "KeyA"
        case kVK_ANSI_B: return "KeyB"
        case kVK_ANSI_C: return "KeyC"
        case kVK_ANSI_D: return "KeyD"
        case kVK_ANSI_E: return "KeyE"
        case kVK_ANSI_F: return "KeyF"
        case kVK_ANSI_G: return "KeyG"
        case kVK_ANSI_H: return "KeyH"
        case kVK_ANSI_I: return "KeyI"
        case kVK_ANSI_J: return "KeyJ"
        case kVK_ANSI_K: return "KeyK"
        case kVK_ANSI_L: return "KeyL"
        case kVK_ANSI_M: return "KeyM"
        case kVK_ANSI_N: return "KeyN"
        case kVK_ANSI_O: return "KeyO"
        case kVK_ANSI_P: return "KeyP"
        case kVK_ANSI_Q: return "KeyQ"
        case kVK_ANSI_R: return "KeyR"
        case kVK_ANSI_S: return "KeyS"
        case kVK_ANSI_T: return "KeyT"
        case kVK_ANSI_U: return "KeyU"
        case kVK_ANSI_V: return "KeyV"
        case kVK_ANSI_W: return "KeyW"
        case kVK_ANSI_X: return "KeyX"
        case kVK_ANSI_Y: return "KeyY"
        case kVK_ANSI_Z: return "KeyZ"
        case kVK_ANSI_0: return "Digit0"
        case kVK_ANSI_1: return "Digit1"
        case kVK_ANSI_2: return "Digit2"
        case kVK_ANSI_3: return "Digit3"
        case kVK_ANSI_4: return "Digit4"
        case kVK_ANSI_5: return "Digit5"
        case kVK_ANSI_6: return "Digit6"
        case kVK_ANSI_7: return "Digit7"
        case kVK_ANSI_8: return "Digit8"
        case kVK_ANSI_9: return "Digit9"
        case kVK_Space: return "Space"
        case kVK_Delete: return "Backspace"
        case kVK_Return: return "Enter"
        case kVK_Escape: return "Escape"
        case kVK_Tab: return "Tab"
        case kVK_UpArrow: return "ArrowUp"
        case kVK_DownArrow: return "ArrowDown"
        case kVK_LeftArrow: return "ArrowLeft"
        case kVK_RightArrow: return "ArrowRight"
        case kVK_ANSI_Semicolon: return "Semicolon"
        case kVK_ANSI_Quote: return "Quote"
        case kVK_ANSI_Minus: return "Minus"
        case kVK_ANSI_Equal: return "Equal"
        case kVK_PageUp: return "PageUp"
        case kVK_PageDown: return "PageDown"
        case kVK_F1: return "F1"
        case kVK_F2: return "F2"
        case kVK_F3: return "F3"
        case kVK_F4: return "F4"
        case kVK_F5: return "F5"
        case kVK_F6: return "F6"
        case kVK_F7: return "F7"
        case kVK_F8: return "F8"
        case kVK_F9: return "F9"
        case kVK_F10: return "F10"
        case kVK_F11: return "F11"
        case kVK_F12: return "F12"
        default: return "KeyCode\(keyCode)"
        }
    }

    private static let extendedKeyCodes: Set<UInt16> = [
        UInt16(kVK_UpArrow),
        UInt16(kVK_DownArrow),
        UInt16(kVK_LeftArrow),
        UInt16(kVK_RightArrow),
        UInt16(kVK_PageUp),
        UInt16(kVK_PageDown)
    ]
}

enum NativeAotCommand {
    static func inputEvent(forSelectorName selectorName: String) -> NativeAotInputEvent? {
        switch selectorName {
        case "deleteBackward:":
            return event(
                key: TC_INPUT_KEY_BACKSPACE,
                physicalKey: "Backspace",
                physicalScanCode: kVK_Delete)
        case "insertNewline:", "insertLineBreak:":
            return event(
                key: TC_INPUT_KEY_ENTER,
                physicalKey: "Enter",
                physicalScanCode: kVK_Return)
        case "cancelOperation:":
            return event(
                key: TC_INPUT_KEY_ESCAPE,
                physicalKey: "Escape",
                physicalScanCode: kVK_Escape)
        case "insertTab:":
            return event(
                key: TC_INPUT_KEY_TAB,
                physicalKey: "Tab",
                physicalScanCode: kVK_Tab)
        case "moveUp:":
            return event(
                key: TC_INPUT_KEY_ARROW_UP,
                physicalKey: "ArrowUp",
                physicalScanCode: kVK_UpArrow)
        case "moveDown:":
            return event(
                key: TC_INPUT_KEY_ARROW_DOWN,
                physicalKey: "ArrowDown",
                physicalScanCode: kVK_DownArrow)
        case "moveLeft:":
            return event(
                key: TC_INPUT_KEY_ARROW_LEFT,
                physicalKey: "ArrowLeft",
                physicalScanCode: kVK_LeftArrow)
        case "moveRight:":
            return event(
                key: TC_INPUT_KEY_ARROW_RIGHT,
                physicalKey: "ArrowRight",
                physicalScanCode: kVK_RightArrow)
        case "scrollPageUp:", "pageUp:":
            return event(
                key: TC_INPUT_KEY_PAGE_PREVIOUS,
                physicalKey: "PageUp",
                physicalScanCode: kVK_PageUp)
        case "scrollPageDown:", "pageDown:":
            return event(
                key: TC_INPUT_KEY_PAGE_NEXT,
                physicalKey: "PageDown",
                physicalScanCode: kVK_PageDown)
        default:
            return nil
        }
    }

    private static func event(
        key: tc_input_key,
        physicalKey: String,
        physicalScanCode: Int
    ) -> NativeAotInputEvent {
        return NativeAotInputEvent(
            key: Int32(key.rawValue),
            logicalText: "",
            modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue),
            physicalKey: physicalKey,
            physicalScanCode: Int32(physicalScanCode))
    }
}
