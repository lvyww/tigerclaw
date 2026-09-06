@preconcurrency import AppKit

enum CandidateAnchor {
    static func isUsable(_ rect: NSRect) -> Bool {
        rect.origin.x.isFinite &&
            rect.origin.y.isFinite &&
            rect.size.width.isFinite &&
            rect.size.height.isFinite &&
            rect.width >= 0 &&
            rect.height > 0
    }
}

private final class CandidateButton: NSButton {
    var onRightMouseDown: ((CandidateButton, NSEvent) -> Void)?

    override func rightMouseDown(with event: NSEvent) {
        onRightMouseDown?(self, event)
    }
}

struct CandidateRevealPolicy {
    static func isExpanded(delayMs: Int, elapsedMs: Int) -> Bool {
        delayMs <= 0 || elapsedMs >= delayMs
    }
}

final class CandidatePanel: NSObject, @unchecked Sendable {
    private struct Presentation {
        let candidates: [String]
        let selectedIndex: Int
        let pageIndex: Int
        let pageCount: Int
        let preedit: String
        let configuration: NativeAotConfiguration
        let anchorRect: NSRect?
        let onCandidateSelected: (Int) -> Void
        let onCandidateAdjusted: (Int, NativeAotCandidateAdjustment) -> Void
    }

    private let panel: NSPanel
    private let stack: NSStackView
    private var onCandidateSelected: ((Int) -> Void)?
    private var onCandidateAdjusted: ((Int, NativeAotCandidateAdjustment) -> Void)?
    private var presentation: Presentation?
    private var revealSessionStart: Date?
    private var revealTimer: Timer?

