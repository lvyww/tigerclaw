import Carbon
import Darwin
import Foundation

enum NativeAotSmoke {
    static func runIfRequested(arguments: [String]) -> Int32? {
        if let index = arguments.firstIndex(of: "--nativeaot-sentence-expect") {
            guard arguments.indices.contains(index + 2) else {
                print("usage: --nativeaot-sentence-expect <code> <expected-first-candidate>")
                return 64
            }
            return runSentenceExpectation(
                code: arguments[index + 1],
                expected: arguments[index + 2])
        }
        if let index = arguments.firstIndex(of: "--nativeaot-normal-expect") {
            guard arguments.indices.contains(index + 2) else {
                print("usage: --nativeaot-normal-expect <code> <expected-first-candidate>")
                return 64
            }
            return runNormalExpectation(
                code: arguments[index + 1],
                expected: arguments[index + 2])
        }
        if arguments.contains("--nativeaot-sentence-digit-smoke") {
            return runSentenceDigitSmoke()
        }
        if arguments.contains("--nativeaot-sentence-rules-smoke") {
            return runSentenceRulesSmoke()
        }
        if arguments.contains("--nativeaot-sentence-async-smoke") {
            return runSentenceAsyncSmoke()
        }
        if arguments.contains("--nativeaot-sentence-smoke") || arguments.contains("--nativeaot-sentence-smoke-current") {
            return runSentenceSmoke(useCurrentSchema: arguments.contains("--nativeaot-sentence-smoke-current"))
        }
        if arguments.contains("--nativeaot-perf-smoke") {
            return runPerformanceSmoke()
        }
        if arguments.contains("--nativeaot-revision-perf-smoke") {
            return runSchemaRevisionPerformanceSmoke()
        }
        if arguments.contains("--nativeaot-quick-symbol-smoke") {
            do {
                try verifyQuickSymbolReload()
                print("NATIVEAOT_QUICK_SYMBOL_SMOKE_PASS")
                print("Scenario=edit 快符.txt while running -> new [a mapping commits immediately")
                return 0
            } catch {
                print("NATIVEAOT_QUICK_SYMBOL_SMOKE_FAIL error=\(error)")
                return 1
            }
        }
        if arguments.contains("--nativeaot-quick-phrase-smoke") {
            do {
                try verifyQuickPhraseExpansion()
                print("NATIVEAOT_QUICK_PHRASE_SMOKE_PASS")
                print("Scenario=日期/时间/重复/随机快符 expand at commit time")
                return 0
            } catch {
                print("NATIVEAOT_QUICK_PHRASE_SMOKE_FAIL error=\(error)")
                return 1
            }
        }
        if arguments.contains("--nativeaot-quick-add-word-smoke") {
            do {
                try verifyQuickAddWordCodeConstruction()
                print("NATIVEAOT_QUICK_ADD_WORD_SMOKE_PASS")
                print("Scenario=recent commits construct TigerClaw code for Ctrl+=")
                return 0
            } catch {
                print("NATIVEAOT_QUICK_ADD_WORD_SMOKE_FAIL error=\(error)")
                return 1
            }
        }
        if arguments.contains("--nativeaot-windows-parity-smoke") {
            do {
                try verifyWindowsParityContracts()
                print("NATIVEAOT_WINDOWS_PARITY_SMOKE_PASS")
                print("Scenarios=active-schema sentence data, Windows 用户调整.txt, 补充语料.txt, bracket fast symbols, quick phrases")
                return 0
            } catch {
                print("NATIVEAOT_WINDOWS_PARITY_SMOKE_FAIL error=\(error)")
                return 1
            }
        }
        if arguments.contains("--nativeaot-schema-smoke") {
            do {
                try verifySchemaManagement()
                print("NATIVEAOT_SCHEMA_SMOKE_PASS")
                return 0
            } catch {
                print("NATIVEAOT_SCHEMA_SMOKE_FAIL error=\(error)")
                return 1
            }
        }
        guard arguments.contains("--nativeaot-smoke") else {
            return nil
        }

        do {
            let temporarySmokeUserDictionaryURL = FileManager.default.temporaryDirectory
                .appendingPathComponent("tigerclaw-nativeaot-smoke-\(UUID().uuidString).tsv")
            defer { try? FileManager.default.removeItem(at: temporarySmokeUserDictionaryURL) }
            let bridge = try NativeAotBridge(
                lexiconURL: NativeAotBridge.defaultLexiconURL(),
                configuration: NativeAotConfiguration(),
                userDictionaryURL: temporarySmokeUserDictionaryURL)
            try bridge.activate()

            let afterA = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue),
                logicalText: "a",
                modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue),
                physicalKey: "KeyA",
                physicalScanCode: 0
            ))
            guard afterA.handled,
                  afterA.preedit == "a",
                  afterA.candidates == ["来", "那个"],
                  afterA.candidateTotal == 2,
                  afterA.pageIndex == 0,
                  afterA.pageCount == 1 else {
                print("NATIVEAOT_IMK_SMOKE_FAIL afterA=\(afterA)")
                return 1
            }

            let afterSpace = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_SPACE.rawValue),
                logicalText: " ",
                modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue),
                physicalKey: "Space",
                physicalScanCode: 49
            ))
            guard afterSpace.handled,
                  afterSpace.commit == "来" else {
                print("NATIVEAOT_IMK_SMOKE_FAIL afterSpace=\(afterSpace)")
                return 1
            }

            print("NATIVEAOT_IMK_SMOKE_PASS")
            print("Scenario=a -> 来 -> Space")
            print("CandidatesAfterA=\(afterA.candidates)")

            _ = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "`", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Backquote", physicalScanCode: 50))
            _ = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "b", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyB", physicalScanCode: 11))
            let pinyinBa = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "a", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0))
            guard pinyinBa.preedit == "·ba", pinyinBa.candidates.first == "吧" else {
                print("NATIVEAOT_IMK_SMOKE_FAIL pinyin reverse=\(pinyinBa)")
                return 1
            }
            let pinyinCommit = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_SPACE.rawValue), logicalText: " ", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Space", physicalScanCode: 49))
            guard pinyinCommit.commit == "吧" else {
                print("NATIVEAOT_IMK_SMOKE_FAIL pinyin commit=\(pinyinCommit)")
                return 1
            }
            print("PinyinReverseScenario=`ba -> 吧")

            let selectionBridge = try NativeAotBridge(
                lexiconURL: NativeAotBridge.defaultLexiconURL(),
                configuration: NativeAotConfiguration(),
                userDictionaryURL: temporarySmokeUserDictionaryURL)
            try selectionBridge.activate()
            _ = try selectionBridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "a", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0))
            let selectedSecondCandidate = try selectionBridge.selectCandidate(at: 1)
            guard selectedSecondCandidate.handled,
                  selectedSecondCandidate.commit == "那个" else {
                print("NATIVEAOT_IMK_SMOKE_FAIL selectedSecondCandidate=\(selectedSecondCandidate)")
                return 1
            }
            print("CandidateSelectionScenario=a -> click second -> 那个")

            var customKeyConfiguration = NativeAotConfiguration()
            customKeyConfiguration.pageSize = 1
            customKeyConfiguration.selectionKeys = "s"
            customKeyConfiguration.previousPageKeys = "["
            customKeyConfiguration.nextPageKeys = "]"
            let customKeyBridge = try NativeAotBridge(
                lexiconURL: NativeAotBridge.defaultLexiconURL(),
                configuration: customKeyConfiguration,
                userDictionaryURL: temporarySmokeUserDictionaryURL)
            try customKeyBridge.activate()
            _ = try customKeyBridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "a", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0))
            let customNextPage = try customKeyBridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "]", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "BracketRight", physicalScanCode: 30))
            let customSelect = try customKeyBridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "s", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyS", physicalScanCode: 1))
            guard customNextPage.handled,
                  customNextPage.candidates == ["那个"],
                  customSelect.handled,
                  customSelect.commit == "那个" else {
                print("NATIVEAOT_IMK_SMOKE_FAIL customKey=(customNextPage), select=(customSelect)")
                return 1
            }
            print("CustomKeyScenario=a -> ] -> s -> 那个")

            var pageKeyConfiguration = NativeAotConfiguration()
            pageKeyConfiguration.pageSize = 1
            let pageKeyBridge = try NativeAotBridge(
                lexiconURL: NativeAotBridge.defaultLexiconURL(),
                configuration: pageKeyConfiguration,
                userDictionaryURL: temporarySmokeUserDictionaryURL)
            try pageKeyBridge.activate()
            _ = try pageKeyBridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "a", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0))
            let pageDown = try pageKeyBridge.process(NativeAotInputEvent(
                key: NativeAotInputEvent.abiKey(forKeyCode: UInt16(kVK_PageDown), logicalText: ""), logicalText: "", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: NativeAotInputEvent.physicalKeyName(for: UInt16(kVK_PageDown)), physicalScanCode: Int32(kVK_PageDown), isExtended: 1))
            let pageUp = try pageKeyBridge.process(NativeAotInputEvent(
                key: NativeAotInputEvent.abiKey(forKeyCode: UInt16(kVK_PageUp), logicalText: ""), logicalText: "", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: NativeAotInputEvent.physicalKeyName(for: UInt16(kVK_PageUp)), physicalScanCode: Int32(kVK_PageUp), isExtended: 1))
            guard pageDown.handled,
                  pageDown.candidates == ["那个"],
                  pageUp.handled,
                  pageUp.candidates == ["来"],
                  NativeAotInputEvent.abiKey(forKeyCode: UInt16(kVK_PageUp), logicalText: "") == Int32(TC_INPUT_KEY_PAGE_PREVIOUS.rawValue),
                  NativeAotInputEvent.abiKey(forKeyCode: UInt16(kVK_PageDown), logicalText: "") == Int32(TC_INPUT_KEY_PAGE_NEXT.rawValue) else {
                print("NATIVEAOT_IMK_SMOKE_FAIL pageKeys down=\\(pageDown) up=\\(pageUp)")
                return 1
            }
            print("PageKeyScenario=a -> PageDown -> 那个 -> PageUp -> 来")

            let arbitrarySelectionBindings = try NativeAotSelectionKeyBindings.parse("""
            1选 F2 VK_SHIFT
            2选 KEY_A 0x31 MAC_18
            3选 VK_CONTROL
            4选 VK_CAPITAL
            """)
            guard arbitrarySelectionBindings.selectionIndex(for: UInt16(kVK_F2)) == 0,
                  arbitrarySelectionBindings.selectionIndex(for: UInt16(kVK_Shift)) == 0,
                  arbitrarySelectionBindings.selectionIndex(for: UInt16(kVK_RightShift)) == 0,
                  arbitrarySelectionBindings.selectionIndex(for: UInt16(kVK_ANSI_A)) == 1,
                  arbitrarySelectionBindings.selectionIndex(for: UInt16(kVK_ANSI_1)) == 1,
                  arbitrarySelectionBindings.selectionIndex(for: UInt16(kVK_Control)) == 2,
                  arbitrarySelectionBindings.selectionIndex(for: UInt16(kVK_RightControl)) == 2,
                  arbitrarySelectionBindings.selectionIndex(for: UInt16(kVK_CapsLock)) == 3,
                  NativeAotInputEvent.physicalKeyName(for: UInt16(kVK_F2)) == "F2" else {
                print("NATIVEAOT_IMK_SMOKE_FAIL arbitrary selection keys")
                return 1
            }
            print("ArbitrarySelectionKeyScenario=F2/Shift/KeyA/Ctrl/CapsLock/keycode -> current page candidates")

            let punctuationBridge = try NativeAotBridge(
                lexiconURL: NativeAotBridge.defaultLexiconURL(),
                configuration: NativeAotConfiguration(),
                userDictionaryURL: temporarySmokeUserDictionaryURL)
            try punctuationBridge.activate()
            _ = try punctuationBridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "a", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0))
            let commaAfterCandidate = try punctuationBridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: ",", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Comma", physicalScanCode: 43))
            guard commaAfterCandidate.handled,
                  commaAfterCandidate.commit == "来，",
                  !commaAfterCandidate.isComposing else {
                print("NATIVEAOT_IMK_SMOKE_FAIL commaAfterCandidate=\(commaAfterCandidate)")
                return 1
            }
            print("CandidatePunctuationScenario=a, -> 来，")

            let idleComma = try punctuationBridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: ",", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Comma", physicalScanCode: 43))
            guard idleComma.handled,
                  idleComma.commit == "，",
                  !idleComma.isComposing else {
                print("NATIVEAOT_IMK_SMOKE_FAIL idleComma=\(idleComma)")
                return 1
            }
            print("IdlePunctuationScenario=, -> ，")

            let shiftDigitBridge = try NativeAotBridge(
                lexiconURL: NativeAotBridge.defaultLexiconURL(),
                configuration: NativeAotConfiguration(),
                userDictionaryURL: temporarySmokeUserDictionaryURL)
            try shiftDigitBridge.activate()
            _ = try shiftDigitBridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "a", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0))
            let shiftDigit = try shiftDigitBridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_DIGIT.rawValue), logicalText: "!", modifiers: 1,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Digit1", physicalScanCode: 18))
            guard shiftDigit.handled,
                  shiftDigit.commit == "来！",
                  !shiftDigit.isComposing,
                  NativeAotInputEvent.abiKey(
                    forKeyCode: UInt16(kVK_ANSI_1),
                    logicalText: "!") == Int32(TC_INPUT_KEY_DIGIT.rawValue),
                  NativeAotInputEvent.abiModifiers(
                    fromInputMethodKitModifiers: 1 << 17) == 1 else {
                print("NATIVEAOT_IMK_SMOKE_FAIL shiftDigit=\(shiftDigit)")
                return 1
            }
            print("CandidateShiftDigitScenario=a+Shift1 -> 来！")

            let commandShortcut = NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "v", modifiers: 1 << 3,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyV", physicalScanCode: 9)
            let shiftDigitShortcut = NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_DIGIT.rawValue), logicalText: "!", modifiers: 1,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Digit1", physicalScanCode: 18)
            guard commandShortcut.isApplicationShortcut,
                  !shiftDigitShortcut.isApplicationShortcut else {
                print("NATIVEAOT_IMK_SMOKE_FAIL shortcut routing")
                return 1
            }
            print("ShortcutScenario=Command-V passes through; Shift-1 remains IME input")

            let legacyCharacter = NativeAotInputEvent.legacyTextEvent("a")
            let legacySpace = NativeAotInputEvent.legacyTextEvent(" ")
            let legacyDigit = NativeAotInputEvent.legacyTextEvent("2")
            let legacyPageNext = NativeAotInputEvent.legacyTextEvent("=")
            guard legacyCharacter.key == Int32(TC_INPUT_KEY_CHARACTER.rawValue),
                  legacyCharacter.logicalText == "a",
                  legacyCharacter.physicalScanCode == -1,
                  legacySpace.key == Int32(TC_INPUT_KEY_SPACE.rawValue),
                  legacyDigit.key == Int32(TC_INPUT_KEY_DIGIT.rawValue),
                  legacyPageNext.key == Int32(TC_INPUT_KEY_PAGE_NEXT.rawValue),
                  NativeAotCommand.inputEvent(forSelectorName: "insertNewline:")?.key == Int32(TC_INPUT_KEY_ENTER.rawValue),
                  NativeAotCommand.inputEvent(forSelectorName: "moveDown:")?.key == Int32(TC_INPUT_KEY_ARROW_DOWN.rawValue) else {
                print("NATIVEAOT_IMK_SMOKE_FAIL legacy client routing")
                return 1
            }
            print("LegacyClientScenario=inputText:client: and didCommandBySelector:client: map to engine events")

            let compositionClearBridge = try NativeAotBridge(
                lexiconURL: NativeAotBridge.defaultLexiconURL(),
                configuration: NativeAotConfiguration(),
                userDictionaryURL: temporarySmokeUserDictionaryURL)
            try compositionClearBridge.activate()
            _ = try compositionClearBridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "a", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0))
            guard let deleteBackward = NativeAotCommand.inputEvent(forSelectorName: "deleteBackward:"),
                  NativeAotCommand.inputEvent(forSelectorName: "deleteForward:") == nil else {
                print("NATIVEAOT_IMK_SMOKE_FAIL command routing")
                return 1
            }
            let canceledComposition = try compositionClearBridge.process(deleteBackward)
            guard canceledComposition.handled,
                  canceledComposition.commit.isEmpty,
                  !canceledComposition.isComposing else {
                print("NATIVEAOT_IMK_SMOKE_FAIL canceledComposition=\(canceledComposition)")
                return 1
            }
            guard CompositionPresentation.needsMarkedTextClear(
                wasComposing: true,
                result: canceledComposition) else {
                print("NATIVEAOT_IMK_SMOKE_FAIL canceled composition must clear marked text")
                return 1
            }
            print("CompositionClearScenario=last Backspace clears marked text without a commit")

            let inlineAnchor = NSRect(x: 180, y: 320, width: 1, height: 18)
            guard !CandidateAnchor.isUsable(.zero),
                  CandidateAnchor.isUsable(inlineAnchor) else {
                print("NATIVEAOT_IMK_SMOKE_FAIL candidate anchor validation")
                return 1
            }
            print("CandidateAnchorScenario=empty primary -> valid selection anchor")

            let bundledSchemas = NativeAotSchema.bundled
            let sentenceSchema = NativeAotSchema.sentenceInputSchema
            guard bundledSchemas.map(\.identifier) == ["tiger_sentence", "tiger_sentence_full"],
                  bundledSchemas.map(\.displayName) == ["虎单", "虎整句"],
                  sentenceSchema.identifier == "tiger_sentence_full",
                  FileManager.default.fileExists(atPath: sentenceSchema.lexiconURL().path),
                  FileManager.default.fileExists(atPath: NativeAotSchema.current.lexiconURL().path) else {
                print("NATIVEAOT_IMK_SMOKE_FAIL schema registry")
                return 1
            }

            var constrainedConfiguration = NativeAotConfiguration()
            constrainedConfiguration.maxCandidates = 1
            constrainedConfiguration.pageSize = 1
            let constrainedBridge = try NativeAotBridge(
                lexiconURL: NativeAotBridge.defaultLexiconURL(),
                configuration: constrainedConfiguration,
                userDictionaryURL: temporarySmokeUserDictionaryURL)
            try constrainedBridge.activate()
            let constrainedAfterA = try constrainedBridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "a", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0))
            guard constrainedAfterA.candidates == ["来"],
                  constrainedAfterA.candidateTotal == 1,
                  constrainedAfterA.pageCount == 1 else {
                print("NATIVEAOT_IMK_SMOKE_FAIL constrainedConfiguration=\(constrainedAfterA)")
                return 1
            }
            print("ConfigurationScenario=max-candidates=1 -> [来]")

            guard CandidateRevealPolicy.isExpanded(delayMs: 0, elapsedMs: 0),
                  !CandidateRevealPolicy.isExpanded(delayMs: 500, elapsedMs: 499),
                  CandidateRevealPolicy.isExpanded(delayMs: 500, elapsedMs: 500) else {
                print("NATIVEAOT_IMK_SMOKE_FAIL candidate reveal delay")
                return 1
            }
            print("CandidateRevealDelayScenario=composition-relative delay expands at configured threshold")

            var maskingConfiguration = NativeAotConfiguration()
            maskingConfiguration.codeMasking = "①②"
            guard maskingConfiguration.displayPreedit(compositionPrefix: "来", activeInputCode: "ab;") == "来①②①" else {
                print("NATIVEAOT_IMK_SMOKE_FAIL code masking")
                return 1
            }
            print("CodeMaskingScenario=来ab; -> 来①②①")

            var inputMode = TigerClawInputMode(defaultChinese: true)
            inputMode.toggle()
            guard !inputMode.isChinese else {
                print("NATIVEAOT_IMK_SMOKE_FAIL input mode toggle")
                return 1
            }
            inputMode.toggle()
            guard inputMode.isChinese else {
                print("NATIVEAOT_IMK_SMOKE_FAIL input mode restore")
                return 1
            }
            print("InputModeScenario=Chinese -> English direct -> Chinese")

            var fixedContinuationConfiguration = NativeAotConfiguration()
            fixedContinuationConfiguration.autoCommitUniqueTerminalCode = false
            let fixedContinuationBridge = try NativeAotBridge(
                lexiconURL: NativeAotSchema.bundled[0].lexiconURL(),
                configuration: fixedContinuationConfiguration,
                userDictionaryURL: temporarySmokeUserDictionaryURL)
            try fixedContinuationBridge.activate()
            for character in "aaaa" {
                _ = try fixedContinuationBridge.process(NativeAotInputEvent(
                    key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: String(character), modifiers: 0,
                    action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0))
            }
            let fixedContinuation = try fixedContinuationBridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "Q", modifiers: 1,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyQ", physicalScanCode: 12))
            guard fixedContinuation.commit == "卍",
                  fixedContinuation.isComposing,
                  fixedContinuation.preedit == "q" else {
                print("NATIVEAOT_IMK_SMOKE_FAIL fixedContinuation=\(fixedContinuation)")
                return 1
            }
            print("FixedContinuationScenario=aaaaQ -> 卍 + q")

            let temporaryUserDictionaryURL = FileManager.default.temporaryDirectory
                .appendingPathComponent("tigerclaw-nativeaot-user-\(UUID().uuidString).tsv")
            defer { try? FileManager.default.removeItem(at: temporaryUserDictionaryURL) }
            try "自定义\ta\n".write(to: temporaryUserDictionaryURL, atomically: true, encoding: .utf8)
            let userDictionaryBridge = try NativeAotBridge(
                lexiconURL: NativeAotBridge.defaultLexiconURL(),
                configuration: NativeAotConfiguration(),
                userDictionaryURL: temporaryUserDictionaryURL)
            try userDictionaryBridge.activate()
            let userDictionaryAfterA = try userDictionaryBridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "a", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0))
            guard userDictionaryAfterA.candidates == ["自定义", "来", "那个"] else {
                print("NATIVEAOT_IMK_SMOKE_FAIL userDictionary=\(userDictionaryAfterA)")
                return 1
            }
            print("UserDictionaryScenario=a -> 自定义")

            try verifyQuickSymbolReload()
            print("QuickSymbolReloadScenario=edit 快符.txt ;a -> [a takes effect without restarting the process")
            try verifyQuickSymbolInSentenceMode()
            print("SentenceQuickSymbolScenario=active schema 烟 + [a -> 烟！ without committing at [")

            try verifyQuickPhraseExpansion()
            print("QuickPhraseScenario=日期/时间/重复/随机快符 expand at commit time")

            var mixedConfiguration = NativeAotConfiguration()
            mixedConfiguration.maxCodeLength = 1
            mixedConfiguration.autoCommitUniqueTerminalCode = false
            mixedConfiguration.unlimitedMixedInput = true
            let mixedBridge = try NativeAotBridge(
                lexiconURL: NativeAotBridge.defaultLexiconURL(),
                configuration: mixedConfiguration,
                userDictionaryURL: temporarySmokeUserDictionaryURL)
            try mixedBridge.activate()
            _ = try mixedBridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "a", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0))
            let mixedPreedit = try mixedBridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "A", modifiers: 1,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0))
            guard mixedPreedit.preedit == "来A",
                  mixedPreedit.compositionPrefix == "来",
                  mixedPreedit.activeInputCode == "A" else {
                print("NATIVEAOT_IMK_SMOKE_FAIL mixedPreedit=\(mixedPreedit)")
                return 1
            }
            let mixedCommit = try mixedBridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_SPACE.rawValue), logicalText: " ", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Space", physicalScanCode: 49))
            guard mixedCommit.commit == "来来" else {
                print("NATIVEAOT_IMK_SMOKE_FAIL mixedCommit=\(mixedCommit)")
                return 1
            }
            print("MixedScenario=aA -> 来A -> 来来")

            try verifySchemaManagement()
            print("SchemaManagementScenario=isolated import -> rename -> per-schema data -> export -> delete")
            return 0
        } catch {
            print("NATIVEAOT_IMK_SMOKE_FAIL error=\(error)")
            return 1
        }
    }

    private static func runSentenceExpectation(code: String, expected: String) -> Int32 {
        do {
            var configuration = NativeAotConfiguration.current
            configuration.sentenceInputEnabled = true
            configuration.sentenceNeuralRerankEnabled = true
            configuration.maxCandidates = 20
            let bridge = try NativeAotBridge(
                lexiconURL: NativeAotSchema.current.lexiconURL(),
                configuration: configuration)
            try bridge.activate()

            var result: NativeAotResult?
            for (offset, letter) in code.enumerated() {
                result = try bridge.process(NativeAotInputEvent(
                    key: Int32(TC_INPUT_KEY_CHARACTER.rawValue),
                    logicalText: String(letter),
                    modifiers: 0,
                    action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue),
                    physicalKey: "Key\(String(letter).uppercased())",
                    physicalScanCode: Int32(offset)))
            }
            guard var result else {
                print("NATIVEAOT_SENTENCE_EXPECT_FAIL code=\(code) no-result")
                return 1
            }
            let deadline = Date().addingTimeInterval(15)
            while result.sentenceRerankPending && Date() < deadline {
                Thread.sleep(forTimeInterval: 0.05)
                result = try bridge.currentSnapshot()
            }
            let first = result.candidates.first ?? ""
            guard first == expected else {
                print("NATIVEAOT_SENTENCE_EXPECT_FAIL code=\(code) expected=\(expected) actual=\(first) segmented=\(result.activeInputCode) candidates=\(result.candidates)")
                return 1
            }
            print("NATIVEAOT_SENTENCE_EXPECT_PASS code=\(code) first=\(first) segmented=\(result.activeInputCode)")
            return 0
        } catch {
            print("NATIVEAOT_SENTENCE_EXPECT_FAIL code=\(code) error=\(error)")
            return 1
        }
    }

    private static func runNormalExpectation(code: String, expected: String) -> Int32 {
        do {
            var configuration = NativeAotConfiguration.current
            configuration.sentenceInputEnabled = false
            configuration.autoSentenceInput = false
            configuration.sentenceNeuralRerankEnabled = false
            configuration.autoCommitUniqueTerminalCode = false
            configuration.maxCandidates = 20
            let bridge = try NativeAotBridge(
                lexiconURL: NativeAotSchema.current.lexiconURL(),
                configuration: configuration)
            try bridge.activate()

            var result: NativeAotResult?
            for (offset, letter) in code.enumerated() {
                result = try bridge.process(NativeAotInputEvent(
                    key: Int32(TC_INPUT_KEY_CHARACTER.rawValue),
                    logicalText: String(letter),
                    modifiers: 0,
                    action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue),
                    physicalKey: "Key\(String(letter).uppercased())",
                    physicalScanCode: Int32(offset)))
            }
            guard let result else {
                print("NATIVEAOT_NORMAL_EXPECT_FAIL code=\(code) no-result")
                return 1
            }
            let first = result.candidates.first ?? ""
            guard first == expected else {
                print("NATIVEAOT_NORMAL_EXPECT_FAIL code=\(code) expected=\(expected) actual=\(first) candidates=\(result.candidates)")
                return 1
            }
            print("NATIVEAOT_NORMAL_EXPECT_PASS code=\(code) first=\(first)")
            return 0
        } catch {
            print("NATIVEAOT_NORMAL_EXPECT_FAIL code=\(code) error=\(error)")
            return 1
        }
    }

    private static func runSentenceDigitSmoke() -> Int32 {
        do {
            var configuration = NativeAotConfiguration.current
            configuration.sentenceInputEnabled = true
            configuration.autoSentenceInput = false
            configuration.sentenceNeuralRerankEnabled = false
            configuration.autoCommitUniqueTerminalCode = false
            let bridge = try NativeAotBridge(
                lexiconURL: NativeAotSchema.current.lexiconURL(),
                configuration: configuration)
            try bridge.activate()

            let idleDigit = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_DIGIT.rawValue), logicalText: "2", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Digit2", physicalScanCode: 19))
            guard !idleDigit.handled,
                  !idleDigit.isComposing,
                  idleDigit.preedit.isEmpty,
                  idleDigit.candidates.isEmpty else {
                print("NATIVEAOT_SENTENCE_DIGIT_SMOKE_FAIL idle=\(idleDigit)")
                return 1
            }

            let decimalPoint = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: ".", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Period", physicalScanCode: Int32(kVK_ANSI_Period)))
            guard decimalPoint.handled,
                  decimalPoint.commit == ".",
                  !decimalPoint.isComposing else {
                print("NATIVEAOT_SENTENCE_DIGIT_SMOKE_FAIL decimal=\(decimalPoint)")
                return 1
            }

            for (offset, letter) in "tu".enumerated() {
                _ = try bridge.process(NativeAotInputEvent(
                    key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: String(letter), modifiers: 0,
                    action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue),
                    physicalKey: "Key\(String(letter).uppercased())", physicalScanCode: Int32(offset)))
            }
            let selectorDigit = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_DIGIT.rawValue), logicalText: "2", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Digit2", physicalScanCode: 19))
            guard selectorDigit.handled,
                  selectorDigit.isComposing,
                  selectorDigit.activeInputCode == "tu2" else {
                print("NATIVEAOT_SENTENCE_DIGIT_SMOKE_FAIL selector=\(selectorDigit)")
                return 1
            }

            print("NATIVEAOT_SENTENCE_DIGIT_SMOKE_PASS idle=pass-through decimal=period composing=selector")
            return 0
        } catch {
            print("NATIVEAOT_SENTENCE_DIGIT_SMOKE_FAIL error=\(error)")
            return 1
        }
    }

    private static func runSentenceRulesSmoke() -> Int32 {
        do {
            let temporaryDirectory = FileManager.default.temporaryDirectory
                .appendingPathComponent("tigerclaw-nativeaot-sentence-rules-\(UUID().uuidString)", isDirectory: true)
            defer { try? FileManager.default.removeItem(at: temporaryDirectory) }
            try FileManager.default.createDirectory(at: temporaryDirectory, withIntermediateDirectories: true)
            let lexiconURL = temporaryDirectory.appendingPathComponent("rules.dict.yaml")
            let userDictionaryURL = temporaryDirectory.appendingPathComponent("user.tsv")
            try """
            name: rules
            version: "1"
            columns:
              - text
              - weight
              - code
              - stem
            ...
            的 100 u aa
            的 90 u abc
            甲 100 j db
            乙 90 j db
            是 100 o ot
            """.write(to: lexiconURL, atomically: true, encoding: .utf8)

            func decode(_ code: String, configuration: NativeAotConfiguration) throws -> NativeAotResult {
                let bridge = try NativeAotBridge(
                    lexiconURL: lexiconURL,
                    configuration: configuration,
                    userDictionaryURL: userDictionaryURL)
                try bridge.activate()
                var result: NativeAotResult?
                for (offset, letter) in code.enumerated() {
                    result = try bridge.process(NativeAotInputEvent(
                        key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: String(letter), modifiers: 0,
                        action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Key\(String(letter).uppercased())", physicalScanCode: Int32(offset)))
                }
                let deadline = Date().addingTimeInterval(10)
                while result?.sentenceRerankPending == true && Date() < deadline {
                    Thread.sleep(forTimeInterval: 0.01)
                    result = try bridge.currentSnapshot()
                }
                guard let result, !result.sentenceRerankPending else {
                    throw NativeAotBridgeError.runtimeCreateFailed(-1)
                }
                return result
            }

            var restricted = NativeAotConfiguration.current
            restricted.sentenceInputEnabled = true
            restricted.autoSentenceInput = false
            restricted.sentenceNeuralRerankEnabled = false
            restricted.autoCommitUniqueTerminalCode = false
            restricted.sentenceOptimalCodeHighFreqLimit = 1
            restricted.sentenceFullCodeWhitelist = ""
            restricted.sentenceAllowDuplicateSingleCharacters = false

            let blockedFullCode = try decode("abcot", configuration: restricted)
            guard !blockedFullCode.candidates.contains("的是") else {
                print("NATIVEAOT_SENTENCE_RULES_SMOKE_FAIL high-frequency=\(blockedFullCode)")
                return 1
            }

            var whitelisted = restricted
            whitelisted.sentenceFullCodeWhitelist = "的"
            let allowedFullCode = try decode("abcot", configuration: whitelisted)
            guard allowedFullCode.candidates.contains("的是") else {
                print("NATIVEAOT_SENTENCE_RULES_SMOKE_FAIL whitelist=\(allowedFullCode)")
                return 1
            }

            var noDuplicates = restricted
            noDuplicates.sentenceOptimalCodeHighFreqLimit = 0
            let filteredDuplicate = try decode("dbot", configuration: noDuplicates)
            guard !filteredDuplicate.candidates.contains("乙是") else {
                print("NATIVEAOT_SENTENCE_RULES_SMOKE_FAIL duplicate-off=\(filteredDuplicate)")
                return 1
            }

            var allowDuplicates = noDuplicates
            allowDuplicates.sentenceAllowDuplicateSingleCharacters = true
            let allowedDuplicate = try decode("dbot", configuration: allowDuplicates)
            guard allowedDuplicate.candidates.contains("乙是") else {
                print("NATIVEAOT_SENTENCE_RULES_SMOKE_FAIL duplicate-on=\(allowedDuplicate)")
                return 1
            }

            print("NATIVEAOT_SENTENCE_RULES_SMOKE_PASS high-frequency=filtered whitelist=allowed duplicate=toggle")
            return 0
        } catch {
            print("NATIVEAOT_SENTENCE_RULES_SMOKE_FAIL error=\(error)")
            return 1
        }
    }

    private static func runSentenceAsyncSmoke() -> Int32 {
        do {
            var configuration = NativeAotConfiguration.current
            configuration.sentenceInputEnabled = true
            configuration.autoSentenceInput = false
            configuration.sentenceNeuralRerankEnabled = false
            configuration.autoCommitUniqueTerminalCode = false
            configuration.unlimitedMixedInput = false
            configuration.maxCandidates = 20
            // Keep this latency smoke deterministic. Product input now uses
            // the active schema (like Windows); this fixture explicitly uses
            // the bundled 虎整句 table whose `tuot` expectation is 我是.
            let sentenceLexiconURL = NativeAotSchema.sentenceInputSchema.lexiconURL()
            let bridge = try NativeAotBridge(
                lexiconURL: sentenceLexiconURL,
                configuration: configuration)
            try bridge.activate()

            func type(_ letter: Character, offset: Int) throws -> (NativeAotResult, Int) {
                let started = Date()
                let result = try bridge.process(NativeAotInputEvent(
                    key: Int32(TC_INPUT_KEY_CHARACTER.rawValue),
                    logicalText: String(letter),
                    modifiers: 0,
                    action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue),
                    physicalKey: "Key\(String(letter).uppercased())",
                    physicalScanCode: Int32(offset)))
                return (result, Int(Date().timeIntervalSince(started) * 1_000))
            }

            let (singleA, _) = try type("a", offset: 0)
            guard singleA.candidates.first == "来", !singleA.sentenceRerankPending else {
                print("NATIVEAOT_SENTENCE_ASYNC_SMOKE_FAIL single-code=\(singleA)")
                return 1
            }
            let semicolonSelection = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_SEMICOLON.rawValue), logicalText: ";", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Semicolon", physicalScanCode: 41))
            guard semicolonSelection.commit == "那个", !semicolonSelection.isComposing else {
                print("NATIVEAOT_SENTENCE_ASYNC_SMOKE_FAIL single-code-semicolon=\(semicolonSelection)")
                return 1
            }

            let (afterT, firstKeyMilliseconds) = try type("t", offset: 0)
            guard afterT.handled,
                  afterT.isComposing,
                  !afterT.sentenceRerankPending,
                  afterT.candidates == ["我", "我们"],
                  firstKeyMilliseconds < 100 else {
                print("NATIVEAOT_SENTENCE_ASYNC_SMOKE_FAIL first=\(afterT) key-ms=\(firstKeyMilliseconds)")
                return 1
            }

            let (afterU, secondKeyMilliseconds) = try type("u", offset: 1)
            guard afterU.sentenceRerankPending,
                  secondKeyMilliseconds < 100,
                  afterU.candidates == afterT.candidates,
                  afterU.activeInputCode.replacingOccurrences(of: " ", with: "") == "tu" else {
                print("NATIVEAOT_SENTENCE_ASYNC_SMOKE_FAIL retained=\(afterU) previous=\(afterT.candidates) key-ms=\(secondKeyMilliseconds)")
                return 1
            }

            let (afterO, thirdKeyMilliseconds) = try type("o", offset: 2)
            let (afterFinalT, fourthKeyMilliseconds) = try type("t", offset: 3)
            guard afterO.sentenceRerankPending,
                  afterFinalT.sentenceRerankPending,
                  max(thirdKeyMilliseconds, fourthKeyMilliseconds) < 100 else {
                print("NATIVEAOT_SENTENCE_ASYNC_SMOKE_FAIL rapid-input after-o=\(afterO) after-t=\(afterFinalT) key-ms=[\(thirdKeyMilliseconds), \(fourthKeyMilliseconds)]")
                return 1
            }

            var final = afterFinalT
            let finalDeadline = Date().addingTimeInterval(10)
            while final.sentenceRerankPending && Date() < finalDeadline {
                Thread.sleep(forTimeInterval: 0.01)
                final = try bridge.currentSnapshot()
            }
            guard !final.sentenceRerankPending,
                  final.activeInputCode.replacingOccurrences(of: " ", with: "") == "tuot",
                  final.candidates.first == "我是" else {
                print("NATIVEAOT_SENTENCE_ASYNC_SMOKE_FAIL final=\(final)")
                return 1
            }

            _ = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_ESCAPE.rawValue), logicalText: "", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Escape", physicalScanCode: Int32(kVK_Escape)))
            for (offset, letter) in "tuot".enumerated() {
                _ = try type(letter, offset: offset)
            }
            let committedWhilePending = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_SPACE.rawValue), logicalText: " ", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Space", physicalScanCode: Int32(kVK_Space)))
            guard committedWhilePending.commit == "我是", !committedWhilePending.isComposing else {
                print("NATIVEAOT_SENTENCE_ASYNC_SMOKE_FAIL synchronous commit=\(committedWhilePending)")
                return 1
            }

            for (offset, letter) in "tuotx".enumerated() {
                _ = try type(letter, offset: offset)
            }
            let afterBackspace = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_BACKSPACE.rawValue), logicalText: "", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Backspace", physicalScanCode: Int32(kVK_Delete)))
            guard afterBackspace.sentenceRerankPending,
                  afterBackspace.activeInputCode.replacingOccurrences(of: " ", with: "") == "tuot" else {
                print("NATIVEAOT_SENTENCE_ASYNC_SMOKE_FAIL backspace pending=\(afterBackspace)")
                return 1
            }
            var afterBackspaceReady = afterBackspace
            let backspaceDeadline = Date().addingTimeInterval(10)
            while afterBackspaceReady.sentenceRerankPending && Date() < backspaceDeadline {
                Thread.sleep(forTimeInterval: 0.01)
                afterBackspaceReady = try bridge.currentSnapshot()
            }
            guard !afterBackspaceReady.sentenceRerankPending,
                  afterBackspaceReady.candidates.first == "我是" else {
                print("NATIVEAOT_SENTENCE_ASYNC_SMOKE_FAIL stale backspace result=\(afterBackspaceReady)")
                return 1
            }

            print("NATIVEAOT_SENTENCE_ASYNC_SMOKE_PASS key-ms=[\(firstKeyMilliseconds), \(secondKeyMilliseconds), \(thirdKeyMilliseconds), \(fourthKeyMilliseconds)] first=\(final.candidates[0])")
            return 0
        } catch {
            print("NATIVEAOT_SENTENCE_ASYNC_SMOKE_FAIL error=\(error)")
            return 1
        }
    }

    private static func runSentenceSmoke(useCurrentSchema: Bool) -> Int32 {
        do {
            var configuration = NativeAotConfiguration()
            configuration.sentenceInputEnabled = true
            configuration.maxCandidates = 20
            let temporaryUserDictionaryURL = FileManager.default.temporaryDirectory
                .appendingPathComponent("tigerclaw-sentence-smoke-\(UUID().uuidString).tsv")
            defer { try? FileManager.default.removeItem(at: temporaryUserDictionaryURL) }

            let loadStarted = Date()
            let bridge = try NativeAotBridge(
                lexiconURL: useCurrentSchema ? NativeAotSchema.current.lexiconURL() : NativeAotBridge.defaultLexiconURL(),
                configuration: configuration,
                userDictionaryURL: temporaryUserDictionaryURL)
            try bridge.activate()
            let loadMilliseconds = Int(Date().timeIntervalSince(loadStarted) * 1_000)

            let code = "aujtijxriej"
            var result: NativeAotResult?
            var keyMilliseconds: [Int] = []
            for (offset, letter) in code.enumerated() {
                let started = Date()
                result = try bridge.process(NativeAotInputEvent(
                    key: Int32(TC_INPUT_KEY_CHARACTER.rawValue),
                    logicalText: String(letter),
                    modifiers: 0,
                    action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue),
                    physicalKey: "Key\(String(letter).uppercased())",
                    physicalScanCode: Int32(offset)))
                keyMilliseconds.append(Int(Date().timeIntervalSince(started) * 1_000))
            }

            guard var decodedResult = result else {
                print("NATIVEAOT_SENTENCE_SMOKE_FAIL result=nil")
                return 1
            }
            let decodeDeadline = Date().addingTimeInterval(10)
            while decodedResult.candidates.isEmpty && decodedResult.sentenceRerankPending && Date() < decodeDeadline {
                Thread.sleep(forTimeInterval: 0.01)
                decodedResult = try bridge.currentSnapshot()
            }
            guard decodedResult.handled || decodedResult.isComposing,
                  decodedResult.isComposing,
                  !decodedResult.candidates.isEmpty,
                  decodedResult.activeInputCode.replacingOccurrences(of: " ", with: "") == code else {
                print("NATIVEAOT_SENTENCE_SMOKE_FAIL result=\(decodedResult)")
                return 1
            }

            var rankedResult = decodedResult
            let rerankDeadline = Date().addingTimeInterval(15)
            while rankedResult.sentenceRerankPending && Date() < rerankDeadline {
                Thread.sleep(forTimeInterval: 0.05)
                rankedResult = try bridge.currentSnapshot()
            }
            guard !rankedResult.sentenceRerankPending else {
                print("NATIVEAOT_SENTENCE_SMOKE_FAIL Qwen rerank timed out")
                return 1
            }

            let selected = rankedResult.candidates[0]
            let committed = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_SPACE.rawValue),
                logicalText: " ",
                modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue),
                physicalKey: "Space",
                physicalScanCode: Int32(kVK_Space)))
            guard committed.commit == selected, !committed.isComposing else {
                print("NATIVEAOT_SENTENCE_SMOKE_FAIL commit=\(committed) selected=\(selected)")
                return 1
            }

            _ = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "a", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0))
            var secondRank = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_SEMICOLON.rawValue), logicalText: ";", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Semicolon", physicalScanCode: Int32(kVK_ANSI_Semicolon)))
            let selectorDeadline = Date().addingTimeInterval(10)
            while secondRank.candidates.isEmpty && secondRank.sentenceRerankPending && Date() < selectorDeadline {
                Thread.sleep(forTimeInterval: 0.01)
                secondRank = try bridge.currentSnapshot()
            }
            guard secondRank.activeInputCode == "a;", secondRank.candidates.first == "那个" else {
                print("NATIVEAOT_SENTENCE_SMOKE_FAIL selector=\(secondRank)")
                return 1
            }
            _ = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_ESCAPE.rawValue), logicalText: "", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Escape", physicalScanCode: Int32(kVK_Escape)))

            var traversed: NativeAotResult?
            for (offset, letter) in code.enumerated() {
                traversed = try bridge.process(NativeAotInputEvent(
                    key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: String(letter), modifiers: 0,
                    action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Key\(String(letter).uppercased())", physicalScanCode: Int32(offset)))
            }
            guard let beforeTraverse = traversed, beforeTraverse.isComposing else {
                print("NATIVEAOT_SENTENCE_SMOKE_FAIL traverse-source=\(String(describing: traversed))")
                return 1
            }
            let afterTab = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_TAB.rawValue), logicalText: "\t", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Tab", physicalScanCode: Int32(kVK_Tab)))
            guard afterTab.selectedIndex == 1,
                  afterTab.candidates.count > 1,
                  afterTab.activeInputCode.replacingOccurrences(of: " ", with: "") == code else {
                print("NATIVEAOT_SENTENCE_SMOKE_FAIL tab=\(afterTab)")
                return 1
            }
            let clicked = try bridge.selectCandidate(at: 1)
            guard clicked.commit == afterTab.candidates[1] else {
                print("NATIVEAOT_SENTENCE_SMOKE_FAIL click=\(clicked)")
                return 1
            }

            print("NATIVEAOT_SENTENCE_SMOKE_PASS load-ms=\(loadMilliseconds) key-ms=\(keyMilliseconds) top=\(selected) segmented=\(rankedResult.activeInputCode) candidates=\(rankedResult.candidates.count) qwen=ready")
            return 0
        } catch {
            print("NATIVEAOT_SENTENCE_SMOKE_FAIL error=\(error)")
            return 1
        }
    }

    private static func runPerformanceSmoke() -> Int32 {
        do {
            let schema = NativeAotSchema.current
            var durations: [TimeInterval] = []
            for _ in 0 ..< 3 {
                let started = Date()
                let bridge = try NativeAotBridge(
                    lexiconURL: schema.lexiconURL(),
                    configuration: NativeAotConfiguration.current)
                try bridge.activate()
                bridge.deactivate()
                durations.append(Date().timeIntervalSince(started) * 1_000)
            }
            let ordered = durations.sorted()
            let message = "schema=\(schema.identifier) median-ms=\(Int(ordered[1])) samples-ms=\(ordered.map { Int($0) }) cached-runtimes=\(NativeAotRuntimeCache.shared.count)"
            guard ordered[1] < 250 else {
                print("NATIVEAOT_PERF_SMOKE_FAIL \(message)")
                return 1
            }
            print("NATIVEAOT_PERF_SMOKE_PASS \(message)")
            return 0
        } catch {
            print("NATIVEAOT_PERF_SMOKE_FAIL error=\(error)")
            return 1
        }
    }

    private static func runSchemaRevisionPerformanceSmoke() -> Int32 {
        let schema = NativeAotSchema.current
        let samples = 20
        _ = schema.contentRevision()
        let started = Date()
        for _ in 0 ..< samples {
            _ = schema.contentRevision()
        }
        let averageMilliseconds = Date().timeIntervalSince(started) * 1_000 / Double(samples)
        let message = String(format: "schema=%@ revision-check-average-ms=%.2f", schema.identifier, averageMilliseconds)
        guard averageMilliseconds < 2 else {
            print("NATIVEAOT_REVISION_PERF_SMOKE_FAIL \(message)")
            return 1
        }
        print("NATIVEAOT_REVISION_PERF_SMOKE_PASS \(message)")
        return 0
    }

    private static func verifyQuickSymbolReload() throws {
        let temporaryDirectory = FileManager.default.temporaryDirectory
            .appendingPathComponent("tigerclaw-nativeaot-quick-symbol-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: temporaryDirectory) }
        try FileManager.default.createDirectory(at: temporaryDirectory, withIntermediateDirectories: true)

        let lexiconURL = temporaryDirectory.appendingPathComponent("quick.dict.yaml")
        let quickSymbolURL = temporaryDirectory.appendingPathComponent("快符.txt")
        let userDictionaryURL = temporaryDirectory.appendingPathComponent("user.tsv")
        try "---\nname: quick\ncolumns:\n  - text\n  - code\n...\n来\ta\n".write(
            to: lexiconURL,
            atomically: true,
            encoding: .utf8)
        try "！\t[a\n{日期.}\t[d\n".write(
            to: quickSymbolURL,
            atomically: true,
            encoding: .utf8)

        let schema = NativeAotSchema(
            identifier: "quick-symbol-smoke",
            displayName: "quick-symbol-smoke",
            lexiconResourceName: "",
            importedLexiconURL: lexiconURL)
        let revisionBeforeEdit = schema.contentRevision()

        func quickSymbolCommit(revision: String) throws -> String {
            let bridge = try NativeAotBridge(
                lexiconURL: lexiconURL,
                configuration: NativeAotConfiguration(),
                userDictionaryURL: userDictionaryURL,
                lexiconRevision: revision)
            try bridge.activate()
            _ = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "[", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "BracketLeft", physicalScanCode: 33))
            let result = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "a", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0))
            bridge.deactivate()
            return result.commit
        }

        let beforeEdit = try quickSymbolCommit(revision: "before-edit")
        try "？\t[a\n".write(to: quickSymbolURL, atomically: true, encoding: .utf8)
        let revisionAfterEdit = schema.contentRevision(forceRefresh: true)
        let afterEdit = try quickSymbolCommit(revision: "after-edit")
        guard beforeEdit == "！",
              revisionBeforeEdit != revisionAfterEdit,
              afterEdit == "？" else {
            throw NativeAotConfigurationError.invalidValue(
                "quick symbol reload before=\(beforeEdit) after=\(afterEdit)")
        }
    }

    private static func verifyQuickSymbolInSentenceMode() throws {
        let temporaryDirectory = FileManager.default.temporaryDirectory
            .appendingPathComponent("tigerclaw-nativeaot-sentence-quick-symbol-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: temporaryDirectory) }
        try FileManager.default.createDirectory(at: temporaryDirectory, withIntermediateDirectories: true)

        let lexiconURL = temporaryDirectory.appendingPathComponent("quick.dict.yaml")
        let quickSymbolURL = temporaryDirectory.appendingPathComponent("快符.txt")
        let userDictionaryURL = temporaryDirectory.appendingPathComponent("user.tsv")
        // The bundled 虎整句 table maps `a` to 来.  A distinct entry here
        // proves sentence decoding uses this active schema instead of silently
        // falling back to that bundled table.
        try "---\nname: sentence quick\ncolumns:\n  - text\n  - code\n...\n烟\ta\n".write(
            to: lexiconURL,
            atomically: true,
            encoding: .utf8)
        // This is the live tigress schema's bracket-guided fast-symbol table.
        // Keep sentence-mode coverage aligned with the real user-facing codes,
        // rather than proving only the first `!` entry with a synthetic table.
        let bracketSymbols: [(code: String, text: String)] = [
            ("a", "！"), ("b", "》"), ("c", "】"), ("d", "、"), ("e", "（"),
            ("f", "“"), ("g", "”"), ("h", "『"), ("i", "——"), ("j", "』"),
            ("k", "￥"), ("l", "%"), ("m", "」"), ("n", "「"), ("o", "〖"),
            ("p", "〗"), ("q", "：“"), ("r", "）"), ("s", "……"), ("t", "→"),
            ("u", "~"), ("v", "《"), ("w", "？"), ("x", "【"), ("y", "·"),
            ("z", "|")
        ]
        let quickTable = bracketSymbols
            .map { "\($0.text)\t[\($0.code)" }
            .joined(separator: "\n") + "\n{日期}\t/rq\n{日期.}\t/rq\n{日期-}\t/rq\n{日期/}\t/rq\n"
        try quickTable.write(to: quickSymbolURL, atomically: true, encoding: .utf8)

        var configuration = NativeAotConfiguration()
        configuration.sentenceInputEnabled = true
        configuration.autoSentenceInput = false
        configuration.sentenceNeuralRerankEnabled = false
        let bridge = try NativeAotBridge(
            lexiconURL: lexiconURL,
            configuration: configuration,
            userDictionaryURL: userDictionaryURL,
            lexiconRevision: "sentence-quick-symbol")
        try bridge.activate()
        defer { bridge.deactivate() }

        _ = try bridge.process(NativeAotInputEvent(
            key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "[", modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "BracketLeft", physicalScanCode: 33))
        let bracketResult = try bridge.process(NativeAotInputEvent(
            key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "a", modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0))
        guard bracketResult.commit == "！", !bracketResult.isComposing else {
            throw NativeAotConfigurationError.invalidValue("sentence bracket quick symbol result=\(bracketResult)")
        }

        _ = try bridge.process(NativeAotInputEvent(
            key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "a", modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0))
        let quickPrefix = try bridge.process(NativeAotInputEvent(
            key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "[", modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "BracketLeft", physicalScanCode: 33))
        guard quickPrefix.isComposing, quickPrefix.commit.isEmpty, quickPrefix.activeInputCode == "[" else {
            throw NativeAotConfigurationError.invalidValue("sentence quick symbol prefix result=\(quickPrefix)")
        }
        let sentenceThenQuickResult = try bridge.process(NativeAotInputEvent(
            key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "a", modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0))
        guard sentenceThenQuickResult.commit == "烟！", !sentenceThenQuickResult.isComposing else {
            throw NativeAotConfigurationError.invalidValue("sentence then quick symbol result=\(sentenceThenQuickResult)")
        }

        // Check every bracket code which is shipped in the current tigress
        // table.  Each must retain the sentence candidate until the symbol is
        // complete, then commit the two pieces together.
        for symbol in bracketSymbols where symbol.code != "a" {
            _ = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "a", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0))
            let prefix = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "[", modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "BracketLeft", physicalScanCode: 33))
            guard prefix.isComposing, prefix.commit.isEmpty, prefix.activeInputCode == "[" else {
                throw NativeAotConfigurationError.invalidValue("sentence quick symbol prefix code=\(symbol.code) result=\(prefix)")
            }
            let result = try bridge.process(NativeAotInputEvent(
                key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: symbol.code, modifiers: 0,
                action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Smoke", physicalScanCode: 0))
            guard result.commit == "烟\(symbol.text)", !result.isComposing else {
                throw NativeAotConfigurationError.invalidValue("sentence quick symbol code=\(symbol.code) result=\(result)")
            }
        }

        // The schema's date/time/add-word commands keep their Windows slash
        // codes.  `[/` explicitly enters that command namespace so it can be
        // used inside a sentence without turning an ordinary `/` into 顿号.
        _ = try bridge.process(NativeAotInputEvent(
            key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "a", modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0))
        let slashCommandPrefix = try bridge.process(NativeAotInputEvent(
            key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "[", modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "BracketLeft", physicalScanCode: 33))
        guard slashCommandPrefix.isComposing, slashCommandPrefix.activeInputCode == "[" else {
            throw NativeAotConfigurationError.invalidValue("sentence slash command bracket prefix=\(slashCommandPrefix)")
        }
        let slashCommandStart = try bridge.process(NativeAotInputEvent(
            key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "/", modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Slash", physicalScanCode: 44))
        guard slashCommandStart.isComposing,
              slashCommandStart.commit.isEmpty,
              slashCommandStart.activeInputCode == "[/" else {
            throw NativeAotConfigurationError.invalidValue("sentence slash command start=\(slashCommandStart)")
        }
        _ = try bridge.process(NativeAotInputEvent(
            key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "r", modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyR", physicalScanCode: 15))
        let dateCandidates = try bridge.process(NativeAotInputEvent(
            key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "q", modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyQ", physicalScanCode: 12))
        let dateFormatter = DateFormatter()
        dateFormatter.locale = Locale(identifier: "en_US_POSIX")
        dateFormatter.calendar = Calendar(identifier: .gregorian)
        dateFormatter.dateFormat = "yyyy.MM.dd"
        guard dateCandidates.isComposing,
              dateCandidates.activeInputCode == "[/rq",
              dateCandidates.candidates.count == 4 else {
            throw NativeAotConfigurationError.invalidValue("sentence slash command candidates=\(dateCandidates)")
        }
        let selectedDate = try bridge.selectCandidate(at: 1)
        guard selectedDate.commit == "烟\(dateFormatter.string(from: Date()))", !selectedDate.isComposing else {
            throw NativeAotConfigurationError.invalidValue("sentence slash command selection=\(selectedDate)")
        }

        // A cancelled quick-symbol prefix must not discard the sentence which
        // was suspended for it.  Backspace returns to the exact original raw
        // sentence code, ready for an ordinary selection or further typing.
        _ = try bridge.process(NativeAotInputEvent(
            key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "a", modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0))
        _ = try bridge.process(NativeAotInputEvent(
            key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "[", modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "BracketLeft", physicalScanCode: 33))
        let restoredSentence = try bridge.process(NativeAotInputEvent(
            key: Int32(TC_INPUT_KEY_BACKSPACE.rawValue), logicalText: "\u{08}", modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Backspace", physicalScanCode: 51))
        guard restoredSentence.isComposing,
              restoredSentence.commit.isEmpty,
              restoredSentence.activeInputCode == "a" else {
            throw NativeAotConfigurationError.invalidValue("sentence quick symbol restore result=\(restoredSentence)")
        }
        let restoredCommit = try bridge.process(NativeAotInputEvent(
            key: Int32(TC_INPUT_KEY_SPACE.rawValue), logicalText: " ", modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Space", physicalScanCode: 49))
        guard restoredCommit.commit == "烟", !restoredCommit.isComposing else {
            throw NativeAotConfigurationError.invalidValue("sentence quick symbol restored commit=\(restoredCommit)")
        }

    }

    // The Windows Core builds sentence decoding from the selected code table,
    // then applies the table-local 用户调整.txt and 补充语料.txt.  Keep that
    // integration contract executable on macOS instead of testing the shared
    // decoder in isolation.
    private static func verifyWindowsParityContracts() throws {
        let temporaryDirectory = FileManager.default.temporaryDirectory
            .appendingPathComponent("tigerclaw-nativeaot-windows-parity-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: temporaryDirectory) }
        try FileManager.default.createDirectory(at: temporaryDirectory, withIntermediateDirectories: true)

        let lexiconURL = temporaryDirectory.appendingPathComponent("parity.dict.yaml")
        let userDictionaryURL = temporaryDirectory.appendingPathComponent("user.tsv")
        let adjustmentsURL = temporaryDirectory.appendingPathComponent("用户调整.txt")
        let supplementsURL = temporaryDirectory.appendingPathComponent("补充语料.txt")
        try "---\nname: parity\ncolumns:\n  - text\n  - code\n  - weight\n...\n甲乙\taa\t100\n丙丁\taa2\t100\n".write(
            to: lexiconURL,
            atomically: true,
            encoding: .utf8)
        try "{置顶}aa\t丙丁\n".write(to: adjustmentsURL, atomically: true, encoding: .utf8)
        // The loader clamps the reward, so this value deliberately tests the
        // documented high-weight path rather than relying on lexical order.
        try "丙丁\t1000000000\n".write(to: supplementsURL, atomically: true, encoding: .utf8)

        let inputA = NativeAotInputEvent(
            key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "a", modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0)
        let space = NativeAotInputEvent(
            key: Int32(TC_INPUT_KEY_SPACE.rawValue), logicalText: " ", modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Space", physicalScanCode: 49)

        let ordinaryBridge = try NativeAotBridge(
            lexiconURL: lexiconURL,
            configuration: NativeAotConfiguration(),
            userDictionaryURL: userDictionaryURL,
            lexiconRevision: "windows-parity-ordinary")
        try ordinaryBridge.activate()
        _ = try ordinaryBridge.process(inputA)
        let adjustedCandidates = try ordinaryBridge.process(inputA)
        ordinaryBridge.deactivate()
        guard adjustedCandidates.candidates == ["丙丁", "甲乙"] else {
            throw NativeAotConfigurationError.invalidValue(
                "windows adjustment candidates=\(adjustedCandidates.candidates)")
        }

        var sentenceConfiguration = NativeAotConfiguration()
        sentenceConfiguration.sentenceInputEnabled = true
        sentenceConfiguration.autoSentenceInput = false
        sentenceConfiguration.sentenceNeuralRerankEnabled = false
        sentenceConfiguration.maxCandidates = 20
        let sentenceBridge = try NativeAotBridge(
            lexiconURL: lexiconURL,
            configuration: sentenceConfiguration,
            userDictionaryURL: userDictionaryURL,
            lexiconRevision: "windows-parity-sentence")
        try sentenceBridge.activate()
        defer { sentenceBridge.deactivate() }
        _ = try sentenceBridge.process(inputA)
        _ = try sentenceBridge.process(inputA)
        let sentenceCommit = try sentenceBridge.process(space)
        guard sentenceCommit.commit == "丙丁", !sentenceCommit.isComposing else {
            throw NativeAotConfigurationError.invalidValue(
                "windows sentence data commit=\(sentenceCommit)")
        }

        try verifyQuickSymbolInSentenceMode()
        try verifyQuickPhraseExpansion()
    }

    private static func verifyQuickPhraseExpansion() throws {
        let temporaryDirectory = FileManager.default.temporaryDirectory
            .appendingPathComponent("tigerclaw-nativeaot-quick-phrase-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: temporaryDirectory) }
        try FileManager.default.createDirectory(at: temporaryDirectory, withIntermediateDirectories: true)

        let lexiconURL = temporaryDirectory.appendingPathComponent("quick-phrase.dict.yaml")
        let quickSymbolURL = temporaryDirectory.appendingPathComponent("快符.txt")
        let userDictionaryURL = temporaryDirectory.appendingPathComponent("user.tsv")
        try "---\nname: quick phrase\ncolumns:\n  - text\n  - code\n...\n{日期.}\ta\n静态\ta\n".write(
            to: lexiconURL,
            atomically: true,
            encoding: .utf8)
        try "{时分}\t[tm\n{重复上屏}\t[rp\n{甲|乙|丙}\t[rd\n{日期}\t/rq\n{日期.}\t/rq\n".write(
            to: quickSymbolURL,
            atomically: true,
            encoding: .utf8)

        var configuration = NativeAotConfiguration()
        configuration.unlimitedMixedInput = true
        let bridge = try NativeAotBridge(
            lexiconURL: lexiconURL,
            configuration: configuration,
            userDictionaryURL: userDictionaryURL)
        try bridge.activate()

        func processCode(_ code: String) throws -> NativeAotResult {
            var result: NativeAotResult?
            for character in code {
                result = try bridge.process(NativeAotInputEvent(
                    key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: String(character), modifiers: 0,
                    action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Smoke", physicalScanCode: 0))
            }
            guard let result else {
                throw NativeAotConfigurationError.invalidValue("empty quick phrase code")
            }
            return result
        }

        let dateCandidates = try processCode("a")
        let date = try bridge.process(NativeAotInputEvent(
            key: Int32(TC_INPUT_KEY_SPACE.rawValue), logicalText: " ", modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Space", physicalScanCode: 49))
        let time = try processCode("[tm")
        let repeatDate = try processCode("[rp")
        let random = try processCode("[rd")
        let quickPhraseCandidates = try processCode("/rq")
        let backspaceEvent = NativeAotInputEvent(
            key: Int32(TC_INPUT_KEY_BACKSPACE.rawValue), logicalText: "\u{08}", modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "Backspace", physicalScanCode: 51)
        let afterFirstBackspace = try bridge.process(backspaceEvent)
        let afterSecondBackspace = try bridge.process(backspaceEvent)
        let afterThirdBackspace = try bridge.process(backspaceEvent)

        let dateFormatter = DateFormatter()
        dateFormatter.locale = Locale(identifier: "en_US_POSIX")
        dateFormatter.calendar = Calendar(identifier: .gregorian)
        dateFormatter.dateFormat = "yyyy.MM.dd"
        let expectedDate = dateFormatter.string(from: Date())
        let timePieces = time.commit.split(separator: ":")
        guard date.commit == expectedDate,
              dateCandidates.candidates.first == expectedDate,
              timePieces.count == 2,
              timePieces.allSatisfy({ $0.count == 2 && $0.allSatisfy(\.isNumber) }),
              repeatDate.commit == time.commit,
              ["甲", "乙", "丙"].contains(random.commit),
              quickPhraseCandidates.isComposing,
              quickPhraseCandidates.candidates.count == 2,
              afterFirstBackspace.isComposing,
              afterFirstBackspace.preedit == "/r",
              afterSecondBackspace.isComposing,
              afterSecondBackspace.preedit == "/",
              !afterThirdBackspace.isComposing,
              afterThirdBackspace.candidates.isEmpty,
              afterThirdBackspace.preedit.isEmpty else {
            throw NativeAotConfigurationError.invalidValue(
                "quick phrases date=\(date.commit) time=\(time.commit) repeat=\(repeatDate.commit) random=\(random.commit) quickCandidates=\(quickPhraseCandidates.candidates) afterDelete=\(afterThirdBackspace)")
        }
        bridge.deactivate()
    }

    private static func verifyQuickAddWordCodeConstruction() throws {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("tigerclaw-nativeaot-quick-add-\(UUID().uuidString).dict.yaml")
        defer { try? FileManager.default.removeItem(at: url) }
        try "---\nname: code construction\ncolumns:\n  - text\n  - code\n...\n甲\tabcd\n乙\tefgh\n丙\tijkl\n丁\tmnop\n".write(
            to: url,
            atomically: true,
            encoding: .utf8)

        let one = NativeAotCodeComposer.constructCode(for: "甲", lexiconURL: url)
        let two = NativeAotCodeComposer.constructCode(for: "甲乙", lexiconURL: url)
        let three = NativeAotCodeComposer.constructCode(for: "甲乙丙", lexiconURL: url)
        let four = NativeAotCodeComposer.constructCode(for: "甲乙丙丁", lexiconURL: url)
        let manuallyTypedCode = QuickAddWordPrefill.resolve(
            activeInputCode: "dfnk",
            candidates: [],
            committedTextHistory: ["了", "下"],
            lexiconURL: url)
        guard one == "abcd", two == "abef", three == "aeij", four == "aeim",
              manuallyTypedCode.code == "dfnk", manuallyTypedCode.text == "了下" else {
            throw NativeAotConfigurationError.invalidValue(
                "quick add code construction one=\(one) two=\(two) three=\(three) four=\(four) manual=\(manuallyTypedCode)")
        }
    }

    private static func verifySchemaManagement() throws {
        let defaults = UserDefaults.standard
        let selectedKey = "TigerClawSelectedSchema"
        let namesKey = "TigerClawSchemaCustomNames"
        let originalSelection = defaults.object(forKey: selectedKey)
        let originalNames = defaults.object(forKey: namesKey)
        let originalRoot = getenv("TIGERCLAW_DATA_ROOT").map { String(cString: $0) }
        let identifier = "schema-smoke-\(UUID().uuidString.replacingOccurrences(of: "-", with: ""))"
        let temporaryRoot = FileManager.default.temporaryDirectory
            .appendingPathComponent("tigerclaw-schema-management-\(UUID().uuidString)", isDirectory: true)
        let sourceDirectory = temporaryRoot.appendingPathComponent("source", isDirectory: true)
        let dataRoot = temporaryRoot.appendingPathComponent("data", isDirectory: true)

        defer {
            if let originalSelection {
                defaults.set(originalSelection, forKey: selectedKey)
            } else {
                defaults.removeObject(forKey: selectedKey)
            }
            if let originalNames {
                defaults.set(originalNames, forKey: namesKey)
            } else {
                defaults.removeObject(forKey: namesKey)
            }
            defaults.synchronize()
            if let originalRoot {
                setenv("TIGERCLAW_DATA_ROOT", originalRoot, 1)
            } else {
                unsetenv("TIGERCLAW_DATA_ROOT")
            }
            try? FileManager.default.removeItem(at: temporaryRoot)
        }

        try FileManager.default.createDirectory(at: sourceDirectory, withIntermediateDirectories: true)
        let primary = sourceDirectory.appendingPathComponent("\(identifier).dict.yaml")
        try "---\nname: schema smoke\ncolumns:\n  - text\n  - code\n...\n烟\ta\n".write(
            to: primary,
            atomically: true,
            encoding: .utf8)
        try "！\t;a\n".write(
            to: sourceDirectory.appendingPathComponent("快符.txt"),
            atomically: true,
            encoding: .utf8)
        try "方案管理冒烟 5000\n".write(
            to: sourceDirectory.appendingPathComponent("补充语料.txt"),
            atomically: true,
            encoding: .utf8)
        try "烟\t烟注\n".write(
            to: sourceDirectory.appendingPathComponent("冒烟.注释"),
            atomically: true,
            encoding: .utf8)
        try "烟\t火因\n".write(
            to: sourceDirectory.appendingPathComponent("冒烟.拆分"),
            atomically: true,
            encoding: .utf8)
        setenv("TIGERCLAW_DATA_ROOT", dataRoot.path, 1)

        let imported = try NativeAotSchema.importLexicon(at: primary)
        guard !imported.isBundled,
              imported.lexiconURL().path.contains("/schemas/\(identifier)/"),
              FileManager.default.fileExists(atPath: imported.lexiconURL().path) else {
            throw NativeAotConfigurationError.invalidValue("schema import isolation")
        }
        try NativeAotSchema.renameImported(identifier: identifier, displayName: "方案管理冒烟")
        guard NativeAotSchema.available.first(where: { $0.identifier == identifier })?.displayName == "方案管理冒烟" else {
            throw NativeAotConfigurationError.invalidValue("schema rename")
        }

        try NativeAotSchema.select(identifier: identifier)
        try NativeAotConfiguration.setPersistedValue(key: "max-candidates", value: "1")
        try NativeAotConfiguration.setPersistedValue(key: "sentence-auto-commit-enabled", value: "on")
        guard FileManager.default.fileExists(atPath: imported.editableDirectoryURL!.appendingPathComponent("冒烟.注释").path),
              FileManager.default.fileExists(atPath: imported.editableDirectoryURL!.appendingPathComponent("冒烟.拆分").path),
              FileManager.default.fileExists(atPath: imported.editableDirectoryURL!.appendingPathComponent("补充语料.txt").path) else {
            throw NativeAotConfigurationError.invalidValue("schema sidecar import")
        }
        var annotationConfiguration = NativeAotConfiguration.current
        annotationConfiguration.showCandidateComment = true
        annotationConfiguration.showCandidateSplit = false
        guard NativeAotCandidateAnnotations.annotation(for: "烟", configuration: annotationConfiguration) == "烟注" else {
            throw NativeAotConfigurationError.invalidValue("candidate annotation")
        }
        annotationConfiguration.showCandidateSplit = true
        guard NativeAotCandidateAnnotations.annotation(for: "烟", configuration: annotationConfiguration) == "火因 烟注" else {
            throw NativeAotConfigurationError.invalidValue("candidate split")
        }
        let legacySchemaDataDirectory = dataRoot.appendingPathComponent("schema-data/\(identifier)", isDirectory: true)
        try FileManager.default.createDirectory(at: legacySchemaDataDirectory, withIntermediateDirectories: true)
        try "旧词\ta\n".write(
            to: legacySchemaDataDirectory.appendingPathComponent("user-dictionary.tsv"),
            atomically: true,
            encoding: .utf8)
        let adjustmentsURL = imported.editableDirectoryURL!.appendingPathComponent("用户调整.txt")
        guard try NativeAotUserDictionary.entries().contains(NativeAotUserDictionaryEntry(code: "a", text: "旧词")),
              (try String(contentsOf: adjustmentsURL, encoding: .utf8)).contains("{添加}a\t旧词\n{置顶}a\t旧词") else {
            throw NativeAotConfigurationError.invalidValue("legacy user dictionary migration")
        }
        _ = try NativeAotUserDictionary.remove(code: "a", text: "旧词")
        _ = try NativeAotUserDictionary.add(code: "a", text: "自定义烟")
        let configurationAfterWrite = NativeAotConfiguration.current
        let userEntriesAfterWrite = try NativeAotUserDictionary.entries()
        let adjustmentsAfterWrite = try String(contentsOf: adjustmentsURL, encoding: .utf8)
        guard configurationAfterWrite.maxCandidates == 1,
              configurationAfterWrite.sentenceAutoCommitEnabled,
              userEntriesAfterWrite.contains(NativeAotUserDictionaryEntry(code: "a", text: "自定义烟")),
              adjustmentsAfterWrite.contains("{添加}a\t自定义烟") else {
            throw NativeAotConfigurationError.invalidValue(
                "schema scoped data max=\(configurationAfterWrite.maxCandidates) autoCommit=\(configurationAfterWrite.sentenceAutoCommitEnabled) entries=\(userEntriesAfterWrite) adjustment=\(adjustmentsAfterWrite.debugDescription)")
        }
        let configurationURL = try NativeAotDataRoot.configurationURL(for: identifier)
        var externallySavedConfiguration = try JSONDecoder().decode(
            [String: String].self,
            from: Data(contentsOf: configurationURL))
        guard externallySavedConfiguration["max-candidates"] == "1",
              externallySavedConfiguration["sentence-auto-commit-enabled"] == "on" else {
            throw NativeAotConfigurationError.invalidValue("external configuration save")
        }
        externallySavedConfiguration["max-candidates"] = "12"
        try JSONEncoder().encode(externallySavedConfiguration).write(to: configurationURL, options: .atomic)
        defaults.removeObject(forKey: "TigerClawSchema.\(identifier).TigerClawMaxCandidates")
        let externallyReloadedConfiguration = NativeAotConfiguration.current
        guard externallyReloadedConfiguration.maxCandidates == 12 else {
            throw NativeAotConfigurationError.invalidValue(
                "external configuration reload schema=\(NativeAotSchema.current.identifier) max=\(externallyReloadedConfiguration.maxCandidates) file=\(externallySavedConfiguration)")
        }
        try NativeAotUserDictionary.adjust(code: "a", text: "烟", operation: .delete)
        guard (try String(contentsOf: adjustmentsURL, encoding: .utf8)).contains("{删除}a\t烟") else {
            throw NativeAotConfigurationError.invalidValue("windows user adjustment format")
        }
        let adjustedBridge = try NativeAotBridge(
            lexiconURL: imported.lexiconURL(),
            configuration: NativeAotConfiguration.current)
        try adjustedBridge.activate()
        let adjustedResult = try adjustedBridge.process(NativeAotInputEvent(
            key: Int32(TC_INPUT_KEY_CHARACTER.rawValue), logicalText: "a", modifiers: 0,
            action: Int32(TC_KEY_ACTION_KEY_DOWN.rawValue), physicalKey: "KeyA", physicalScanCode: 0))
        guard adjustedResult.candidates == ["自定义烟"] else {
            throw NativeAotConfigurationError.invalidValue("schema candidate adjustment")
        }
        try NativeAotSchema.select(identifier: NativeAotSchema.bundled[0].identifier)
        guard !(try NativeAotUserDictionary.entries()).contains(NativeAotUserDictionaryEntry(code: "a", text: "自定义烟")) else {
            throw NativeAotConfigurationError.invalidValue("schema data isolation")
        }

        let archive = try NativeAotSchema.exportArchive(identifier: identifier)
        guard FileManager.default.fileExists(atPath: archive.path) else {
            throw NativeAotConfigurationError.invalidValue("schema export")
        }
        try NativeAotSchema.removeExportArchive(at: archive.path)
        guard !FileManager.default.fileExists(atPath: archive.path) else {
            throw NativeAotConfigurationError.invalidValue("schema export cleanup")
        }

        try NativeAotSchema.deleteImported(identifier: identifier)
        guard !NativeAotSchema.available.contains(where: { $0.identifier == identifier }),
              !FileManager.default.fileExists(atPath: configurationURL.path) else {
            throw NativeAotConfigurationError.invalidValue("schema delete")
        }
    }
}
