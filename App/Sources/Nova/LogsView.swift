import AppKit
import Combine
import NovaCore
import SwiftUI

struct LogsView: View {
    @Environment(\.novaAnimate) private var windowVisible
    @Environment(\.novaPageActive) private var pageActive
    @ObservedObject private var log = LogTail.shared
    @State private var filter = ""
    @State private var follow = true
    private let timer = Timer.publish(every: 1.5, on: .main, in: .common).autoconnect()

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            PageHeader(title: "Логи", subtitle: SystemPaths.logFile)
            HStack(spacing: 10) {
                TextField("Фильтр", text: $filter).novaTextField().frame(maxWidth: 280)
                Toggle("Следить за концом", isOn: $follow)
                    .toggleStyle(.checkbox)
                    .font(Theme.body(13))
                    .fixedSize()
                Spacer()
                Button {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(log.visible(filter).joined(separator: "\n"), forType: .string)
                } label: { Label("Копировать", systemImage: "doc.on.doc") }
                    .buttonStyle(NovaButtonStyle())
                    .fixedSize()
                Button {
                    NSWorkspace.shared.open(URL(fileURLWithPath: SystemPaths.logFile))
                } label: { Label("Открыть", systemImage: "arrow.up.forward.app") }
                    .buttonStyle(NovaButtonStyle())
                    .fixedSize()
            }
            LogTextView(lines: log.visible(filter), version: log.version &+ filter.hashValue, follow: follow)
                .background(RoundedRectangle(cornerRadius: 16).fill(Color.black.opacity(0.35)))
                .overlay(RoundedRectangle(cornerRadius: 16).stroke(Theme.line))
                .clipShape(RoundedRectangle(cornerRadius: 16))
            if log.lines.isEmpty {
                Text("Лог пуст — ядро ещё не запускалось.").font(Theme.body(13)).foregroundStyle(Theme.muted)
            }
        }
        .padding(.horizontal, 36)
        .padding(.vertical, 28)
        .onAppear { log.poll() }
        .onChange(of: pageActive) { _, active in if active { log.poll() } }
        .onReceive(timer) { _ in if windowVisible && pageActive { log.poll() } }
    }
}

/// Reads only the bytes appended since the last poll; keeps the last 1500 lines.
@MainActor
final class LogTail: ObservableObject {
    @Published private(set) var lines: [String] = []
    @Published private(set) var version = 0
    private let path: String
    private var offset: UInt64 = 0
    private var partial = ""
    private static let keep = 1500
    private static let ansi = try! NSRegularExpression(pattern: "\u{1B}\\[[0-9;]*m")

    static let shared = LogTail(path: SystemPaths.logFile)

    init(path: String) { self.path = path }

    func visible(_ filter: String) -> [String] {
        filter.isEmpty ? lines : lines.filter { $0.localizedCaseInsensitiveContains(filter) }
    }

    func poll() {
        guard let h = FileHandle(forReadingAtPath: path) else { return }
        defer { try? h.close() }
        let size = (try? h.seekToEnd()) ?? 0
        if size < offset {  // rotated or truncated
            offset = 0
            partial = ""
            lines = []
        }
        guard size > offset else { return }
        // First read: only the tail.
        if offset == 0, size > 300_000 { offset = size - 300_000 }
        try? h.seek(toOffset: offset)
        let data = (try? h.readToEnd()) ?? Data()
        offset = size
        var text = partial + String(decoding: data, as: UTF8.self)
        partial = ""
        if !text.hasSuffix("\n"), let nl = text.lastIndex(of: "\n") {
            partial = String(text[text.index(after: nl)...])
            text = String(text[..<nl])
        }
        let range = NSRange(text.startIndex..., in: text)
        let clean = Self.ansi.stringByReplacingMatches(in: text, range: range, withTemplate: "")
        let new = clean.split(separator: "\n", omittingEmptySubsequences: true).map(String.init)
        guard !new.isEmpty else { return }
        lines = Array((lines + new).suffix(Self.keep))
        version &+= 1
    }
}

/// Native text view: one attributed string instead of hundreds of SwiftUI rows.
struct LogTextView: NSViewRepresentable {
    let lines: [String]
    let version: Int
    let follow: Bool

    final class Coordinator {
        var shown = Int.min
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSTextView.scrollableTextView()
        scroll.drawsBackground = false
        scroll.hasVerticalScroller = true
        let tv = scroll.documentView as! NSTextView
        tv.isEditable = false
        tv.isSelectable = true
        tv.drawsBackground = false
        tv.textContainerInset = NSSize(width: 12, height: 12)
        tv.isAutomaticLinkDetectionEnabled = false
        tv.layoutManager?.allowsNonContiguousLayout = true
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard context.coordinator.shown != version, let tv = scroll.documentView as? NSTextView else { return }
        context.coordinator.shown = version
        let font = NSFont.monospacedSystemFont(ofSize: 11.5, weight: .medium)
        let normal = NSColor(Theme.soft), warn = NSColor(Theme.amber), err = NSColor(Theme.danger)
        let out = NSMutableAttributedString()
        for line in lines {
            let color = line.contains("ERROR") || line.contains("FATAL") ? err : line.contains("WARN") ? warn : normal
            out.append(NSAttributedString(string: line + "\n", attributes: [.font: font, .foregroundColor: color]))
        }
        tv.textStorage?.setAttributedString(out)
        if follow { tv.scrollToEndOfDocument(nil) }
    }
}
