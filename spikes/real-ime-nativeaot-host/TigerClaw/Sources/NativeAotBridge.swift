import AppKit
import Carbon
import Foundation

struct NativeAotConfiguration: Hashable {
    private final class ExternalValuesCache: @unchecked Sendable {
        private let lock = NSLock()
        private var path = ""
        private var revision = ""
        private var cachedValues: [String: String] = [:]

        func values(at url: URL) -> [String: String] {
            let currentRevision = revision(for: url)
            lock.lock()
            defer { lock.unlock() }
            guard path != url.path || revision != currentRevision else {
                return cachedValues
            }
            let values: [String: String]
            if let data = try? Data(contentsOf: url),
               let decoded = try? JSONDecoder().decode([String: String].self, from: data) {
                values = decoded
            } else {
                values = [:]
            }
            path = url.path
            revision = currentRevision
            cachedValues = values
            return values
        }

        func invalidate(url: URL) {
            lock.lock()
            defer { lock.unlock() }
            guard path == url.path else { return }
            revision = ""
            cachedValues = [:]
        }

        private func revision(for url: URL) -> String {
            guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
                  let modified = attributes[.modificationDate] as? Date else {
                return "missing"
            }
            return "\(modified.timeIntervalSinceReferenceDate):\(String(describing: attributes[.size]))"
        }
    }

    private enum DefaultsKey {
        static let maxCandidates = "TigerClawMaxCandidates"
        static let pageSize = "TigerClawPageSize"
        static let maxCodeLength = "TigerClawMaxCodeLength"
        static let autoCommitUniqueTerminalCode = "TigerClawAutoCommitUniqueTerminalCode"
        static let secondCandidateSemicolon = "TigerClawSecondCandidateSemicolon"
        static let thirdCandidateQuote = "TigerClawThirdCandidateQuote"
        static let unlimitedMixedInput = "TigerClawUnlimitedMixedInput"
        static let useEnglishPunctuationInChinese = "TigerClawUseEnglishPunctuationInChinese"
        static let slashOutputsDunhao = "TigerClawSlashOutputsDunhao"
        static let tabClearsComposition = "TigerClawTabClearsComposition"
        static let enterClearsComposition = "TigerClawEnterClearsComposition"
        static let pinyinReverseEnabled = "TigerClawPinyinReverseEnabled"
        static let clearOnNoCode = "TigerClawClearOnNoCode"
        static let sentenceInputEnabled = "TigerClawSentenceInputEnabled"
        static let autoSentenceInput = "TigerClawAutoSentenceInput"
        static let sentenceNeuralRerankEnabled = "TigerClawSentenceNeuralRerankEnabled"
        static let sentenceAutoCommitEnabled = "TigerClawSentenceAutoCommitEnabled"
        static let sentenceOptimalCodeHighFreqLimit = "TigerClawSentenceOptimalCodeHighFreqLimit"
        static let sentenceFullCodeWhitelist = "TigerClawSentenceFullCodeWhitelist"
        static let sentenceAllowDuplicateSingleCharacters = "TigerClawSentenceAllowDuplicateSingleCharacters"
        static let previousPageKeys = "TigerClawPreviousPageKeys"
        static let nextPageKeys = "TigerClawNextPageKeys"
        static let hideCandidates = "TigerClawHideCandidates"
        static let verticalCandidates = "TigerClawVerticalCandidates"
        static let showCandidateIndex = "TigerClawShowCandidateIndex"
        static let showInputCode = "TigerClawShowInputCode"
        static let candidateFontSize = "TigerClawCandidateFontSize"
        static let candidateFontName = "TigerClawCandidateFontName"
        static let candidateTheme = "TigerClawCandidateTheme"
        static let candidateWindowAnimation = "TigerClawCandidateWindowAnimation"
        static let showCandidateComment = "TigerClawShowCandidateComment"
        static let showCandidateSplit = "TigerClawShowCandidateSplit"
        static let candidateExpandDelayMs = "TigerClawCandidateExpandDelayMs"
        static let annotationExpandDelayMs = "TigerClawAnnotationExpandDelayMs"
        static let keySoundEnabled = "TigerClawKeySoundEnabled"
        static let keySoundVolume = "TigerClawKeySoundVolume"
        static let codeMasking = "TigerClawCodeMasking"
        static let defaultChinese = "TigerClawDefaultChinese"
        static let ctrlSpaceTogglesInputMode = "TigerClawCtrlSpaceTogglesInputMode"
        static let ctrlEqualAddsWord = "TigerClawCtrlEqualAddsWord"
        static let capsLockTogglesInputMode = "TigerClawCapsLockTogglesInputMode"
        static let ctrlMTogglesRecentSchema = "TigerClawCtrlMTogglesRecentSchema"
    }

    private static let externalKeyByDefaultsKey: [String: String] = [
        DefaultsKey.maxCandidates: "max-candidates",
        DefaultsKey.pageSize: "page-size",
        DefaultsKey.maxCodeLength: "max-code-length",
        DefaultsKey.autoCommitUniqueTerminalCode: "auto-commit-unique",
        DefaultsKey.secondCandidateSemicolon: "second-candidate-semicolon",
        DefaultsKey.thirdCandidateQuote: "third-candidate-quote",
        DefaultsKey.unlimitedMixedInput: "unlimited-mixed-input",
        DefaultsKey.useEnglishPunctuationInChinese: "use-english-punctuation-in-chinese",
        DefaultsKey.slashOutputsDunhao: "slash-outputs-dunhao",
        DefaultsKey.tabClearsComposition: "tab-clears-composition",
        DefaultsKey.enterClearsComposition: "enter-clears-composition",
        DefaultsKey.pinyinReverseEnabled: "pinyin-reverse-enabled",
        DefaultsKey.clearOnNoCode: "clear-on-no-code",
        DefaultsKey.sentenceInputEnabled: "sentence-input-enabled",
        DefaultsKey.autoSentenceInput: "auto-sentence-input",
        DefaultsKey.sentenceNeuralRerankEnabled: "sentence-neural-rerank-enabled",
        DefaultsKey.sentenceAutoCommitEnabled: "sentence-auto-commit-enabled",
        DefaultsKey.sentenceOptimalCodeHighFreqLimit: "sentence-optimal-code-high-frequency-limit",
        DefaultsKey.sentenceFullCodeWhitelist: "sentence-full-code-whitelist",
        DefaultsKey.sentenceAllowDuplicateSingleCharacters: "sentence-allow-duplicate-single-characters",
        DefaultsKey.previousPageKeys: "previous-page-keys",
        DefaultsKey.nextPageKeys: "next-page-keys",
        DefaultsKey.hideCandidates: "hide-candidates",
        DefaultsKey.verticalCandidates: "vertical-candidates",
        DefaultsKey.showCandidateIndex: "show-candidate-index",
        DefaultsKey.showInputCode: "show-input-code",
        DefaultsKey.candidateFontSize: "candidate-font-size",
        DefaultsKey.candidateFontName: "candidate-font-name",
        DefaultsKey.candidateTheme: "candidate-theme",
        DefaultsKey.candidateWindowAnimation: "candidate-window-animation",
        DefaultsKey.showCandidateComment: "show-candidate-comment",
        DefaultsKey.showCandidateSplit: "show-candidate-split",
        DefaultsKey.candidateExpandDelayMs: "candidate-expand-delay-ms",
        DefaultsKey.annotationExpandDelayMs: "annotation-expand-delay-ms",
        DefaultsKey.keySoundEnabled: "key-sound-enabled",
        DefaultsKey.keySoundVolume: "key-sound-volume",
        DefaultsKey.codeMasking: "code-masking",
        DefaultsKey.defaultChinese: "default-chinese",
        DefaultsKey.ctrlSpaceTogglesInputMode: "ctrl-space-toggles-input-mode",
        DefaultsKey.ctrlEqualAddsWord: "ctrl-equal-adds-word",
        DefaultsKey.capsLockTogglesInputMode: "caps-lock-toggles-input-mode",
        DefaultsKey.ctrlMTogglesRecentSchema: "ctrl-m-toggles-recent-schema",
    ]
    private static let externalValuesCache = ExternalValuesCache()

    var maxCandidates = 9
    var pageSize = 5
    var maxCodeLength = 4
    var autoCommitUniqueTerminalCode = true
    var secondCandidateSemicolon = true
    var thirdCandidateQuote = true
    var unlimitedMixedInput = false
    var useEnglishPunctuationInChinese = false
    var slashOutputsDunhao = true
    var tabClearsComposition = true
    var enterClearsComposition = false
    var pinyinReverseEnabled = true
    var clearOnNoCode = true
    var sentenceInputEnabled = false
    var autoSentenceInput = true
    var sentenceNeuralRerankEnabled = true
    var sentenceAutoCommitEnabled = false
    var sentenceOptimalCodeHighFreqLimit = 1500
    var sentenceFullCodeWhitelist = "便深候整调脸照病增响剑哪微营修愿密脑续假值弹您球激游模静源副座喝富宣呼检救嘴税探脱误释跳睡减蒙镇域洞湾卖暴输缓熟庭俄韩混词授摆诺稳塔潜硬萧侵懂蒋赞赛胸偷烧墙爆操挑撤筑戴植援凭聚凌梁箭圈惨飘旗牌废缩碎挺晓桥赫凝潮掩拔播艘滚兽隆薄愤漫爹撒佩绕"
    var sentenceAllowDuplicateSingleCharacters = true
    // Selection is intentionally handled by selection-keys.conf in the host,
    // rather than by the core's character-only selection string.  Keeping the
    // engine value empty prevents its fixed digit fallback from bypassing an
    // explicit per-schema selection-key file.
    var selectionKeys = ""
    var previousPageKeys = "-"
    var nextPageKeys = "="
    var hideCandidates = false
    var verticalCandidates = true
    var showCandidateIndex = true
    var showInputCode = false
    var candidateFontSize = 17
    var candidateFontName = ""
    var candidateTheme = "system"
    var candidateWindowAnimation = true
    var showCandidateComment = true
    var showCandidateSplit = false
    var candidateExpandDelayMs = 0
    var annotationExpandDelayMs = 0
    var keySoundEnabled = false
    var keySoundVolume = 30
    var codeMasking = ""
    var defaultChinese = true
    var ctrlSpaceTogglesInputMode = true
    var ctrlEqualAddsWord = true
    var capsLockTogglesInputMode = true
    var ctrlMTogglesRecentSchema = true

    static var current: NativeAotConfiguration {
        UserDefaults.standard.synchronize()
        let schemaIdentifier = NativeAotSchema.current.identifier
        let externalValues = externalValues(for: schemaIdentifier)
        var value = NativeAotConfiguration()
        value.maxCandidates = integer(
            DefaultsKey.maxCandidates,
            fallback: value.maxCandidates,
            range: 1 ... 99,
            externalValues: externalValues)
        value.pageSize = integer(DefaultsKey.pageSize, fallback: value.pageSize, range: 1 ... 10, externalValues: externalValues)
        value.maxCodeLength = integer(DefaultsKey.maxCodeLength, fallback: value.maxCodeLength, range: 1 ... 16, externalValues: externalValues)
        value.autoCommitUniqueTerminalCode = boolean(
            DefaultsKey.autoCommitUniqueTerminalCode,
            fallback: value.autoCommitUniqueTerminalCode,
            externalValues: externalValues)
        value.secondCandidateSemicolon = boolean(
            DefaultsKey.secondCandidateSemicolon,
            fallback: value.secondCandidateSemicolon,
            externalValues: externalValues)
        value.thirdCandidateQuote = boolean(
            DefaultsKey.thirdCandidateQuote,
            fallback: value.thirdCandidateQuote,
            externalValues: externalValues)
        value.unlimitedMixedInput = boolean(DefaultsKey.unlimitedMixedInput, fallback: value.unlimitedMixedInput, externalValues: externalValues)
        value.useEnglishPunctuationInChinese = boolean(DefaultsKey.useEnglishPunctuationInChinese, fallback: value.useEnglishPunctuationInChinese, externalValues: externalValues)
        value.slashOutputsDunhao = boolean(DefaultsKey.slashOutputsDunhao, fallback: value.slashOutputsDunhao, externalValues: externalValues)
        value.tabClearsComposition = boolean(DefaultsKey.tabClearsComposition, fallback: value.tabClearsComposition, externalValues: externalValues)
        value.enterClearsComposition = boolean(DefaultsKey.enterClearsComposition, fallback: value.enterClearsComposition, externalValues: externalValues)
        value.pinyinReverseEnabled = boolean(DefaultsKey.pinyinReverseEnabled, fallback: value.pinyinReverseEnabled, externalValues: externalValues)
        value.clearOnNoCode = boolean(DefaultsKey.clearOnNoCode, fallback: value.clearOnNoCode, externalValues: externalValues)
        value.sentenceInputEnabled = boolean(DefaultsKey.sentenceInputEnabled, fallback: value.sentenceInputEnabled, externalValues: externalValues)
        value.autoSentenceInput = boolean(DefaultsKey.autoSentenceInput, fallback: value.autoSentenceInput, externalValues: externalValues)
        value.sentenceNeuralRerankEnabled = boolean(DefaultsKey.sentenceNeuralRerankEnabled, fallback: value.sentenceNeuralRerankEnabled, externalValues: externalValues)
        value.sentenceAutoCommitEnabled = boolean(DefaultsKey.sentenceAutoCommitEnabled, fallback: value.sentenceAutoCommitEnabled, externalValues: externalValues)
        value.sentenceOptimalCodeHighFreqLimit = sentenceHighFrequencyLimit(
            DefaultsKey.sentenceOptimalCodeHighFreqLimit,
            fallback: value.sentenceOptimalCodeHighFreqLimit,
            externalValues: externalValues)
        value.sentenceFullCodeWhitelist = text(
            DefaultsKey.sentenceFullCodeWhitelist,
            fallback: value.sentenceFullCodeWhitelist,
            externalValues: externalValues)
        value.sentenceAllowDuplicateSingleCharacters = boolean(
            DefaultsKey.sentenceAllowDuplicateSingleCharacters,
            fallback: value.sentenceAllowDuplicateSingleCharacters,
            externalValues: externalValues)
        value.previousPageKeys = keySequence(DefaultsKey.previousPageKeys, fallback: value.previousPageKeys, externalValues: externalValues)
        value.nextPageKeys = keySequence(DefaultsKey.nextPageKeys, fallback: value.nextPageKeys, externalValues: externalValues)
        value.hideCandidates = boolean(DefaultsKey.hideCandidates, fallback: value.hideCandidates, externalValues: externalValues)
        value.verticalCandidates = boolean(DefaultsKey.verticalCandidates, fallback: value.verticalCandidates, externalValues: externalValues)
        value.showCandidateIndex = boolean(DefaultsKey.showCandidateIndex, fallback: value.showCandidateIndex, externalValues: externalValues)
        value.showInputCode = boolean(DefaultsKey.showInputCode, fallback: value.showInputCode, externalValues: externalValues)
        value.candidateFontSize = integer(DefaultsKey.candidateFontSize, fallback: value.candidateFontSize, range: 12 ... 32, externalValues: externalValues)
        value.candidateFontName = fontName(DefaultsKey.candidateFontName, fallback: value.candidateFontName, externalValues: externalValues)
        value.candidateTheme = theme(DefaultsKey.candidateTheme, fallback: value.candidateTheme, externalValues: externalValues)
        value.candidateWindowAnimation = boolean(DefaultsKey.candidateWindowAnimation, fallback: value.candidateWindowAnimation, externalValues: externalValues)
        value.showCandidateComment = boolean(DefaultsKey.showCandidateComment, fallback: value.showCandidateComment, externalValues: externalValues)
        value.showCandidateSplit = boolean(DefaultsKey.showCandidateSplit, fallback: value.showCandidateSplit, externalValues: externalValues)
        value.candidateExpandDelayMs = integer(DefaultsKey.candidateExpandDelayMs, fallback: value.candidateExpandDelayMs, range: 0 ... 60_000, externalValues: externalValues)
        value.annotationExpandDelayMs = integer(DefaultsKey.annotationExpandDelayMs, fallback: value.annotationExpandDelayMs, range: 0 ... 60_000, externalValues: externalValues)
        value.keySoundEnabled = boolean(DefaultsKey.keySoundEnabled, fallback: value.keySoundEnabled, externalValues: externalValues)
        value.keySoundVolume = integer(DefaultsKey.keySoundVolume, fallback: value.keySoundVolume, range: 0 ... 100, externalValues: externalValues)
        value.codeMasking = text(DefaultsKey.codeMasking, fallback: value.codeMasking, externalValues: externalValues)
        value.defaultChinese = boolean(DefaultsKey.defaultChinese, fallback: value.defaultChinese, externalValues: externalValues)
        value.ctrlSpaceTogglesInputMode = boolean(DefaultsKey.ctrlSpaceTogglesInputMode, fallback: value.ctrlSpaceTogglesInputMode, externalValues: externalValues)
        value.ctrlEqualAddsWord = boolean(DefaultsKey.ctrlEqualAddsWord, fallback: value.ctrlEqualAddsWord, externalValues: externalValues)
        value.capsLockTogglesInputMode = boolean(DefaultsKey.capsLockTogglesInputMode, fallback: value.capsLockTogglesInputMode, externalValues: externalValues)
        value.ctrlMTogglesRecentSchema = boolean(DefaultsKey.ctrlMTogglesRecentSchema, fallback: value.ctrlMTogglesRecentSchema, externalValues: externalValues)
        return value
    }

