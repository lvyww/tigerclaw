#!/usr/bin/env swift

import Carbon
import Foundation

private enum Phase: String, CaseIterable {
    case register
    case verifyInstalled
    case enableParent
    case verifyParent
    case enableMode
    case verifyMode
    case select
    case verifySelected
}

private let retryDelays: [TimeInterval] = [0, 0.15, 0.4, 0.8, 1.5, 2.5]
private let stableBundleIdentifier = "net.tigerclaw.inputmethod.NativeAotImkSpike"
private let stableModeIdentifier = "net.tigerclaw.inputmethod.NativeAotImkSpike.Hans"

private func fail(_ message: String, _ status: Int32 = 1) -> Never {
    fputs("\(message)\n", stderr)
    exit(status)
}

private func stringProperty(_ source: TISInputSource, _ key: CFString) -> String? {
    guard let pointer = TISGetInputSourceProperty(source, key) else { return nil }
    return Unmanaged<CFString>.fromOpaque(pointer).takeUnretainedValue() as String
}

private func sourceList(includeAllInstalled: Bool) -> [TISInputSource] {
    guard let unmanaged = TISCreateInputSourceList(nil, includeAllInstalled) else {
        return []
    }
    let sources = unmanaged.takeRetainedValue()
    return (0 ..< CFArrayGetCount(sources)).map {
        unsafeBitCast(CFArrayGetValueAtIndex(sources, $0), to: TISInputSource.self)
    }
}

private func source(_ identifier: String, includeAllInstalled: Bool) -> TISInputSource? {
    sourceList(includeAllInstalled: includeAllInstalled).first {
        stringProperty($0, kTISPropertyInputSourceID) == identifier
    }
}

private func sourceIDs(includeAllInstalled: Bool, matching bundleIdentifier: String) -> [String] {
    sourceList(includeAllInstalled: includeAllInstalled).compactMap { source in
        guard stringProperty(source, kTISPropertyBundleID) == bundleIdentifier else { return nil }
        return stringProperty(source, kTISPropertyInputSourceID)
    }.sorted()
}

private func currentSourceIdentifier() -> String? {
    guard let current = TISCopyCurrentKeyboardInputSource()?.takeRetainedValue() else {
        return nil
    }
    return stringProperty(current, kTISPropertyInputSourceID)
}

private func selectFallbackSource(beforeSelecting modeIdentifier: String) {
    guard currentSourceIdentifier() == modeIdentifier else { return }

    let fallbackIdentifiers = [
        "com.apple.keylayout.ABC",
        "com.apple.keylayout.US",
    ]
    for identifier in fallbackIdentifiers {
        guard let fallback = source(identifier, includeAllInstalled: false) else { continue }
        let status = TISSelectInputSource(fallback)
        print("select-fallback=\(status) \(identifier)")
        if status == noErr { return }
    }
    fputs("warning: could not leave the active TigerClaw mode before refreshing its IMK session\n", stderr)
}

private func terminateProcess(named name: String) {
    let process = Process()
    process.executableURL = URL(fileURLWithPath: "/usr/bin/killall")
    process.arguments = [name]
    process.standardOutput = FileHandle.nullDevice
    process.standardError = FileHandle.nullDevice
    do {
        try process.run()
        process.waitUntilExit()
    } catch {
        fputs("warning: could not terminate \(name): \(error.localizedDescription)\n", stderr)
    }
}

private func refreshInputMethodSession(applicationURL: URL, modeIdentifier: String) {
    selectFallbackSource(beforeSelecting: modeIdentifier)
    if let bundle = Bundle(url: applicationURL),
       let executableName = bundle.object(forInfoDictionaryKey: "CFBundleExecutable") as? String {
        terminateProcess(named: executableName)
    }
    terminateProcess(named: "imklaunchagent")
    Thread.sleep(forTimeInterval: 1)
}

