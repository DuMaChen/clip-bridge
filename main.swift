import Cocoa
import Foundation

let PROCESSED_TYPE = NSPasteboard.PasteboardType("com.antigravity.clipbridge.processed")
let APP_NAME = "clipbridge"
let LAUNCH_AGENT_ID = "com.antigravity.clipbridge"

func getStorageDirectory() -> URL {
    let home = FileManager.default.homeDirectoryForCurrentUser
    let dir = home.appendingPathComponent(".agy_screenshots", isDirectory: true)
    if !FileManager.default.fileExists(atPath: dir.path) {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true, attributes: nil)
    }
    return dir
}

func cleanupOldScreenshots(in directory: URL, maxAgeDays: Double = 7.0, maxCount: Int = 200) {
    let fileManager = FileManager.default
    guard let files = try? fileManager.contentsOfDirectory(at: directory, includingPropertiesForKeys: [.creationDateKey, .fileSizeKey], options: [.skipsHiddenFiles]) else {
        return
    }
    
    let now = Date()
    var validFiles: [(url: URL, date: Date)] = []
    
    for file in files {
        // Only our regular PNG captures; never configs, logs, directories or symlinks.
        guard file.lastPathComponent.hasPrefix("screenshot_"),
              file.pathExtension == "png",
              let values = try? file.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey]),
              values.isRegularFile == true, values.isSymbolicLink != true else { continue }
        if let attrs = try? fileManager.attributesOfItem(atPath: file.path),
           let creationDate = attrs[.creationDate] as? Date {
            let ageInDays = now.timeIntervalSince(creationDate) / (24 * 3600)
            if ageInDays > maxAgeDays {
                try? fileManager.removeItem(at: file)
            } else {
                validFiles.append((url: file, date: creationDate))
            }
        }
    }
    
    if validFiles.count > maxCount {
        validFiles.sort { $0.date < $1.date }
        let toRemove = validFiles.prefix(validFiles.count - maxCount)
        for item in toRemove {
            try? fileManager.removeItem(at: item.url)
        }
    }
}

class ClipboardWatcher {
    private let pasteboard = NSPasteboard.general
    private var lastChangeCount: Int
    private var timer: Timer?
    private let storageDir: URL
    private var richItems: [[NSPasteboard.PasteboardType: Data]]?
    private var paths: [String] = []
    private var pathMode: Bool?
    private var activationObserver: NSObjectProtocol?
    private let pathApps: Set<String>
    private let mediaExtensions: Set<String> = [
        "png", "jpg", "jpeg", "webp", "gif", "bmp", "heic", "heif", "tif", "tiff", "ico", "svg", "avif",
        "mp3", "wav", "flac", "aac", "m4a", "ogg", "opus", "wma", "aiff",
        "mp4", "mov", "mkv", "avi", "webm", "wmv", "m4v", "mpeg", "mpg"
    ]

    init() {
        lastChangeCount = pasteboard.changeCount
        storageDir = getStorageDirectory()
        let configured = try? String(contentsOf: storageDir.appendingPathComponent("path_apps.txt"), encoding: .utf8)
        pathApps = Set((ProcessInfo.processInfo.environment["CLIPBRIDGE_PATH_APPS"] ?? configured ??
            "com.apple.Terminal,com.googlecode.iterm2,dev.warp.Warp-Stable,com.microsoft.VSCode,com.todesktop.230313mzl4w4u92,com.openai.codex")
            .components(separatedBy: CharacterSet(charactersIn: ",\r\n"))
            .map { $0.trimmingCharacters(in: .whitespaces).lowercased() }.filter { !$0.isEmpty })
    }

