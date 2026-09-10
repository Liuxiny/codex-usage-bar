import Foundation
import UsageCore

final class AppServer: @unchecked Sendable {
    private let lock = NSLock()
    private var process: Process?
    private var input: FileHandle?
    private var nextID = 0
    private var epoch = UUID()
    private let arguments: [String]
    init(arguments: [String] = ["app-server", "--listen", "stdio://"]) { self.arguments = arguments }
    private var pending: [Int: (Result<[String: Any], Error>) -> Void] = [:]
    var updated: (() -> Void)?
    var exited: (() -> Void)?
    var alive: Bool { lock.lock(); defer { lock.unlock() }; return process?.isRunning == true }

    static func candidates(custom: String) -> [URL] {
        let env = ProcessInfo.processInfo.environment
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let explicit = custom.isEmpty ? env["CODEX_EXECUTABLE"] ?? "" : custom
        if !explicit.isEmpty { return [URL(fileURLWithPath: (explicit as NSString).expandingTildeInPath)] }
        let paths = ["/Applications/Codex.app/Contents/Resources/codex", home + "/Applications/Codex.app/Contents/Resources/codex",
                     "/opt/homebrew/bin/codex", "/usr/local/bin/codex", home + "/.local/bin/codex"] +
            (env["PATH"] ?? "").split(separator: ":").map { String($0) + "/codex" }
        var seen = Set<String>()
        return paths.filter { seen.insert($0).inserted && FileManager.default.isExecutableFile(atPath: $0) }.map { URL(fileURLWithPath: $0) }
    }

    func start(custom: String) async throws {
        stop()
        let token = currentEpoch()
        for candidate in Self.candidates(custom: custom) {
            guard currentEpoch() == token else { throw CancellationError() }
            do {
                let child = Process(), stdinPipe = Pipe(), stdoutPipe = Pipe()
                child.executableURL = candidate; child.arguments = arguments
                var environment = ProcessInfo.processInfo.environment
                environment["PATH"] = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:" + (environment["PATH"] ?? "")
                child.environment = environment
                child.standardInput = stdinPipe; child.standardOutput = stdoutPipe
                child.standardError = FileHandle.nullDevice // Never persist auth/server response text.
                child.terminationHandler = { [weak self, weak child] _ in
                    guard let self, let child else { return }
                    self.lock.lock(); let current = self.process === child; self.lock.unlock()
                    if current { self.failAll(UsageError.message("App Server exited")); self.exited?() }
                }
                try child.run()
                guard install(child, input: stdinPipe.fileHandleForWriting, token: token) else {
                    child.terminate(); throw CancellationError()
                }
                DispatchQueue.global(qos: .utility).async { [weak self] in
                    var buffer = Data()
                    while true {
                        let data = stdoutPipe.fileHandleForReading.availableData
                        if data.isEmpty { break }
                        buffer.append(data)
                        while let newline = buffer.firstIndex(of: 10) {
                            let line = Data(buffer[..<newline]); buffer.removeSubrange(...newline)
                            if line.count > 4 * 1024 * 1024 { child.terminate(); return }
                            self?.receive(line, from: child)
                        }
                        if buffer.count > 4 * 1024 * 1024 { child.terminate(); break }
                    }
                }
                _ = try await request("initialize", params: ["clientInfo": ["name": "codex-usage-bar", "title": "Codex Usage Bar", "version": "0.7.6"], "capabilities": ["experimentalApi": true]])
                guard currentEpoch() == token else { throw CancellationError() }
                try write(["method": "initialized"])
                return
            } catch {
                guard currentEpoch() == token else { throw CancellationError() }
                shutdown(invalidate: false)
            }
        }
        throw UsageError.message("Cannot connect to Codex CLI. Set its path and run codex login.")
    }

    private func currentEpoch() -> UUID { lock.lock(); defer { lock.unlock() }; return epoch }
    private func install(_ child: Process, input: FileHandle, token: UUID) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard epoch == token else { return false }
        process = child; self.input = input; return true
    }
    func request(_ method: String, params: [String: Any]? = nil) async throws -> [String: Any] {
        try await withCheckedThrowingContinuation { continuation in
            lock.lock(); nextID += 1; let id = nextID
            pending[id] = { continuation.resume(with: $0) }; lock.unlock()
            var message: [String: Any] = ["id": id, "method": method]
            if let params { message["params"] = params }
            do { try write(message) } catch { complete(id, .failure(error)) }
            DispatchQueue.global().asyncAfter(deadline: .now() + 10) { [weak self] in
                self?.complete(id, .failure(UsageError.message("App Server request timed out")))
            }
        }
    }
    private func write(_ message: [String: Any]) throws {
        var data = try JSONSerialization.data(withJSONObject: message); data.append(10)
        lock.lock(); defer { lock.unlock() }
        guard process?.isRunning == true, let input else { throw UsageError.message("App Server disconnected") }
        try input.write(contentsOf: data)
    }
    private func receive(_ data: Data, from child: Process) {
        lock.lock(); let current = process === child; lock.unlock()
        guard current, let map = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return }
        if let id = map["id"] as? Int {
            if map["error"] != nil { complete(id, .failure(UsageError.message("App Server rejected the request; check login and CLI version"))) }
            else { complete(id, .success((map["result"] as? [String: Any]) ?? [:])) }
        } else if map["method"] as? String == "account/rateLimits/updated" { updated?() }
    }
    private func complete(_ id: Int, _ result: Result<[String: Any], Error>) {
        lock.lock(); let callback = pending.removeValue(forKey: id); lock.unlock(); callback?(result)
    }
    private func failAll(_ error: Error) {
        lock.lock(); let callbacks = Array(pending.values); pending.removeAll(); lock.unlock()
        callbacks.forEach { $0(.failure(error)) }
    }
    func stop() { shutdown(invalidate: true) }
    private func shutdown(invalidate: Bool) {
        lock.lock()
        if invalidate { epoch = UUID() }
        let child = process; process = nil; let stream = input; input = nil; lock.unlock()
        try? stream?.close()
        if let child, child.isRunning {
            child.terminate()
            DispatchQueue.global().asyncAfter(deadline: .now() + 2) { if child.isRunning { kill(child.processIdentifier, SIGKILL) } }
        }
        failAll(UsageError.message("App Server stopped"))
    }
    deinit { stop() }
}