private func synchronizeMenuRoster(bundleIdentifier: String, modeIdentifier: String) -> Bool {
    let invokedScriptURL = URL(fileURLWithPath: CommandLine.arguments[0])
    let scriptDirectory = invokedScriptURL.deletingLastPathComponent()
    let synchronizationScript = scriptDirectory.appendingPathComponent("sync-input-source-menu.sh")
    guard FileManager.default.fileExists(atPath: synchronizationScript.path) else {
        fputs("missing menu-roster synchronization script: \(synchronizationScript.path)\n", stderr)
        return false
    }

    let process = Process()
    process.executableURL = URL(fileURLWithPath: "/bin/zsh")
    process.arguments = [synchronizationScript.path, bundleIdentifier, modeIdentifier]
    do {
        try process.run()
        process.waitUntilExit()
    } catch {
        fputs("failed to synchronize the input menu roster: \(error.localizedDescription)\n", stderr)
        return false
    }
    return process.terminationStatus == 0
}

private func activateThroughInputMenu(bundleIdentifier: String, modeIdentifier: String) -> Bool {
    let invokedScriptURL = URL(fileURLWithPath: CommandLine.arguments[0])
    let synchronizationScript = invokedScriptURL
        .deletingLastPathComponent()
        .appendingPathComponent("sync-input-source-menu.sh")
    let process = Process()
    process.executableURL = URL(fileURLWithPath: "/bin/zsh")
    process.arguments = [synchronizationScript.path, bundleIdentifier, modeIdentifier, "activate"]
    do {
        try process.run()
        process.waitUntilExit()
    } catch {
        fputs("failed to activate TigerClaw through the input-menu model: \(error.localizedDescription)\n", stderr)
        return false
    }
    return process.terminationStatus == 0
}

private func modeIdentifier(for bundle: Bundle, bundleIdentifier: String) -> String? {
    guard let component = bundle.object(forInfoDictionaryKey: "ComponentInputModeDict") as? [String: Any],
          let visibleModes = component["tsVisibleInputModeOrderedArrayKey"] as? [String],
          visibleModes.count == 1,
          let modeIdentifier = visibleModes.first,
          modeIdentifier.hasPrefix(bundleIdentifier + ".") else {
        return nil
    }
    return modeIdentifier
}

private func runPhase(applicationPath: String, phase: Phase, selectedSourceIdentifier: String? = nil) -> Int32 {
    let applicationURL = URL(fileURLWithPath: applicationPath, isDirectory: true)
    guard let bundle = Bundle(url: applicationURL),
          let executableName = bundle.object(forInfoDictionaryKey: "CFBundleExecutable") as? String,
          let bundleIdentifier = bundle.bundleIdentifier,
          let bundledModeIdentifier = modeIdentifier(for: bundle, bundleIdentifier: bundleIdentifier) else {
        fputs("input method bundle has invalid executable or input-mode metadata\n", stderr)
        return 1
    }
    let process = Process()
    process.executableURL = applicationURL.appendingPathComponent("Contents/MacOS/\(executableName)")
    switch phase {
    case .register:
        process.arguments = ["--tigerclaw-tis-phase", "register"]
    case .enableParent:
        process.arguments = ["--tigerclaw-tis-phase", "enableParent"]
    case .enableMode:
        process.arguments = ["--tigerclaw-tis-phase", "enableMode"]
    case .select:
        process.arguments = ["--tigerclaw-tis-phase", "select"]
    case .verifyInstalled, .verifyParent, .verifyMode, .verifySelected:
        return runPhase(
            phase,
            applicationURL: applicationURL,
            bundleIdentifier: bundleIdentifier,
            modeIdentifier: bundledModeIdentifier,
            selectedSourceIdentifier: selectedSourceIdentifier
        )
    }
    do {
        try process.run()
        process.waitUntilExit()
    } catch {
        fputs("failed to run TIS phase \(phase.rawValue): \(error.localizedDescription)\n", stderr)
        return 1
    }
    return process.terminationStatus
}

