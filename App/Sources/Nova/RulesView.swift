import AppKit
import NovaCore
import SwiftUI
import UniformTypeIdentifiers

struct RulesView: View {
    @EnvironmentObject var vpn: VPNController
    @State private var addingDomain = false
    @State private var addingApp = false

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            PageHeader(title: "Правила", subtitle: "Нажми на метку сервера, чтобы выбрать маршрут") {
                HStack(spacing: 10) {
                    Button { addingDomain = true } label: { Label("Домен", systemImage: "plus") }
                        .buttonStyle(NovaButtonStyle())
                    Button { addingApp = true } label: { Label("Приложение", systemImage: "plus") }
                        .buttonStyle(NovaButtonStyle(prominent: true))
                }
            }
            PriorityPipeline()
            HStack(alignment: .top, spacing: 24) {
                RuleColumn(title: "ДОМЕНЫ", icon: "globe", count: vpn.profile.domainRules.count) {
                    if vpn.profile.domainRules.isEmpty {
                        EmptyHint(text: "Добавь домен, зону вроде .ru или готовый набор — YouTube, Китай…")
                    }
                    ForEach(vpn.profile.domainRules) { r in
                        DomainRuleRow(rule: r)
                            .transition(.opacity.combined(with: .scale(scale: 0.96)))
                    }
                }
                RuleColumn(title: "ПРИЛОЖЕНИЯ", icon: "square.grid.2x2", count: vpn.profile.appRules.count) {
                    if vpn.profile.appRules.isEmpty {
                        EmptyHint(text: "Добавь приложение — весь его трафик пойдёт через выбранный сервер.")
                    }
                    ForEach(vpn.profile.appRules) { r in
                        AppRuleRow(rule: r)
                            .transition(.opacity.combined(with: .scale(scale: 0.96)))
                    }
                }
            }
            // Only inserts and removals animate; retargeting a rule just recolors it.
            .animation(.spring(response: 0.35, dampingFraction: 0.85), value: vpn.profile.domainRules.map(\.id))
            .animation(.spring(response: 0.35, dampingFraction: 0.85), value: vpn.profile.appRules.map(\.id))
            restRow
        }
        .padding(.horizontal, 36)
        .padding(.vertical, 28)
        .sheet(isPresented: $addingDomain) { AddDomainSheet() }
        .sheet(isPresented: $addingApp) { AppPickerSheet() }
    }

    private var restRow: some View {
        let target = vpn.profile.settings.defaultTarget
        return HStack(spacing: 14) {
            StepNumber(n: 4, filled: false)
            VStack(alignment: .leading, spacing: 2) {
                Text("Всё остальное").font(Theme.body(15, .bold))
                Text("трафик, который не попал ни в одно правило").font(Theme.body(12)).foregroundStyle(Theme.soft)
            }
            Spacer()
            TargetMenu(selected: target) { vpn.setDefaultTarget($0) }
        }
        .padding(.horizontal, 18)
        .frame(height: 64)
        .background(
            RoundedRectangle(cornerRadius: 16)
                .fill(LinearGradient(colors: [Theme.color(for: target, in: vpn.profile).opacity(0.22), Theme.color(for: target, in: vpn.profile).opacity(0.04)],
                                     startPoint: .leading, endPoint: .trailing))
        )
        .overlay(RoundedRectangle(cornerRadius: 16).stroke(Theme.color(for: target, in: vpn.profile).opacity(0.3)))
        .animation(.easeOut(duration: 0.3), value: target)
    }
}

struct PriorityPipeline: View {
    var body: some View {
        HStack(spacing: 6) {
            step(1, "Локальная сеть", "всегда напрямую", highlight: false)
            chevron
            step(2, "Домены", "точнее — главнее", highlight: true)
            chevron
            step(3, "Приложения", "весь её трафик", highlight: true)
            chevron
            step(4, "Всё остальное", "по умолчанию", highlight: false)
        }
        .overlay(SweepingLight(axis: .horizontal, color: Theme.violet.opacity(0.35), thickness: 120, period: 3.2).allowsHitTesting(false))
        .clipShape(RoundedRectangle(cornerRadius: 16))
        .frame(height: 62)
    }

    private var chevron: some View {
        Image(systemName: "chevron.right").font(.system(size: 11, weight: .bold)).foregroundStyle(Color(hex: "#4A4F70"))
    }