    static func setPersistedValue(key: String, value: String) throws {
        switch key {
        case "max-candidates":
        UserDefaults.standard.set(try integer(value, range: 1 ... 99), forKey: scopedKey(DefaultsKey.maxCandidates))
        case "page-size":
        UserDefaults.standard.set(try integer(value, range: 1 ... 10), forKey: scopedKey(DefaultsKey.pageSize))
        case "max-code-length":
        UserDefaults.standard.set(try integer(value, range: 1 ... 16), forKey: scopedKey(DefaultsKey.maxCodeLength))
        case "auto-commit-unique":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.autoCommitUniqueTerminalCode))
        case "second-candidate-semicolon":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.secondCandidateSemicolon))
        case "third-candidate-quote":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.thirdCandidateQuote))
        case "unlimited-mixed-input":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.unlimitedMixedInput))
        case "use-english-punctuation-in-chinese":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.useEnglishPunctuationInChinese))
        case "slash-outputs-dunhao":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.slashOutputsDunhao))
        case "tab-clears-composition":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.tabClearsComposition))
        case "enter-clears-composition":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.enterClearsComposition))
        case "pinyin-reverse-enabled":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.pinyinReverseEnabled))
        case "clear-on-no-code":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.clearOnNoCode))
        case "sentence-input-enabled":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.sentenceInputEnabled))
        case "auto-sentence-input":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.autoSentenceInput))
        case "sentence-neural-rerank-enabled":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.sentenceNeuralRerankEnabled))
        case "sentence-auto-commit-enabled":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.sentenceAutoCommitEnabled))
        case "sentence-optimal-code-high-frequency-limit":
        UserDefaults.standard.set(try sentenceHighFrequencyLimit(value), forKey: scopedKey(DefaultsKey.sentenceOptimalCodeHighFreqLimit))
        case "sentence-full-code-whitelist":
        UserDefaults.standard.set(value, forKey: scopedKey(DefaultsKey.sentenceFullCodeWhitelist))
        case "sentence-allow-duplicate-single-characters":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.sentenceAllowDuplicateSingleCharacters))
        case "previous-page-keys":
        UserDefaults.standard.set(try keySequence(value), forKey: scopedKey(DefaultsKey.previousPageKeys))
        case "next-page-keys":
        UserDefaults.standard.set(try keySequence(value), forKey: scopedKey(DefaultsKey.nextPageKeys))
        case "hide-candidates":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.hideCandidates))
        case "vertical-candidates":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.verticalCandidates))
        case "show-candidate-index":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.showCandidateIndex))
        case "show-input-code":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.showInputCode))
        case "candidate-font-size":
        UserDefaults.standard.set(try integer(value, range: 12 ... 32), forKey: scopedKey(DefaultsKey.candidateFontSize))
        case "candidate-font-name":
        UserDefaults.standard.set(try validatedFontName(value), forKey: scopedKey(DefaultsKey.candidateFontName))
        case "candidate-theme":
        UserDefaults.standard.set(try theme(value), forKey: scopedKey(DefaultsKey.candidateTheme))
        case "candidate-window-animation":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.candidateWindowAnimation))
        case "show-candidate-comment":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.showCandidateComment))
        case "show-candidate-split":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.showCandidateSplit))
        case "candidate-expand-delay-ms":
        UserDefaults.standard.set(try integer(value, range: 0 ... 60_000), forKey: scopedKey(DefaultsKey.candidateExpandDelayMs))
        case "annotation-expand-delay-ms":
        UserDefaults.standard.set(try integer(value, range: 0 ... 60_000), forKey: scopedKey(DefaultsKey.annotationExpandDelayMs))
        case "key-sound-enabled":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.keySoundEnabled))
        case "key-sound-volume":
        UserDefaults.standard.set(try integer(value, range: 0 ... 100), forKey: scopedKey(DefaultsKey.keySoundVolume))
        case "code-masking":
        UserDefaults.standard.set(value, forKey: scopedKey(DefaultsKey.codeMasking))
        case "default-chinese":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.defaultChinese))
        case "ctrl-space-toggles-input-mode":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.ctrlSpaceTogglesInputMode))
        case "ctrl-equal-adds-word":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.ctrlEqualAddsWord))
        case "caps-lock-toggles-input-mode":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.capsLockTogglesInputMode))
        case "ctrl-m-toggles-recent-schema":
        UserDefaults.standard.set(try boolean(value), forKey: scopedKey(DefaultsKey.ctrlMTogglesRecentSchema))
        default:
            throw NativeAotConfigurationError.unknownKey(key)
        }
        UserDefaults.standard.synchronize()
        try writeExternalValue(key: key, value: value, for: NativeAotSchema.current.identifier)
    }

    static func diagnosticLines() -> [String] {
        let value = current
        return [
            "max-candidates=\(value.maxCandidates)",
            "page-size=\(value.pageSize)",
            "max-code-length=\(value.maxCodeLength)",
            "auto-commit-unique=\(value.autoCommitUniqueTerminalCode ? "on" : "off")",
            "second-candidate-semicolon=\(value.secondCandidateSemicolon ? "on" : "off")",
            "third-candidate-quote=\(value.thirdCandidateQuote ? "on" : "off")",
            "unlimited-mixed-input=\(value.unlimitedMixedInput ? "on" : "off")",
            "use-english-punctuation-in-chinese=\(value.useEnglishPunctuationInChinese ? "on" : "off")",
            "slash-outputs-dunhao=\(value.slashOutputsDunhao ? "on" : "off")",
            "tab-clears-composition=\(value.tabClearsComposition ? "on" : "off")",
            "enter-clears-composition=\(value.enterClearsComposition ? "on" : "off")",
            "pinyin-reverse-enabled=\(value.pinyinReverseEnabled ? "on" : "off")",
            "clear-on-no-code=\(value.clearOnNoCode ? "on" : "off")",
            "sentence-input-enabled=\(value.sentenceInputEnabled ? "on" : "off")",
            "auto-sentence-input=\(value.autoSentenceInput ? "on" : "off")",
            "sentence-neural-rerank-enabled=\(value.sentenceNeuralRerankEnabled ? "on" : "off")",
            "sentence-auto-commit-enabled=\(value.sentenceAutoCommitEnabled ? "on" : "off")",
            "sentence-optimal-code-high-frequency-limit=\(value.sentenceOptimalCodeHighFreqLimit)",
            "sentence-full-code-whitelist=\(value.sentenceFullCodeWhitelist)",
            "sentence-allow-duplicate-single-characters=\(value.sentenceAllowDuplicateSingleCharacters ? "on" : "off")",
            "selection-keys-file=\((try? NativeAotSelectionKeyBindings.configURL().path) ?? "unavailable")",
            "previous-page-keys=\(value.previousPageKeys)",
            "next-page-keys=\(value.nextPageKeys)",
            "hide-candidates=\(value.hideCandidates ? "on" : "off")",
            "vertical-candidates=\(value.verticalCandidates ? "on" : "off")",
            "show-candidate-index=\(value.showCandidateIndex ? "on" : "off")",
            "show-input-code=\(value.showInputCode ? "on" : "off")",
            "candidate-font-size=\(value.candidateFontSize)",
            "candidate-font-name=\(value.candidateFontName)",
            "candidate-theme=\(value.candidateTheme)",
            "candidate-window-animation=\(value.candidateWindowAnimation ? "on" : "off")",
            "show-candidate-comment=\(value.showCandidateComment ? "on" : "off")",
            "show-candidate-split=\(value.showCandidateSplit ? "on" : "off")",
            "candidate-expand-delay-ms=\(value.candidateExpandDelayMs)",
            "annotation-expand-delay-ms=\(value.annotationExpandDelayMs)",
            "key-sound-enabled=\(value.keySoundEnabled ? "on" : "off")",
            "key-sound-volume=\(value.keySoundVolume)",
            "code-masking=\(value.codeMasking)",
            "default-chinese=\(value.defaultChinese ? "on" : "off")",
            "ctrl-space-toggles-input-mode=\(value.ctrlSpaceTogglesInputMode ? "on" : "off")",
            "ctrl-equal-adds-word=\(value.ctrlEqualAddsWord ? "on" : "off")",
            "caps-lock-toggles-input-mode=\(value.capsLockTogglesInputMode ? "on" : "off")",
            "ctrl-m-toggles-recent-schema=\(value.ctrlMTogglesRecentSchema ? "on" : "off")",
        ]
    }

    static func removePersistedValues(for schemaIdentifier: String) {
        [
            DefaultsKey.maxCandidates,
            DefaultsKey.pageSize,
            DefaultsKey.maxCodeLength,
            DefaultsKey.autoCommitUniqueTerminalCode,
            DefaultsKey.secondCandidateSemicolon,
            DefaultsKey.thirdCandidateQuote,
            DefaultsKey.unlimitedMixedInput,
            DefaultsKey.useEnglishPunctuationInChinese,
            DefaultsKey.slashOutputsDunhao,
            DefaultsKey.tabClearsComposition,
            DefaultsKey.enterClearsComposition,
            DefaultsKey.pinyinReverseEnabled,
            DefaultsKey.clearOnNoCode,
            DefaultsKey.sentenceInputEnabled,
            DefaultsKey.autoSentenceInput,
            DefaultsKey.sentenceNeuralRerankEnabled,
            DefaultsKey.sentenceAutoCommitEnabled,
            DefaultsKey.sentenceOptimalCodeHighFreqLimit,
            DefaultsKey.sentenceFullCodeWhitelist,
            DefaultsKey.sentenceAllowDuplicateSingleCharacters,
            DefaultsKey.previousPageKeys,
            DefaultsKey.nextPageKeys,
            DefaultsKey.hideCandidates,
            DefaultsKey.verticalCandidates,
            DefaultsKey.showCandidateIndex,
            DefaultsKey.showInputCode,
            DefaultsKey.candidateFontSize,
            DefaultsKey.candidateFontName,
            DefaultsKey.candidateTheme,
            DefaultsKey.candidateWindowAnimation,
            DefaultsKey.showCandidateComment,
            DefaultsKey.showCandidateSplit,
            DefaultsKey.candidateExpandDelayMs,
            DefaultsKey.annotationExpandDelayMs,
            DefaultsKey.keySoundEnabled,
            DefaultsKey.keySoundVolume,
            DefaultsKey.codeMasking,
            DefaultsKey.defaultChinese,
            DefaultsKey.ctrlSpaceTogglesInputMode,
            DefaultsKey.ctrlEqualAddsWord,
            DefaultsKey.capsLockTogglesInputMode,
            DefaultsKey.ctrlMTogglesRecentSchema,
        ].forEach { key in
            UserDefaults.standard.removeObject(forKey: "TigerClawSchema.\(schemaIdentifier).\(key)")
        }
        if let url = try? NativeAotDataRoot.configurationURL(for: schemaIdentifier) {
            try? FileManager.default.removeItem(at: url)
        }
    }

    var abiValue: tc_engine_config {
        var value = tc_engine_config()
        value.max_candidates = Int32(maxCandidates)
        value.page_size = Int32(pageSize)
        value.max_code_length = Int32(maxCodeLength)
        value.auto_commit_unique_terminal_code = autoCommitUniqueTerminalCode ? 1 : 0
        value.second_candidate_semicolon = secondCandidateSemicolon ? 1 : 0
        value.third_candidate_quote = thirdCandidateQuote ? 1 : 0
        value.unlimited_mixed_input = unlimitedMixedInput ? 1 : 0
        value.use_english_punctuation_in_chinese = useEnglishPunctuationInChinese ? 1 : 0
        value.slash_outputs_dunhao = slashOutputsDunhao ? 1 : 0
        value.tab_clears_composition = tabClearsComposition ? 1 : 0
        value.enter_clears_composition = enterClearsComposition ? 1 : 0
        value.pinyin_reverse_enabled = pinyinReverseEnabled ? 1 : 0
        value.clear_on_no_code = clearOnNoCode ? 1 : 0
        value.sentence_neural_rerank_enabled = sentenceNeuralRerankEnabled ? 1 : 0
        value.sentence_auto_commit_enabled = sentenceAutoCommitEnabled ? 1 : 0
        value.sentence_optimal_code_high_freq_limit = Int32(sentenceOptimalCodeHighFreqLimit)
        value.sentence_allow_duplicate_single_characters = sentenceAllowDuplicateSingleCharacters ? 1 : 0
        return value
    }

    func sentenceInputActive(for schema: NativeAotSchema) -> Bool {
        sentenceInputEnabled || (autoSentenceInput && schema.displayName.contains("整句"))
    }

    private static func integer(
        _ key: String,
        fallback: Int,
        range: ClosedRange<Int>,
        externalValues: [String: String]
    ) -> Int {
        guard let value = persistedValue(for: key, externalValues: externalValues) else {
            return fallback
        }
        let number: Int?
        if let numberValue = value as? NSNumber {
            number = numberValue.intValue
        } else if let stringValue = value as? String {
            number = Int(stringValue)
        } else {
            number = nil
        }
        guard let number else { return fallback }
        return min(max(number, range.lowerBound), range.upperBound)
    }

    private static func sentenceHighFrequencyLimit(
        _ key: String,
        fallback: Int,
        externalValues: [String: String]
    ) -> Int {
        guard let value = persistedValue(for: key, externalValues: externalValues) else {
            return fallback
        }
        if let number = value as? NSNumber {
            return max(0, number.intValue)
        }
        guard let raw = value as? String,
              !raw.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              let number = Int(raw.trimmingCharacters(in: .whitespacesAndNewlines)) else {
            return 0
        }
        return max(0, number)
    }

    private static func sentenceHighFrequencyLimit(_ raw: String) throws -> Int {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty {
            return 0
        }
        guard let number = Int(trimmed), number >= 0 else {
            throw NativeAotConfigurationError.invalidValue(raw)
        }
        return number
    }

    private static func boolean(
        _ key: String,
        fallback: Bool,
        externalValues: [String: String]
    ) -> Bool {
        guard let value = persistedValue(for: key, externalValues: externalValues) else {
            return fallback
        }
        if let numberValue = value as? NSNumber {
            return numberValue.boolValue
        }
        if let stringValue = value as? String,
           let parsed = try? boolean(stringValue) {
            return parsed
        }
        return fallback
    }

    private static func text(_ key: String, fallback: String, externalValues: [String: String]) -> String {
        guard let value = persistedValue(for: key, externalValues: externalValues) as? String else {
            return fallback
        }
        return String(value.prefix(32))
    }

    private static func fontName(_ key: String, fallback: String, externalValues: [String: String]) -> String {
        guard let value = persistedValue(for: key, externalValues: externalValues) as? String else {
            return fallback
        }
        return (try? validatedFontName(value)) ?? fallback
    }

    private static func theme(_ key: String, fallback: String, externalValues: [String: String]) -> String {
        guard let value = persistedValue(for: key, externalValues: externalValues) as? String else {
            return fallback
        }
        return (try? theme(value)) ?? fallback
    }

    private static func theme(_ value: String) throws -> String {
        let normalized = value.trimmingCharacters(in: .whitespacesAndNewlines)
        switch normalized.lowercased() {
        case "system": return "system"
        case "light": return "light"
        case "dark": return "dark"
        default:
            switch normalized {
            case "默认", "通透", "一般通透", "迷雾", "星夜", "纸", "粉", "赛博朋克", "清晨":
                return normalized
            default:
                throw NativeAotConfigurationError.invalidValue(value)
            }
        }
    }

    private static func validatedFontName(_ value: String) throws -> String {
        let normalized = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard normalized.count <= 128,
              !normalized.contains(where: { $0.isNewline }) else {
            throw NativeAotConfigurationError.invalidValue(value)
        }
        guard normalized.isEmpty || NSFont(name: normalized, size: 12) != nil else {
            throw NativeAotConfigurationError.invalidValue("未找到候选字体：\(value)")
        }
        return normalized
    }

    func displayPreedit(compositionPrefix: String, activeInputCode: String) -> String {
        guard !codeMasking.isEmpty else {
            return compositionPrefix + activeInputCode
        }
        let mask = Array(codeMasking)
        guard !mask.isEmpty else {
            return compositionPrefix + activeInputCode
        }
        let lut = Array("abcdefghijklmnopqrstuvwxyz;")
        let masked = activeInputCode.map { character -> Character in
            guard let index = lut.firstIndex(of: Character(character.lowercased())) else {
                return mask[0]
            }
            return mask[index % mask.count]
        }
        return compositionPrefix + String(masked)
    }

    private static func integer(_ value: String, range: ClosedRange<Int>) throws -> Int {
        guard let number = Int(value), range.contains(number) else {
            throw NativeAotConfigurationError.invalidValue(value)
        }
        return number
    }

    private static func boolean(_ value: String) throws -> Bool {
        switch value.lowercased() {
        case "on", "true", "1": return true
        case "off", "false", "0": return false
        default: throw NativeAotConfigurationError.invalidValue(value)
        }
    }

    private static func keySequence(_ key: String, fallback: String, externalValues: [String: String]) -> String {
        guard let value = persistedValue(for: key, externalValues: externalValues), let sequence = value as? String, let valid = try? keySequence(sequence) else {
            return fallback
        }
        return valid
    }

    private static func keySequence(_ value: String) throws -> String {
        let scalars = Array(value.unicodeScalars)
        guard !scalars.isEmpty,
              scalars.count <= 10,
              scalars.allSatisfy({ $0.value >= 33 && $0.value <= 126 }),
              Set(scalars).count == scalars.count else {
            throw NativeAotConfigurationError.invalidValue(value)
        }
        return value
    }

    private static func scopedKey(_ key: String) -> String {
        "TigerClawSchema.\(NativeAotSchema.current.identifier).\(key)"
    }

    private static func persistedValue(for key: String, externalValues: [String: String]) -> Any? {
        externalValues[externalKeyByDefaultsKey[key] ?? key]
            ?? UserDefaults.standard.object(forKey: scopedKey(key))
            ?? UserDefaults.standard.object(forKey: key)
    }

    private static func externalValues(for schemaIdentifier: String) -> [String: String] {
        guard let url = try? NativeAotDataRoot.configurationURL(for: schemaIdentifier) else {
            return [:]
        }
        return externalValuesCache.values(at: url)
    }

    private static func writeExternalValue(key: String, value: String, for schemaIdentifier: String) throws {
        let url = try NativeAotDataRoot.configurationURL(for: schemaIdentifier)
        var values = externalValues(for: schemaIdentifier)
        values[key] = value
        let data = try JSONEncoder().encode(values)
        try data.write(to: url, options: .atomic)
        externalValuesCache.invalidate(url: url)
    }
}

