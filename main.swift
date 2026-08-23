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
    
    init() {
        self.lastChangeCount = pasteboard.changeCount
        self.storageDir = getStorageDirectory()
    }
    
    func start() {
        print("[\(APP_NAME)] 🚀 ClipBridge started. Listening for screenshots & clipboard images...")
        print("[\(APP_NAME)] 📁 Screenshots storage: \(storageDir.path)")
        
        // Initial cleanup
        cleanupOldScreenshots(in: storageDir)
        
        timer = Timer.scheduledTimer(withTimeInterval: 0.4, repeats: true) { [weak self] _ in
            self?.checkPasteboard()
        }
        RunLoop.main.run()
    }
    
    private func checkPasteboard() {
        let currentCount = pasteboard.changeCount
        if currentCount == lastChangeCount {
            return
        }
        lastChangeCount = currentCount
        
        // Check if this change was made by us
        if let types = pasteboard.types, types.contains(PROCESSED_TYPE) {
            return
        }
        
        // If it's already a file URL copied from Finder, check if it's an image
        if let urls = pasteboard.readObjects(forClasses: [NSURL.self], options: nil) as? [URL],
           let firstURL = urls.first, firstURL.isFileURL {
            let ext = firstURL.pathExtension.lowercased()
            let imageExtensions = ["png", "jpg", "jpeg", "webp", "gif", "bmp", "heic", "tiff"]
            if imageExtensions.contains(ext) {
                let currentStr = pasteboard.string(forType: .string)
                if currentStr != firstURL.path {
                    enrichPasteboardWithExistingFile(fileURL: firstURL)
                }
                return
            }
        }
        
        // Check if there is image data on pasteboard
        guard let types = pasteboard.types else { return }
        let hasImage = types.contains(.png) || types.contains(.tiff) || types.map({ $0.rawValue }).contains("public.png") || types.map({ $0.rawValue }).contains("public.tiff")
        
        if !hasImage {
            return
        }
        
        // Extract Image
        guard let image = NSImage(pasteboard: pasteboard) else {
            return
        }
        
        guard let tiffData = image.tiffRepresentation,
              let bitmap = NSBitmapImageRep(data: tiffData),
              let pngData = bitmap.representation(using: .png, properties: [:]) else {
            return
        }
        
        // Save to file
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyyMMdd_HHmmss_SSS"
        let filename = "screenshot_\(formatter.string(from: Date())).png"
        let fileURL = storageDir.appendingPathComponent(filename)
        
        do {
            try pngData.write(to: fileURL)
            print("[\(APP_NAME)] 📸 Image captured: \(fileURL.path)")
            
            // Re-write pasteboard with multi-type item:
            // 1. .png & .tiff -> Rich image for WeChat / Slack / Browsers
            // 2. .string -> Plain path for Terminal / CLI / Code editors
            // 3. .fileURL -> File reference for Finder / file managers
            // 4. PROCESSED_TYPE -> prevent loop
            let item = NSPasteboardItem()
            item.setData(pngData, forType: .png)
            item.setData(tiffData, forType: .tiff)
            item.setString(fileURL.path, forType: .string)
            item.setString(fileURL.absoluteString, forType: .fileURL)
            item.setString("1", forType: PROCESSED_TYPE)
            
            pasteboard.clearContents()
            pasteboard.writeObjects([item])
            
            // Update last change count to prevent self-triggering
            self.lastChangeCount = pasteboard.changeCount
            
            // Periodically clean up
            cleanupOldScreenshots(in: storageDir)
        } catch {
            print("[\(APP_NAME)] ❌ Error saving screenshot: \(error)")
        }
    }
    
    private func enrichPasteboardWithExistingFile(fileURL: URL) {
        guard let image = NSImage(contentsOf: fileURL),
              let tiffData = image.tiffRepresentation,
              let bitmap = NSBitmapImageRep(data: tiffData),
              let pngData = bitmap.representation(using: .png, properties: [:]) else {
            return
        }
        
        let item = NSPasteboardItem()
        item.setData(pngData, forType: .png)
        item.setData(tiffData, forType: .tiff)
        item.setString(fileURL.path, forType: .string)
        item.setString(fileURL.absoluteString, forType: .fileURL)
        item.setString("1", forType: PROCESSED_TYPE)
        
        pasteboard.clearContents()
        pasteboard.writeObjects([item])
        self.lastChangeCount = pasteboard.changeCount
        print("[\(APP_NAME)] 📎 File path enriched: \(fileURL.path)")
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