    override init() {
        stack = NSStackView()
        panel = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: 240, height: 1),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false)
        panel.level = .popUpMenu
        panel.isOpaque = false
        panel.hasShadow = true
        panel.backgroundColor = .windowBackgroundColor
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.hidesOnDeactivate = false

        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 2
        stack.edgeInsets = NSEdgeInsets(top: 6, left: 8, bottom: 6, right: 8)
        panel.contentView = stack
    }

    func present(
        candidates: [String],
        selectedIndex: Int,
        pageIndex: Int,
        pageCount: Int,
        preedit: String,
        configuration: NativeAotConfiguration,
        anchorRect: NSRect?,
        onCandidateSelected: @escaping (Int) -> Void,
        onCandidateAdjusted: @escaping (Int, NativeAotCandidateAdjustment) -> Void
    ) {
        if revealSessionStart == nil {
            revealSessionStart = Date()
        }
        presentation = Presentation(
            candidates: candidates,
            selectedIndex: selectedIndex,
            pageIndex: pageIndex,
            pageCount: pageCount,
            preedit: preedit,
            configuration: configuration,
            anchorRect: anchorRect,
            onCandidateSelected: onCandidateSelected,
            onCandidateAdjusted: onCandidateAdjusted)
        renderPresentation()
    }

    private func renderPresentation() {
        guard let presentation else {
            hide()
            return
        }
        let elapsedMs = max(0, Int(Date().timeIntervalSince(revealSessionStart ?? Date()) * 1_000))
        let candidatesExpanded = CandidateRevealPolicy.isExpanded(
            delayMs: presentation.configuration.candidateExpandDelayMs,
            elapsedMs: elapsedMs)
        let annotationsExpanded = CandidateRevealPolicy.isExpanded(
            delayMs: presentation.configuration.annotationExpandDelayMs,
            elapsedMs: elapsedMs)
        let visibleCandidates = candidatesExpanded ? presentation.candidates : []
        let showCodeOnly = presentation.configuration.showInputCode && !presentation.preedit.isEmpty
        guard !visibleCandidates.isEmpty || showCodeOnly else {
            panel.orderOut(nil)
            scheduleRevealTimer(elapsedMs: elapsedMs, presentation: presentation)
            return
        }
        self.onCandidateSelected = presentation.onCandidateSelected
        self.onCandidateAdjusted = presentation.onCandidateAdjusted
        let palette = candidatePalette(presentation.configuration)
        panel.appearance = palette.appearance
        panel.backgroundColor = .clear
        stack.wantsLayer = true
        stack.layer?.backgroundColor = palette.background.cgColor
        stack.layer?.borderColor = palette.border.cgColor
        stack.layer?.borderWidth = palette.borderWidth
        stack.layer?.cornerRadius = palette.cornerRadius

        stack.arrangedSubviews.forEach {
            stack.removeArrangedSubview($0)
            $0.removeFromSuperview()
        }
        stack.orientation = presentation.configuration.verticalCandidates ? .vertical : .horizontal
        stack.alignment = presentation.configuration.verticalCandidates ? .leading : .centerY
        stack.spacing = presentation.configuration.verticalCandidates ? 2 : 5

        if showCodeOnly {
            let code = NSTextField(labelWithString: presentation.preedit)
            code.font = .systemFont(ofSize: 12)
            code.textColor = palette.secondaryForeground
            stack.addArrangedSubview(code)
        }
        for (index, candidate) in visibleCandidates.enumerated() {
            let title = presentation.configuration.showCandidateIndex ? "\(index + 1). \(candidate)" : candidate
            let annotation = annotationsExpanded
                ? NativeAotCandidateAnnotations.annotation(for: candidate, isPinyin: presentation.preedit.hasPrefix("·"), configuration: presentation.configuration)
                : ""
            let button = CandidateButton(title: title, target: self, action: #selector(candidateClicked(_:)))
            button.tag = index
            button.onRightMouseDown = { [weak self] button, event in
                self?.showAdjustmentMenu(for: button, event: event)
            }
            button.bezelStyle = .regularSquare
            button.isBordered = false
            button.alignment = .left
            button.font = candidateFont(presentation.configuration)
            button.contentTintColor = palette.foreground
            if !annotation.isEmpty {
                let attributed = NSMutableAttributedString(
                    string: title,
                    attributes: [.font: candidateFont(presentation.configuration), .foregroundColor: palette.foreground])
                attributed.append(NSAttributedString(
                    string: "  \(annotation)",
                    attributes: [.font: NSFont.systemFont(ofSize: 12), .foregroundColor: palette.secondaryForeground]))
                button.attributedTitle = attributed
                button.toolTip = annotation
            }
            button.wantsLayer = true
            let backgroundColor = index == presentation.selectedIndex ? palette.selectionBackground : NSColor.clear
            button.layer?.backgroundColor = backgroundColor.cgColor
            button.layer?.cornerRadius = 3
            button.translatesAutoresizingMaskIntoConstraints = false
            stack.addArrangedSubview(button)
            if presentation.configuration.verticalCandidates {
                button.widthAnchor.constraint(greaterThanOrEqualToConstant: 220).isActive = true
            }
        }

        if !visibleCandidates.isEmpty, presentation.pageCount > 1 {
            let page = NSTextField(labelWithString: "\(presentation.pageIndex + 1) / \(presentation.pageCount)")
            page.font = .systemFont(ofSize: 11)
            page.textColor = palette.secondaryForeground
            stack.addArrangedSubview(page)
        }

        let animateAppearance = presentation.configuration.candidateWindowAnimation && !panel.isVisible
        panel.contentView?.layoutSubtreeIfNeeded()
        let size = panel.contentView?.fittingSize ?? NSSize(width: 240, height: 1)
        panel.setContentSize(NSSize(width: max(240, size.width), height: max(1, size.height)))
        panel.setFrameOrigin(frameOrigin(anchorRect: presentation.anchorRect))
        panel.alphaValue = animateAppearance ? 0 : 1
        panel.orderFrontRegardless()
        if animateAppearance {
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.12
                panel.animator().alphaValue = 1
            }
        }
        scheduleRevealTimer(elapsedMs: elapsedMs, presentation: presentation)
    }

    func hide() {
        presentation = nil
        revealSessionStart = nil
        revealTimer?.invalidate()
        revealTimer = nil
        onCandidateSelected = nil
        onCandidateAdjusted = nil
        panel.alphaValue = 1
        panel.orderOut(nil)
    }

    private func scheduleRevealTimer(elapsedMs: Int, presentation: Presentation) {
        revealTimer?.invalidate()
        let remaining = [
            presentation.configuration.candidateExpandDelayMs,
            presentation.configuration.annotationExpandDelayMs,
        ].filter { $0 > elapsedMs }
        guard let nextDelay = remaining.min() else {
            revealTimer = nil
            return
        }
        revealTimer = Timer.scheduledTimer(
            timeInterval: TimeInterval(max(1, nextDelay - elapsedMs)) / 1_000,
            target: self,
            selector: #selector(revealTimerFired(_:)),
            userInfo: nil,
            repeats: false)
    }

    @objc private func revealTimerFired(_ timer: Timer) {
        guard timer === revealTimer else { return }
        revealTimer = nil
        renderPresentation()
    }

    @objc private func candidateClicked(_ sender: NSButton) {
        onCandidateSelected?(sender.tag)
    }

    private func showAdjustmentMenu(for button: CandidateButton, event: NSEvent) {
        let menu = NSMenu()
        menu.addItem(menuItem(title: "前移", action: #selector(advanceCandidate(_:)), index: button.tag))
        menu.addItem(menuItem(title: "置顶", action: #selector(topCandidate(_:)), index: button.tag))
        menu.addItem(.separator())
        menu.addItem(menuItem(title: "删除候选", action: #selector(deleteCandidate(_:)), index: button.tag))
        let point = button.convert(event.locationInWindow, from: nil)
        menu.popUp(positioning: nil, at: point, in: button)
    }

    private func menuItem(title: String, action: Selector, index: Int) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
        item.target = self
        item.representedObject = index
        return item
    }

    @objc private func advanceCandidate(_ sender: NSMenuItem) {
        adjust(sender, operation: .advance)
    }

    @objc private func topCandidate(_ sender: NSMenuItem) {
        adjust(sender, operation: .top)
    }

    @objc private func deleteCandidate(_ sender: NSMenuItem) {
        adjust(sender, operation: .delete)
    }

    private func adjust(_ sender: NSMenuItem, operation: NativeAotCandidateAdjustment) {
        guard let index = sender.representedObject as? Int else { return }
        onCandidateAdjusted?(index, operation)
    }

    private func frameOrigin(anchorRect: NSRect?) -> NSPoint {
        let anchor: NSRect
        if let anchorRect, !anchorRect.isEmpty {
            anchor = anchorRect
        } else {
            anchor = NSRect(origin: NSEvent.mouseLocation, size: .zero)
        }
        let screen = NSScreen.screens.first(where: { $0.visibleFrame.intersects(anchor) }) ?? NSScreen.main
        guard let screen else {
            return NSPoint(x: anchor.minX + 10, y: anchor.minY - panel.frame.height - 10)
        }

        let visibleFrame = screen.visibleFrame
        let spacing: CGFloat = 6
        let desiredY = anchor.minY - panel.frame.height - spacing
        let y = desiredY >= visibleFrame.minY
            ? desiredY
            : min(anchor.maxY + spacing, visibleFrame.maxY - panel.frame.height)
        let x = min(max(anchor.minX, visibleFrame.minX), visibleFrame.maxX - panel.frame.width)
        return NSPoint(x: x, y: y)
    }

    private func candidateFont(_ configuration: NativeAotConfiguration) -> NSFont {
        let size = CGFloat(configuration.candidateFontSize)
        guard !configuration.candidateFontName.isEmpty,
              let font = NSFont(name: configuration.candidateFontName, size: size) else {
            return .systemFont(ofSize: size)
        }
        return font
    }

    private func candidatePalette(_ configuration: NativeAotConfiguration) -> CandidateThemePalette {
        switch configuration.candidateTheme {
        case "light":
            return .system(appearance: NSAppearance(named: .aqua))
        case "dark":
            return .system(appearance: NSAppearance(named: .darkAqua))
        case "默认": return .windows(foreground: "000000", background: "FFF8F3", border: "1A7B6B", selection: "48000000")
        case "通透": return .windows(foreground: "2277EE", background: "00000000", border: "00000000", selection: "482277EE")
        case "一般通透": return .windows(foreground: "2277EE", background: "1A000000", border: "00000000", selection: "482277EE")
        case "迷雾": return .windows(foreground: "D9D9D9", background: "2F2F2F", border: "5A5A5A", selection: "48D9D9D9")
        case "星夜": return .windows(foreground: "FFDC6A", background: "232B39", border: "3A6B9B", selection: "48FFDC6A")
        case "纸": return .windows(foreground: "111111", background: "F5F2E8", border: "A8A09D", selection: "48111111")
        case "粉": return .windows(foreground: "000000", background: "FDF9F5", border: "DEACAC", selection: "48000000")
        case "赛博朋克": return .windows(foreground: "71E4FD", background: "88001122", border: "F651FC", selection: "4871E4FD", borderWidth: 1.5, cornerRadius: 10)
        case "清晨": return .windows(foreground: "303030", background: "FDFDFF", border: "56A1DD", selection: "48303030")
        default:
            return .system(appearance: nil)
        }
    }
}

private struct CandidateThemePalette {
    let foreground: NSColor
    let secondaryForeground: NSColor
    let background: NSColor
    let border: NSColor
    let selectionBackground: NSColor
    let borderWidth: CGFloat
    let cornerRadius: CGFloat
    let appearance: NSAppearance?

    static func system(appearance: NSAppearance?) -> CandidateThemePalette {
        CandidateThemePalette(
            foreground: .labelColor,
            secondaryForeground: .secondaryLabelColor,
            background: .windowBackgroundColor,
            border: .separatorColor,
            selectionBackground: .selectedContentBackgroundColor,
            borderWidth: 1.25,
            cornerRadius: 5,
            appearance: appearance)
    }

    static func windows(
        foreground: String,
        background: String,
        border: String,
        selection: String,
        borderWidth: CGFloat = 1.25,
        cornerRadius: CGFloat = 5
    ) -> CandidateThemePalette {
        let foregroundColor = NSColor(tigerClawHex: foreground)
        return CandidateThemePalette(
            foreground: foregroundColor,
            secondaryForeground: foregroundColor.withAlphaComponent(0.72),
            background: NSColor(tigerClawHex: background),
            border: NSColor(tigerClawHex: border),
            selectionBackground: NSColor(tigerClawHex: selection),
            borderWidth: borderWidth,
            cornerRadius: cornerRadius,
            appearance: nil)
    }
}

private extension NSColor {
    convenience init(tigerClawHex hex: String) {
        let value = hex.trimmingCharacters(in: .whitespacesAndNewlines)
        let normalized = value.count == 6 ? "FF\(value)" : value
        let number = UInt32(normalized, radix: 16) ?? 0xFF000000
        self.init(
            calibratedRed: CGFloat((number >> 16) & 0xFF) / 255,
            green: CGFloat((number >> 8) & 0xFF) / 255,
            blue: CGFloat(number & 0xFF) / 255,
            alpha: CGFloat((number >> 24) & 0xFF) / 255)
    }
}

/// Reads the Windows-compatible `*.注释` and `*.拆分` sidecar files kept next
/// to an imported code table. The short probe interval makes direct Finder
/// edits visible without putting file I/O on every keystroke.
enum NativeAotCandidateAnnotations {
    private struct Cache {
        let directoryPath: String
        let comments: [String: String]
        let splits: [String: String]
        let fullCodes: [String: String]
        let includesFullCodes: Bool
        let nextProbe: Date
    }

    private static let lock = NSLock()
    nonisolated(unsafe) private static var cache: Cache?

    static func annotation(for candidate: String, isPinyin: Bool = false, configuration: NativeAotConfiguration) -> String {
        guard isPinyin || configuration.showCandidateComment || configuration.showCandidateSplit else {
            return ""
        }

        let maps = maps(for: NativeAotSchema.current, includingFullCodes: isPinyin)
        if isPinyin {
            let splitValues = candidate.map { maps.splits[String($0)] }
            let codeValues = candidate.map { maps.fullCodes[String($0)] }
            let split = splitValues.allSatisfy { $0 != nil } ? splitValues.compactMap { $0 }.joined(separator: "·") : ""
            let code = codeValues.allSatisfy { $0 != nil } ? codeValues.compactMap { $0 }.joined(separator: "·") : ""
            return [split, code].filter { !$0.isEmpty }.joined(separator: " | ")
        }
        var parts: [String] = []
        if configuration.showCandidateSplit {
            let splitValues = candidate.map { maps.splits[String($0)] }
            if splitValues.allSatisfy({ $0 != nil }) {
                parts.append(splitValues.compactMap { $0 }.joined(separator: "·"))
            }
        }
        if configuration.showCandidateComment,
           let comment = maps.comments[candidate],
           !comment.isEmpty {
            parts.append(comment)
        }
        return parts.joined(separator: " ")
    }

    private static func maps(for schema: NativeAotSchema, includingFullCodes: Bool) -> (comments: [String: String], splits: [String: String], fullCodes: [String: String]) {
        lock.lock()
        defer { lock.unlock() }
        let directory = schema.editableDirectoryURL
        let cachePath = schema.lexiconURL().standardizedFileURL.path
        let now = Date()
        if let cache,
           cache.directoryPath == cachePath,
           now < cache.nextProbe,
           (!includingFullCodes || cache.includesFullCodes) {
            return (cache.comments, cache.splits, cache.fullCodes)
        }

        var comments: [String: String] = [:]
        var splits: [String: String] = [:]
        let files = directory.flatMap { try? FileManager.default.contentsOfDirectory(
            at: $0,
            includingPropertiesForKeys: nil,
            options: [.skipsHiddenFiles]) } ?? []
        for file in files where file.pathExtension == "注释" || file.pathExtension == "拆分" {
            guard let contents = try? String(contentsOf: file, encoding: .utf8) else { continue }
            for rawLine in contents.split(whereSeparator: \.isNewline) {
                let line = String(rawLine).trimmingCharacters(in: .whitespacesAndNewlines)
                guard !line.isEmpty, !line.hasPrefix("#") else { continue }
                let fields = line.split(
                    maxSplits: 1,
                    omittingEmptySubsequences: true,
                    whereSeparator: { $0 == "\t" || $0 == " " })
                guard fields.count == 2 else { continue }
                let key = decode(String(fields[0]))
                let value = decode(String(fields[1]))
                guard !key.isEmpty, !value.isEmpty else { continue }
                if file.pathExtension == "注释" {
                    comments[key] = comments[key].map { "\($0) \(value)" } ?? value
                } else {
                    splits[key] = value
                }
            }
        }
        var fullCodes: [String: String] = [:]
        if includingFullCodes {
            let lexiconDirectory = schema.lexiconURL().deletingLastPathComponent()
            let tableFiles = ((try? FileManager.default.contentsOfDirectory(
                at: lexiconDirectory,
                includingPropertiesForKeys: nil,
                options: [.skipsHiddenFiles])) ?? [])
                .filter { $0.lastPathComponent.hasSuffix(".dict.yaml") || $0.pathExtension.lowercased() == "txt" }
            for tableFile in tableFiles {
                guard let contents = try? String(contentsOf: tableFile, encoding: .utf8) else { continue }
                var body = !tableFile.lastPathComponent.hasSuffix(".dict.yaml")
                for rawLine in contents.split(whereSeparator: \.isNewline) {
                    let line = String(rawLine).trimmingCharacters(in: .whitespacesAndNewlines)
                    if !body { body = line == "..."; continue }
                    guard !line.isEmpty, !line.hasPrefix("#") else { continue }
                    let fields = line.split(whereSeparator: { $0 == "\t" || $0 == " " })
                    guard fields.count >= 2 else { continue }
                    let text = String(fields[0]); let code = String(fields[1])
                    guard !code.allSatisfy({ $0.isNumber }) else { continue }
                    for character in text where fullCodes[String(character)]?.count ?? 0 < code.count {
                        fullCodes[String(character)] = code
                    }
                }
            }
        }
        cache = Cache(
            directoryPath: cachePath,
            comments: comments,
            splits: splits,
            fullCodes: fullCodes,
            includesFullCodes: includingFullCodes,
            nextProbe: now.addingTimeInterval(0.75))
        return (comments, splits, fullCodes)
    }

    private static func decode(_ value: String) -> String {
        value
            .replacingOccurrences(of: "\\t", with: "\t")
            .replacingOccurrences(of: "\\n", with: "\n")
            .replacingOccurrences(of: "\\s", with: " ")
    }
}
