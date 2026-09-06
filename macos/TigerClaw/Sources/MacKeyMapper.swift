import AppKit

struct TigerClawHostInputEvent {
    let key: TigerClawHostInputKey
    let action: TigerClawHostInputAction
    let modifiers: TigerClawHostInputModifiers
    let text: String
    let physicalKeyCode: Int
    let repeatCount: Int
}

enum TigerClawHostInputAction {
    case keyDown
    case keyUp
}

enum TigerClawHostInputKey {
    case unknown
    case character
    case digit
    case space
    case backspace
    case enter
    case escape
    case tab
    case arrowUp
    case arrowDown
    case arrowLeft
    case arrowRight
    case pageUp
    case pageDown
    case semicolon
    case quote
    case slash
    case comma
    case period
    case minus
    case equals
    case leftBracket
    case rightBracket
    case backslash
    case backquote
}

struct TigerClawHostInputModifiers: OptionSet {
    let rawValue: Int

    static let shift = TigerClawHostInputModifiers(rawValue: 1 << 0)
    static let control = TigerClawHostInputModifiers(rawValue: 1 << 1)
    static let option = TigerClawHostInputModifiers(rawValue: 1 << 2)
    static let command = TigerClawHostInputModifiers(rawValue: 1 << 3)
    static let capsLock = TigerClawHostInputModifiers(rawValue: 1 << 4)
    static let numericPad = TigerClawHostInputModifiers(rawValue: 1 << 5)
}

enum MacKeyMapper {
    static func map(_ event: NSEvent) -> TigerClawHostInputEvent? {
        guard let action = mapAction(event.type) else { return nil }

        let text = event.characters ?? ""
        let semanticText = event.charactersIgnoringModifiers ?? text
        let key = mapKey(keyCode: event.keyCode, semanticText: semanticText)

        return TigerClawHostInputEvent(
            key: key,
            action: action,
            modifiers: mapModifiers(event.modifierFlags),
            text: text,
            physicalKeyCode: Int(event.keyCode),
            repeatCount: event.isARepeat ? 2 : 1)
    }

    private static func mapAction(_ type: NSEvent.EventType) -> TigerClawHostInputAction? {
        switch type {
        case .keyDown:
            return .keyDown
        case .keyUp:
            return .keyUp
        default:
            return nil
        }
    }

    private static func mapModifiers(_ flags: NSEvent.ModifierFlags) -> TigerClawHostInputModifiers {
        var modifiers: TigerClawHostInputModifiers = []
        if flags.contains(.shift) { modifiers.insert(.shift) }
        if flags.contains(.control) { modifiers.insert(.control) }
        if flags.contains(.option) { modifiers.insert(.option) }
        if flags.contains(.command) { modifiers.insert(.command) }
        if flags.contains(.capsLock) { modifiers.insert(.capsLock) }
        if flags.contains(.numericPad) { modifiers.insert(.numericPad) }
        return modifiers
    }

    private static func mapKey(keyCode: UInt16, semanticText: String) -> TigerClawHostInputKey {
        switch keyCode {
        case 49: return .space
        case 51: return .backspace
        case 36, 76: return .enter
        case 53: return .escape
        case 48: return .tab
        case 126: return .arrowUp
        case 125: return .arrowDown
        case 123: return .arrowLeft
        case 124: return .arrowRight
        case 116: return .pageUp
        case 121: return .pageDown
        case 41: return .semicolon
        case 39: return .quote
        case 44: return .slash
        case 43: return .comma
        case 47: return .period
        case 27: return .minus
        case 24: return .equals
        case 33: return .leftBracket
        case 30: return .rightBracket
        case 42: return .backslash
        case 50: return .backquote
        default:
            if semanticText.count == 1 {
                let scalar = semanticText.unicodeScalars.first?.value ?? 0
                if scalar >= 48 && scalar <= 57 { return .digit }
                if (scalar >= 65 && scalar <= 90) || (scalar >= 97 && scalar <= 122) { return .character }
            }
            return .unknown
        }
    }
}
