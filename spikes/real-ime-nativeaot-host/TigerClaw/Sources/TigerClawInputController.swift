import AppKit
import Carbon
import Foundation
import InputMethodKit

enum CompositionPresentation {
    static func needsMarkedTextClear(wasComposing: Bool, result: NativeAotResult) -> Bool {
        wasComposing && !result.isComposing && result.commit.isEmpty
    }
}

struct TigerClawInputMode {
    private(set) var isChinese: Bool

    init(defaultChinese: Bool = true) {
        isChinese = defaultChinese
    }

    mutating func toggle() {
        isChinese.toggle()
    }
}

enum QuickAddWordPrefill {
    static func resolve(
        activeInputCode: String,
        candidates: [String],
        committedTextHistory: [String],
        lexiconURL: URL
    ) -> QuickAddWordPrefillResult {
        // A user can deliberately type a new code that has no existing candidate.
        // Keep that raw code and pair it with the recent text, instead of silently
        // replacing it with an automatically constructed code.
        if !activeInputCode.isEmpty {
            return QuickAddWordPrefillResult(
                code: activeInputCode,
                text: candidates.first ?? committedTextHistory.suffix(2).joined())
        }
        let text = committedTextHistory.suffix(2).joined()
        return QuickAddWordPrefillResult(
            code: NativeAotCodeComposer.constructCode(for: text, lexiconURL: lexiconURL),
            text: text)
    }
}

struct QuickAddWordPrefillResult: Equatable {
    let code: String
    let text: String
}

final class TigerClawInputController: IMKInputController {
    private var engine: NativeAotBridge?
    private var engineConfiguration: NativeAotConfiguration?
    private var engineSchema: NativeAotSchema?
    private var engineLexiconRevision: String?
    private var engineUserDictionaryRevision: String?
    private var selectionKeyBindings = NativeAotSelectionKeyBindings.loadCurrentSchema()
    private var selectionKeyConfigurationRevision: String?
    private var engineRedeployGeneration: String?
    private let candidatePanel = CandidatePanel()
    private var composedText = ""
    private var isComposing = false
    private var activeInputCode = ""
    private var activeCandidates: [String] = []
    private var activeSelectedIndex = 0
    private var committedTextHistory: [String] = []
    private var sentenceRefreshGeneration = 0
    private var sentenceRefreshTimer: Timer?
    private var sentenceRefreshClient: IMKTextInput?
    private var sentenceRefreshDeadline = Date.distantPast
    private var inputMode = TigerClawInputMode()
    private let normalTypingSound = NSSound(named: NSSound.Name("Tink"))
    private let spaceTypingSound = NSSound(named: NSSound.Name("Pop"))
    private let functionTypingSound = NSSound(named: NSSound.Name("Basso"))

    override init!(server: IMKServer!, delegate: Any!, client inputClient: Any!) {
        super.init(server: server, delegate: delegate, client: inputClient)
        LifecycleTrace.record(
            "controller init client=\(Self.clientIdentifier(inputClient)) type=\(String(describing: type(of: inputClient)))")
        inputMode = TigerClawInputMode(defaultChinese: NativeAotConfiguration.current.defaultChinese)
        createEngine(reason: "init")
    }

    override func activateServer(_ sender: Any!) {
        super.activateServer(sender)
        let clientIdentifier = Self.clientIdentifier(sender)
        let configuration = NativeAotConfiguration.current
        let schema = NativeAotSchema.current
        let lexiconRevision = schema.contentRevision()
        let userDictionaryRevision = NativeAotDataRoot.userDictionaryRevision()
        let selectionKeyRevision = NativeAotSelectionKeyBindings.configurationRevision()
        let redeployGeneration = NativeAotSchema.redeployGeneration
        if engine == nil ||
            engineConfiguration != configuration ||
            engineSchema != schema ||
            engineLexiconRevision != lexiconRevision ||
            engineUserDictionaryRevision != userDictionaryRevision ||
            selectionKeyConfigurationRevision != selectionKeyRevision ||
            engineRedeployGeneration != redeployGeneration {
            cancelComposition()
            engine?.deactivate()
            candidatePanel.hide()
            inputMode = TigerClawInputMode(defaultChinese: configuration.defaultChinese)
            createEngine(reason: "activate")
        }
        do {
            try engine?.activate()
            LifecycleTrace.record("session activate ok client=\(clientIdentifier)")
        } catch {
            LifecycleTrace.record("session activate failed client=\(clientIdentifier) \(error)")
        }
    }