private func converge(
    label: String,
    applicationPath: String,
    action: Phase,
    verify: Phase,
    selectedSourceIdentifier: String? = nil
) -> Bool {
    for (index, delay) in retryDelays.enumerated() {
        let actionStatus = runPhase(
            applicationPath: applicationPath,
            phase: action,
            selectedSourceIdentifier: selectedSourceIdentifier
        )
        if delay > 0 { Thread.sleep(forTimeInterval: delay) }
        let verificationStatus = runPhase(
            applicationPath: applicationPath,
            phase: verify,
            selectedSourceIdentifier: selectedSourceIdentifier
        )
        print("\(label) attempt \(index + 1): action=\(actionStatus), verify=\(verificationStatus)")
        if verificationStatus == 0 { return true }
    }
    return false
}

private func runPhase(
    _ phase: Phase,
    applicationURL: URL,
    bundleIdentifier: String,
    modeIdentifier: String,
    selectedSourceIdentifier: String?
) -> Int32 {
    switch phase {
    case .register:
        let status = TISRegisterInputSource(applicationURL as CFURL)
        print("register=\(status) \(applicationURL.path)")
        return status == noErr ? 0 : 1
    case .verifyInstalled:
        let identifiers = sourceIDs(includeAllInstalled: true, matching: bundleIdentifier)
        print("installed=\(identifiers.joined(separator: ","))")
        return identifiers == [bundleIdentifier, modeIdentifier].sorted() ? 0 : 1
    case .enableParent:
        guard let parent = source(bundleIdentifier, includeAllInstalled: true) else {
            fail("parent source is not installed")
        }
        let status = TISEnableInputSource(parent)
        print("enable-parent=\(status) \(bundleIdentifier)")
        return status == noErr ? 0 : 1
    case .verifyParent:
        let identifiers = sourceIDs(includeAllInstalled: false, matching: bundleIdentifier)
        print("enabled-after-parent=\(identifiers.joined(separator: ","))")
        // Recent macOS releases can acknowledge TISEnableInputSource for an
        // input-method parent while exposing only its concrete input mode in
        // the enabled roster.  Either entry proves that the parent activation
        // reached the Text Input Sources database; verifyMode below still
        // requires the exact bundled mode before registration can succeed.
        return identifiers.contains(bundleIdentifier) || identifiers.contains(modeIdentifier) ? 0 : 1
    case .enableMode:
        guard let mode = source(modeIdentifier, includeAllInstalled: true) else {
            fail("input mode is not installed")
        }
        let status = TISEnableInputSource(mode)
        print("enable-mode=\(status) \(modeIdentifier)")
        return status == noErr ? 0 : 1
    case .verifyMode:
        let identifiers = sourceIDs(includeAllInstalled: false, matching: bundleIdentifier)
        print("enabled-after-mode=\(identifiers.joined(separator: ","))")
        return identifiers.contains(modeIdentifier) ? 0 : 1
    case .select:
        guard let selectedSourceIdentifier else { fail("select phase requires an input-source ID") }
        guard let selected = source(selectedSourceIdentifier, includeAllInstalled: false) else {
            fail("input source is not present in the fresh enabled roster: \(selectedSourceIdentifier)")
        }
        let status = TISSelectInputSource(selected)
        print("select=\(status) \(selectedSourceIdentifier)")
        return status == noErr ? 0 : 1
    case .verifySelected:
        guard let selectedSourceIdentifier else { fail("verify-selected phase requires an input-source ID") }
        let current = currentSourceIdentifier() ?? "(none)"
        print("current=\(current)")
        return current == selectedSourceIdentifier ? 0 : 1
    }
}