enum NativeAotConfigurationError: Error, CustomStringConvertible {
    case unknownKey(String)
    case invalidValue(String)

    var description: String {
        switch self {
        case .unknownKey(let key): return "unknown configuration key: \(key)"
        case .invalidValue(let value): return "invalid configuration value: \(value)"
        }
    }
}

struct NativeAotSchema: Equatable {
    let identifier: String
    let displayName: String
    let lexiconResourceName: String
    private let importedLexiconURL: URL?
    private let importedDirectoryURL: URL?
    private let builtIn: Bool

    init(
        identifier: String,
        displayName: String,
        lexiconResourceName: String,
        importedLexiconURL: URL? = nil,
        importedDirectoryURL: URL? = nil,
        builtIn: Bool = false
    ) {
        self.identifier = identifier
        self.displayName = displayName
        self.lexiconResourceName = lexiconResourceName
        self.importedLexiconURL = importedLexiconURL
        self.importedDirectoryURL = importedDirectoryURL
        self.builtIn = builtIn
    }

    private static let selectedDefaultsKey = "TigerClawSelectedSchema"
    private static let previousDefaultsKey = "TigerClawPreviousSchema"
    private static let customNamesDefaultsKey = "TigerClawSchemaCustomNames"
    private static let redeployGenerationDefaultsKey = "TigerClawSchemaRedeployGeneration"
    private static let selectedSchemaFileName = "selected-schema.txt"
    private static let manifestFileName = ".tigerclaw-schema.json"
    private static let companionTableFilenames = ["快符.txt", "常用符号.txt", "补充语料.txt"]
    private static let availableCache = AvailableCache()
    private static let contentRevisionCache = ContentRevisionCache()
    static let bundled: [NativeAotSchema] = {
        let single = NativeAotSchema(
            identifier: "tiger_sentence",
            displayName: "虎单",
            lexiconResourceName: "tiger_sentence.codes.txt",
            importedLexiconURL: nil,
            builtIn: true)
        let fallbackSentence = NativeAotSchema(
            identifier: "tiger_sentence_full",
            displayName: "虎整句",
            lexiconResourceName: "tiger_sentence.codes.txt",
            importedLexiconURL: nil,
            builtIn: true)
        guard let source = Bundle.main.url(forResource: "tiger_sentence.codes", withExtension: "txt"),
              let schemasDirectory = try? NativeAotDataRoot.schemasDirectoryURL() else {
            return [single, fallbackSentence]
        }
        let directory = schemasDirectory.appendingPathComponent("tiger_sentence_full", isDirectory: true)
        let preferredLexicon = directory.appendingPathComponent("虎整句.codes.txt", isDirectory: false)
        let legacyLexicon = directory.appendingPathComponent("虎整句.dict.yaml", isDirectory: false)
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let lexicon: URL
            if FileManager.default.fileExists(atPath: preferredLexicon.path) {
                lexicon = preferredLexicon
            } else if FileManager.default.fileExists(atPath: legacyLexicon.path) {
                // Keep prior editable installations working; new installs use the upstream plain-text table.
                lexicon = legacyLexicon
            } else {
                try FileManager.default.copyItem(at: source, to: preferredLexicon)
                lexicon = preferredLexicon
            }
            let supplement = directory.appendingPathComponent("补充语料.txt", isDirectory: false)
            if !FileManager.default.fileExists(atPath: supplement.path) {
                try "# 每行格式：文本 [权重]；保存后点击“重新部署当前方案”生效。\n".write(
                    to: supplement,
                    atomically: true,
                    encoding: .utf8)
            }
            let manifest = ImportedSchemaManifest(
                identifier: "tiger_sentence_full",
                primaryFileName: lexicon.lastPathComponent)
            let manifestURL = directory.appendingPathComponent(manifestFileName, isDirectory: false)
            if !FileManager.default.fileExists(atPath: manifestURL.path) {
                try JSONEncoder().encode(manifest).write(to: manifestURL, options: .atomic)
            }
            return [
                single,
                NativeAotSchema(
                    identifier: "tiger_sentence_full",
                    displayName: "虎整句",
                    lexiconResourceName: "tiger_sentence.codes.txt",
                    importedLexiconURL: lexicon,
                    importedDirectoryURL: directory,
                    builtIn: true),
            ]
        } catch {
            return [single, fallbackSentence]
        }
    }()

    static var sentenceInputSchema: NativeAotSchema {
        bundled.first(where: { $0.identifier == "tiger_sentence_full" }) ?? bundled[0]
    }

    var isBundled: Bool { builtIn }
    var isEditable: Bool { editableDirectoryURL != nil }

    var editableDirectoryURL: URL? {
        importedDirectoryURL ?? importedLexiconURL?.deletingLastPathComponent()
    }

    func contentRevision(forceRefresh: Bool = false) -> String {
        Self.contentRevisionCache.revision(for: self, forceRefresh: forceRefresh)
    }

    static var redeployGeneration: String {
        UserDefaults.standard.string(forKey: redeployGenerationDefaultsKey) ?? ""
    }

    static func requestRedeploy() {
        UserDefaults.standard.set(UUID().uuidString, forKey: redeployGenerationDefaultsKey)
        UserDefaults.standard.synchronize()
    }

    static var available: [NativeAotSchema] {
        let bundledIdentifiers = Set(bundled.map(\.identifier))
        guard let directory = try? NativeAotDataRoot.schemasDirectoryURL(),
              let urls = try? FileManager.default.contentsOfDirectory(
                  at: directory,
                  includingPropertiesForKeys: [.isDirectoryKey, .isRegularFileKey],
                  options: [.skipsHiddenFiles]) else {
            return bundled
        }
        // The same schema filenames and timestamps can occur under a
        // redeployed data root (and in isolated smoke roots). A cache key
        // based only on names then leaks schemas from the previous root.
        let signature = availabilitySignature(for: urls, directory: directory)
        if let cached = availableCache.value(for: signature) {
            return cached
        }
        let legacyFiles = urls.filter { url in
            let values = try? url.resourceValues(forKeys: [.isRegularFileKey])
            return values?.isRegularFile == true
        }
        let importedTableIdentifiers = Set(legacyFiles.flatMap(referencedImportTables(at:)))
        let legacy = legacyFiles.compactMap { url -> NativeAotSchema? in
                let extensionName = url.pathExtension.lowercased()
                guard extensionName == "txt" || extensionName == "yaml",
                      url.lastPathComponent.hasSuffix(".dict.yaml") || extensionName == "txt" else {
                    return nil
                }
                let identifier = extensionName == "txt"
                    ? url.deletingPathExtension().lastPathComponent
                    : url.deletingPathExtension().deletingPathExtension().lastPathComponent
                guard !identifier.isEmpty,
                      !bundledIdentifiers.contains(identifier),
                      !importedTableIdentifiers.contains(identifier),
                      !companionTableFilenames.contains(url.lastPathComponent) else {
                    return nil
                }
                return NativeAotSchema(
                    identifier: identifier,
                    displayName: configuredDisplayName(
                        for: identifier,
                        fallback: displayName(for: url, fallback: identifier)),
                    lexiconResourceName: "",
                    importedLexiconURL: url)
            }
        let managed = urls.compactMap { url -> NativeAotSchema? in
            let values = try? url.resourceValues(forKeys: [.isDirectoryKey])
            guard values?.isDirectory == true else { return nil }
            return schemaFromManagedDirectory(url, bundledIdentifiers: bundledIdentifiers)
        }
        let imported = (legacy + managed)
            .sorted { $0.displayName.localizedStandardCompare($1.displayName) == .orderedAscending }
        let result = bundled + imported
        availableCache.store(result, signature: signature)
        return result
    }

    static var current: NativeAotSchema {
        UserDefaults.standard.synchronize()
        let schemas = available
        let persistedSelection = try? String(
            contentsOf: NativeAotDataRoot.schemaSelectionURL(fileName: selectedSchemaFileName),
            encoding: .utf8)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let selected = persistedSelection?.isEmpty == false
            ? persistedSelection
            : UserDefaults.standard.string(forKey: selectedDefaultsKey)
        guard let selected,
              let schema = schemas.first(where: { $0.identifier == selected }) else {
            return schemas[0]
        }
        return schema
    }

    static func select(identifier: String) throws {
        guard available.contains(where: { $0.identifier == identifier }) else {
            throw NativeAotConfigurationError.invalidValue(identifier)
        }
        let currentIdentifier = current.identifier
        if currentIdentifier != identifier {
            UserDefaults.standard.set(currentIdentifier, forKey: previousDefaultsKey)
        }
        UserDefaults.standard.set(identifier, forKey: selectedDefaultsKey)
        UserDefaults.standard.synchronize()
        try identifier.write(
            to: NativeAotDataRoot.schemaSelectionURL(fileName: selectedSchemaFileName),
            atomically: true,
            encoding: .utf8)
    }

    static func toggleRecent() throws -> NativeAotSchema? {
        let currentIdentifier = current.identifier
        let previousIdentifier = UserDefaults.standard.string(forKey: previousDefaultsKey)
        let replacement = available.first(where: { $0.identifier == previousIdentifier && $0.identifier != currentIdentifier })
            ?? available.first(where: { $0.identifier != currentIdentifier })
        guard let replacement else {
            return nil
        }
        try select(identifier: replacement.identifier)
        return replacement
    }

    static func renameImported(identifier: String, displayName: String) throws {
        guard let schema = available.first(where: { $0.identifier == identifier }), !schema.isBundled else {
            throw NativeAotConfigurationError.invalidValue("内置方案不能重命名：\(identifier)")
        }
        let normalized = displayName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !normalized.isEmpty,
              normalized.count <= 60,
              !normalized.contains(where: { $0.isNewline || $0 == "\t" }) else {
            throw NativeAotConfigurationError.invalidValue(displayName)
        }
        var names = customDisplayNames()
        names[identifier] = normalized
        UserDefaults.standard.set(names, forKey: customNamesDefaultsKey)
        UserDefaults.standard.synchronize()
        availableCache.invalidate()
    }

    static func deleteImported(identifier: String) throws {
        guard let schema = available.first(where: { $0.identifier == identifier }) else {
            throw NativeAotConfigurationError.invalidValue(identifier)
        }
        guard !schema.isBundled else {
            throw NativeAotConfigurationError.invalidValue("内置方案不能删除：\(schema.displayName)")
        }

        if let directory = schema.importedDirectoryURL {
            try FileManager.default.removeItem(at: directory)
        } else {
            let otherFiles = try Set(available
                .filter { $0.identifier != schema.identifier }
                .flatMap(managedFiles(for:))
                .map { $0.standardizedFileURL.path })
            for url in try managedFiles(for: schema) where !otherFiles.contains(url.standardizedFileURL.path) {
                try? FileManager.default.removeItem(at: url)
            }
        }

        var names = customDisplayNames()
        names.removeValue(forKey: identifier)
        UserDefaults.standard.set(names, forKey: customNamesDefaultsKey)
        NativeAotConfiguration.removePersistedValues(for: identifier)
        try NativeAotDataRoot.removeSchemaData(identifier: identifier)
        if current.identifier == identifier {
            try select(identifier: bundled[0].identifier)
        }
        UserDefaults.standard.synchronize()
        availableCache.invalidate()
    }

    static func exportArchive(identifier: String) throws -> URL {
        guard let schema = available.first(where: { $0.identifier == identifier }), !schema.isBundled else {
            throw NativeAotConfigurationError.invalidValue("只能导出已导入的方案：\(identifier)")
        }
        let exportsDirectory = try NativeAotDataRoot.exportsDirectoryURL()
        let uniqueName = "\(identifier)-\(UUID().uuidString)"
        let payloadDirectory = exportsDirectory.appendingPathComponent(uniqueName, isDirectory: true)
        let archiveURL = exportsDirectory.appendingPathComponent("\(uniqueName).zip", isDirectory: false)
        try FileManager.default.createDirectory(at: payloadDirectory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: payloadDirectory) }
        for source in try managedFiles(for: schema) {
            try FileManager.default.copyItem(at: source, to: payloadDirectory.appendingPathComponent(source.lastPathComponent))
        }

        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        process.arguments = ["-c", "-k", "--keepParent", payloadDirectory.path, archiveURL.path]
        try process.run()
        process.waitUntilExit()
        guard process.terminationStatus == 0,
              FileManager.default.fileExists(atPath: archiveURL.path) else {
            throw NativeAotConfigurationError.invalidValue("无法导出方案：\(schema.displayName)")
        }
        return archiveURL
    }

    static func removeExportArchive(at path: String) throws {
        let archive = URL(fileURLWithPath: path).standardizedFileURL
        let exportsDirectory = try NativeAotDataRoot.exportsDirectoryURL().standardizedFileURL
        guard archive.pathExtension.lowercased() == "zip",
              archive.path.hasPrefix(exportsDirectory.path + "/") else {
            throw NativeAotConfigurationError.invalidValue(path)
        }
        try? FileManager.default.removeItem(at: archive)
    }

    func lexiconURL() -> URL {
        importedLexiconURL ?? Bundle.main.url(forResource: lexiconResourceName, withExtension: nil)
            ?? Bundle.main.resourceURL?.appendingPathComponent(lexiconResourceName)
            ?? URL(fileURLWithPath: lexiconResourceName)
    }

    static func importLexicon(at sourceURL: URL) throws -> NativeAotSchema {
        let filename = sourceURL.lastPathComponent
        let extensionName = sourceURL.pathExtension.lowercased()
        guard extensionName == "txt" || filename.hasSuffix(".dict.yaml") else {
            throw NativeAotConfigurationError.invalidValue("仅支持 .txt 或 .dict.yaml 码表")
        }
        let baseIdentifier = sourceURL.deletingPathExtension().deletingPathExtension().lastPathComponent
        let identifier = baseIdentifier.unicodeScalars.map { scalar in
            CharacterSet.alphanumerics.contains(scalar) || scalar.value == 45 || scalar.value == 95
                ? String(scalar)
                : "-"
        }.joined().trimmingCharacters(in: CharacterSet(charactersIn: "-"))
        guard !identifier.isEmpty else {
            throw NativeAotConfigurationError.invalidValue(filename)
        }
        let sourceFiles = try requiredLexiconFiles(startingAt: sourceURL)
        let directory = try NativeAotDataRoot.schemasDirectoryURL()
        let destinationDirectory = directory.appendingPathComponent(identifier, isDirectory: true)
        guard !FileManager.default.fileExists(atPath: destinationDirectory.path) else {
            throw NativeAotConfigurationError.invalidValue("已导入同名码表：\(identifier)")
        }
        try FileManager.default.createDirectory(at: destinationDirectory, withIntermediateDirectories: true)
        do {
            for source in sourceFiles {
                try FileManager.default.copyItem(
                    at: source,
                    to: destinationDirectory.appendingPathComponent(source.lastPathComponent))
            }
            let manifest = ImportedSchemaManifest(identifier: identifier, primaryFileName: sourceURL.lastPathComponent)
            let manifestData = try JSONEncoder().encode(manifest)
            try manifestData.write(
                to: destinationDirectory.appendingPathComponent(manifestFileName),
                options: .atomic)
        } catch {
            try? FileManager.default.removeItem(at: destinationDirectory)
            throw error
        }
        guard let schema = available.first(where: { $0.identifier == identifier }) else {
            throw NativeAotConfigurationError.invalidValue(identifier)
        }
        availableCache.invalidate()
        return schema
    }

    private static func requiredLexiconFiles(startingAt sourceURL: URL) throws -> [URL] {
        var result: [URL] = []
        var visited = Set<String>()

        func collect(_ url: URL) throws {
            let fullPath = url.standardizedFileURL.path
            guard visited.insert(fullPath).inserted else { return }
            guard FileManager.default.fileExists(atPath: fullPath) else {
                throw NativeAotConfigurationError.invalidValue("缺少码表依赖：\(url.lastPathComponent)")
            }
            result.append(url)
            for identifier in referencedImportTables(at: url) {
                guard identifier.rangeOfCharacter(from: CharacterSet(charactersIn: "/\\")) == nil,
                      !identifier.contains("..") else {
                    throw NativeAotConfigurationError.invalidValue("不支持的码表依赖：\(identifier)")
                }
                try collect(url.deletingLastPathComponent().appendingPathComponent("\(identifier).dict.yaml"))
            }
        }

        try collect(sourceURL)
        let windowsUserAdjustmentsURL = sourceURL.deletingLastPathComponent()
            .appendingPathComponent("用户调整.txt")
        if FileManager.default.fileExists(atPath: windowsUserAdjustmentsURL.path) {
            result.append(windowsUserAdjustmentsURL)
        }
        if sourceURL.lastPathComponent.hasSuffix(".dict.yaml") {
            let schemaURL = sourceURL.deletingPathExtension().deletingPathExtension()
                .appendingPathExtension("schema.yaml")
            if FileManager.default.fileExists(atPath: schemaURL.path) {
                result.append(schemaURL)
            }
            for filename in companionTableFilenames {
                let companionURL = sourceURL.deletingLastPathComponent().appendingPathComponent(filename)
                if FileManager.default.fileExists(atPath: companionURL.path) {
                    result.append(companionURL)
                }
            }
        }
        // Windows keeps candidate annotations and character splits in sibling
        // sidecar files (for example `虎码.注释` / `虎码.拆分`). They are not
        // referenced by the dictionary header, so include them explicitly
        // when importing or exporting a portable schema directory.
        let sourceDirectory = sourceURL.deletingLastPathComponent()
        let sidecars = (try? FileManager.default.contentsOfDirectory(
            at: sourceDirectory,
            includingPropertiesForKeys: [.isRegularFileKey],
            options: [.skipsHiddenFiles])) ?? []
        for sidecar in sidecars where sidecar.pathExtension == "注释" || sidecar.pathExtension == "拆分" {
            let path = sidecar.standardizedFileURL.path
            if visited.insert(path).inserted {
                result.append(sidecar)
            }
        }
        return result
    }

    private static func schemaFromManagedDirectory(
        _ directory: URL,
        bundledIdentifiers: Set<String>
    ) -> NativeAotSchema? {
        let manifestURL = directory.appendingPathComponent(manifestFileName)
        guard let data = try? Data(contentsOf: manifestURL),
              let manifest = try? JSONDecoder().decode(ImportedSchemaManifest.self, from: data),
              isValidIdentifier(manifest.identifier),
              !bundledIdentifiers.contains(manifest.identifier) else {
            return nil
        }
        let primaryURL = directory.appendingPathComponent(manifest.primaryFileName)
        guard FileManager.default.fileExists(atPath: primaryURL.path),
              primaryURL.lastPathComponent.hasSuffix(".dict.yaml") || primaryURL.pathExtension.lowercased() == "txt" else {
            return nil
        }
        return NativeAotSchema(
            identifier: manifest.identifier,
            displayName: configuredDisplayName(
                for: manifest.identifier,
                fallback: displayName(for: primaryURL, fallback: manifest.identifier)),
            lexiconResourceName: "",
            importedLexiconURL: primaryURL,
            importedDirectoryURL: directory)
    }

    private static func managedFiles(for schema: NativeAotSchema) throws -> [URL] {
        if let directory = schema.importedDirectoryURL {
            return try FileManager.default.contentsOfDirectory(
                at: directory,
                includingPropertiesForKeys: [.isRegularFileKey],
                options: [.skipsHiddenFiles])
                .filter { $0.lastPathComponent != manifestFileName }
        }
        guard let primary = schema.importedLexiconURL else { return [] }
        return try requiredLexiconFiles(startingAt: primary)
    }

    private static func fileRevision(_ url: URL) -> String {
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
              let modified = attributes[.modificationDate] as? Date,
              let size = attributes[.size] as? NSNumber else {
            return "\(url.lastPathComponent):missing"
        }
        return "\(url.lastPathComponent):\(modified.timeIntervalSinceReferenceDate):\(size.intValue)"
    }

    private static func contentRevision(for files: [URL]) -> String {
        files.map(fileRevision).sorted().joined(separator: "|")
    }

    private static func customDisplayNames() -> [String: String] {
        UserDefaults.standard.dictionary(forKey: customNamesDefaultsKey) as? [String: String] ?? [:]
    }

    private static func configuredDisplayName(for identifier: String, fallback: String) -> String {
        customDisplayNames()[identifier] ?? fallback
    }

    private static func isValidIdentifier(_ identifier: String) -> Bool {
        !identifier.isEmpty && identifier.unicodeScalars.allSatisfy {
            CharacterSet.alphanumerics.contains($0) || $0.value == 45 || $0.value == 95
        }
    }

    private static func availabilitySignature(for urls: [URL], directory: URL) -> String {
        let entries = urls.map { url in
            let values = try? url.resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey, .isDirectoryKey])
            let modified = values?.contentModificationDate?.timeIntervalSinceReferenceDate ?? 0
            let size = values?.fileSize ?? 0
            return "\(url.lastPathComponent):\(values?.isDirectory == true ? "d" : "f"):\(modified):\(size)"
        }
        .sorted()
        .joined(separator: "|")
        return "\(directory.standardizedFileURL.path)|\(entries)"
    }

    private static func referencedImportTables(at url: URL) -> [String] {
        guard url.lastPathComponent.hasSuffix(".dict.yaml"),
              let text = try? String(contentsOf: url, encoding: .utf8) else {
            return []
        }
        var values: [String] = []
        var readingImports = false
        for rawLine in text.split(whereSeparator: \.isNewline) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line == "..." { break }
            if line == "import_tables:" {
                readingImports = true
                continue
            }
            if readingImports, line.hasPrefix("- ") {
                let value = line.dropFirst(2).trimmingCharacters(in: .whitespaces)
                if !value.isEmpty { values.append(value) }
                continue
            }
            if !rawLine.first.map({ $0.isWhitespace })! {
                readingImports = false
            }
        }
        return values
    }

    private static func displayName(for url: URL, fallback: String) -> String {
        if fallback == "tigress" {
            return "虎码字词"
        }
        let schemaURL = url.deletingPathExtension().deletingPathExtension()
            .appendingPathExtension("schema.yaml")
        if let schemaText = try? String(contentsOf: schemaURL, encoding: .utf8),
           let schemaName = schemaDisplayName(in: schemaText) {
            return schemaName
        }
        guard let text = try? String(contentsOf: url, encoding: .utf8) else {
            return fallback
        }
        for rawLine in text.split(whereSeparator: \.isNewline).prefix(80) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("name:") {
                let name = line.dropFirst("name:".count).trimmingCharacters(in: .whitespacesAndNewlines)
                if !name.isEmpty { return name }
            }
        }
        return fallback
    }

    private static func schemaDisplayName(in text: String) -> String? {
        for rawLine in text.split(whereSeparator: \.isNewline).prefix(80) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line == "switches:" { break }
            if line.hasPrefix("name:") {
                let name = line.dropFirst("name:".count)
                    .trimmingCharacters(in: .whitespacesAndNewlines)
                    .trimmingCharacters(in: CharacterSet(charactersIn: "\"'"))
                if !name.isEmpty { return name }
            }
        }
        return nil
    }

    private struct ImportedSchemaManifest: Codable {
        let identifier: String
        let primaryFileName: String
    }

    private final class AvailableCache: @unchecked Sendable {
        private let lock = NSLock()
        private var signature: String?
        private var schemas: [NativeAotSchema]?

        func value(for signature: String) -> [NativeAotSchema]? {
            lock.lock()
            defer { lock.unlock() }
            return self.signature == signature ? schemas : nil
        }

        func store(_ schemas: [NativeAotSchema], signature: String) {
            lock.lock()
            self.signature = signature
            self.schemas = schemas
            lock.unlock()
        }

        func invalidate() {
            lock.lock()
            signature = nil
            schemas = nil
            lock.unlock()
        }
    }

    private final class ContentRevisionCache: @unchecked Sendable {
        private struct Entry {
            let files: [URL]
            let primaryRevision: String
            let revision: String
            let nextProbeDate: Date
        }

        private let probeInterval: TimeInterval = 0.75
        private let lock = NSLock()
        private var entries: [String: Entry] = [:]

        func revision(for schema: NativeAotSchema, forceRefresh: Bool) -> String {
            let primary = schema.lexiconURL()
            let key = primary.standardizedFileURL.path

            lock.lock()
            defer { lock.unlock() }
            let now = Date()
            if let entry = entries[key], !forceRefresh, now < entry.nextProbeDate {
                return entry.revision
            }

            let currentPrimaryRevision = NativeAotSchema.fileRevision(primary)
            if let entry = entries[key], entry.primaryRevision == currentPrimaryRevision {
                let currentRevision = NativeAotSchema.contentRevision(for: entry.files)
                if currentRevision == entry.revision {
                    entries[key] = Entry(
                        files: entry.files,
                        primaryRevision: currentPrimaryRevision,
                        revision: entry.revision,
                        nextProbeDate: now.addingTimeInterval(probeInterval))
                    return entry.revision
                }
                entries[key] = Entry(
                    files: entry.files,
                    primaryRevision: currentPrimaryRevision,
                    revision: currentRevision,
                    nextProbeDate: now.addingTimeInterval(probeInterval))
                return currentRevision
            }

            let files = (try? NativeAotSchema.managedFiles(for: schema)) ?? [primary]
            let revision = NativeAotSchema.contentRevision(for: files)
            entries[key] = Entry(
                files: files,
                primaryRevision: currentPrimaryRevision,
                revision: revision,
                nextProbeDate: now.addingTimeInterval(probeInterval))
            return revision
        }
    }
}