    func start() {
        print("[\(APP_NAME)] Listening for screenshots & media files...")
        print("[\(APP_NAME)] Screenshots storage: \(storageDir.path)")
        cleanupOldScreenshots(in: storageDir)
        activationObserver = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main
        ) { [weak self] _ in
            self?.checkPasteboard()
            self?.publishForForeground()
        }
        timer = Timer.scheduledTimer(withTimeInterval: 0.15, repeats: true) { [weak self] _ in
            self?.checkPasteboard()
            self?.publishForForeground()
        }
        RunLoop.main.run()
    }

    // Materialize every original item/type before changing ownership. If any
    // format cannot be read, leave the original clipboard completely untouched.
    private func snapshot() -> [[NSPasteboard.PasteboardType: Data]]? {
        guard let originals = pasteboard.pasteboardItems else { return nil }
        var result: [[NSPasteboard.PasteboardType: Data]] = []
        for original in originals {
            var formats: [NSPasteboard.PasteboardType: Data] = [:]
            for type in original.types {
                guard let data = original.data(forType: type) else { return nil }
                formats[type] = data
            }
            result.append(formats)
        }
        return result
    }

    private func checkPasteboard() {
        let count = pasteboard.changeCount
        if count == lastChangeCount { return }
        richItems = nil
        paths = []
        pathMode = nil
        lastChangeCount = count
        if pasteboard.types?.contains(PROCESSED_TYPE) == true { return }

        if let urls = pasteboard.readObjects(forClasses: [NSURL.self], options: nil) as? [URL],
           !urls.isEmpty, urls.allSatisfy({ $0.isFileURL }) {
            guard urls.contains(where: { mediaExtensions.contains($0.pathExtension.lowercased()) }),
                  var originals = snapshot(), !originals.isEmpty else { return }
            // Keep all items/types, including non-media siblings and Finder cut metadata.
            if urls.count == 1, originals.count == 1,
               let image = NSImage(contentsOf: urls[0]),
               let tiff = image.tiffRepresentation,
               let bitmap = NSBitmapImageRep(data: tiff),
               let png = bitmap.representation(using: .png, properties: [:]) {
                originals[0][.png] = png
                originals[0][.tiff] = tiff
            }
            guard pasteboard.changeCount == count else { return }
            richItems = originals
            paths = urls.map { $0.path }
            publishForForeground()
            return
        }

        guard let types = pasteboard.types,
              types.contains(.png) || types.contains(.tiff),
              let image = NSImage(pasteboard: pasteboard),
              let tiff = image.tiffRepresentation,
              let bitmap = NSBitmapImageRep(data: tiff),
              let png = bitmap.representation(using: .png, properties: [:]),
              pasteboard.changeCount == count else { return }
        let filename = "screenshot_\(Int(Date().timeIntervalSince1970 * 1000))_\(UUID().uuidString).png"
        let fileURL = storageDir.appendingPathComponent(filename)
        do {
            try png.write(to: fileURL, options: .atomic)
            guard pasteboard.changeCount == count else { return }
            richItems = [[.png: png, .tiff: tiff,
                          .fileURL: Data(fileURL.absoluteString.utf8)]]
            paths = [fileURL.path]
            print("[\(APP_NAME)] Image captured: \(fileURL.path)")
            publishForForeground()
            cleanupOldScreenshots(in: storageDir)
        } catch {
            print("[\(APP_NAME)] Error saving screenshot: \(error)")
        }
    }

    private func publishForForeground() {
        guard let originals = richItems else { return }
        guard pasteboard.changeCount == lastChangeCount else {
            richItems = nil
            paths = []
            pathMode = nil
            return
        }
        let bundleID = NSWorkspace.shared.frontmostApplication?.bundleIdentifier?.lowercased() ?? ""
        let wantsPaths = pathApps.contains(bundleID)
        if pathMode == wantsPaths { return }
        var formats = originals
        if wantsPaths {
            formats = [[.string: Data(paths.joined(separator: "\n").utf8)]]
        }
        let items = formats.map { entries -> NSPasteboardItem in
            let item = NSPasteboardItem()
            for (type, data) in entries { item.setData(data, forType: type) }
            item.setString("1", forType: PROCESSED_TYPE)
            return item
        }
        // Check again after materialization; never intentionally publish a stale copy.
        guard pasteboard.changeCount == lastChangeCount else { return }
        let ownedCount = pasteboard.clearContents()
        guard pasteboard.changeCount == ownedCount else { return }
        if pasteboard.writeObjects(items) {
            let published = pasteboard.changeCount
            guard pasteboard.string(forType: PROCESSED_TYPE) == "1",
                  pasteboard.changeCount == published else {
                richItems = nil
                paths = []
                pathMode = nil
                return
            }
            lastChangeCount = published
            pathMode = wantsPaths
        } else {
            richItems = nil
            paths = []
            pathMode = nil
            print("[\(APP_NAME)] Could not publish clipboard formats")
        }
    }
}
// Service & CLI Management
func getExecutablePath() -> String {
    let arg0 = CommandLine.arguments[0]
    if arg0.hasPrefix("/") {
        return arg0
    }
    return URL(fileURLWithPath: FileManager.default.currentDirectoryPath).appendingPathComponent(arg0).path
}