    private func step(_ n: Int, _ title: String, _ sub: String, highlight: Bool) -> some View {
        HStack(spacing: 10) {
            StepNumber(n: n, filled: highlight)
            VStack(alignment: .leading, spacing: 1) {
                Text(title).font(Theme.body(13, .bold)).lineLimit(1).minimumScaleFactor(0.75)
                Text(sub).font(Theme.body(11)).foregroundStyle(Theme.muted).lineLimit(1).minimumScaleFactor(0.75)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 10)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(RoundedRectangle(cornerRadius: 14).fill(Theme.panel))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(highlight ? Theme.violet.opacity(0.35) : Color.white.opacity(0.08)))
    }
}

struct StepNumber: View {
    let n: Int
    let filled: Bool

    var body: some View {
        Text("\(n)").font(Theme.display(12, .bold))
            .frame(width: 28, height: 28)
            .background(Circle().fill(filled ? Theme.violetDeep : Color.white.opacity(0.1)))
    }
}

struct RuleColumn<Content: View>: View {
    let title: String
    let icon: String
    let count: Int
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Image(systemName: icon)
                Text("\(title) · \(count)")
            }
            .font(Theme.body(12, .bold))
            .foregroundStyle(Theme.muted)
            ScrollView {
                VStack(spacing: 10) { content }
                    .padding(.bottom, 4)
            }
            .scrollIndicators(.never)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
    }
}

struct EmptyHint: View {
    let text: String

    var body: some View {
        Text(text)
            .font(Theme.body(13))
            .foregroundStyle(Theme.muted)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(16)
            .background(RoundedRectangle(cornerRadius: 14).strokeBorder(style: StrokeStyle(lineWidth: 1, dash: [6, 6])).foregroundStyle(Theme.violet.opacity(0.4)))
    }
}

/// Server chip with a dropdown of all targets.
struct TargetMenu: View {
    @EnvironmentObject var vpn: VPNController
    let selected: TargetID
    let onSelect: (TargetID) -> Void

    var body: some View {
        Menu {
            ForEach(vpn.profile.servers) { s in
                Button("\(s.code) — \(s.name)") { onSelect(s.id) }
            }
            Button("DIRECT — напрямую") { onSelect(Target.direct) }
        } label: {
            TargetChip(label: Theme.label(for: selected, in: vpn.profile), color: Theme.color(for: selected, in: vpn.profile), showChevron: true)
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .fixedSize()
    }
}

struct RuleRowShell<Leading: View>: View {
    let title: String
    let subtitle: String
    let target: TargetID
    let enabled: Bool
    @ViewBuilder var leading: Leading
    let onTarget: (TargetID) -> Void
    let onToggle: () -> Void
    let onDelete: () -> Void
    @State private var hover = false

    var body: some View {
        HStack(spacing: 12) {
            leading.frame(width: 34, height: 34)
            VStack(alignment: .leading, spacing: 1) {
                Text(title).font(Theme.body(14, .bold)).lineLimit(1).minimumScaleFactor(0.85)
                Text(subtitle).font(Theme.body(12)).foregroundStyle(Theme.muted).lineLimit(1).truncationMode(.middle)
            }
            Spacer(minLength: 6)
            TargetMenu(selected: target, onSelect: onTarget)
            Button(action: onToggle) {
                Image(systemName: enabled ? "pause.circle" : "play.circle").font(.system(size: 16))
            }
            .buttonStyle(.plain)
            .foregroundStyle(Theme.muted)
            .help(enabled ? "Выключить правило" : "Включить правило")
            .accessibilityLabel(Text(enabled ? "Выключить правило" : "Включить правило"))
            Button(action: onDelete) {
                Image(systemName: "xmark").font(.system(size: 13, weight: .bold))
                    .frame(width: 30, height: 30)
                    .background(RoundedRectangle(cornerRadius: 8).fill(hover ? Theme.danger.opacity(0.15) : .clear))
            }
            .buttonStyle(.plain)
            .foregroundStyle(hover ? Theme.danger : Color(hex: "#6B7092"))
            .onHover { hover = $0 }
            .accessibilityLabel(Text("Удалить правило"))
        }
        .padding(.leading, 12).padding(.trailing, 8)
        .frame(height: 56)
        .background(RoundedRectangle(cornerRadius: 14).fill(Theme.panel))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(Color.white.opacity(0.07)))
        .opacity(enabled ? 1 : 0.45)
    }
}

