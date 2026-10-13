import Foundation

// The runner prepends the actual cleanup function from main.swift.
let manager = FileManager.default
let testDirectory = manager.temporaryDirectory.appendingPathComponent("clipbridge-cleanup-" + UUID().uuidString)
try manager.createDirectory(at: testDirectory, withIntermediateDirectories: true)
defer { try? manager.removeItem(at: testDirectory) }
let oldDate = Date().addingTimeInterval(-20 * 86400)
func makeFile(_ name: String, old: Bool = false) throws -> URL {
    let url = testDirectory.appendingPathComponent(name)
    try Data("fixture".utf8).write(to: url)
    if old { try manager.setAttributes([.creationDate: oldDate], ofItemAtPath: url.path) }
    return url
}
let config = try makeFile("config.ini", old: true)
let rules = try makeFile("path_apps.txt", old: true)
let other = try makeFile("original.png", old: true)
let oldCapture = try makeFile("screenshot_old.png", old: true)
let folder = testDirectory.appendingPathComponent("screenshot_directory.png")
try manager.createDirectory(at: folder, withIntermediateDirectories: true)
let logs = testDirectory.appendingPathComponent("logs")
try manager.createDirectory(at: logs, withIntermediateDirectories: true)
try Data("log".utf8).write(to: logs.appendingPathComponent("clipbridge.log"))
let link = testDirectory.appendingPathComponent("screenshot_link.png")
try manager.createSymbolicLink(at: link, withDestinationURL: other)
for index in 0..<4 { _ = try makeFile("screenshot_recent_\(index).png") }
cleanupOldScreenshots(in: testDirectory, maxAgeDays: 7, maxCount: 2)
precondition(!manager.fileExists(atPath: oldCapture.path), "Expired capture survived")
for url in [config, rules, other, folder, logs] {
    precondition(manager.fileExists(atPath: url.path), "Protected entry removed: \(url)")
}
let destination = try manager.destinationOfSymbolicLink(atPath: link.path)
precondition(destination == other.path, "Symlink removed")
let remaining = try manager.contentsOfDirectory(at: testDirectory, includingPropertiesForKeys: nil)
precondition(remaining.filter { $0.lastPathComponent.hasPrefix("screenshot_recent_") }.count == 2,
             "Count limit failed")
print("PASS: macOS cleanup preserves configs, rules, unrelated files, logs, directories and symlinks")