private func install(
    applicationURL: URL,
    bundleIdentifier: String,
    modeIdentifier: String,
    shouldSelect: Bool,
    shouldRefreshSession: Bool
) {
    let applicationPath = applicationURL.path
    guard converge(label: "registered", applicationPath: applicationPath, action: .register, verify: .verifyInstalled) else {
        fail("TigerClaw did not become a unique installed parent/mode pair; retry after a login-session refresh")
    }
    guard converge(label: "parent enabled", applicationPath: applicationPath, action: .enableParent, verify: .verifyParent) else {
        fail("TigerClaw parent did not enter the enabled roster; retry after a login-session refresh")
    }
    guard converge(label: "mode enabled", applicationPath: applicationPath, action: .enableMode, verify: .verifyMode) else {
        fail("TigerClaw input mode did not enter the enabled roster; retry after a login-session refresh")
    }

    let menuRosterSynchronized = synchronizeMenuRoster(
        bundleIdentifier: bundleIdentifier,
        modeIdentifier: modeIdentifier
    )
    if !menuRosterSynchronized && !shouldSelect {
        fail("TigerClaw entered Carbon's enabled roster but not the real input-menu roster")
    }
    if !menuRosterSynchronized {
        // A newly introduced bundle identifier may not enter
        // TextInputMenuCore's cache until it has been selected once.  The
        // selection path below refreshes that cache and then proves activation
        // through the real input-menu model before succeeding.
        fputs("warning: input-menu roster is stale; continuing with first selection refresh\n", stderr)
    }

    print("registered and enabled \(bundleIdentifier) with mode \(modeIdentifier)")
    guard shouldSelect else { return }
    // A forced imklaunchagent restart makes long-lived clients such as Feishu
    // keep a dead InputMethodKit connection until the client itself restarts.
    // Default updates therefore only leave and re-enter the source.  The old
    // host can finish its active clients normally; a deliberately requested
    // refresh is still available for diagnostics or an immediate code reload.
    if shouldRefreshSession {
        refreshInputMethodSession(applicationURL: applicationURL, modeIdentifier: modeIdentifier)
    } else {
        selectFallbackSource(beforeSelecting: modeIdentifier)
        Thread.sleep(forTimeInterval: 0.2)
    }
    guard converge(
        label: "mode selected",
        applicationPath: applicationPath,
        action: .select,
        verify: .verifySelected,
        selectedSourceIdentifier: modeIdentifier
    ) else {
        fail("TigerClaw is enabled but selection did not converge")
    }
    guard activateThroughInputMenu(bundleIdentifier: bundleIdentifier, modeIdentifier: modeIdentifier),
          currentSourceIdentifier() == modeIdentifier else {
        fail("TigerClaw was selected in Carbon but did not activate through the real input-menu model")
    }
    print("selected \(modeIdentifier)")
}

let arguments = Array(CommandLine.arguments.dropFirst())
guard let applicationPath = arguments.first else {
    fail("usage: register-input-source.swift <InputMethod.app> [--select] [--refresh-session]", 64)
}

let applicationURL = URL(fileURLWithPath: applicationPath, isDirectory: true)
guard FileManager.default.fileExists(atPath: applicationURL.path),
      let bundle = Bundle(url: applicationURL),
      let bundleIdentifier = bundle.bundleIdentifier,
      let modeIdentifier = modeIdentifier(for: bundle, bundleIdentifier: bundleIdentifier),
      bundleIdentifier == stableBundleIdentifier,
      modeIdentifier == stableModeIdentifier else {
    fail("input method bundle has an unexpected stable parent/mode identity", 66)
}

if arguments.count >= 2, arguments[1] == "--phase" {
    guard arguments.count == 3 || arguments.count == 4,
          let phase = Phase(rawValue: String(arguments[2])) else {
        fail("invalid internal phase", 64)
    }
    let selectedSourceIdentifier = arguments.count == 4 ? String(arguments[3]) : nil
    exit(runPhase(
        phase,
        applicationURL: applicationURL,
        bundleIdentifier: bundleIdentifier,
        modeIdentifier: modeIdentifier,
        selectedSourceIdentifier: selectedSourceIdentifier
    ))
}

let options = Set(arguments.dropFirst())
let knownOptions: Set<String> = ["--select", "--refresh-session"]
guard options.isSubset(of: knownOptions),
      options.count == arguments.count - 1,
      !options.contains("--refresh-session") || options.contains("--select") else {
    fail("usage: register-input-source.swift <InputMethod.app> [--select] [--refresh-session]", 64)
}

install(
    applicationURL: applicationURL,
    bundleIdentifier: bundleIdentifier,
    modeIdentifier: modeIdentifier,
    shouldSelect: options.contains("--select"),
    shouldRefreshSession: options.contains("--refresh-session")
)