struct DomainRuleRow: View {
    @EnvironmentObject var vpn: VPNController
    let rule: DomainRule

    var body: some View {
        RuleRowShell(title: title, subtitle: subtitle, target: rule.target, enabled: rule.enabled) {
            Image(systemName: icon)
                .font(.system(size: 14, weight: .semibold))
                .frame(width: 34, height: 34)
                .background(RoundedRectangle(cornerRadius: 10).fill(Theme.color(for: rule.target, in: vpn.profile).opacity(0.18)))
                .foregroundStyle(Theme.soft)
        } onTarget: { t in
            mutate { $0.target = t }
        } onToggle: {
            mutate { $0.enabled.toggle() }
        } onDelete: {
            vpn.update { $0.domainRules.removeAll { $0.id == rule.id } }
        }
    }

    private func mutate(_ f: (inout DomainRule) -> Void) {
        vpn.update { p in
            if let i = p.domainRules.firstIndex(where: { $0.id == rule.id }) { f(&p.domainRules[i]) }
        }
    }

    private var title: String {
        switch rule.kind {
        case .domain:
            let d = ConfigBuilder.normalizeDomain(rule.value)
            return d.contains(".") ? d : ".\(d)"
        case .ruleset: return RuleSetPreset.title(for: rule.value)
        default: return rule.value
        }
    }

    private var subtitle: String {
        switch rule.kind {
        case .domain: return ConfigBuilder.normalizeDomain(rule.value).contains(".") ? "домен и поддомены" : "вся зона"
        case .keyword: return "ключевое слово"
        case .ip: return "IP / подсеть"
        case .ruleset: return "набор · \(rule.value)"
        }
    }

    private var icon: String {
        switch rule.kind {
        case .domain: return "globe"
        case .keyword: return "textformat"
        case .ip: return "number"
        case .ruleset: return "square.stack.3d.up"
        }
    }
}

struct AppRuleRow: View {
    @EnvironmentObject var vpn: VPNController
    let rule: AppRule

    var body: some View {
        let exists = FileManager.default.fileExists(atPath: rule.path)
        let subtitle = !exists ? "не найдено: \(rule.path)"
            : AppInfo.usesSharedWebKit(rule.path) ? "ходит в сеть через системный WebKit — лучше правила по доменам" : rule.path
        RuleRowShell(title: rule.name, subtitle: subtitle, target: rule.target, enabled: rule.enabled) {
            Image(nsImage: IconCache.icon(for: rule.path)).resizable()
        } onTarget: { t in
            mutate { $0.target = t }
        } onToggle: {
            mutate { $0.enabled.toggle() }
        } onDelete: {
            vpn.update { $0.appRules.removeAll { $0.id == rule.id } }
        }
    }

    private func mutate(_ f: (inout AppRule) -> Void) {
        vpn.update { p in
            if let i = p.appRules.firstIndex(where: { $0.id == rule.id }) { f(&p.appRules[i]) }
        }
    }
}

// MARK: - Add domain