enum NativeAotDataRoot {
    private static let directoryName = "TigerClaw"
    private static let userDictionaryFileName = "user-dictionary.tsv"
    private static let candidateAdjustmentsFileName = "candidate-adjustments.tsv"
    private static let windowsUserAdjustmentsFileName = "用户调整.txt"
    private static let selectionKeyConfigFileName = "selection-keys.conf"

    static func userDictionaryURL() throws -> URL {
        if let url = try windowsUserAdjustmentsURL() {
            return url
        }
        return try schemaDataURL(baseName: userDictionaryFileName)
    }

    static func usesWindowsUserAdjustments() -> Bool {
        let schema = NativeAotSchema.current
        return !schema.isBundled && schema.editableDirectoryURL != nil
    }

    static func candidateAdjustmentsURL() throws -> URL {
        try schemaDataURL(baseName: candidateAdjustmentsFileName)
    }

    static func selectionKeyConfigURL() throws -> URL {
        let root = try rootURL()
        let schemaIdentifier = NativeAotSchema.current.identifier
        let schemaDirectory = root.appendingPathComponent("schema-data/\(schemaIdentifier)", isDirectory: true)
        try FileManager.default.createDirectory(at: schemaDirectory, withIntermediateDirectories: true)
        return schemaDirectory.appendingPathComponent(selectionKeyConfigFileName, isDirectory: false)
    }

