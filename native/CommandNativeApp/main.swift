import AppKit

final class Launcher: NSObject, NSApplicationDelegate {
    private let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 640, height: 480), styleMask: [.titled, .closable, .miniaturizable], backing: .buffered, defer: false)
    private let game = NSTextField(string: ProcessInfo.processInfo.environment["CMO_GAME_DIR"] ?? UserDefaults.standard.string(forKey: "gameDirectory") ?? "")
    private let bottle = NSTextField(string: ProcessInfo.processInfo.environment["CMO_BOTTLE"] ?? UserDefaults.standard.string(forKey: "bottleName") ?? "")
    private let mode = NSPopUpButton()
    private let launch = NSButton(title: "Launch Command", target: nil, action: nil)
    private let status = NSTextField(wrappingLabelWithString: "Choose the installed game. The launcher prepares a separate runtime for this session.")
    private let output = NSTextView()
    private var process: Process?
    private var runtime: URL?
    private var logFile: FileHandle?

    func applicationDidFinishLaunching(_ notification: Notification) {
        window.title = Bundle.main.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String ?? "Command Native"
        window.center()
        let content = window.contentView!
        let title = NSTextField(labelWithString: "Command Modern Operations")
        title.font = .boldSystemFont(ofSize: 22)
        let subtitle = NSTextField(wrappingLabelWithString: "Native macOS controls and web rendering")
        subtitle.textColor = .secondaryLabelColor
        let choose = NSButton(title: "Choose…", target: self, action: #selector(chooseGame))
        mode.addItems(withTitles: ["Native controls and browser (development)", "Native browser only (development)", "Original Windows interface"])
        let requested = ProcessInfo.processInfo.environment["CMO_NATIVE_UI"] ?? "all"
        mode.selectItem(at: requested == "0" ? 2 : requested == "browser" ? 1 : 0)
        launch.target = self; launch.action = #selector(start); launch.keyEquivalent = "\r"
        let scroll = NSScrollView(); scroll.hasVerticalScroller = true; scroll.borderType = .bezelBorder
        output.isEditable = false; output.font = .monospacedSystemFont(ofSize: 11, weight: .regular); scroll.documentView = output
        func place(_ view: NSView, _ x: CGFloat, _ y: CGFloat, _ width: CGFloat, _ height: CGFloat) {
            view.frame = NSRect(x: x, y: y, width: width, height: height); content.addSubview(view)
        }
        place(title, 24, 425, 590, 30); place(subtitle, 24, 396, 590, 24)
        place(NSTextField(labelWithString: "Game folder"), 24, 355, 110, 20)
        place(game, 24, 323, 480, 26); place(choose, 512, 320, 104, 32)
        place(NSTextField(labelWithString: "CrossOver bottle"), 24, 288, 140, 20); place(bottle, 172, 284, 180, 26)
        place(NSTextField(labelWithString: "Interface"), 24, 249, 140, 20); place(mode, 168, 242, 450, 32)
        place(launch, 24, 191, 165, 34); place(status, 204, 180, 410, 50); place(scroll, 24, 24, 592, 144)
        game.setAccessibilityLabel("Game folder"); bottle.setAccessibilityLabel("CrossOver bottle"); mode.setAccessibilityLabel("Interface mode")
        let menu = NSMenu(); let application = NSMenuItem(); menu.addItem(application)
        let submenu = NSMenu(); submenu.addItem(withTitle: "Quit Command Native", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"); application.submenu = submenu; NSApp.mainMenu = menu
        window.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true)
        if CommandLine.arguments.contains("--launch") { start() }
    }
    @objc private func chooseGame() {
        guard process == nil else { return }
        let panel = NSOpenPanel(); panel.canChooseFiles = false; panel.canChooseDirectories = true; panel.allowsMultipleSelection = false
        panel.message = "Choose the folder containing Command.exe."
        panel.beginSheetModal(for: window) { [weak self] result in if result == .OK, let url = panel.url { self?.game.stringValue = url.path } }
    }
    private func append(_ data: Data) {
        try? logFile?.write(contentsOf: data)
        let text = String(decoding: data, as: UTF8.self)
        output.textStorage?.append(NSAttributedString(string: text))
        if output.string.count > 60000 { output.string = String(output.string.suffix(40000)) }
        output.scrollToEndOfDocument(nil)
    }
    @objc private func start() {
        guard process == nil else { return }
        do {
            let fm = FileManager.default
            let source = URL(fileURLWithPath: game.stringValue).standardizedFileURL
            guard fm.fileExists(atPath: source.appendingPathComponent("Command.exe").path), fm.fileExists(atPath: source.appendingPathComponent("Command.exe.config").path) else {
                throw NSError(domain: "CommandNative", code: 1, userInfo: [NSLocalizedDescriptionKey: "Choose the installed game folder containing Command.exe and Command.exe.config."])
            }
            guard !bottle.stringValue.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                throw NSError(domain: "CommandNative", code: 2, userInfo: [NSLocalizedDescriptionKey: "Enter the CrossOver bottle containing the game."])
            }
            UserDefaults.standard.set(source.path, forKey: "gameDirectory")
            UserDefaults.standard.set(bottle.stringValue, forKey: "bottleName")
            let resources = Bundle.main.resourceURL!
            let adapter = resources.appendingPathComponent("adapter")
            let support = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("CommandNative")
            let session = support.appendingPathComponent("Runtime/" + UUID().uuidString)
            try fm.createDirectory(at: session, withIntermediateDirectories: true)
            runtime = session
            let replaced: Set<String> = ["CommandBrowser.exe", "CommandBrowser.exe.config", "CommandMetal.exe", "CommandMetal.exe.config"]
            for entry in try fm.contentsOfDirectory(at: source, includingPropertiesForKeys: nil) where !replaced.contains(entry.lastPathComponent) {
                try fm.createSymbolicLink(at: session.appendingPathComponent(entry.lastPathComponent), withDestinationURL: entry)
            }
            for name in ["CommandBrowser", "CommandMetal"] {
                try fm.copyItem(at: adapter.appendingPathComponent(name + ".exe"), to: session.appendingPathComponent(name + ".exe"))
                try fm.copyItem(at: source.appendingPathComponent("Command.exe.config"), to: session.appendingPathComponent(name + ".exe.config"))
            }
            let logs = support.appendingPathComponent("Logs"); try fm.createDirectory(at: logs, withIntermediateDirectories: true)
            let log = logs.appendingPathComponent(UUID().uuidString + ".log"); fm.createFile(atPath: log.path, contents: nil)
            logFile = try FileHandle(forWritingTo: log)
            let task = Process(); task.executableURL = URL(fileURLWithPath: "/bin/zsh"); task.arguments = [adapter.appendingPathComponent("Play-D3DMetal.command").path]
            var env = ProcessInfo.processInfo.environment
            env["CMO_GAME_DIR"] = session.path; env["CMO_BOTTLE"] = bottle.stringValue
            env["CMO_NATIVE_UI"] = ["all", "browser", "0"][mode.indexOfSelectedItem]
            env["CMO_NATIVE_HOST"] = resources.appendingPathComponent("libCommandNativeHost.dylib").path
            task.environment = env
            let pipe = Pipe(); task.standardOutput = pipe; task.standardError = pipe
            pipe.fileHandleForReading.readabilityHandler = { [weak self] handle in
                let data = handle.availableData
                if data.isEmpty { handle.readabilityHandler = nil; return }
                DispatchQueue.main.async { self?.append(data) }
            }
            task.terminationHandler = { [weak self] completed in DispatchQueue.main.async {
                guard let self = self else { return }
                self.process = nil; self.launch.isEnabled = true; self.game.isEnabled = true; self.bottle.isEnabled = true; self.mode.isEnabled = true
                self.status.stringValue = completed.terminationStatus == 0 ? "Command closed. Your session log is saved." : "Command could not complete this session. Review the log below."
                try? self.logFile?.close(); self.logFile = nil
                if let session = self.runtime { try? fm.removeItem(at: session) }; self.runtime = nil
            } }
            try task.run(); process = task
            launch.isEnabled = false; game.isEnabled = false; bottle.isEnabled = false; mode.isEnabled = false
            status.stringValue = "Command is running. Native interface changes apply to this session."
        } catch {
            status.stringValue = error.localizedDescription
            append(Data((error.localizedDescription + "\n").utf8))
            if let session = runtime { try? FileManager.default.removeItem(at: session) }; runtime = nil
        }
    }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard process != nil else { return .terminateNow }
        status.stringValue = "Save and close Command before quitting this launcher."
        window.makeKeyAndOrderFront(nil)
        return .terminateCancel
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { process == nil }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag { window.makeKeyAndOrderFront(nil) }
        return true
    }
}
let app = NSApplication.shared
let delegate = Launcher()
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()
