import AppKit

@main
struct ApplicationMain {
    @MainActor static func main() {
        if CommandLine.arguments.contains("--script-worker") { ScriptRunner.worker() }
        if CommandLine.arguments.contains("--mock-app-server") { SelfTest.mockServer() }
        if CommandLine.arguments.contains("--self-test") { SelfTest.run() }
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        let controller = Controller()
        app.delegate = controller
        withExtendedLifetime(controller) { app.run() }
    }
}