    static func schemasDirectoryURL() throws -> URL {
        let root = try rootURL()
        let directory = root.appendingPathComponent("schemas", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    static func configurationURL(for schemaIdentifier: String) throws -> URL {
        let root = try rootURL()
        let directory = root.appendingPathComponent("settings", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory.appendingPathComponent("\(schemaIdentifier).json", isDirectory: false)
    }

    static func schemaSelectionURL(fileName: String) throws -> URL {
        try rootURL().appendingPathComponent(fileName, isDirectory: false)
    }

    static func exportsDirectoryURL() throws -> URL {
        let directory = try rootURL().appendingPathComponent("exports", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    static func pinyinLexiconURL() throws -> URL? {
        let directory = try rootURL().appendingPathComponent("拼音反查码表", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let destination = directory.appendingPathComponent("拼音.txt", isDirectory: false)
        if !FileManager.default.fileExists(atPath: destination.path),
           let source = Bundle.main.url(forResource: "拼音", withExtension: "txt", subdirectory: "拼音反查码表") {
            try FileManager.default.copyItem(at: source, to: destination)
        }
        return FileManager.default.fileExists(atPath: destination.path) ? destination : nil
    }

    static func sentenceModelURL() -> URL? {
        Bundle.main.url(
            forResource: "sentence-ngram-v2",
            withExtension: "bin",
            subdirectory: "Models")
    }

    static func sentenceQwenModelURL() -> URL? {
        Bundle.main.url(
            forResource: "sentence-qwen-q8",
            withExtension: "gguf",
            subdirectory: "Models")
    }

    static func sentenceQwenNativeLibraryURL() -> URL? {
        Bundle.main.privateFrameworksURL?
            .appendingPathComponent("libTigerClaw.Sentence.Native.dylib", isDirectory: false)
    }

    static func removeSchemaData(identifier: String) throws {
        let root = try rootURL()
        try? FileManager.default.removeItem(
            at: root.appendingPathComponent("schema-data/\(identifier)", isDirectory: true))
        for filename in ["user-dictionary-\(identifier).tsv", "candidate-adjustments-\(identifier).tsv"] {
            try? FileManager.default.removeItem(at: root.appendingPathComponent(filename, isDirectory: false))
        }
    }

    private static func rootURL() throws -> URL {
        if let rawPath = getenv("TIGERCLAW_DATA_ROOT"), rawPath.pointee != 0 {
            let root = URL(fileURLWithPath: String(cString: rawPath), isDirectory: true)
                .standardizedFileURL
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
            return root
        }
        let applicationSupport = try FileManager.default.url(
            for: .applicationSupportDirectory,
            in: .userDomainMask,
            appropriateFor: nil,
            create: true)
        let root = applicationSupport.appendingPathComponent(directoryName, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }

    private static func schemaDataURL(baseName: String) throws -> URL {
        let root = try rootURL()
        let schemaIdentifier = NativeAotSchema.current.identifier
        let schemaDirectory = root.appendingPathComponent("schema-data/\(schemaIdentifier)", isDirectory: true)
        try FileManager.default.createDirectory(at: schemaDirectory, withIntermediateDirectories: true)
        let destination = schemaDirectory.appendingPathComponent(baseName, isDirectory: false)
        let legacyFlat = root.appendingPathComponent("\(baseName.dropLast(4))-\(schemaIdentifier).tsv", isDirectory: false)
        let legacy = root.appendingPathComponent(baseName, isDirectory: false)
        if !FileManager.default.fileExists(atPath: destination.path),
           FileManager.default.fileExists(atPath: legacyFlat.path) {
            try FileManager.default.moveItem(at: legacyFlat, to: destination)
        } else if !FileManager.default.fileExists(atPath: destination.path),
                  FileManager.default.fileExists(atPath: legacy.path) {
            try FileManager.default.moveItem(at: legacy, to: destination)
        }
        return destination
    }

    private static func windowsUserAdjustmentsURL() throws -> URL? {
        let schema = NativeAotSchema.current
        guard !schema.isBundled,
              let directory = schema.editableDirectoryURL else {
            return nil
        }
        let destination = directory.appendingPathComponent(windowsUserAdjustmentsFileName, isDirectory: false)
        guard !FileManager.default.fileExists(atPath: destination.path) else {
            return destination
        }

        let legacyUserDictionary = try schemaDataURL(baseName: userDictionaryFileName)
        let legacyCandidateAdjustments = try schemaDataURL(baseName: candidateAdjustmentsFileName)
        let migrated = try windowsAdjustmentMigrationContents(
            legacyUserDictionary: legacyUserDictionary,
            legacyCandidateAdjustments: legacyCandidateAdjustments)
        guard !migrated.isEmpty else {
            return destination
        }
        try migrated.write(to: destination, atomically: true, encoding: .utf8)
        return destination
    }

    private static func windowsAdjustmentMigrationContents(
        legacyUserDictionary: URL,
        legacyCandidateAdjustments: URL
    ) throws -> String {
        var lines: [String] = []
        if FileManager.default.fileExists(atPath: legacyUserDictionary.path) {
            let contents = try String(contentsOf: legacyUserDictionary, encoding: .utf8)
            for rawLine in contents.split(whereSeparator: \.isNewline) {
                let fields = rawLine.split(separator: "\t", omittingEmptySubsequences: false)
                guard fields.count == 2 else { continue }
                let text = String(fields[0])
                let code = String(fields[1]).trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
                guard !text.isEmpty, !code.isEmpty else { continue }
                // The former macOS TSV inserted user entries at the front. Preserve
                // that established ordering while converting to Windows operations.
                lines.append("{添加}\(code)\t\(text)")
                lines.append("{置顶}\(code)\t\(text)")
            }
        }
        if FileManager.default.fileExists(atPath: legacyCandidateAdjustments.path) {
            let contents = try String(contentsOf: legacyCandidateAdjustments, encoding: .utf8)
            for rawLine in contents.split(whereSeparator: \.isNewline) {
                let fields = rawLine.split(separator: "\t", omittingEmptySubsequences: false)
                guard fields.count == 3 else { continue }
                let operation = switch String(fields[0]) {
                case "top": "置顶"
                case "advance": "前移"
                case "delete": "删除"
                default: ""
                }
                let code = String(fields[1]).trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
                let text = String(fields[2])
                guard !operation.isEmpty, !code.isEmpty, !text.isEmpty else { continue }
                lines.append("{\(operation)}\(code)\t\(text)")
            }
        }
        return lines.isEmpty ? "" : lines.joined(separator: "\n") + "\n"
    }

    static func userDictionaryRevision() -> String {
        let urls = [try? userDictionaryURL(), try? candidateAdjustmentsURL()].compactMap { $0 }
        let revisions = urls.compactMap { url -> String? in
            guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
                  let modificationDate = attributes[.modificationDate] as? Date,
                  let size = attributes[.size] as? NSNumber else {
                return nil
            }
            return "\(url.lastPathComponent):\(modificationDate.timeIntervalSinceReferenceDate):\(size.intValue)"
        }
        return revisions.isEmpty ? "missing" : revisions.sorted().joined(separator: ",")
    }
}

/// Per-schema physical selection-key bindings.  The configuration deliberately
/// uses Carbon key codes because macOS does not expose Windows virtual-key
/// values to InputMethodKit.  Named tokens keep normal edits readable while a
/// decimal or 0x-prefixed macOS key code covers every other physical key.
struct NativeAotSelectionKeyBindings: Equatable {
    private let selectionByKeyCode: [UInt16: Int]

    private init(selectionByKeyCode: [UInt16: Int]) {
        self.selectionByKeyCode = selectionByKeyCode
    }

    static let defaultConfiguration = """
    # TigerClaw macOS custom selection keys
    # Format: <n选> <key1> <key2> ...   (n: 1 through 10)
    # A key can be a named physical key, a Windows Virtual-Key value in decimal / 0x hex,
    # or a macOS Carbon key code prefixed with MAC_ (for example MAC_18 or MAC_0x12).
    # Named keys: KEY_A..KEY_Z, DIGIT_0..DIGIT_9, F1..F12, SHIFT, CONTROL,
    # OPTION, COMMAND, CAPS_LOCK, TAB, SPACE, ENTER, ESCAPE, BACKSPACE,
    # SEMICOLON, QUOTE, MINUS, EQUAL, COMMA, PERIOD, SLASH, LEFT_BRACKET,
    # RIGHT_BRACKET.  Windows-style VK_* aliases (for example VK_SHIFT, VK_A,
    # VK_F1, VK_CAPITAL) are accepted too.  Numeric values use Windows Virtual-Key
    # semantics so existing Windows files can be reused.  SHIFT / CONTROL / OPTION / COMMAND
    # each include left and right keys.
    # Modifier keys choose a candidate only while a composition is visible;
    # outside composition, Caps Lock continues to switch Chinese/English.

    1选 DIGIT_1
    2选 DIGIT_2
    3选 DIGIT_3
    4选 DIGIT_4
    5选 DIGIT_5
    6选 DIGIT_6
    7选 DIGIT_7
    8选 DIGIT_8
    9选 DIGIT_9
    10选 DIGIT_0
    """

    static func configURL() throws -> URL {
        try NativeAotDataRoot.selectionKeyConfigURL()
    }

    static func loadCurrentSchema() -> NativeAotSelectionKeyBindings {
        do {
            let url = try configURL()
            if !FileManager.default.fileExists(atPath: url.path) {
                try defaultConfiguration.write(to: url, atomically: true, encoding: .utf8)
            }
            return try parse(String(contentsOf: url, encoding: .utf8))
        } catch {
            LifecycleTrace.record("selection key configuration fallback error=\(error)")
            return (try? parse(defaultConfiguration)) ?? NativeAotSelectionKeyBindings(selectionByKeyCode: [:])
        }
    }

    static func configurationRevision() -> String {
        guard let url = try? configURL(),
              let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
              let modificationDate = attributes[.modificationDate] as? Date,
              let size = attributes[.size] as? NSNumber else {
            return "missing"
        }
        return "\(modificationDate.timeIntervalSinceReferenceDate):\(size.intValue)"
    }

    static func parse(_ contents: String) throws -> NativeAotSelectionKeyBindings {
        var bindings: [UInt16: Int] = [:]
        for (lineNumber, rawLine) in contents.split(whereSeparator: \.isNewline).enumerated() {
            let line = rawLine.split(separator: "#", maxSplits: 1, omittingEmptySubsequences: false)[0]
                .trimmingCharacters(in: .whitespacesAndNewlines)
            guard !line.isEmpty else { continue }
            let fields = line.split(whereSeparator: \.isWhitespace)
            guard let first = fields.first,
                  first.last == "选",
                  let slot = Int(first.dropLast()),
                  (1 ... 10).contains(slot) else {
                throw NativeAotConfigurationError.invalidValue("selection key line \(lineNumber + 1): \(line)")
            }
            for token in fields.dropFirst() {
                for keyCode in try keyCodes(for: String(token)) where bindings[keyCode] == nil {
                    bindings[keyCode] = slot - 1
                }
            }
        }
        return NativeAotSelectionKeyBindings(selectionByKeyCode: bindings)
    }

    func selectionIndex(for keyCode: UInt16) -> Int? {
        selectionByKeyCode[keyCode]
    }

    static func modifierIsPressed(for event: NSEvent) -> Bool {
        switch Int(event.keyCode) {
        case kVK_Shift, kVK_RightShift:
            return event.modifierFlags.contains(.shift)
        case kVK_Control, kVK_RightControl:
            return event.modifierFlags.contains(.control)
        case kVK_Option, kVK_RightOption:
            return event.modifierFlags.contains(.option)
        case kVK_Command, kVK_RightCommand:
            return event.modifierFlags.contains(.command)
        case kVK_CapsLock:
            return event.modifierFlags.contains(.capsLock)
        default:
            return false
        }
    }

    private static func keyCodes(for rawToken: String) throws -> [UInt16] {
        let token = rawToken.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
        if token.hasPrefix("MAC_") {
            let valueToken = token.dropFirst(4)
            if valueToken.hasPrefix("0X"), let value = UInt16(valueToken.dropFirst(2), radix: 16) {
                return [value]
            }
            if let value = UInt16(valueToken) {
                return [value]
            }
            throw NativeAotConfigurationError.invalidValue("invalid macOS selection key \(rawToken)")
        }
        if token.hasPrefix("0X"), let value = UInt16(token.dropFirst(2), radix: 16),
           let keyCodes = keyCodes(forWindowsVirtualKey: value) {
            return keyCodes
        }
        if let value = UInt16(token), let keyCodes = keyCodes(forWindowsVirtualKey: value) {
            return keyCodes
        }
        if let functionKeyCode = functionKeyCodes[token] {
            return [functionKeyCode]
        }
        if token.hasPrefix("VK_"), let functionKeyCode = functionKeyCodes[String(token.dropFirst(3))] {
            return [functionKeyCode]
        }
        if let code = namedKeyCodes[token] {
            return code
        }
        throw NativeAotConfigurationError.invalidValue("unknown selection key \(rawToken)")
    }

    private static func keyCodes(forWindowsVirtualKey value: UInt16) -> [UInt16]? {
        switch Int(value) {
        case 0x08: return namedKeyCodes["BACKSPACE"]
        case 0x09: return namedKeyCodes["TAB"]
        case 0x0D: return namedKeyCodes["ENTER"]
        case 0x10: return namedKeyCodes["SHIFT"]
        case 0x11: return namedKeyCodes["CONTROL"]
        case 0x12: return namedKeyCodes["OPTION"]
        case 0x14: return namedKeyCodes["CAPS_LOCK"]
        case 0x1B: return namedKeyCodes["ESCAPE"]
        case 0x20: return namedKeyCodes["SPACE"]
        case 0x30 ... 0x39:
            return namedKeyCodes["DIGIT_\(value - 0x30)"]
        case 0x41 ... 0x5A:
            guard let scalar = UnicodeScalar(Int(value)) else { return nil }
            return namedKeyCodes["KEY_\(Character(scalar))"]
        case 0x70 ... 0x7B:
            return functionKeyCodes["F\(value - 0x70 + 1)"].map { [$0] }
        case 0xBA: return namedKeyCodes["SEMICOLON"]
        case 0xBB: return namedKeyCodes["EQUAL"]
        case 0xBC: return namedKeyCodes["COMMA"]
        case 0xBD: return namedKeyCodes["MINUS"]
        case 0xBE: return namedKeyCodes["PERIOD"]
        case 0xBF: return namedKeyCodes["SLASH"]
        case 0xDB: return namedKeyCodes["LEFT_BRACKET"]
        case 0xDD: return namedKeyCodes["RIGHT_BRACKET"]
        case 0xDE: return namedKeyCodes["QUOTE"]
        default: return nil
        }
    }

    private static let namedKeyCodes: [String: [UInt16]] = {
        var values: [String: [UInt16]] = [
            "SHIFT": [UInt16(kVK_Shift), UInt16(kVK_RightShift)],
            "VK_SHIFT": [UInt16(kVK_Shift), UInt16(kVK_RightShift)],
            "CONTROL": [UInt16(kVK_Control), UInt16(kVK_RightControl)],
            "CTRL": [UInt16(kVK_Control), UInt16(kVK_RightControl)],
            "VK_CONTROL": [UInt16(kVK_Control), UInt16(kVK_RightControl)],
            "OPTION": [UInt16(kVK_Option), UInt16(kVK_RightOption)],
            "ALT": [UInt16(kVK_Option), UInt16(kVK_RightOption)],
            "VK_MENU": [UInt16(kVK_Option), UInt16(kVK_RightOption)],
            "COMMAND": [UInt16(kVK_Command), UInt16(kVK_RightCommand)],
            "CMD": [UInt16(kVK_Command), UInt16(kVK_RightCommand)],
            "CAPS_LOCK": [UInt16(kVK_CapsLock)],
            "VK_CAPITAL": [UInt16(kVK_CapsLock)],
            "TAB": [UInt16(kVK_Tab)],
            "VK_TAB": [UInt16(kVK_Tab)],
            "SPACE": [UInt16(kVK_Space)],
            "VK_SPACE": [UInt16(kVK_Space)],
            "ENTER": [UInt16(kVK_Return)],
            "VK_RETURN": [UInt16(kVK_Return)],
            "ESCAPE": [UInt16(kVK_Escape)],
            "VK_ESCAPE": [UInt16(kVK_Escape)],
            "BACKSPACE": [UInt16(kVK_Delete)],
            "VK_BACK": [UInt16(kVK_Delete)],
            "SEMICOLON": [UInt16(kVK_ANSI_Semicolon)],
            "QUOTE": [UInt16(kVK_ANSI_Quote)],
            "MINUS": [UInt16(kVK_ANSI_Minus)],
            "EQUAL": [UInt16(kVK_ANSI_Equal)],
            "COMMA": [UInt16(kVK_ANSI_Comma)],
            "PERIOD": [UInt16(kVK_ANSI_Period)],
            "SLASH": [UInt16(kVK_ANSI_Slash)],
            "LEFT_BRACKET": [UInt16(kVK_ANSI_LeftBracket)],
            "RIGHT_BRACKET": [UInt16(kVK_ANSI_RightBracket)],
        ]
        let letters: [(Character, Int)] = [
            ("A", kVK_ANSI_A), ("B", kVK_ANSI_B), ("C", kVK_ANSI_C), ("D", kVK_ANSI_D),
            ("E", kVK_ANSI_E), ("F", kVK_ANSI_F), ("G", kVK_ANSI_G), ("H", kVK_ANSI_H),
            ("I", kVK_ANSI_I), ("J", kVK_ANSI_J), ("K", kVK_ANSI_K), ("L", kVK_ANSI_L),
            ("M", kVK_ANSI_M), ("N", kVK_ANSI_N), ("O", kVK_ANSI_O), ("P", kVK_ANSI_P),
            ("Q", kVK_ANSI_Q), ("R", kVK_ANSI_R), ("S", kVK_ANSI_S), ("T", kVK_ANSI_T),
            ("U", kVK_ANSI_U), ("V", kVK_ANSI_V), ("W", kVK_ANSI_W), ("X", kVK_ANSI_X),
            ("Y", kVK_ANSI_Y), ("Z", kVK_ANSI_Z),
        ]
        for (letter, code) in letters {
            values["KEY_\(letter)"] = [UInt16(code)]
            values["VK_\(letter)"] = [UInt16(code)]
        }
        let digits: [(Character, Int)] = [
            ("0", kVK_ANSI_0), ("1", kVK_ANSI_1), ("2", kVK_ANSI_2), ("3", kVK_ANSI_3),
            ("4", kVK_ANSI_4), ("5", kVK_ANSI_5), ("6", kVK_ANSI_6), ("7", kVK_ANSI_7),
            ("8", kVK_ANSI_8), ("9", kVK_ANSI_9),
        ]
        for (digit, code) in digits {
            values["DIGIT_\(digit)"] = [UInt16(code)]
            values["VK_\(digit)"] = [UInt16(code)]
        }
        return values
    }()

    private static let functionKeyCodes: [String: UInt16] = [
        "F1": UInt16(kVK_F1), "F2": UInt16(kVK_F2), "F3": UInt16(kVK_F3),
        "F4": UInt16(kVK_F4), "F5": UInt16(kVK_F5), "F6": UInt16(kVK_F6),
        "F7": UInt16(kVK_F7), "F8": UInt16(kVK_F8), "F9": UInt16(kVK_F9),
        "F10": UInt16(kVK_F10), "F11": UInt16(kVK_F11), "F12": UInt16(kVK_F12),
    ]
}

struct NativeAotUserDictionaryEntry: Equatable {
    let code: String
    let text: String
}

enum NativeAotUserDictionary {
    static func entries() throws -> [NativeAotUserDictionaryEntry] {
        let url = try NativeAotDataRoot.userDictionaryURL()
        guard FileManager.default.fileExists(atPath: url.path) else {
            return []
        }

        if NativeAotDataRoot.usesWindowsUserAdjustments() {
            return windowsAdjustmentEntries(url)
        }

        return try String(contentsOf: url, encoding: .utf8)
            .split(whereSeparator: \.isNewline)
            .compactMap { line in
                let fields = line.split(separator: "\t", omittingEmptySubsequences: false)
                guard fields.count == 2,
                      let code = normalizedCode(String(fields[1])),
                      !fields[0].isEmpty else {
                    return nil
                }
                return NativeAotUserDictionaryEntry(code: code, text: String(fields[0]))
            }
    }

    static func add(code: String, text: String) throws -> Bool {
        guard let normalizedCode = normalizedCode(code), isValidText(text) else {
            throw NativeAotConfigurationError.invalidValue("\(code) / \(text)")
        }

        var values = try entries()
        guard !values.contains(where: { $0.code == normalizedCode && $0.text == text }) else {
            return false
        }
        if NativeAotDataRoot.usesWindowsUserAdjustments() {
            try appendWindowsAdjustment(operation: "添加", code: normalizedCode, text: text)
            return true
        }
        values.append(NativeAotUserDictionaryEntry(code: normalizedCode, text: text))
        try write(values)
        return true
    }

    static func remove(code: String, text: String) throws -> Bool {
        guard let normalizedCode = normalizedCode(code), isValidText(text) else {
            throw NativeAotConfigurationError.invalidValue("\(code) / \(text)")
        }

        let values = try entries()
        guard values.contains(where: { $0.code == normalizedCode && $0.text == text }) else {
            return false
        }
        if NativeAotDataRoot.usesWindowsUserAdjustments() {
            try appendWindowsAdjustment(operation: "删除", code: normalizedCode, text: text)
            return true
        }
        let remaining = values.filter { $0.code != normalizedCode || $0.text != text }
        try write(remaining)
        return true
    }

    static func adjust(code: String, text: String, operation: NativeAotCandidateAdjustment) throws {
        guard let normalizedCode = normalizedCode(code), isValidText(text) else {
            throw NativeAotConfigurationError.invalidValue("\(code) / \(text)")
        }
        if NativeAotDataRoot.usesWindowsUserAdjustments() {
            let windowsOperation: String
            switch operation {
            case .top:
                windowsOperation = "置顶"
            case .advance:
                windowsOperation = "前移"
            case .delete:
                windowsOperation = "删除"
            }
            try appendWindowsAdjustment(operation: windowsOperation, code: normalizedCode, text: text)
            return
        }
        let url = try NativeAotDataRoot.candidateAdjustmentsURL()
        let line = "\(operation.rawValue)\t\(normalizedCode)\t\(text)\n"
        guard let data = line.data(using: .utf8) else {
            throw NativeAotConfigurationError.invalidValue("candidate adjustment encoding")
        }
        if FileManager.default.fileExists(atPath: url.path) {
            let handle = try FileHandle(forWritingTo: url)
            defer { try? handle.close() }
            try handle.seekToEnd()
            try handle.write(contentsOf: data)
        } else {
            try data.write(to: url, options: .atomic)
        }
    }

    private static func write(_ entries: [NativeAotUserDictionaryEntry]) throws {
        let contents = entries.map { "\($0.text)\t\($0.code)" }.joined(separator: "\n")
        let value = contents.isEmpty ? "" : contents + "\n"
        guard let data = value.data(using: .utf8) else {
            throw NativeAotConfigurationError.invalidValue("user dictionary encoding")
        }
        try data.write(to: NativeAotDataRoot.userDictionaryURL(), options: .atomic)
    }

    private static func windowsAdjustmentEntries(_ url: URL) -> [NativeAotUserDictionaryEntry] {
        var values: [NativeAotUserDictionaryEntry] = []
        guard let contents = try? String(contentsOf: url, encoding: .utf8) else {
            return values
        }
        for rawLine in contents.split(whereSeparator: \.isNewline) {
            let line = String(rawLine).trimmingCharacters(in: .whitespacesAndNewlines)
            let operations: [(prefix: String, adds: Bool)] = [("{添加}", true), ("{删除}", false)]
            guard let operation = operations.first(where: { line.hasPrefix($0.prefix) }) else {
                continue
            }
            let payload = String(line.dropFirst(operation.prefix.count))
            guard let separator = payload.firstIndex(of: "\t"),
                  let code = normalizedCode(String(payload[..<separator])) else {
                continue
            }
            let text = String(payload[payload.index(after: separator)...])
            guard isValidText(text) else {
                continue
            }
            if operation.adds {
                values.removeAll { $0.code == code && $0.text == text }
                values.append(NativeAotUserDictionaryEntry(code: code, text: text))
            } else {
                values.removeAll { $0.code == code && $0.text == text }
            }
        }
        return values
    }

    private static func appendWindowsAdjustment(operation: String, code: String, text: String) throws {
        let url = try NativeAotDataRoot.userDictionaryURL()
        let line = "{\(operation)}\(code)\t\(text)\n"
        guard let data = line.data(using: .utf8) else {
            throw NativeAotConfigurationError.invalidValue("user adjustment encoding")
        }
        if FileManager.default.fileExists(atPath: url.path) {
            let handle = try FileHandle(forWritingTo: url)
            defer { try? handle.close() }
            try handle.seekToEnd()
            try handle.write(contentsOf: data)
        } else {
            try data.write(to: url, options: .atomic)
        }
    }

    private static func normalizedCode(_ value: String) -> String? {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty,
              trimmed.unicodeScalars.allSatisfy({
                  (65 ... 90).contains($0.value) ||
                  (97 ... 122).contains($0.value) ||
                  $0.value == 59 || $0.value == 47 || $0.value == 91
              }) else {
            return nil
        }
        return trimmed.lowercased()
    }

    private static func isValidText(_ value: String) -> Bool {
        !value.isEmpty && !value.contains(where: { $0 == "\n" || $0 == "\r" || $0 == "\t" })
    }
}

enum NativeAotCodeComposer {
    static func constructCode(for text: String, lexiconURL: URL) -> String {
        let source = text.replacingOccurrences(of: " ", with: "")
            .replacingOccurrences(of: "\n", with: "")
            .replacingOccurrences(of: "\r", with: "")
            .replacingOccurrences(of: "\t", with: "")
        guard !source.isEmpty else { return "" }

        let lookup = singleCharacterCodeLookup(lexiconURL: lexiconURL)
        let codes = source.compactMap { lookup[String($0)] }
        guard codes.count == source.count else { return "" }

        switch codes.count {
        case 1:
            return codes[0]
        case 2:
            guard codes[0].count >= 2, codes[1].count >= 2 else { return "" }
            return String(codes[0].prefix(2)) + String(codes[1].prefix(2))
        case 3:
            guard codes[0].count >= 1, codes[1].count >= 1, codes[2].count >= 2 else { return "" }
            return String(codes[0].prefix(1)) + String(codes[1].prefix(1)) + String(codes[2].prefix(2))
        default:
            guard let first = codes.first?.first,
                  let second = codes[1].first,
                  let third = codes[2].first,
                  let last = codes.last?.first else { return "" }
            return String([first, second, third, last])
        }
    }

    private static func singleCharacterCodeLookup(lexiconURL: URL) -> [String: String] {
        guard let contents = try? String(contentsOf: lexiconURL, encoding: .utf8) else {
            return [:]
        }

        var inBody = !lexiconURL.lastPathComponent.lowercased().hasSuffix(".dict.yaml")
        var columns: [String] = []
        var readingColumns = false
        var result: [String: String] = [:]

        for rawLine in contents.split(whereSeparator: \.isNewline) {
            let line = rawLine.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !line.isEmpty, !line.hasPrefix("#") else { continue }
            if !inBody {
                if line == "..." {
                    inBody = true
                    readingColumns = false
                    continue
                }
                if line == "columns:" {
                    readingColumns = true
                    continue
                }
                if readingColumns, line.hasPrefix("- ") {
                    columns.append(String(line.dropFirst(2)).trimmingCharacters(in: .whitespaces))
                } else if !rawLine.first!.isWhitespace {
                    readingColumns = false
                }
                continue
            }

            let parts = line.split(whereSeparator: \.isWhitespace)
            let textIndex = columns.firstIndex(where: { $0.caseInsensitiveCompare("text") == .orderedSame }) ?? 0
            let codeIndex = columns.firstIndex(where: { $0.caseInsensitiveCompare("code") == .orderedSame }) ?? 1
            guard parts.count > max(textIndex, codeIndex) else { continue }
            let text = String(parts[textIndex])
            guard text.count == 1 else { continue }
            let code = normalizedBaseCode(String(parts[codeIndex]))
            guard code.count >= 2 else { continue }
            if let existing = result[text] {
                if code.count < existing.count || (code.count == existing.count && code < existing) {
                    result[text] = code
                }
            } else {
                result[text] = code
            }
        }
        return result
    }

    private static func normalizedBaseCode(_ source: String) -> String {
        guard let last = source.last else { return source }
        if (last >= "2" && last <= "9") || last == ";" || last == "'" {
            return String(source.dropLast())
        }
        return source
    }
}

enum NativeAotCandidateAdjustment: String {
    case top
    case advance
    case delete
}

enum NativeAotConfigurationCommand {
    static func runIfRequested(arguments: [String]) -> Int32? {
        if let index = arguments.firstIndex(of: "--tigerclaw-mixed-input") {
            guard arguments.indices.contains(index + 1) else {
                fputs("missing on/off value\n", stderr)
                return 64
            }
            do {
                try NativeAotConfiguration.setPersistedValue(key: "unlimited-mixed-input", value: arguments[index + 1])
                print("mixed-input=\(NativeAotConfiguration.current.unlimitedMixedInput ? "on" : "off")")
                return 0
            } catch {
                fputs("\(error)\n", stderr)
                return 64
            }
        }

        if let index = arguments.firstIndex(of: "--tigerclaw-config") {
            let values = Array(arguments.dropFirst(index + 1))
            switch values {
            case ["show"]:
                NativeAotConfiguration.diagnosticLines().forEach { print($0) }
                return 0
            case let values where values.count == 3 && values[0] == "set":
                do {
                    let key = values[1]
                    let value = values[2]
                    try NativeAotConfiguration.setPersistedValue(key: key, value: value)
                    print("\(key)=\(value)")
                    return 0
                } catch {
                    fputs("\(error)\n", stderr)
                    return 64
                }
            default:
                fputs("usage: --tigerclaw-config show | set <key> <value>\n", stderr)
                return 64
            }
        }

        if let index = arguments.firstIndex(of: "--tigerclaw-selection-keys") {
            let values = Array(arguments.dropFirst(index + 1))
            do {
                let url = try NativeAotSelectionKeyBindings.configURL()
                if !FileManager.default.fileExists(atPath: url.path) {
                    try NativeAotSelectionKeyBindings.defaultConfiguration.write(to: url, atomically: true, encoding: .utf8)
                }
                switch values {
                case ["path"]:
                    print("path=\(url.path)")
                case ["show"]:
                    print(try String(contentsOf: url, encoding: .utf8), terminator: "")
                case let values where values.count == 2 && values[0] == "set":
                    let contents = values[1]
                    _ = try NativeAotSelectionKeyBindings.parse(contents)
                    try contents.write(to: url, atomically: true, encoding: .utf8)
                    print("path=\(url.path)")
                case ["reset"]:
                    try NativeAotSelectionKeyBindings.defaultConfiguration.write(to: url, atomically: true, encoding: .utf8)
                    print("path=\(url.path)")
                default:
                    fputs("usage: --tigerclaw-selection-keys path | show | set <contents> | reset\n", stderr)
                    return 64
                }
                return 0
            } catch {
                fputs("\(error)\n", stderr)
                return 64
            }
        }

        if let index = arguments.firstIndex(of: "--tigerclaw-schema") {
            let values = Array(arguments.dropFirst(index + 1))
            switch values {
            case ["show"]:
                let schema = NativeAotSchema.current
                print("schema=\(schema.identifier)")
                print("display-name=\(schema.displayName)")
            return 0
            case ["path"]:
                let schema = NativeAotSchema.current
                guard let directory = schema.editableDirectoryURL else {
                    fputs("内置方案不能直接编辑\n", stderr)
                    return 64
                }
                print("schema=\(schema.identifier)")
                print("path=\(directory.path)")
                return 0
            case let values where values.count == 2 && values[0] == "path":
                guard let schema = NativeAotSchema.available.first(where: { $0.identifier == values[1] }),
                      let directory = schema.editableDirectoryURL else {
                    fputs("方案不存在或不能直接编辑\n", stderr)
                    return 64
                }
                print("schema=\(schema.identifier)")
                print("path=\(directory.path)")
                return 0
            case ["redeploy"]:
                NativeAotSchema.requestRedeploy()
                print("redeploy=\(NativeAotSchema.redeployGeneration)")
                return 0
            case ["list"]:
                NativeAotSchema.available.forEach {
                    let kind = $0.isBundled
                        ? ($0.isEditable ? "bundled-editable" : "bundled")
                        : "imported"
                    print("\($0.identifier)\t\($0.displayName)\t\(kind)")
                }
                return 0
            case let values where values.count == 2 && values[0] == "import":
                do {
                    let schema = try NativeAotSchema.importLexicon(at: URL(fileURLWithPath: values[1]))
                    print("schema=\(schema.identifier)")
                    return 0
                } catch {
                    fputs("\(error)\n", stderr)
                    return 64
                }
            case let values where values.count == 2 && values[0] == "select":
                do {
                    let identifier = values[1]
                    try NativeAotSchema.select(identifier: identifier)
                    print("schema=\(NativeAotSchema.current.identifier)")
                    return 0
                } catch {
                    fputs("\(error)\n", stderr)
                    return 64
                }
            case let values where values.count == 3 && values[0] == "rename":
                do {
                    try NativeAotSchema.renameImported(identifier: values[1], displayName: values[2])
                    print("schema=\(values[1])")
                    return 0
                } catch {
                    fputs("\(error)\n", stderr)
                    return 64
                }
            case let values where values.count == 2 && values[0] == "delete":
                do {
                    try NativeAotSchema.deleteImported(identifier: values[1])
                    print("schema=\(NativeAotSchema.current.identifier)")
                    return 0
                } catch {
                    fputs("\(error)\n", stderr)
                    return 64
                }
            case let values where values.count == 2 && values[0] == "export":
                do {
                    print("archive=\(try NativeAotSchema.exportArchive(identifier: values[1]).path)")
                    return 0
                } catch {
                    fputs("\(error)\n", stderr)
                    return 64
                }
            case let values where values.count == 2 && values[0] == "cleanup-export":
                do {
                    try NativeAotSchema.removeExportArchive(at: values[1])
                    return 0
                } catch {
                    fputs("\(error)\n", stderr)
                    return 64
                }
            default:
                fputs("usage: --tigerclaw-schema show | list | path [identifier] | redeploy | import <path> | select <identifier> | rename <identifier> <name> | delete <identifier> | export <identifier> | cleanup-export <path>\n", stderr)
                return 64
            }
        }

        if let index = arguments.firstIndex(of: "--tigerclaw-user-dictionary") {
            let values = Array(arguments.dropFirst(index + 1))
            do {
                switch values {
                case ["list"]:
                    try NativeAotUserDictionary.entries().forEach { print("\($0.code)\t\($0.text)") }
                case let values where values.count == 3 && values[0] == "add":
                    print(try NativeAotUserDictionary.add(code: values[1], text: values[2]) ? "user-dictionary=added" : "user-dictionary=unchanged")
                case let values where values.count == 3 && values[0] == "remove":
                    print(try NativeAotUserDictionary.remove(code: values[1], text: values[2]) ? "user-dictionary=removed" : "user-dictionary=missing")
                default:
                    fputs("usage: --tigerclaw-user-dictionary list | add <code> <text> | remove <code> <text>\n", stderr)
                    return 64
                }
                return 0
            } catch {
                fputs("\(error)\n", stderr)
                return 64
            }
        }

        return nil
    }
}

struct NativeAotResult {
    let handled: Bool
    let isComposing: Bool
    let selectedIndex: Int
    let caret: Int
    let candidateTotal: Int
    let pageIndex: Int
    let pageCount: Int
    let sentenceRerankPending: Bool
    let action: NativeAotAction
    let preedit: String
    let compositionPrefix: String
    let activeInputCode: String
    let commit: String
    let candidates: [String]
}

enum NativeAotAction: Int32 {
    case none = 0
    case openAddWord = 1
    case toggleHideCandidates = 2
}

enum NativeAotBridgeError: Error, CustomStringConvertible {
    case missingLexicon
    case runtimeCreateFailed(Int32)
    case sessionCreateFailed(Int32)
    case sessionActivateFailed(Int32)
    case processFailed(Int32)
    case invalidCandidateIndex(Int)

    var description: String {
        switch self {
        case .missingLexicon:
            return "missing bundled tiger_sentence.codes.txt"
        case .runtimeCreateFailed(let status):
            return "tc_runtime_create failed status=\(status)"
        case .sessionCreateFailed(let status):
            return "tc_session_create failed status=\(status)"
        case .sessionActivateFailed(let status):
            return "tc_session_activate failed status=\(status)"
        case .processFailed(let status):
            return "tc_session_process failed status=\(status)"
        case .invalidCandidateIndex(let index):
            return "invalid candidate index \(index)"
        }
    }
}

final class NativeAotRuntimeCache: @unchecked Sendable {
    private struct Key: Hashable {
        let lexiconPath: String
        let lexiconRevision: String
        let configuration: NativeAotConfiguration
        let userDictionaryPath: String
        let userDataRevision: String
        let pinyinLexiconPath: String
        let pinyinLexiconRevision: String
        let sentenceInputActive: Bool
        let sentenceLexiconPath: String
        let sentenceLexiconRevision: String
        let sentenceModelPath: String
        let sentenceModelRevision: String
        let sentenceQwenNativeLibraryPath: String
        let sentenceQwenNativeLibraryRevision: String
        let sentenceQwenModelPath: String
        let sentenceQwenModelRevision: String
    }

    static let shared = NativeAotRuntimeCache()

    private let lock = NSLock()
    private var runtimes: [Key: tc_runtime_t] = [:]

    var count: Int {
        lock.lock()
        defer { lock.unlock() }
        return runtimes.count
    }

    func shutdown() {
        lock.lock()
        let values = Array(runtimes.values)
        runtimes.removeAll()
        lock.unlock()
        values.forEach(tc_runtime_release)
    }

    func acquire(
        lexiconURL: URL,
        lexiconRevision: String,
        configuration: NativeAotConfiguration,
        userDictionaryURL: URL
    ) throws -> tc_runtime_t {
        let pinyinLexiconURL = try NativeAotDataRoot.pinyinLexiconURL()
        let sentenceModelURL = NativeAotDataRoot.sentenceModelURL()
        let sentenceQwenNativeLibraryURL = NativeAotDataRoot.sentenceQwenNativeLibraryURL()
        let sentenceQwenModelURL = NativeAotDataRoot.sentenceQwenModelURL()
        // Keep sentence decoding on the same active schema as ordinary input.
        // The Windows Core builds its sentence index from the current code
        // table, including that table's user entries and 补充语料.txt.  Using
        // the bundled 虎整句 table here made macOS silently ignore imported
        // schema data whenever sentence mode was enabled.
        let activeSchema = NativeAotSchema.current
        let sentenceInputActive = configuration.sentenceInputActive(for: activeSchema)
        let sentenceLexiconURL = sentenceInputActive ? lexiconURL : nil
        let key = Key(
            lexiconPath: lexiconURL.standardizedFileURL.path,
            lexiconRevision: lexiconRevision,
            configuration: configuration,
            userDictionaryPath: userDictionaryURL.standardizedFileURL.path,
            userDataRevision: revision(for: userDictionaryURL),
            pinyinLexiconPath: pinyinLexiconURL?.standardizedFileURL.path ?? "",
            pinyinLexiconRevision: pinyinLexiconURL.map(revision(for:)) ?? "missing",
            sentenceInputActive: sentenceInputActive,
            sentenceLexiconPath: sentenceLexiconURL?.standardizedFileURL.path ?? "",
            sentenceLexiconRevision: sentenceInputActive
                ? lexiconRevision
                : "missing",
            sentenceModelPath: sentenceModelURL?.standardizedFileURL.path ?? "",
            sentenceModelRevision: sentenceModelURL.map(revision(for:)) ?? "missing",
            sentenceQwenNativeLibraryPath: sentenceQwenNativeLibraryURL?.standardizedFileURL.path ?? "",
            sentenceQwenNativeLibraryRevision: sentenceQwenNativeLibraryURL.map(revision(for:)) ?? "missing",
            sentenceQwenModelPath: sentenceQwenModelURL?.standardizedFileURL.path ?? "",
            sentenceQwenModelRevision: sentenceQwenModelURL.map(revision(for:)) ?? "missing")
        lock.lock()
        defer { lock.unlock() }
        if let runtime = runtimes[key] {
            return runtime
        }

        var createdRuntime: tc_runtime_t?
        var abiConfiguration = configuration.abiValue
        let status = withUtf8Slice(key.lexiconPath) { lexiconPath in
            withUtf8Slice(key.userDictionaryPath) { userDictionaryPath in
                withUtf8Slice(configuration.selectionKeys) { selectionKeys in
                    withUtf8Slice(configuration.previousPageKeys) { previousPageKeys in
                        withUtf8Slice(configuration.nextPageKeys) { nextPageKeys in
                            withUtf8Slice(key.pinyinLexiconPath) { pinyinLexiconPath in
                                withUtf8Slice(key.sentenceModelPath) { sentenceModelPath in
                                    withUtf8Slice(key.sentenceLexiconPath) { sentenceLexiconPath in
                                        withUtf8Slice(key.sentenceQwenNativeLibraryPath) { sentenceQwenNativeLibraryPath in
                                            withUtf8Slice(key.sentenceQwenModelPath) { sentenceQwenModelPath in
                                                withUtf8Slice(configuration.sentenceFullCodeWhitelist) { sentenceFullCodeWhitelist in
                                                    abiConfiguration.user_dictionary_path = userDictionaryPath
                                                    abiConfiguration.selection_keys = selectionKeys
                                                    abiConfiguration.previous_page_keys = previousPageKeys
                                                    abiConfiguration.next_page_keys = nextPageKeys
                                                    abiConfiguration.pinyin_lexicon_path = pinyinLexiconPath
                                                    abiConfiguration.sentence_input_enabled = key.sentenceInputActive ? 1 : 0
                                                    abiConfiguration.sentence_model_path = sentenceModelPath
                                                    abiConfiguration.sentence_qwen_native_library_path = sentenceQwenNativeLibraryPath
                                                    abiConfiguration.sentence_qwen_model_path = sentenceQwenModelPath
                                                    abiConfiguration.sentence_lexicon_path = sentenceLexiconPath
                                                    abiConfiguration.sentence_full_code_whitelist = sentenceFullCodeWhitelist
                                                    return tc_runtime_create_with_config(lexiconPath, &abiConfiguration, &createdRuntime)
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        guard status == TC_STATUS_OK.rawValue, let createdRuntime else {
            throw NativeAotBridgeError.runtimeCreateFailed(status)
        }
        runtimes[key] = createdRuntime
        return createdRuntime
    }

    private func revision(for userDictionaryURL: URL) -> String {
        let adjustmentsURL = userDictionaryURL
            .deletingLastPathComponent()
            .appendingPathComponent("candidate-adjustments.tsv", isDirectory: false)
        return [userDictionaryURL, adjustmentsURL].map { url in
            guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
                  let modificationDate = attributes[.modificationDate] as? Date,
                  let size = attributes[.size] as? NSNumber else {
                return "missing"
            }
            return "\(modificationDate.timeIntervalSinceReferenceDate):\(size.intValue)"
        }.joined(separator: ",")
    }
}

final class NativeAotBridge {
    private var runtime: tc_runtime_t?
    private var session: tc_session_t?

    convenience init(configuration: NativeAotConfiguration = .current) throws {
        try self.init(lexiconURL: NativeAotBridge.defaultLexiconURL(), configuration: configuration)
    }

    init(
        lexiconURL: URL,
        configuration: NativeAotConfiguration = .current,
        userDictionaryURL: URL? = nil,
        lexiconRevision: String? = nil
    ) throws {
        guard FileManager.default.fileExists(atPath: lexiconURL.path) else {
            throw NativeAotBridgeError.missingLexicon
        }

        let resolvedUserDictionaryURL: URL
        if let userDictionaryURL {
            resolvedUserDictionaryURL = userDictionaryURL
        } else {
            resolvedUserDictionaryURL = try NativeAotDataRoot.userDictionaryURL()
        }
        let cachedRuntime = try NativeAotRuntimeCache.shared.acquire(
            lexiconURL: lexiconURL,
            lexiconRevision: lexiconRevision ?? Self.fileRevision(for: lexiconURL),
            configuration: configuration,
            userDictionaryURL: resolvedUserDictionaryURL)
        runtime = cachedRuntime

        var createdSession: tc_session_t?
        let sessionStatus = tc_session_create(cachedRuntime, &createdSession)
        guard sessionStatus == TC_STATUS_OK.rawValue, createdSession != nil else {
            throw NativeAotBridgeError.sessionCreateFailed(sessionStatus)
        }
        session = createdSession
    }

    private static func fileRevision(for url: URL) -> String {
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
              let modificationDate = attributes[.modificationDate] as? Date,
              let size = attributes[.size] as? NSNumber else {
            return "missing"
        }
        return "\(modificationDate.timeIntervalSinceReferenceDate):\(size.intValue)"
    }

    deinit {
        deactivate()
        if let session {
            tc_session_release(session)
        }
    }

    func activate() throws {
        guard let session else {
            throw NativeAotBridgeError.sessionCreateFailed(Int32(TC_STATUS_INVALID_ARGUMENT.rawValue))
        }
        let status = tc_session_activate(session)
        guard status == TC_STATUS_OK.rawValue else {
            throw NativeAotBridgeError.sessionActivateFailed(status)
        }
    }

    func deactivate() {
        if let session {
            _ = tc_session_deactivate(session)
        }
    }

    func process(_ mappedEvent: NativeAotInputEvent) throws -> NativeAotResult {
        guard let session else {
            throw NativeAotBridgeError.sessionCreateFailed(Int32(TC_STATUS_INVALID_ARGUMENT.rawValue))
        }

        var snapshot: tc_snapshot_t?
        let status = mappedEvent.withCInputEvent { input in
            var mutableInput = input
            return tc_session_process(session, &mutableInput, &snapshot)
        }
        guard status == TC_STATUS_OK.rawValue, let snapshot else {
            throw NativeAotBridgeError.processFailed(status)
        }
        defer { tc_snapshot_release(snapshot) }

        return result(from: snapshot)
    }

    func selectCandidate(at pageIndex: Int) throws -> NativeAotResult {
        guard pageIndex >= 0, pageIndex <= Int(Int32.max) else {
            throw NativeAotBridgeError.invalidCandidateIndex(pageIndex)
        }
        guard let session else {
            throw NativeAotBridgeError.sessionCreateFailed(Int32(TC_STATUS_INVALID_ARGUMENT.rawValue))
        }

        var snapshot: tc_snapshot_t?
        let status = tc_session_select_candidate(session, Int32(pageIndex), &snapshot)
        guard status == TC_STATUS_OK.rawValue, let snapshot else {
            throw NativeAotBridgeError.processFailed(status)
        }
        defer { tc_snapshot_release(snapshot) }

        return result(from: snapshot)
    }

    func currentSnapshot() throws -> NativeAotResult {
        guard let session else {
            throw NativeAotBridgeError.sessionCreateFailed(Int32(TC_STATUS_INVALID_ARGUMENT.rawValue))
        }

        var snapshot: tc_snapshot_t?
        let status = tc_session_query_snapshot(session, &snapshot)
        guard status == TC_STATUS_OK.rawValue, let snapshot else {
            throw NativeAotBridgeError.processFailed(status)
        }
        defer { tc_snapshot_release(snapshot) }
        return result(from: snapshot)
    }

    private func result(from snapshot: tc_snapshot_t) -> NativeAotResult {
        let candidateCount = max(0, Int(tc_snapshot_get_candidate_count(snapshot)))
        let candidates = (0 ..< candidateCount).map {
            string(from: tc_snapshot_get_candidate(snapshot, Int32($0)))
        }

        return NativeAotResult(
            handled: tc_snapshot_get_handled(snapshot) != 0,
            isComposing: tc_snapshot_get_is_composing(snapshot) != 0,
            selectedIndex: Int(tc_snapshot_get_selected_index(snapshot)),
            caret: Int(tc_snapshot_get_caret(snapshot)),
            candidateTotal: Int(tc_snapshot_get_candidate_total(snapshot)),
            pageIndex: Int(tc_snapshot_get_page_index(snapshot)),
            pageCount: Int(tc_snapshot_get_page_count(snapshot)),
            sentenceRerankPending: tc_snapshot_get_sentence_rerank_pending(snapshot) != 0,
            action: NativeAotAction(rawValue: tc_snapshot_get_action(snapshot)) ?? .none,
            preedit: string(from: tc_snapshot_get_preedit(snapshot)),
            compositionPrefix: string(from: tc_snapshot_get_composition_prefix(snapshot)),
            activeInputCode: string(from: tc_snapshot_get_active_input_code(snapshot)),
            commit: string(from: tc_snapshot_get_commit(snapshot)),
            candidates: candidates
        )
    }

    static func defaultLexiconURL() -> URL {
        if let override = ProcessInfo.processInfo.environment["TIGERCLAW_NATIVEAOT_LEXICON_PATH"] {
            return URL(fileURLWithPath: override)
        }
        return NativeAotSchema.current.lexiconURL()
    }
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
