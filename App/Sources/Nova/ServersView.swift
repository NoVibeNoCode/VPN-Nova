import AppKit
import NovaCore
import SwiftUI

struct ServersView: View {
    @EnvironmentObject var vpn: VPNController
    @State private var editing: Server?
    @State private var adding = false
    @State private var pasteLink = ""
    @State private var pasteError: String?
    @State private var pendingInsecure: Server?

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            PageHeader(title: "Серверы", subtitle: "Через что может идти трафик") {
                Button { adding = true } label: { Label("Добавить", systemImage: "plus") }
                    .buttonStyle(NovaButtonStyle(prominent: true))
            }
            ScrollView {
                VStack(spacing: 14) {
                    LazyVGrid(columns: [GridItem(.flexible(), spacing: 14), GridItem(.flexible(), spacing: 14)], spacing: 14) {
                        ForEach(vpn.profile.servers) { s in
                            ServerCard(server: s, health: vpn.health[s.id], history: vpn.delayHistory[s.id] ?? [], active: vpn.status == .on) {
                                editing = s
                            }
                        }
                    }
                    LinkDropZone(text: $pasteLink, error: pasteError) { importLink() }
                }
            }
            .scrollIndicators(.never)
        }
        .padding(.horizontal, 36)
        .padding(.vertical, 28)
        .sheet(item: $editing) { s in ServerEditor(original: s) }
        .sheet(isPresented: $adding) { ServerEditor(original: nil) }
        .alert("Сертификат сервера не проверяется", isPresented: Binding(get: { pendingInsecure != nil }, set: { if !$0 { pendingInsecure = nil } }),
               presenting: pendingInsecure) { server in
            Button("Добавить всё равно", role: .destructive) {
                vpn.update { $0.servers.append(server) }
                pasteLink = ""
                pendingInsecure = nil
            }
            Button("Отмена", role: .cancel) { pendingInsecure = nil }
        } message: { _ in
            Text("В ссылке включён allowInsecure. Кто угодно на пути к серверу сможет подменить его и читать трафик. Безопаснее попросить у владельца сервера ссылку с нормальным сертификатом или с Reality.")
        }
    }

    private func importLink() {
        do {
            let parsed = try LinkParser.parse(pasteLink)
            let used = Set(vpn.profile.servers.map(\.colorHex))
            let color = Server.palette.first { !used.contains($0) } ?? Server.palette[0]
            let server = Server(name: parsed.name, code: ServerEditor.guessCode(parsed.name), colorHex: color, outbound: parsed.outbound)
            pasteError = nil
            if server.skipsCertificateCheck {
                pendingInsecure = server
                return
            }
            vpn.update { $0.servers.append(server) }
            pasteLink = ""
        } catch {
            pasteError = error.localizedDescription
        }
    }
}

struct ServerCard: View {
    let server: Server
    let health: HealthMonitor.ServerHealth?
    let history: [Int]
    let active: Bool
    let onEdit: () -> Void
    @State private var drawn = false

    var body: some View {
        let color = Color(hex: server.colorHex)
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .top, spacing: 14) {
                Text(server.code).font(Theme.display(36, .heavy))
                VStack(alignment: .leading, spacing: 2) {
                    Text(server.name).font(Theme.body(16, .bold)).lineLimit(1)
                    Text("\(Self.mask(server.host)) : \(server.port.map(String.init) ?? "—")").font(Theme.mono(12)).foregroundStyle(.white.opacity(0.7))
                }
                Spacer()
                Button(action: onEdit) {
                    Image(systemName: "pencil").frame(width: 32, height: 32)
                        .background(RoundedRectangle(cornerRadius: 9).fill(.white.opacity(0.12)))
                }
                .buttonStyle(.plain)
                .accessibilityLabel(Text("Изменить сервер"))
            }
            HStack(spacing: 6) {
                ForEach(tags, id: \.self) { t in
                    Text(t).font(Theme.body(11, .bold)).padding(.horizontal, 9).padding(.vertical, 4)
                        .background(Capsule().fill(.white.opacity(0.12)))
                }
                if server.skipsCertificateCheck {
                    Label("сертификат не проверяется", systemImage: "exclamationmark.shield.fill")
                        .font(Theme.body(11, .bold))
                        .padding(.horizontal, 9).padding(.vertical, 4)
                        .background(Capsule().fill(Theme.danger.opacity(0.85)))
                        .help("allowInsecure: соединение можно перехватить")
                }
            }
            HStack(alignment: .bottom, spacing: 16) {
                LatencyLine(values: history)
                    .trim(from: 0, to: drawn ? 1 : 0)
                    .stroke(.white.opacity(0.85), style: StrokeStyle(lineWidth: 2.2, lineCap: .round, lineJoin: .round))
                    .frame(height: 46)
                    .onAppear { withAnimation(.easeOut(duration: 1.6)) { drawn = true } }
                VStack(alignment: .trailing, spacing: 0) {
                    if active, health?.isDown == true {
                        Text(health?.fallbackActive == true ? "напрямую" : "недоступен").font(Theme.display(16, .bold)).foregroundStyle(Theme.amber)
                    } else {
                        HStack(alignment: .firstTextBaseline, spacing: 3) {
                            Text(active ? health?.delayMs.map(String.init) ?? "…" : "—").font(Theme.display(24, .bold))
                            Text("мс").font(Theme.body(12)).foregroundStyle(.white.opacity(0.7))
                        }
                    }
                    Text("задержка").font(Theme.body(12)).foregroundStyle(.white.opacity(0.7))
                }
            }
        }
        .padding(.horizontal, 20).padding(.vertical, 18)
        .background(
            RoundedRectangle(cornerRadius: 20)
                .fill(LinearGradient(colors: [color.opacity(0.75), color.opacity(0.18)], startPoint: .topLeading, endPoint: .bottomTrailing))
        )
        .overlay(RoundedRectangle(cornerRadius: 20).stroke(.white.opacity(0.22)))
        .shadow(color: color.opacity(0.3), radius: 24, y: 10)
    }

    private var tags: [String] {
        var t = [server.protocolName.uppercased()]
        if server.outbound["tls"]?["reality"]?["enabled"] == .bool(true) { t.append("Reality") }
        else if server.outbound["tls"]?["enabled"] == .bool(true) { t.append("TLS") }
        if let flow = server.outbound["flow"]?.stringValue, flow.contains("vision") { t.append("Vision") }
        if let tr = server.outbound["transport"]?["type"]?.stringValue { t.append(tr.uppercased()) }
        return t
    }

    static func mask(_ host: String) -> String {
        let parts = host.split(separator: ".")
        guard parts.count == 4, parts.allSatisfy({ Int($0) != nil }) else { return host }
        return "\(parts[0]).\(parts[1]).•••.\(parts[3])"
    }
}

