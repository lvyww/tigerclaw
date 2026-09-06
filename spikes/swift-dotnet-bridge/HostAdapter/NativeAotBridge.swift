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

struct EngineKeyResult {
    let handled: Bool
    let preedit: String
    let commit: String
}

final class NativeAotBridge {
    private var engine: UnsafeMutableRawPointer?

    init?() {
        guard let engine = engine_create() else { return nil }
        self.engine = engine
    }

    deinit {
        engine_destroy(engine)
    }

    func process(key: String) -> EngineKeyResult {
        guard let engine else {
            return EngineKeyResult(handled: false, preedit: "", commit: "")
        }

        let handled = key.withCString { engine_process_key(engine, $0) } == 1
        return EngineKeyResult(
            handled: handled,
            preedit: takeString(engine_get_preedit(engine)),
            commit: takeString(engine_get_commit(engine)))
    }

    private func takeString(_ pointer: UnsafeMutablePointer<CChar>?) -> String {
        guard let pointer else { return "" }
        defer { engine_free(pointer) }
        return String(cString: pointer)
    }
}
