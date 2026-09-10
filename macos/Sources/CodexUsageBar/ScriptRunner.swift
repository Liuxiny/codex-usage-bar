import Foundation
import JavaScriptCore
import Darwin
import UsageCore

// A disposable helper process owns each JS context. The parent enforces a hard
// deadline even for while(true), and never exposes host objects to JavaScript.
enum ScriptRunner {
    static let limit = 2 * 1024 * 1024
    static func worker() -> Never {
        DispatchQueue.global(qos: .utility).async {
            while true {
                var usage = rusage()
                if getrusage(RUSAGE_SELF, &usage) == 0 && usage.ru_maxrss > 256 * 1024 * 1024 { exit(4) }
                usleep(20_000)
            }
        }
        autoreleasepool {
            let data = FileHandle.standardInput.readDataToEndOfFile()
            guard data.count <= limit, let script = String(data: data, encoding: .utf8), let context = JSContext() else { exit(2) }
            context.exceptionHandler = { _, _ in }
            let value = context.evaluateScript(script)
            guard context.exception == nil, let text = value?.toString(), let output = text.data(using: .utf8), output.count <= limit else { exit(3) }
            try? FileHandle.standardOutput.write(contentsOf: output)
        }
        exit(0)
    }

    static func evaluate(_ script: String) async throws -> Any {
        guard script.utf8.count <= limit else { throw UsageError.message("Usage script exceeds 2 MiB") }
        return try await withCheckedThrowingContinuation { continuation in
            DispatchQueue.global(qos: .utility).async {
                do { continuation.resume(returning: try evaluateBlocking(script)) }
                catch { continuation.resume(throwing: error) }
            }
        }
    }

    private static func evaluateBlocking(_ script: String) throws -> Any {
            let child = Process(), input = Pipe(), output = Pipe()
            child.executableURL = Bundle.main.executableURL ?? URL(fileURLWithPath: CommandLine.arguments[0])
            child.arguments = ["--script-worker"]
            child.standardInput = input; child.standardOutput = output; child.standardError = FileHandle.nullDevice
            let done = DispatchSemaphore(value: 0)
            let result = ScriptOutput()
            try child.run()
            DispatchQueue.global().async {
                // Write concurrently so a large script cannot fill the pipe on the watchdog thread.
                try? input.fileHandleForWriting.write(contentsOf: Data(script.utf8))
                try? input.fileHandleForWriting.close()
            }
            DispatchQueue.global().async {
                while true {
                    let data = output.fileHandleForReading.availableData
                    if data.isEmpty { break }
                    if !result.append(data) { kill(child.processIdentifier, SIGKILL); break }
                }
                done.signal()
            }
            if done.wait(timeout: .now() + 5) == .timedOut {
                kill(child.processIdentifier, SIGKILL)
                child.waitUntilExit()
                throw UsageError.message("Usage script exceeded 5 seconds")
            }
            child.waitUntilExit()
            guard child.terminationStatus == 0, let data = result.data else { throw UsageError.message("Usage script failed") }
            return try JSONSerialization.jsonObject(with: data, options: .fragmentsAllowed)
    }

    static func code(_ provider: Provider) -> String {
        var code = provider.code.trimmingCharacters(in: .whitespacesAndNewlines)
        while code.hasSuffix(";") { code.removeLast() }
        for (key, value) in [("baseUrl", provider.baseUrl), ("apiKey", provider.apiKey), ("accessToken", provider.accessToken), ("userId", provider.userId)] {
            // Matches CC Switch's literal placeholder substitution contract.
            code = code.replacingOccurrences(of: "{{\(key)}}", with: value)
        }
        return code
    }
    static func request(_ provider: Provider) async throws -> [String: Any] {
        guard provider.enabled, !provider.code.isEmpty else { throw UsageError.message("Enable a usage script in CC Switch") }
        let value = try await evaluate("JSON.stringify((\(code(provider))).request)")
        guard let request = value as? [String: Any] else { throw UsageError.message("Script must return request + extractor") }
        return request
    }
    static func extract(_ provider: Provider, response: Data) async throws -> [UsageRow] {
        let json = try JSONSerialization.jsonObject(with: response)
        let normalized = try JSONSerialization.data(withJSONObject: json, options: [.fragmentsAllowed])
        let body = String(decoding: normalized, as: UTF8.self)
        let value = try await evaluate("JSON.stringify((\(code(provider))).extractor(\(body)))")
        if let array = value as? [Any], array.isEmpty || array.count > 32 || array.contains(where: { !($0 is [String: Any]) }) {
            throw UsageError.message("Extractor must return 1–32 usage rows")
        }
        let rows = UsageParser.thirdParty(value, response: (json as? [String: Any]) ?? [:])
        guard !rows.isEmpty else { throw UsageError.message("Extractor returned no usage rows") }
        return rows
    }
}

private final class ScriptOutput: @unchecked Sendable {
    private let lock = NSLock()
    private var buffer = Data()
    private var overflow = false
    func append(_ data: Data) -> Bool {
        lock.lock(); defer { lock.unlock() }
        if buffer.count + data.count > ScriptRunner.limit { overflow = true; return false }
        buffer.append(data); return true
    }
    var data: Data? { lock.lock(); defer { lock.unlock() }; return overflow ? nil : buffer }
}

final class UsageHTTP: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }

    func fetch(_ definition: [String: Any], timeout: Int) async throws -> Data {
        guard let text = definition["url"] as? String, let url = URL(string: text),
              ["https", "http"].contains(url.scheme?.lowercased() ?? ""), url.host != nil,
              url.user == nil, url.password == nil else { throw UsageError.message("Invalid HTTP usage URL") }
        var request = URLRequest(url: url, timeoutInterval: TimeInterval(min(30, max(2, timeout))))
        request.httpMethod = ((definition["method"] as? String) ?? "GET").uppercased()
        for (key, value) in (definition["headers"] as? [String: String]) ?? [:] { request.setValue(value, forHTTPHeaderField: key) }
        if let body = definition["body"] as? String { request.httpBody = Data(body.utf8) }
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil; config.urlCredentialStorage = nil; config.urlCache = nil
        config.timeoutIntervalForResource = TimeInterval(min(30, max(2, timeout)))
        let session = URLSession(configuration: config, delegate: self, delegateQueue: nil)
        defer { session.invalidateAndCancel() }
        let (bytes, response) = try await session.bytes(for: request)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            throw UsageError.message("Usage HTTP request failed (redirects are disabled)")
        }
        guard response.expectedContentLength <= 1048576 else { throw UsageError.message("Usage response exceeds 1 MiB") }
        var data = Data()
        for try await byte in bytes {
            guard data.count < 1048576 else { throw UsageError.message("Usage response exceeds 1 MiB") }
            data.append(byte)
        }
        return data
    }
}
