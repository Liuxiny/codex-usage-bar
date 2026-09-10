import Foundation
import Network
import UsageCore

// Only reached by --self-test. Binds loopback and uses synthetic credentials.
enum HTTPFixture {
    static func test() async throws {
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: .any)
        let listener = try NWListener(using: parameters)
        let queue = DispatchQueue(label: "CodexBar.HTTPFixture")
        let ready = DispatchSemaphore(value: 0)
        listener.stateUpdateHandler = { state in
            if case .ready = state { ready.signal() }
            if case .failed = state { ready.signal() }
        }
        listener.newConnectionHandler = { connection in
            connection.start(queue: queue)
            receive(connection, buffer: Data())
        }
        listener.start(queue: queue)
        defer { listener.cancel() }
        let started = await Task.detached { ready.wait(timeout: .now() + 5) == .success }.value
        guard started, let port = listener.port else { throw UsageError.message("Loopback listener failed") }
        let base = "http://127.0.0.1:\(port.rawValue)"
        let data = try await UsageHTTP().fetch(["url": base + "/ok", "headers": ["Authorization": "Bearer synthetic"]], timeout: 2)
        try SelfTest.check(String(decoding: data, as: UTF8.self) == "{\"ok\":true}", "HTTP response/header contract")
        for path in ["/redirect", "/large"] {
            var failed = false
            do { _ = try await UsageHTTP().fetch(["url": base + path], timeout: 2) } catch { failed = true }
            try SelfTest.check(failed, "HTTP must reject \(path)")
        }
    }
    private static func receive(_ connection: NWConnection, buffer: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 8192) { data, _, complete, error in
            var request = buffer; request.append(data ?? Data())
            guard error == nil, request.count <= 16384 else { connection.cancel(); return }
            let text = String(decoding: request, as: UTF8.self)
            if !text.contains("\r\n\r\n") {
                if complete { connection.cancel() } else { receive(connection, buffer: request) }
                return
            }
            let response: String
            if text.hasPrefix("GET /redirect ") {
                response = "HTTP/1.1 302 Found\r\nLocation: /ok\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
            } else if text.hasPrefix("GET /large ") {
                response = "HTTP/1.1 200 OK\r\nContent-Length: 1048577\r\nConnection: close\r\n\r\n"
            } else if text.lowercased().contains("authorization: bearer synthetic") {
                response = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 11\r\nConnection: close\r\n\r\n{\"ok\":true}"
            } else {
                response = "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
            }
            connection.send(content: Data(response.utf8), completion: .contentProcessed { _ in connection.cancel() })
        }
    }
}