    override func deactivateServer(_ sender: Any!) {
        cancelComposition()
        engine?.deactivate()
        candidatePanel.hide()
        LifecycleTrace.record("session deactivate client=\(Self.clientIdentifier(sender))")
        super.deactivateServer(sender)
    }

    override func recognizedEvents(_ sender: Any!) -> Int {
        let events = super.recognizedEvents(sender)
        LifecycleTrace.record(
            "recognized events client=\(Self.clientIdentifier(sender)) mask=\(events)")
        return events
    }

    override func inputControllerWillClose() {
        cancelComposition()
        engine?.deactivate()
        candidatePanel.hide()
        LifecycleTrace.record("controller close")
        engine = nil
        engineConfiguration = nil
        engineSchema = nil
        engineLexiconRevision = nil
        engineUserDictionaryRevision = nil
        selectionKeyConfigurationRevision = nil
        engineRedeployGeneration = nil
        super.inputControllerWillClose()
    }

    override func menu() -> NSMenu! {
        let menu = NSMenu(title: "虎爪输入法")
        let settings = NSMenuItem(
            title: "打开虎爪设置…",
            action: #selector(openTigerClawSettings(_:)),
            keyEquivalent: "")
        // InputMethodKit sends input-source menu commands back through
        // doCommandBySelector:, whose default implementation invokes this
        // controller.  Giving the item an AppKit target bypasses that route.
        menu.addItem(settings)
        menu.addItem(.separator())
        let schema = NSMenuItem(title: "当前方案：\(NativeAotSchema.current.displayName)", action: nil, keyEquivalent: "")
        schema.isEnabled = false
        menu.addItem(schema)
        let schemaSwitcher = NSMenuItem(title: "切换输入方案", action: nil, keyEquivalent: "")
        let schemaMenu = NSMenu(title: "切换输入方案")
        let selectedSchema = NativeAotSchema.current.identifier
        for availableSchema in NativeAotSchema.available {
            let item = NSMenuItem(
                title: availableSchema.displayName,
                action: #selector(selectSchemaFromMenu(_:)),
                keyEquivalent: "")
            item.target = self
            item.representedObject = availableSchema.identifier
            item.state = availableSchema.identifier == selectedSchema ? .on : .off
            schemaMenu.addItem(item)
        }
        schemaSwitcher.submenu = schemaMenu
        menu.addItem(schemaSwitcher)
        menu.addItem(NSMenuItem(
            title: inputMode.isChinese ? "切换为英文直通" : "切换为中文输入",
            action: #selector(toggleInputModeFromMenu(_:)),
            keyEquivalent: ""))
        return menu
    }

    // Must be visible to Objective-C. InputMethodKit resolves menu actions
    // dynamically through doCommandBySelector: instead of Swift dispatch.
    @objc(openTigerClawSettings:)
    func openTigerClawSettings(_ sender: Any?) {
        openTigerClawSettings(arguments: [])
    }