struct LatencyLine: Shape {
    let values: [Int]

    func path(in rect: CGRect) -> Path {
        var p = Path()
        let vals = values.isEmpty ? [0, 0] : values.map { $0 == 0 ? (values.filter { $0 > 0 }.max() ?? 100) : $0 }
        let maxV = Double(max(vals.max() ?? 1, 1)), minV = Double(vals.min() ?? 0)
        let span = max(maxV - minV, 20)
        for (i, v) in vals.enumerated() {
            let x = rect.width * CGFloat(i) / CGFloat(max(vals.count - 1, 1))
            let y = rect.height * CGFloat(0.15 + 0.7 * (Double(v) - minV) / span)
            if i == 0 { p.move(to: CGPoint(x: x, y: y)) } else { p.addLine(to: CGPoint(x: x, y: y)) }
        }
        return p
    }
}

struct LinkDropZone: View {
    @Binding var text: String
    let error: String?
    let onImport: () -> Void

    var body: some View {
        VStack(spacing: 12) {
            HStack(spacing: 12) {
                Image(systemName: "link").font(.system(size: 20, weight: .semibold)).foregroundStyle(Theme.violet)
                VStack(alignment: .leading, spacing: 2) {
                    Text("Вставь ссылку сервера").font(Theme.body(14, .bold))
                    Text("vless://, vmess://, trojan://, ss://, hy2://, tuic://").font(Theme.body(12)).foregroundStyle(Theme.muted)
                }
                Spacer()
            }
            HStack(spacing: 10) {
                TextField("vless://…", text: $text).novaTextField().onSubmit(onImport)
                Button("Импорт", action: onImport).buttonStyle(NovaButtonStyle(prominent: true))
                    .disabled(text.trimmingCharacters(in: .whitespaces).isEmpty)
            }
            if let error {
                Text(error).font(Theme.body(12, .medium)).foregroundStyle(Theme.danger).frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .padding(20)
        .background(MarchingBorder(cornerRadius: 18, color: Theme.violet.opacity(0.6)).allowsHitTesting(false))
    }
}

struct ServerEditor: View {
    @EnvironmentObject var vpn: VPNController
    @Environment(\.dismiss) private var dismiss
    let original: Server?

    @State private var name = ""
    @State private var code = ""
    @State private var color = Server.palette[0]
    @State private var link = ""
    @State private var json = ""
    @State private var error: String?
    @State private var confirmDelete = false
    @State private var confirmInsecure = false
    @State private var insecureAccepted = false

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text(original == nil ? "Новый сервер" : "Сервер \(original!.code)").font(Theme.display(20, .bold))

            HStack(spacing: 12) {
                field("Название") { TextField("Германия", text: $name).novaTextField() }
                field("Метка") {
                    TextField("DE", text: $code).novaTextField().frame(width: 90)
                        .onChange(of: code) { _, v in if v.count > 6 { code = String(v.prefix(6)) } }
                }
            }
            field("Цвет") {
                HStack(spacing: 10) {
                    ForEach(Server.palette, id: \.self) { hex in
                        Button { color = hex } label: {
                            Circle().fill(Color(hex: hex)).frame(width: 26, height: 26)
                                .overlay(Circle().stroke(.white, lineWidth: color == hex ? 2.5 : 0))
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel(Text("Цвет \(hex)"))
                    }
                }
            }
            field("Ссылка (заполнит JSON)") {
                HStack(spacing: 8) {
                    TextField("vless://…", text: $link).novaTextField().onSubmit(parseLink)
                    Button("Разобрать", action: parseLink).buttonStyle(NovaButtonStyle())
                }
            }
            field("Outbound sing-box (JSON, без tag)") {
                TextEditor(text: $json)
                    .font(Theme.mono(12))
                    .scrollContentBackground(.hidden)
                    .padding(8)
                    .frame(height: 220)
                    .background(RoundedRectangle(cornerRadius: 10).fill(Color.black.opacity(0.3)))
                    .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.white.opacity(0.1)))
            }
            if let error {
                Text(error).font(Theme.body(12, .medium)).foregroundStyle(Theme.danger)
            }
            HStack {
                if original != nil {
                    Button("Удалить", role: .destructive) { confirmDelete = true }
                        .buttonStyle(NovaButtonStyle())
                        .foregroundStyle(Theme.danger)
                }
                Spacer()
                Button("Отмена") { dismiss() }.buttonStyle(NovaButtonStyle())
                Button("Сохранить") { save() }
                    .buttonStyle(NovaButtonStyle(prominent: true))
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(28)
        .frame(width: 620)
        .background(Theme.panel)
        .foregroundStyle(Theme.text)
        .preferredColorScheme(.dark)
        .onAppear(perform: load)
        .alert("Сертификат сервера не проверяется", isPresented: $confirmInsecure) {
            Button("Сохранить всё равно", role: .destructive) {
                insecureAccepted = true
                save()
            }
            Button("Отмена", role: .cancel) {}
        } message: {
            Text("В tls включён insecure. Кто угодно на пути к серверу сможет подменить его и читать трафик.")
        }
        .confirmationDialog("Удалить сервер?", isPresented: $confirmDelete) {
            Button("Удалить", role: .destructive) { delete() }
        } message: {
            Text("Правила, которые вели на этот сервер, переключатся на DIRECT.")
        }
    }

    private func field<C: View>(_ title: String, @ViewBuilder _ content: () -> C) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(Theme.body(12, .bold)).foregroundStyle(Theme.muted)
            content()
        }
    }

    private func load() {
        if let s = original {
            name = s.name
            code = s.code
            color = s.colorHex
            json = s.outbound.prettyString()
        } else {
            let used = Set(vpn.profile.servers.map(\.colorHex))
            color = Server.palette.first { !used.contains($0) } ?? Server.palette[0]
            json = JSONValue.object(["type": "vless", "server": "", "server_port": 443, "uuid": ""]).prettyString()
        }
    }

    private func parseLink() {
        do {
            let p = try LinkParser.parse(link)
            json = p.outbound.prettyString()
            if name.isEmpty { name = p.name }
            if code.isEmpty { code = Self.guessCode(p.name) }
            error = nil
        } catch {
            self.error = error.localizedDescription
        }
    }

    private func save() {
        guard !name.trimmingCharacters(in: .whitespaces).isEmpty else { error = "Укажи название"; return }
        guard !code.trimmingCharacters(in: .whitespaces).isEmpty else { error = "Укажи метку, например DE"; return }
        let outbound: JSONValue
        do {
            outbound = try JSONValue.parse(json)
        } catch {
            self.error = "JSON не разбирается: \(error.localizedDescription)"
            return
        }
        guard case .object(var obj) = outbound, obj["type"]?.stringValue?.isEmpty == false else {
            error = "В JSON нужен объект с полем type"
            return
        }
        obj["tag"] = nil
        var server = original ?? Server(name: name, code: code, colorHex: color, outbound: .object(obj))
        server.name = name.trimmingCharacters(in: .whitespaces)
        server.code = code.trimmingCharacters(in: .whitespaces).uppercased()
        server.colorHex = color
        server.outbound = .object(obj)
        if server.skipsCertificateCheck && !insecureAccepted && original?.skipsCertificateCheck != true {
            confirmInsecure = true
            return
        }
        vpn.update { p in
            if let i = p.servers.firstIndex(where: { $0.id == server.id }) { p.servers[i] = server } else { p.servers.append(server) }
        }
        dismiss()
    }

    private func delete() {
        guard let s = original else { return }
        vpn.update { p in
            p.servers.removeAll { $0.id == s.id }
            for i in p.domainRules.indices where p.domainRules[i].target == s.id { p.domainRules[i].target = Target.direct }
            for i in p.appRules.indices where p.appRules[i].target == s.id { p.appRules[i].target = Target.direct }
            if p.settings.defaultTarget == s.id { p.settings.defaultTarget = Target.direct }
        }
        dismiss()
    }

    static func guessCode(_ name: String) -> String { Naming.suggestedCode(name) }
}