func getLaunchAgentPlistPath() -> URL {
    let home = FileManager.default.homeDirectoryForCurrentUser
    let agentsDir = home.appendingPathComponent("Library/LaunchAgents", isDirectory: true)
    if !FileManager.default.fileExists(atPath: agentsDir.path) {
        try? FileManager.default.createDirectory(at: agentsDir, withIntermediateDirectories: true)
    }
    return agentsDir.appendingPathComponent("\(LAUNCH_AGENT_ID).plist")
}

func installLaunchAgent(binaryPath: String) {
    let plistPath = getLaunchAgentPlistPath()
    let logDir = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".agy_screenshots/logs", isDirectory: true)
    try? FileManager.default.createDirectory(at: logDir, withIntermediateDirectories: true)
    
    let stdoutLog = logDir.appendingPathComponent("clipbridge.log").path
    let stderrLog = logDir.appendingPathComponent("clipbridge.err").path
    
    let plistContent = """
    <?xml version="1.0" encoding="UTF-8"?>
    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
    <plist version="1.0">
    <dict>
        <key>Label</key>
        <string>\(LAUNCH_AGENT_ID)</string>
        <key>ProgramArguments</key>
        <array>
            <string>\(binaryPath)</string>
            <string>run</string>
        </array>
        <key>RunAtLoad</key>
        <true/>
        <key>KeepAlive</key>
        <true/>
        <key>StandardOutPath</key>
        <string>\(stdoutLog)</string>
        <key>StandardErrorPath</key>
        <string>\(stderrLog)</string>
    </dict>
    </plist>
    """
    
    do {
        try plistContent.write(to: plistPath, atomically: true, encoding: .utf8)
        print("✅ Created LaunchAgent configuration at: \(plistPath.path)")
        
        let unloadProcess = Process()
        unloadProcess.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        unloadProcess.arguments = ["unload", plistPath.path]
        try? unloadProcess.run()
        unloadProcess.waitUntilExit()
        
        let loadProcess = Process()
        loadProcess.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        loadProcess.arguments = ["load", plistPath.path]
        try loadProcess.run()
        loadProcess.waitUntilExit()
        
        print("✅ Background service started successfully! It will automatically start on login.")
    } catch {
        print("❌ Failed to install LaunchAgent: \(error)")
    }
}

func stopLaunchAgent() {
    let plistPath = getLaunchAgentPlistPath()
    if FileManager.default.fileExists(atPath: plistPath.path) {
        let unloadProcess = Process()
        unloadProcess.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        unloadProcess.arguments = ["unload", plistPath.path]
        try? unloadProcess.run()
        unloadProcess.waitUntilExit()
        try? FileManager.default.removeItem(at: plistPath)
        print("🛑 ClipBridge background service stopped and removed.")
    } else {
        print("ℹ️ ClipBridge background service is not installed.")
    }
}

func checkStatus() {
    let plistPath = getLaunchAgentPlistPath()
    let isInstalled = FileManager.default.fileExists(atPath: plistPath.path)
    
    let checkProcess = Process()
    let pipe = Pipe()
    checkProcess.executableURL = URL(fileURLWithPath: "/bin/launchctl")
    checkProcess.arguments = ["list"]
    checkProcess.standardOutput = pipe
    try? checkProcess.run()
    checkProcess.waitUntilExit()
    
    let data = pipe.fileHandleForReading.readDataToEndOfFile()
    let output = String(data: data, encoding: .utf8) ?? ""
    let isRunning = output.contains(LAUNCH_AGENT_ID)
    
    print("=== ClipBridge Status ===")
    print("• Service Installed: \(isInstalled ? "Yes" : "No")")
    print("• Service Running:   \(isRunning ? "🟢 Running" : "🔴 Stopped")")
    print("• Screenshots Dir:   \(getStorageDirectory().path)")
    print("• Plist Location:    \(plistPath.path)")
}

let args = CommandLine.arguments
let command = args.count > 1 ? args[1] : "run"

switch command {
case "start", "install":
    let binaryPath = getExecutablePath()
    installLaunchAgent(binaryPath: binaryPath)
case "stop", "uninstall":
    stopLaunchAgent()
case "status":
    checkStatus()
case "run":
    let watcher = ClipboardWatcher()
    watcher.start()
case "--help", "-h", "help":
    print("""
    ClipBridge - macOS Screenshot & Clipboard Image to File Path Bridge
    
    Usage:
      clipbridge start    # Install and start background service (runs on login)
      clipbridge stop     # Stop and uninstall background service
      clipbridge status   # Check running status
      clipbridge run      # Run in foreground
    """)
default:
    print("Unknown command '\(command)'. Run with --help for usage.")
}
