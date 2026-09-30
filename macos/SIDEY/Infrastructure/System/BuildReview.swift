import AppKit
import Darwin

/// Debug launch proof comes from the CLI ticket or the signed Xcode build receipt.
/// The executable's compiled build ID, never a disk-only bundle version, identifies it.
@MainActor
final class BuildReview {
    static let shared = BuildReview()
    private static var executablePath: String? {
        guard let path = Bundle.main.executableURL?.path, let canonical = realpath(path, nil) else { return nil }
        defer { free(canonical) }
        return String(cString: canonical)
    }

    private var ticket: Ticket?
    private var readinessTask: Task<Void, Never>?

    private struct Ticket: Codable {
        let build_id: String
        let commit: String
        let input_hash: String
        let target: String
        let configuration: String
        let bundle_id: String
        let session: String
        let executable: String
        let issued_at: Double
    }

    private var directory: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("SIDEY/BuildReview", isDirectory: true)
    }

    func validateLaunch() -> Bool {
        #if DEBUG
        let path = directory.appendingPathComponent("\(SideyBuildStamp.buildID).ticket.json")
        do {
            let embedded = !FileManager.default.fileExists(atPath: path.path)
            let source = embedded
                ? Bundle.main.url(forResource: "SideyBuildReceipt", withExtension: "json") ?? path
                : path
            let data = try Data(contentsOf: source)
            if !embedded {
                // A consumed CLI ticket cannot authorize a second launch.
                try FileManager.default.removeItem(at: path)
            }
            let candidate = try JSONDecoder().decode(Ticket.self, from: data)
            let age = Date().timeIntervalSince1970 - candidate.issued_at
            let checks: [String: Bool] = [
                "age": age >= 0 && age < 60,
                "build": candidate.build_id == SideyBuildStamp.buildID,
                "commit": candidate.commit == SideyBuildStamp.commit,
                "inputs": candidate.input_hash == SideyBuildStamp.inputHash,
                "target": candidate.target == SideyBuildStamp.target,
                "configuration": candidate.configuration == SideyBuildStamp.configuration,
                "bundle": candidate.bundle_id == Bundle.main.bundleIdentifier,
                "executable": candidate.executable == Self.executablePath,
            ]
            let failed = checks.filter { !$0.value }.keys.sorted()
            guard failed.isEmpty else {
                NSLog("SIDEY build review: mismatched fields %@", failed.joined(separator: ", "))
                throw CocoaError(.fileReadCorruptFile)
            }
            if embedded {
                // The bundle is signed and read-only. Consume its proof in the
                // app's writable container so Run Without Building cannot reuse it.
                try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
                let consumed = directory.appendingPathComponent("\(candidate.session).consumed")
                let descriptor = open(consumed.path, O_WRONLY | O_CREAT | O_EXCL, 0o600)
                guard descriptor >= 0 else {
                    throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno))
                }
                close(descriptor)
            }
            ticket = candidate
            return true
        } catch {
            NSLog("SIDEY build review: launch ticket rejected: %@", String(describing: error))
            let alert = NSAlert()
            alert.messageText = InternalL10n.text("build_review.error.unverified_source")
            alert.informativeText = InternalL10n.text("build_review.error.relaunch_instructions")
            alert.addButton(withTitle: InternalL10n.text("common.quit"))
            alert.runModal()
            return false
        }
        #else
        return true
        #endif
    }

    func observeReadyWindow() {
        #if DEBUG
        guard let ticket else { return }
        readinessTask = Task { [weak self] in
            for _ in 0..<200 {
                guard !Task.isCancelled, let self else { return }
                // Check this application's normal startup window, without screen capture.
                if let window = NSApplication.shared.windows.first(where: {
                    $0.isVisible && $0.contentView != nil && $0.level == .normal
                }) {
                    window.displayIfNeeded()
                    let value: [String: Any] = [
                        "session": ticket.session,
                        "build_id": SideyBuildStamp.buildID,
                        "commit": SideyBuildStamp.commit,
                        "input_hash": SideyBuildStamp.inputHash,
                        "target": SideyBuildStamp.target,
                        "configuration": SideyBuildStamp.configuration,
                        "executable": Self.executablePath ?? "",
                        "pid": ProcessInfo.processInfo.processIdentifier,
                        "window_ready": true,
                    ]
                    if let data = try? JSONSerialization.data(withJSONObject: value) {
                        try? data.write(to: self.directory.appendingPathComponent("\(ticket.session).ready.json"), options: .atomic)
                    }
                    return
                }
                try? await Task.sleep(for: .milliseconds(100))
            }
        }
        #endif
    }

    func stop() {
        readinessTask?.cancel()
        readinessTask = nil
    }
}