struct AddDomainSheet: View {
    @EnvironmentObject var vpn: VPNController
    @Environment(\.dismiss) private var dismiss
    @State private var kind: DomainRuleKind = .domain
    @State private var value = ""
    @State private var preset: String?
    @State private var target: TargetID = Target.direct
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text("Новое правило по домену").font(Theme.display(20, .bold))
            Picker("Тип", selection: $kind) {
                ForEach(DomainRuleKind.allCases) { Text($0.title).tag($0) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()

            if kind == .ruleset {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 150), spacing: 8)], spacing: 8) {
                    ForEach(RuleSetPreset.all) { p in
                        Button {
                            preset = p.id
                            value = p.id
                        } label: {
                            Text(p.title)
                                .font(Theme.body(13, .semibold))
                                .frame(maxWidth: .infinity, minHeight: 36)
                                .background(RoundedRectangle(cornerRadius: 10).fill(preset == p.id ? Theme.violetDeep : Color.white.opacity(0.05)))
                                .overlay(RoundedRectangle(cornerRadius: 10).stroke(preset == p.id ? Theme.violet : Color.white.opacity(0.1)))
                        }
                        .buttonStyle(.plain)
                    }
                }
                Text("Или свои теги через запятую, например geosite-twitch").font(Theme.body(12)).foregroundStyle(Theme.muted)
            }
            TextField(placeholder, text: $value)
                .novaTextField()
                .onChange(of: value) { _, _ in error = nil }
            if let error {
                Text(error).font(Theme.body(12, .medium)).foregroundStyle(Theme.danger)
            }

            VStack(alignment: .leading, spacing: 8) {
                Text("Куда направить").font(Theme.body(12, .bold)).foregroundStyle(Theme.muted)
                RouteSegment(options: vpn.profile.servers.map { ($0.id, $0.code, Color(hex: $0.colorHex)) } + [(Target.direct, "DIRECT", Theme.directGray)],
                             selected: target) { target = $0 }
            }

            HStack {
                Spacer()
                Button("Отмена") { dismiss() }.buttonStyle(NovaButtonStyle())
                Button("Добавить") { add() }
                    .buttonStyle(NovaButtonStyle(prominent: true))
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(28)
        .frame(width: 560)
        .background(Theme.panel)
        .foregroundStyle(Theme.text)
        .preferredColorScheme(.dark)
        .onAppear { target = vpn.profile.servers.first?.id ?? Target.direct }
    }

    private var placeholder: String {
        switch kind {
        case .domain: return "youtube.com или .ru"
        case .keyword: return "openai"
        case .ip: return "203.0.113.0/24"
        case .ruleset: return "geosite-youtube"
        }
    }

    private func add() {
        let v = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !v.isEmpty else {
            error = "Заполни поле"
            return
        }
        if kind == .domain, ConfigBuilder.normalizeDomain(v).isEmpty {
            error = "Это не похоже на домен"
            return
        }
        if kind == .ip, !v.contains("."), !v.contains(":") {
            error = "Нужен IP-адрес или подсеть"
            return
        }
        let rule = DomainRule(kind: kind, value: v, target: target)
        vpn.update { $0.domainRules.append(rule) }
        dismiss()
    }
}

// MARK: - App picker

struct InstalledApp: Identifiable, Hashable {
    var id: String { path }
    let name: String
    let path: String
    let running: Bool
}

