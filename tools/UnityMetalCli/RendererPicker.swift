import AppKit
import Darwin

private let renderers = [("base", "Base"), ("builtin", "Built-in"), ("urp", "URP")]
private let preference = "NativeH3VRRenderer"

private struct LaunchError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
    init(_ message: String) { self.message = message }
}

@main
struct RendererPicker {
    @MainActor static func main() {
        let app = Bundle.main.bundleURL
        let args = Array(CommandLine.arguments.dropFirst())
        let environment = ProcessInfo.processInfo.environment
        do {
            // Earlier launchers already verify the Base receipt and set up MacVR.
            // Keep those callers, including existing Base mods, on the Base build.
            if environment["NATIVEH3VR_OPENXR_LOADER"] != nil && environment["NATIVEH3VR_STATE_DIR"] != nil {
                try runPreparedBase(app, args)
            } else {
                let synthetic = args.contains("--synthetic")
                let options = args.filter { $0 != "--synthetic" && !$0.hasPrefix("-psn_") }
                let renderer: String?
                if options.isEmpty {
                    renderer = chooseRenderer()
                } else if options.count == 2 && options[0] == "--renderer" && renderers.contains(where: { $0.0 == options[1] }) {
                    renderer = options[1]
                } else {
                    throw LaunchError("Use --renderer base, builtin or urp.")
                }
                if let renderer {
                    try launch(app, renderer: renderer, synthetic: synthetic, environment: environment)
                }
            }
        } catch {
            let alert = NSAlert()
            alert.alertStyle = .warning
            alert.messageText = "NativeH3VR could not start"
            alert.informativeText = error.localizedDescription
            _ = show(alert)
            exit(1)
        }
    }

    @MainActor private static func show(_ alert: NSAlert) -> NSApplication.ModalResponse {
        NSApplication.shared.setActivationPolicy(.accessory)
        NSApplication.shared.activate(ignoringOtherApps: true)
        return alert.runModal()
    }

    @MainActor private static func chooseRenderer() -> String? {
        let picker = NSPopUpButton(frame: NSRect(x: 0, y: 0, width: 200, height: 28))
        picker.addItems(withTitles: renderers.map { $0.1 })
        let last = UserDefaults.standard.string(forKey: preference)
        picker.selectItem(at: renderers.firstIndex(where: { $0.0 == last }) ?? 2)
        let alert = NSAlert()
        alert.messageText = "Choose a renderer"
        alert.accessoryView = picker
        alert.addButton(withTitle: "Play")
        alert.addButton(withTitle: "Cancel")
        guard show(alert) == .alertFirstButtonReturn else { return nil }
        let renderer = renderers[picker.indexOfSelectedItem].0
        UserDefaults.standard.set(renderer, forKey: preference)
        return renderer
    }

    private static func runPreparedBase(_ app: URL, _ args: [String]) throws {
        let profile = app.appendingPathComponent("Contents/Helpers/Base.bundle")
        guard let program = Bundle(url: profile)?.executableURL?.path else {
            throw LaunchError("The Base renderer is missing from this app.")
        }
        var argv = ([program] + args).map { strdup($0) }
        argv.append(nil)
        defer { argv.forEach { free($0) } }
        let code = argv.withUnsafeMutableBufferPointer { execv(program, $0.baseAddress!) }
        throw LaunchError("The Base renderer could not start (\(code), \(String(cString: strerror(errno)))).")
    }

    private static func launch(_ app: URL, renderer: String, synthetic: Bool, environment: [String: String]) throws {
        let files = FileManager.default
        let install = app.deletingLastPathComponent()
        var repository = install
        while !files.fileExists(atPath: repository.appendingPathComponent("runtime/UnityMetal/MetalOpenXR.mm").path) {
            guard repository.path != "/" else {
                throw LaunchError("Place NativeH3VR.app in your NativeH3VR checkout's .local folder.")
            }
            repository.deleteLastPathComponent()
        }
        let resources = app.appendingPathComponent("Contents/Resources")
        let dotnet = try readSetting(resources.appendingPathComponent("dotnet-path.txt"))
        guard files.isExecutableFile(atPath: dotnet) else {
            throw LaunchError("The .NET host could not be found at \(dotnet).")
        }
        let runtime = try environment["NATIVEH3VR_MACVR_RUNTIME"] ?? readSetting(install.appendingPathComponent("macvr-runtime.txt"))
        let play = repository.appendingPathComponent(".local/headset-play", isDirectory: true)
        let session = play.appendingPathComponent("run.\(UUID().uuidString)", isDirectory: true)
        let log = session.appendingPathComponent("launcher.log")
        try files.createDirectory(at: session, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        guard files.createFile(atPath: log.path, contents: nil) else { throw LaunchError("Could not create \(log.path).") }
        let output = try FileHandle(forWritingTo: log)
        defer { try? output.close() }

        let process = Process()
        process.executableURL = URL(fileURLWithPath: dotnet)
        process.currentDirectoryURL = repository
        process.standardOutput = output
        process.standardError = output
        let state = (synthetic ? session : play).appendingPathComponent("state")
        process.arguments = [resources.appendingPathComponent("Launcher/UnityMetalCli.dll").path, "run",
                             "--app", app.path, "--renderer", renderer, "--runtime", runtime,
                             "--state-dir", state.path, "--log-file", session.appendingPathComponent("player.log").path,
                             synthetic ? "--synthetic" : "--headset"]
        try process.run()
        process.waitUntilExit()
        if process.terminationStatus != 0 {
            let detail = (try? String(contentsOf: log, encoding: .utf8))?.split(separator: "\n").last.map(String.init)
            throw LaunchError("\(detail ?? "The game closed unexpectedly.")\n\nLogs are in \(session.path).")
        }
    }

    private static func readSetting(_ file: URL) throws -> String {
        guard let value = try? String(contentsOf: file, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines),
              !value.isEmpty else { throw LaunchError("Set the launch path in \(file.path).") }
        return value
    }
}
