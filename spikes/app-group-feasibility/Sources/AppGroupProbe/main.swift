import Foundation

struct Arguments {
    let role: String
    let appGroup: String
    let action: String
    let value: String?
}

enum ProbeError: Error, CustomStringConvertible {
    case usage
    case missingContainer
    case unsupportedAction(String)
    case emptyRead(URL)

    var description: String {
        switch self {
        case .usage:
            return "usage: AppGroupProbe --role <IME|Settings> --app-group <group.id> --write <value> | --read"
        case .missingContainer:
            return "App Group container is unavailable for the supplied identifier"
        case let .unsupportedAction(action):
            return "unsupported action: \(action)"
        case let .emptyRead(url):
            return "shared file does not exist at \(url.path)"
        }
    }
}

func parseArguments(_ raw: [String]) throws -> Arguments {
    var role: String?
    var appGroup: String?
    var action: String?
    var value: String?
    var index = 1

    while index < raw.count {
        switch raw[index] {
        case "--role":
            index += 1
            guard index < raw.count else { throw ProbeError.usage }
            role = raw[index]
        case "--app-group":
            index += 1
            guard index < raw.count else { throw ProbeError.usage }
            appGroup = raw[index]
        case "--write":
            index += 1
            guard index < raw.count else { throw ProbeError.usage }
            action = "write"
            value = raw[index]
        case "--read":
            action = "read"
        default:
            throw ProbeError.usage
        }
        index += 1
    }

    guard let role, let appGroup, let action else {
        throw ProbeError.usage
    }

    return Arguments(role: role, appGroup: appGroup, action: action, value: value)
}

func sharedFileURL(for appGroup: String) throws -> URL {
    guard let container = FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: appGroup) else {
        throw ProbeError.missingContainer
    }

    try FileManager.default.createDirectory(at: container, withIntermediateDirectories: true)
    return container.appendingPathComponent("shared-state.txt", isDirectory: false)
}

func run() throws {
    let arguments = try parseArguments(CommandLine.arguments)
    let fileURL = try sharedFileURL(for: arguments.appGroup)

    switch arguments.action {
    case "write":
        let value = arguments.value ?? ""
        let payload = "\(arguments.role):\(value)\n"
        try payload.write(to: fileURL, atomically: true, encoding: .utf8)
        print("WRITE_OK role=\(arguments.role) bytes=\(payload.utf8.count)")
    case "read":
        guard FileManager.default.fileExists(atPath: fileURL.path) else {
            throw ProbeError.emptyRead(fileURL)
        }
        let payload = try String(contentsOf: fileURL, encoding: .utf8)
            .trimmingCharacters(in: .newlines)
        print("READ_OK role=\(arguments.role) value=\(payload)")
    default:
        throw ProbeError.unsupportedAction(arguments.action)
    }
}

do {
    try run()
} catch {
    fputs("ERROR \(error)\n", stderr)
    exit(1)
}