struct AppPickerSheet: View {
    @EnvironmentObject var vpn: VPNController
    @Environment(\.dismiss) private var dismiss
    @State private var apps: [InstalledApp] = []
    @State private var query = ""
    @State private var picked: Set<String> = []
    @State private var target: TargetID = Target.direct

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Text("Добавить приложения").font(Theme.display(20, .bold))
                Spacer()
                Button("Выбрать в Finder…") { chooseInFinder() }.buttonStyle(NovaButtonStyle())
            }
            TextField("Поиск", text: $query).novaTextField()
            ScrollView {
                LazyVStack(spacing: 6) {
                    ForEach(filtered) { app in
                        Button {
                            if picked.contains(app.path) { picked.remove(app.path) } else { picked.insert(app.path) }
                        } label: {
                            HStack(spacing: 12) {
                                Image(nsImage: IconCache.icon(for: app.path)).resizable().frame(width: 30, height: 30)
                                VStack(alignment: .leading, spacing: 1) {
                                    Text(app.name).font(Theme.body(14, .semibold))
                                    Text(app.path).font(Theme.body(11)).foregroundStyle(Theme.muted).lineLimit(1)
                                }
                                Spacer()
                                if app.running {
                                    Text("запущено").font(Theme.body(11, .bold)).foregroundStyle(Theme.mint)
                                }
                                Image(systemName: picked.contains(app.path) ? "checkmark.circle.fill" : "circle")
                                    .foregroundStyle(picked.contains(app.path) ? Theme.violet : Theme.muted)
                                    .font(.system(size: 18))
                            }
                            .padding(.horizontal, 12).padding(.vertical, 8)
                            .background(RoundedRectangle(cornerRadius: 12).fill(picked.contains(app.path) ? Theme.violet.opacity(0.14) : Color.white.opacity(0.03)))
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
            .frame(height: 360)

            VStack(alignment: .leading, spacing: 8) {
                Text("Куда направить").font(Theme.body(12, .bold)).foregroundStyle(Theme.muted)
                RouteSegment(options: vpn.profile.servers.map { ($0.id, $0.code, Color(hex: $0.colorHex)) } + [(Target.direct, "DIRECT", Theme.directGray)],
                             selected: target) { target = $0 }
            }
            HStack {
                Text(picked.isEmpty ? "Ничего не выбрано" : "Выбрано: \(picked.count)").font(Theme.body(13)).foregroundStyle(Theme.muted)
                Spacer()
                Button("Отмена") { dismiss() }.buttonStyle(NovaButtonStyle())
                Button("Добавить") { add() }
                    .buttonStyle(NovaButtonStyle(prominent: true))
                    .keyboardShortcut(.defaultAction)
                    .disabled(picked.isEmpty)
            }
        }
        .padding(28)
        .frame(width: 600)
        .background(Theme.panel)
        .foregroundStyle(Theme.text)
        .preferredColorScheme(.dark)
        .onAppear {
            target = vpn.profile.servers.first?.id ?? Target.direct
        }
        .task {
            // ~80 ms of disk and process queries: off the main thread so the sheet opens instantly.
            let found = await Task.detached(priority: .userInitiated) { Self.scan() }.value
            apps = found + apps.filter { a in !found.contains { $0.path == a.path } }
        }
    }

    private var filtered: [InstalledApp] {
        let existing = Set(vpn.profile.appRules.map(\.path))
        let list = apps.filter { !existing.contains($0.path) }
        guard !query.isEmpty else { return list }
        return list.filter { $0.name.localizedCaseInsensitiveContains(query) }
    }

    private func add() {
        let chosen = apps.filter { picked.contains($0.path) }
        vpn.update { p in
            for a in chosen { p.appRules.append(AppRule(name: a.name, path: a.path, target: target)) }
        }
        dismiss()
    }

    private func chooseInFinder() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.application]
        panel.allowsMultipleSelection = true
        panel.directoryURL = URL(fileURLWithPath: "/Applications")
        guard panel.runModal() == .OK else { return }
        for url in panel.urls {
            let app = InstalledApp(name: AppInfo.displayName(url.path), path: url.path, running: false)
            if !apps.contains(app) { apps.insert(app, at: 0) }
            picked.insert(url.path)
        }
    }

    nonisolated static func scan() -> [InstalledApp] {
        let fm = FileManager.default
        // Running apps with a UI, by outermost bundle (so "Claude Helper" folds into Claude.app);
        // agents and helpers tucked into Library folders or temp dirs are skipped.
        let running = Set(NSWorkspace.shared.runningApplications.compactMap { app -> String? in
            guard app.activationPolicy == .regular, let path = app.bundleURL?.path,
                  let bundle = Naming.bundlePath(containing: path + "/") else { return nil }
            let hidden = ["/Library/", "/private/", "/System/Library/", "/usr/"]
            return hidden.contains { bundle.contains($0) } ? nil : bundle
        })
        let dirs = ["/Applications", "/Applications/Utilities", "/System/Applications", "/System/Applications/Utilities",
                    NSHomeDirectory() + "/Applications"]
        // `/Applications/Safari.app` is a symlink into a Cryptex; compare by real path.
        func real(_ p: String) -> String {
            guard let r = realpath(p, nil) else { return p }
            defer { free(r) }
            return String(cString: r)
        }
        let runningReal = Set(running.map(real))
        var seen = Set<String>()
        var seenReal = Set<String>()
        var out: [InstalledApp] = []
        for d in dirs {
            for name in (try? fm.contentsOfDirectory(atPath: d)) ?? [] where name.hasSuffix(".app") {
                let path = (d as NSString).appendingPathComponent(name)
                guard seen.insert(path).inserted, seenReal.insert(real(path)).inserted else { continue }
                out.append(InstalledApp(name: fm.displayName(atPath: path).replacingOccurrences(of: ".app", with: ""), path: path,
                                        running: runningReal.contains(real(path))))
            }
        }
        for path in running where !seen.contains(path) && !seenReal.contains(real(path)) {
            out.append(InstalledApp(name: AppInfo.displayName(path), path: path, running: true))
        }
        return out.sorted {
            if $0.running != $1.running { return $0.running }
            return $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
        }
    }
}
