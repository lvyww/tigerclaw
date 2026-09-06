import Foundation

@_silgen_name("engine_create") private func engine_create() -> UnsafeMutableRawPointer?
@_silgen_name("engine_destroy") private func engine_destroy(_ handle: UnsafeMutableRawPointer?)
@_silgen_name("engine_process_key") private func engine_process_key(
    _ handle: UnsafeMutableRawPointer?,
    _ key: UnsafePointer<CChar>?
) -> Int32
@_silgen_name("engine_get_preedit") private func engine_get_preedit(_ handle: UnsafeMutableRawPointer?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("engine_get_commit") private func engine_get_commit(_ handle: UnsafeMutableRawPointer?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("engine_free") private func engine_free(_ text: UnsafeMutablePointer<CChar>?)

private func takeString(_ pointer: UnsafeMutablePointer<CChar>?) -> String {
    guard let pointer else { return "" }
    defer { engine_free(pointer) }
    return String(cString: pointer)
}

guard let engine = engine_create() else {
    fputs("engine_create failed\\n", stderr)
    exit(1)
}

let start = DispatchTime.now().uptimeNanoseconds
let handledInvalid = "?".withCString { engine_process_key(engine, $0) }
let handledA = "a".withCString { engine_process_key(engine, $0) }
let preedit = takeString(engine_get_preedit(engine))
let handledSpace = "space".withCString { engine_process_key(engine, $0) }
let commit = takeString(engine_get_commit(engine))

guard handledInvalid == 0, handledA == 1, preedit == "TigerClaw Test", handledSpace == 1, commit == "TigerClaw Test" else {
    fputs("bridge assertion failed: invalid=\(handledInvalid), handledA=\(handledA), preedit=\(preedit), handledSpace=\(handledSpace), commit=\(commit)\n", stderr)
    exit(2)
}

for _ in 0..<1_000 {
    _ = "a".withCString { engine_process_key(engine, $0) }
    _ = "space".withCString { engine_process_key(engine, $0) }
}

for _ in 0..<100 {
    guard let lifecycleEngine = engine_create() else {
        fputs("lifecycle engine_create failed\\n", stderr)
        exit(3)
    }
    _ = "a".withCString { engine_process_key(lifecycleEngine, $0) }
    engine_destroy(lifecycleEngine)
}

engine_destroy(engine)

let elapsedMicros = (DispatchTime.now().uptimeNanoseconds - start) / 1_000
let averageMicros = Double(elapsedMicros) / 2_003.0

print("SWIFT_DOTNET_C_ABI_PASS elapsed_us=\(elapsedMicros) avg_call_us=\(averageMicros) preedit=\(preedit) commit=\(commit)")
