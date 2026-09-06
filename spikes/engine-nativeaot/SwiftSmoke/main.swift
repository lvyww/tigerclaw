import Foundation

let arguments = CommandLine.arguments
guard arguments.count == 2 else {
    fputs("usage: NativeAotSwiftSmoke <lexicon-path>\n", stderr)
    exit(64)
}

func withUtf8Slice<T>(_ value: String, _ body: (tc_utf8_slice) -> T) -> T {
    let bytes = Array(value.utf8)
    return bytes.withUnsafeBufferPointer { buffer in
        body(tc_utf8_slice(data: buffer.baseAddress, length: buffer.count))
    }
}

func string(from slice: tc_utf8_slice) -> String {
    guard let data = slice.data, slice.length > 0 else {
        return ""
    }

    return String(decoding: UnsafeBufferPointer(start: data, count: Int(slice.length)), as: UTF8.self)
}

var runtime: tc_runtime_t?
var session: tc_session_t?
var afterA: tc_snapshot_t?
var afterSpace: tc_snapshot_t?

precondition(withUtf8Slice(arguments[1]) { tc_runtime_create($0, &runtime) } == TC_STATUS_OK.rawValue)
precondition(runtime != nil)
precondition(tc_session_create(runtime, &session) == TC_STATUS_OK.rawValue)
precondition(session != nil)
precondition(tc_session_activate(session) == TC_STATUS_OK.rawValue)

withUtf8Slice("a") { text in
    withUtf8Slice("KeyA") { physicalKey in
        var input = tc_input_event(key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logical_text: text, modifiers: 0, action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physical_key: physicalKey, physical_scan_code: 0, is_extended: 0, is_repeat: 0, repeat_count: 1)
        precondition(tc_session_process(session, &input, &afterA) == TC_STATUS_OK.rawValue)
    }
}
precondition(tc_snapshot_get_handled(afterA) == 1)
precondition(string(from: tc_snapshot_get_preedit(afterA)) == "a")
precondition(string(from: tc_snapshot_get_candidate(afterA, 0)) == "来")

withUtf8Slice(" ") { text in
    withUtf8Slice("Space") { physicalKey in
        var input = tc_input_event(key: Int32(TC_INPUT_KEY_SPACE.rawValue), logical_text: text, modifiers: 0, action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physical_key: physicalKey, physical_scan_code: 49, is_extended: 0, is_repeat: 0, repeat_count: 1)
        precondition(tc_session_process(session, &input, &afterSpace) == TC_STATUS_OK.rawValue)
    }
}
precondition(string(from: tc_snapshot_get_commit(afterSpace)) == "来")
print("ENGINE_NATIVEAOT_SWIFT_PASS")
print("Scenario=a -> 来 -> Space")

if let afterA { tc_snapshot_release(afterA) }
if let afterSpace { tc_snapshot_release(afterSpace) }
if let session {
    _ = tc_session_deactivate(session)
    tc_session_release(session)
}
if let runtime { tc_runtime_release(runtime) }