    private func openTigerClawSettings(arguments: [String]) {
        guard let url = Bundle.main.resourceURL?
            .appendingPathComponent("TigerClaw Settings.app", isDirectory: true),
              FileManager.default.fileExists(atPath: url.path) else {
            LifecycleTrace.record("settings launch failed: bundled TigerClaw Settings.app missing")
            return
        }

        if !arguments.isEmpty {
            let executable = url
                .appendingPathComponent("Contents", isDirectory: true)
                .appendingPathComponent("MacOS", isDirectory: true)
                .appendingPathComponent("TigerClawSettingsSpike")
            guard FileManager.default.isExecutableFile(atPath: executable.path) else {
                LifecycleTrace.record("quick add launch failed: settings executable missing path=\(executable.path)")
                return
            }
            do {
                let process = Process()
                process.executableURL = executable
                process.arguments = arguments
                try process.run()
                LifecycleTrace.record("quick add direct launch requested executable=\(executable.path) arguments=\(arguments)")
            } catch {
                LifecycleTrace.record("quick add direct launch failed executable=\(executable.path) error=\(error)")
            }
            return
        }

        let configuration = NSWorkspace.OpenConfiguration()
        configuration.arguments = arguments
        NSWorkspace.shared.openApplication(at: url, configuration: configuration)
        LifecycleTrace.record("settings launch requested url=\(url.path) arguments=\(arguments)")
    }

    @objc(toggleInputModeFromMenu:)
    func toggleInputModeFromMenu(_ sender: Any?) {
        toggleInputMode(client: nil, reason: "menu")
    }

    @objc(selectSchemaFromMenu:)
    func selectSchemaFromMenu(_ sender: NSMenuItem) {
        guard let identifier = sender.representedObject as? String else {
            return
        }
        do {
            try NativeAotSchema.select(identifier: identifier)
            cancelComposition()
            engine?.deactivate()
            candidatePanel.hide()
            createEngine(reason: "menu-schema-switch")
            try engine?.activate()
            LifecycleTrace.record("schema switched from menu identifier=\(identifier)")
        } catch {
            LifecycleTrace.record("schema switch from menu failed identifier=\(identifier) error=\(error)")
        }
    }

    @objc(handleEvent:client:)
    override func handle(_ event: NSEvent!, client sender: Any!) -> Bool {
        guard let event else {
            return false
        }

        reloadEngineForExternalChangesIfNeeded()

        if event.type == .flagsChanged {
            if inputMode.isChinese,
               isComposing,
               !NativeAotConfiguration.current.sentenceInputActive(for: NativeAotSchema.current),
               NativeAotSelectionKeyBindings.modifierIsPressed(for: event),
               let selectionIndex = selectionKeyBindings.selectionIndex(for: event.keyCode),
               let client = sender as? IMKTextInput {
                return selectCandidate(at: selectionIndex, client: client)
            }
            return handleModifierEvent(event, client: sender)
        }

        guard event.type == .keyDown else { return false }
        let mappedEvent = NativeAotInputEvent(event: event)
        if mappedEvent.key == TC_INPUT_KEY_SPACE.rawValue,
           (mappedEvent.modifiers & (1 << 1)) != 0,
           NativeAotConfiguration.current.ctrlSpaceTogglesInputMode {
            toggleInputMode(client: sender as? IMKTextInput, reason: "ctrl-space")
            return true
        }
        if mappedEvent.isControlEqualShortcut,
           NativeAotConfiguration.current.ctrlEqualAddsWord {
            openQuickAddWord()
            return true
        }
        if mappedEvent.isControlMShortcut,
           NativeAotConfiguration.current.ctrlMTogglesRecentSchema {
            return toggleRecentSchema(client: sender as? IMKTextInput)
        }
        let isConfiguredSelectionKey = mappedEvent.modifiers == 0 &&
            selectionKeyBindings.selectionIndex(for: event.keyCode) != nil
        guard mappedEvent.key != TC_INPUT_KEY_UNKNOWN.rawValue || isConfiguredSelectionKey else {
            LifecycleTrace.record("unhandled unmapped event \(mappedEvent.traceDescription)")
            return false
        }

        return process(mappedEvent, client: sender)
    }

    override func inputText(_ text: String!, key keyCode: Int, modifiers: Int, client sender: Any!) -> Bool {
        reloadEngineForExternalChangesIfNeeded()
        let logicalText = text ?? ""
        let mappedEvent = NativeAotInputEvent(
            key: NativeAotInputEvent.abiKey(forKeyCode: UInt16(keyCode), logicalText: logicalText),
            logicalText: logicalText,
            modifiers: NativeAotInputEvent.abiModifiers(fromInputMethodKitModifiers: modifiers),
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue),
            physicalKey: NativeAotInputEvent.physicalKeyName(for: UInt16(keyCode)),
            physicalScanCode: Int32(keyCode)
        )
        if mappedEvent.key == TC_INPUT_KEY_SPACE.rawValue,
           (mappedEvent.modifiers & (1 << 1)) != 0,
           NativeAotConfiguration.current.ctrlSpaceTogglesInputMode {
            toggleInputMode(client: sender as? IMKTextInput, reason: "ctrl-space-input-text")
            return true
        }
        if mappedEvent.isControlEqualShortcut,
           NativeAotConfiguration.current.ctrlEqualAddsWord {
            openQuickAddWord()
            return true
        }
        if mappedEvent.isControlMShortcut,
           NativeAotConfiguration.current.ctrlMTogglesRecentSchema {
            return toggleRecentSchema(client: sender as? IMKTextInput)
        }
        return process(mappedEvent, client: sender)
    }

    // Some AppKit clients, including WeChat's chat editor, use IMK's legacy
    // key-binding delivery path. In that path ordinary text is sent through
    // inputText:client: and commands through didCommandBySelector:client:
    // instead of handleEvent:client: or inputText:key:modifiers:client:.
    @objc(inputText:client:)
    override func inputText(_ text: String!, client sender: Any!) -> Bool {
        reloadEngineForExternalChangesIfNeeded()
        let mappedEvent = NativeAotInputEvent.legacyTextEvent(text ?? "")
        guard mappedEvent.key != TC_INPUT_KEY_UNKNOWN.rawValue else {
            LifecycleTrace.record("unhandled legacy text \(mappedEvent.traceDescription)")
            return false
        }
        LifecycleTrace.record("legacy text input \(mappedEvent.traceDescription)")
        return process(mappedEvent, client: sender)
    }

    @objc(didCommandBySelector:client:)
    override func didCommand(by selector: Selector!, client sender: Any!) -> Bool {
        let selectorName = selector.map(NSStringFromSelector) ?? ""
        guard isComposing,
              let mappedEvent = NativeAotCommand.inputEvent(forSelectorName: selectorName) else {
            return false
        }
        let handled = process(mappedEvent, client: sender)
        if handled {
            LifecycleTrace.record("handled legacy input command selector=\(selectorName)")
        }
        return handled
    }

    override func doCommand(by selector: Selector!, command commandDictionary: [AnyHashable: Any]!) {
        let selectorName = selector.map(NSStringFromSelector) ?? ""
        if let mappedEvent = NativeAotCommand.inputEvent(forSelectorName: selectorName),
           isComposing,
           let client = commandDictionary.values.first(where: { $0 is IMKTextInput }) as? IMKTextInput,
           process(mappedEvent, client: client) {
            LifecycleTrace.record("handled input command selector=\(selectorName)")
            return
        }
        super.doCommand(by: selector, command: commandDictionary)
    }

    override func composedString(_ sender: Any!) -> Any! {
        isComposing ? composedText : ""
    }

    override func originalString(_ sender: Any!) -> NSAttributedString! {
        // TigerClaw's Escape/cancel policy discards the composition instead of
        // inserting raw keystrokes back into the client document.
        NSAttributedString(string: "")
    }

    override func commitComposition(_ sender: Any!) {
        guard let client = sender as? IMKTextInput,
              isComposing,
              let engine else {
            return
        }

        do {
            let result = try engine.process(commitEvent)
            LifecycleTrace.record("client requested composition commit handled=\(result.handled) commit=\(result.commit.debugDescription)")
            guard result.handled else {
                return
            }
            apply(result, to: client)
        } catch {
            LifecycleTrace.record("client composition commit failed \(error)")
        }
    }

    override func cancelComposition() {
        guard isComposing else {
            candidatePanel.hide()
            return
        }

        do {
            _ = try engine?.process(cancelEvent)
        } catch {
            LifecycleTrace.record("client composition cancel reset failed \(error)")
        }
        clearCompositionState()
        candidatePanel.hide()
        super.cancelComposition()
        LifecycleTrace.record("client requested composition cancel")
    }

    private func createEngine(reason: String, forceSchemaRefresh: Bool = false) {
        do {
            let configuration = NativeAotConfiguration.current
            let schema = NativeAotSchema.current
            let lexiconRevision = schema.contentRevision(forceRefresh: forceSchemaRefresh)
            let bridge = try NativeAotBridge(
                lexiconURL: schema.lexiconURL(),
                configuration: configuration,
                lexiconRevision: lexiconRevision)
            try bridge.activate()
            engine = bridge
            engineConfiguration = configuration
            engineSchema = schema
            engineLexiconRevision = lexiconRevision
            engineUserDictionaryRevision = NativeAotDataRoot.userDictionaryRevision()
            selectionKeyBindings = NativeAotSelectionKeyBindings.loadCurrentSchema()
            selectionKeyConfigurationRevision = NativeAotSelectionKeyBindings.configurationRevision()
            engineRedeployGeneration = NativeAotSchema.redeployGeneration
            LifecycleTrace.record("engine create ok reason=\(reason) schema=\(schema.identifier) userDictionary=\(engineUserDictionaryRevision ?? "missing") config=\(NativeAotConfiguration.diagnosticLines().joined(separator: ","))")
        } catch {
            engine = nil
            engineConfiguration = nil
            engineSchema = nil
            engineLexiconRevision = nil
            engineUserDictionaryRevision = nil
            selectionKeyConfigurationRevision = nil
            engineRedeployGeneration = nil
            LifecycleTrace.record("engine create failed reason=\(reason) error=\(error)")
        }
    }

    private static func clientIdentifier(_ sender: Any?) -> String {
        guard let client = sender as? IMKTextInput else {
            return sender.map { String(describing: type(of: $0)) } ?? "nil"
        }
        return client.bundleIdentifier() ?? "unknown"
    }

    private func reloadEngineForExternalChangesIfNeeded() {
        let configuration = NativeAotConfiguration.current
        let schema = NativeAotSchema.current
        let needsReload = engine == nil ||
            engineConfiguration != configuration ||
            engineSchema != schema ||
            engineLexiconRevision != schema.contentRevision() ||
            engineUserDictionaryRevision != NativeAotDataRoot.userDictionaryRevision() ||
            selectionKeyConfigurationRevision != NativeAotSelectionKeyBindings.configurationRevision() ||
            engineRedeployGeneration != NativeAotSchema.redeployGeneration
        guard needsReload else { return }

        if isComposing {
            cancelComposition()
        }
        engine?.deactivate()
        candidatePanel.hide()
        let redeployRequested = engineRedeployGeneration != NativeAotSchema.redeployGeneration
        createEngine(reason: "external-change", forceSchemaRefresh: redeployRequested)
    }

    private func process(_ mappedEvent: NativeAotInputEvent, client sender: Any!) -> Bool {
        if !inputMode.isChinese {
            return false
        }
        if mappedEvent.isApplicationShortcut {
            LifecycleTrace.record("shortcut pass-through \(mappedEvent.traceDescription)")
            return false
        }

        guard let client = sender as? IMKTextInput,
              let engine else {
            LifecycleTrace.record("process skipped client=\(sender is IMKTextInput) engine=\(engine != nil)")
            return false
        }

        if isComposing,
           !NativeAotConfiguration.current.sentenceInputActive(for: NativeAotSchema.current),
           mappedEvent.modifiers == 0,
           let selectionIndex = selectionKeyBindings.selectionIndex(for: UInt16(mappedEvent.physicalScanCode)) {
            return selectCandidate(at: selectionIndex, client: client)
        }

        do {
            let result = try engine.process(mappedEvent)
            LifecycleTrace.recordKeyEvent("event \(mappedEvent.traceDescription) handled=\(result.handled) composing=\(result.isComposing) selected=\(result.selectedIndex) caret=\(result.caret) page=\(result.pageIndex + 1)/\(result.pageCount) candidates=\(result.candidates.count)/\(result.candidateTotal) preedit=\(result.preedit.debugDescription) commit=\(result.commit.debugDescription) values=\(result.candidates)")

            guard result.handled else {
                return false
            }
            playTypingSound(for: mappedEvent)
            apply(result, to: client)
            return true
        } catch {
            LifecycleTrace.record("process failed \(mappedEvent.traceDescription) error=\(error)")
            return false
        }
    }

    private func apply(_ result: NativeAotResult, to client: IMKTextInput, scheduleSentenceRefresh: Bool = true) {
        let addWordRequest = result.action == .openAddWord
            ? quickAddWordRequest(useActiveComposition: false)
            : nil
        if !result.commit.isEmpty {
            appendCommittedText(result.commit)
            let replacementRange = client.markedRange()
            let resolvedReplacementRange = replacementRange.location == NSNotFound
                ? NSRange(location: NSNotFound, length: 0)
                : replacementRange
            LifecycleTrace.record(
                "commit client=\(client.bundleIdentifier() ?? "unknown") marked=\(replacementRange.location),\(replacementRange.length) replacement=\(resolvedReplacementRange.location),\(resolvedReplacementRange.length) text=\(result.commit.debugDescription)")
            client.insertText(result.commit, replacementRange: resolvedReplacementRange)
        }

        if CompositionPresentation.needsMarkedTextClear(wasComposing: isComposing, result: result) {
            client.setMarkedText(
                "",
                selectionRange: NSRange(location: 0, length: 0),
                replacementRange: NSRange(location: NSNotFound, length: 0)
            )
        }

        guard result.isComposing else {
            clearCompositionState()
            candidatePanel.hide()
            switch result.action {
            case .openAddWord:
                if let addWordRequest {
                    openQuickAddWord(request: addWordRequest)
                }
            case .toggleHideCandidates:
                toggleCandidateVisibilityFromQuickPhrase()
            case .none:
                break
            }
            return
        }

        let configuration = NativeAotConfiguration.current
        let displayPreedit = configuration.displayPreedit(
            compositionPrefix: result.compositionPrefix,
            activeInputCode: result.activeInputCode)
        composedText = displayPreedit
        isComposing = true
        activeInputCode = result.activeInputCode
        activeCandidates = result.candidates
        activeSelectedIndex = result.selectedIndex
        client.setMarkedText(
            displayPreedit,
            selectionRange: NSRange(location: displayPreedit.utf16.count, length: 0),
            replacementRange: NSRange(location: NSNotFound, length: 0)
        )
        if configuration.hideCandidates {
            candidatePanel.hide()
        } else {
            candidatePanel.present(
                candidates: result.candidates,
                selectedIndex: result.selectedIndex,
                pageIndex: result.pageIndex,
                pageCount: result.pageCount,
                preedit: displayPreedit,
                configuration: configuration,
                anchorRect: candidateAnchor(for: client, caret: displayPreedit.utf16.count),
                onCandidateSelected: { [weak self] pageIndex in
                    self?.selectCandidate(at: pageIndex, client: client)
                },
                onCandidateAdjusted: { [weak self] pageIndex, operation in
                    self?.adjustCandidate(at: pageIndex, operation: operation, client: client)
                })
        }

        if scheduleSentenceRefresh {
            if result.sentenceRerankPending {
                beginSentenceRerankRefresh(client: client)
            } else {
                sentenceRefreshGeneration += 1
                sentenceRefreshTimer?.invalidate()
                sentenceRefreshTimer = nil
                sentenceRefreshClient = nil
            }
        }
    }

    private func clearCompositionState() {
        sentenceRefreshGeneration += 1
        sentenceRefreshTimer?.invalidate()
        sentenceRefreshTimer = nil
        sentenceRefreshClient = nil
        composedText = ""
        isComposing = false
        activeInputCode = ""
        activeCandidates = []
        activeSelectedIndex = 0
    }

    private func beginSentenceRerankRefresh(client: IMKTextInput) {
        sentenceRefreshGeneration += 1
        sentenceRefreshTimer?.invalidate()
        sentenceRefreshClient = client
        sentenceRefreshDeadline = Date().addingTimeInterval(15)
        scheduleSentenceRerankPoll()
    }

    private func scheduleSentenceRerankPoll() {
        sentenceRefreshTimer = Timer.scheduledTimer(
            timeInterval: 0.05,
            target: self,
            selector: #selector(sentenceRerankTimerFired(_:)),
            userInfo: nil,
            repeats: false)
    }

    @objc private func sentenceRerankTimerFired(_ timer: Timer) {
        guard timer === sentenceRefreshTimer else { return }
        sentenceRefreshTimer = nil
        guard isComposing,
              Date() < sentenceRefreshDeadline,
              let client = sentenceRefreshClient,
              let engine else {
            sentenceRefreshClient = nil
            return
        }

        do {
            let result = try engine.currentSnapshot()
            let configuration = NativeAotConfiguration.current
            let displayPreedit = configuration.displayPreedit(
                compositionPrefix: result.compositionPrefix,
                activeInputCode: result.activeInputCode)
            if result.candidates != activeCandidates ||
                displayPreedit != composedText ||
                result.selectedIndex != activeSelectedIndex {
                apply(result, to: client, scheduleSentenceRefresh: false)
            }

            if result.sentenceRerankPending {
                scheduleSentenceRerankPoll()
            } else {
                sentenceRefreshClient = nil
            }
        } catch {
            sentenceRefreshClient = nil
            LifecycleTrace.record("sentence rerank refresh failed error=\(error)")
        }
    }

    private func openQuickAddWord() {
        openQuickAddWord(request: quickAddWordRequest(useActiveComposition: true))
    }

    private func quickAddWordRequest(useActiveComposition: Bool) -> QuickAddWordPrefillResult {
        QuickAddWordPrefill.resolve(
            activeInputCode: useActiveComposition ? activeInputCode : "",
            candidates: useActiveComposition ? activeCandidates : [],
            committedTextHistory: committedTextHistory,
            lexiconURL: NativeAotSchema.current.lexiconURL())
    }

    private func openQuickAddWord(request: QuickAddWordPrefillResult) {
        openTigerClawSettings(arguments: ["--quick-add-word", request.code, request.text])
        LifecycleTrace.record("quick add word opened code=\(request.code.debugDescription) text=\(request.text.debugDescription)")
    }

    private func toggleCandidateVisibilityFromQuickPhrase() {
        let hidden = !NativeAotConfiguration.current.hideCandidates
        do {
            try NativeAotConfiguration.setPersistedValue(
                key: "hide-candidates",
                value: hidden ? "on" : "off")
            candidatePanel.hide()
            LifecycleTrace.record("candidate visibility toggled by quick phrase hidden=\(hidden)")
        } catch {
            LifecycleTrace.record("candidate visibility quick phrase failed error=\(error)")
        }
    }

    private func appendCommittedText(_ text: String) {
        guard !text.isEmpty else { return }
        committedTextHistory.append(text)
        if committedTextHistory.count > 20 {
            committedTextHistory.removeFirst(committedTextHistory.count - 20)
        }
    }

    private func playTypingSound(for event: NativeAotInputEvent? = nil) {
        let configuration = NativeAotConfiguration.current
        guard configuration.keySoundEnabled,
              configuration.keySoundVolume > 0 else {
            return
        }

        let sound: NSSound?
        switch event?.key {
        case Int32(TC_INPUT_KEY_SPACE.rawValue):
            sound = spaceTypingSound
        case Int32(TC_INPUT_KEY_BACKSPACE.rawValue), Int32(TC_INPUT_KEY_ENTER.rawValue), Int32(TC_INPUT_KEY_ESCAPE.rawValue):
            sound = functionTypingSound
        default:
            sound = normalTypingSound
        }
        guard let sound else {
            NSSound.beep()
            return
        }
        sound.stop()
        sound.volume = Float(configuration.keySoundVolume) / 100
        sound.play()
    }

    private func handleModifierEvent(_ event: NSEvent, client sender: Any!) -> Bool {
        guard event.keyCode == UInt16(kVK_CapsLock) else {
            return false
        }

        guard NativeAotConfiguration.current.capsLockTogglesInputMode else {
            return false
        }

        toggleInputMode(client: sender as? IMKTextInput, reason: "caps-lock")
        return true
    }

    private func toggleInputMode(client: IMKTextInput?, reason: String) {
        if client != nil, isComposing {
            cancelComposition()
        }
        inputMode.toggle()
        candidatePanel.hide()
        LifecycleTrace.record("input mode toggled reason=\(reason) mode=\(inputMode.isChinese ? "chinese" : "english")")
    }

    @discardableResult
    private func toggleRecentSchema(client: IMKTextInput?) -> Bool {
        do {
            guard let schema = try NativeAotSchema.toggleRecent() else {
                LifecycleTrace.record("recent schema toggle ignored: no alternative schema")
                return false
            }
            if client != nil, isComposing {
                cancelComposition()
            }
            engine?.deactivate()
            candidatePanel.hide()
            createEngine(reason: "ctrl-m-schema-switch")
            try engine?.activate()
            LifecycleTrace.record("recent schema switched identifier=\(schema.identifier)")
            return true
        } catch {
            LifecycleTrace.record("recent schema switch failed error=\(error)")
            return false
        }
    }

    private func candidateAnchor(for client: IMKTextInput, caret: Int) -> NSRect? {
        let primaryIndex = max(0, caret)
        let candidateIndexes = primaryIndex == 0 ? [0] : [primaryIndex, 0]

        for index in candidateIndexes {
            var lineRect = NSRect.zero
            _ = client.attributes(
                forCharacterIndex: index,
                lineHeightRectangle: &lineRect)
            if CandidateAnchor.isUsable(lineRect) {
                return lineRect
            }
        }

        return nil
    }

    @discardableResult
    private func selectCandidate(at pageIndex: Int, client: IMKTextInput) -> Bool {
        guard isComposing,
              let engine else {
            return false
        }

        do {
            let result = try engine.selectCandidate(at: pageIndex)
            LifecycleTrace.record("candidate click pageIndex=\(pageIndex) handled=\(result.handled) commit=\(result.commit.debugDescription)")
            guard result.handled else {
                return false
            }
            playTypingSound()
            apply(result, to: client)
            return true
        } catch {
            LifecycleTrace.record("candidate click failed pageIndex=\(pageIndex) error=\(error)")
            return false
        }
    }

    private func adjustCandidate(
        at pageIndex: Int,
        operation: NativeAotCandidateAdjustment,
        client: IMKTextInput
    ) {
        guard pageIndex >= 0,
              pageIndex < activeCandidates.count,
              !activeInputCode.isEmpty else {
            return
        }

        let candidate = activeCandidates[pageIndex]
        do {
            try NativeAotUserDictionary.adjust(code: activeInputCode, text: candidate, operation: operation)
            LifecycleTrace.record("candidate adjustment operation=\(operation.rawValue) code=\(activeInputCode) text=\(candidate.debugDescription)")
            cancelComposition()
            engine?.deactivate()
            createEngine(reason: "candidate-adjustment")
            try engine?.activate()
            candidatePanel.hide()
        } catch {
            LifecycleTrace.record("candidate adjustment failed operation=\(operation.rawValue) error=\(error)")
        }
    }

    private var commitEvent: NativeAotInputEvent {
        NativeAotInputEvent(
            key: Int32(TC_INPUT_KEY_SPACE.rawValue), logicalText: " ", modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Space", physicalScanCode: 49)
    }

    private var cancelEvent: NativeAotInputEvent {
        NativeAotInputEvent(
            key: Int32(TC_INPUT_KEY_ESCAPE.rawValue), logicalText: "", modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Escape", physicalScanCode: 53)
    }
}
