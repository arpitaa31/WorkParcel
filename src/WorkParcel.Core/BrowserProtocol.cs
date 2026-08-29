using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkParcel.Core.Browser;

public static class BrowserProtocol
{
    public const int CurrentVersion = 1;
    public const int MaxMessageBytes = 1024 * 1024;
    public const int MaxTabCount = 500;
    public const string HostName = "com.workparcel.browser";
    public const string PipeName = "WorkParcel.Browser.v1";

    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
    {
        "hello", "connection_status", "list_windows", "list_tabs", "refresh_tabs", "request_snapshot", "tab_snapshot",
        "open_tabs", "close_tabs", "open_workparcel", "operation_result", "error", "ping", "pong"
    };

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static JsonElement EmptyPayload() => JsonDocument.Parse("{}").RootElement.Clone();

    public static BrowserMessage Create(string type, string requestId, string? browser, object? payload, string? connectionId = null, string? extensionVersion = null) => new()
    {
        Version = CurrentVersion,
        RequestId = requestId,
        Type = type,
        Browser = browser,
        ConnectionId = connectionId,
        ExtensionVersion = extensionVersion,
        TimestampUtc = DateTimeOffset.UtcNow,
        Payload = JsonSerializer.SerializeToElement(payload ?? new { }, JsonOptions)
    };

    public static byte[] Serialize(BrowserMessage message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (bytes.Length > MaxMessageBytes) throw new InvalidDataException("Browser message exceeds the 1 MiB limit.");
        return bytes;
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> bytes, out BrowserMessage? message, out string error) => TryDeserializeCore(bytes, out message, out error, allowVersionMismatch: false);

    /// <summary>
    /// Validates the envelope shape for the native relay while allowing the app to
    /// reject a newer protocol version and return a useful VERSION MISMATCH error.
    /// </summary>
    public static bool TryDeserializeForRelay(ReadOnlySpan<byte> bytes, out BrowserMessage? message, out string error) => TryDeserializeCore(bytes, out message, out error, allowVersionMismatch: true);

    private static bool TryDeserializeCore(ReadOnlySpan<byte> bytes, out BrowserMessage? message, out string error, bool allowVersionMismatch)
    {
        message = null;
        if (bytes.Length == 0 || bytes.Length > MaxMessageBytes) { error = "Message size is outside the permitted range."; return false; }
        try
        {
            message = JsonSerializer.Deserialize<BrowserMessage>(bytes, JsonOptions);
            if (message is null) { error = "Message was empty."; return false; }
            if (string.IsNullOrWhiteSpace(message.Type) || message.Type.Length > 64) { error = "Message type is invalid."; return false; }
            if (!allowVersionMismatch && message.Version != CurrentVersion) { error = $"Protocol version {message.Version} is not supported."; return false; }
            if (string.IsNullOrWhiteSpace(message.RequestId) || message.RequestId.Length > 120) { error = "Request ID is invalid."; return false; }
            if (message.ConnectionId is not null && (message.ConnectionId.Length == 0 || message.ConnectionId.Length > 120)) { error = "Connection ID is invalid."; return false; }
            if (!KnownTypes.Contains(message.Type)) { error = $"Unknown browser message type '{message.Type}'."; return false; }
            if (message.Browser is not null && message.Browser is not ("chrome" or "edge")) { error = "Browser identity is not supported."; return false; }
            if (message.TimestampUtc == default) { error = "Message timestamp is required."; return false; }
            if (message.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) { error = "Message payload is required."; return false; }
            error = string.Empty; return true;
        }
        catch (JsonException) { error = "Message was not valid JSON."; return false; }
    }

    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4]; await ReadExactAsync(stream, header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxMessageBytes) throw new InvalidDataException("Browser message length is invalid.");
        var body = new byte[length]; await ReadExactAsync(stream, body, cancellationToken); return body;
    }

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        if (body.Length <= 0 || body.Length > MaxMessageBytes) throw new InvalidDataException("Browser message length is invalid.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, cancellationToken); await stream.WriteAsync(body, cancellationToken); await stream.FlushAsync(cancellationToken);
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) throw new EndOfStreamException("Browser connection ended before the full message arrived.");
            offset += read;
        }
    }
}

public sealed record BrowserMessage
{
    public int Version { get; init; }
    public string RequestId { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? Browser { get; init; }
    public string? ConnectionId { get; init; }
    public string? ExtensionVersion { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
    public JsonElement Payload { get; init; }
}

public sealed record BrowserTabData
{
    public string SessionTabId { get; init; } = string.Empty;
    public string SessionWindowId { get; init; } = string.Empty;
    public string WindowGroupKey { get; init; } = string.Empty;
    public string? SessionGroupId { get; init; }
    public string Url { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string Browser { get; init; } = string.Empty;
    public int TabIndex { get; init; }
    public bool Pinned { get; init; }
    public bool Active { get; init; }
    public string? GroupTitle { get; init; }
    public string? GroupColor { get; init; }
    public string? FaviconUrl { get; init; }
    public string? ConnectionId { get; init; }
    public bool Incognito { get; init; }
    public bool CanRestore { get; init; }
    public string? UnsupportedReason { get; init; }
    public DateTimeOffset CapturedAtUtc { get; init; }
    public int? WindowLeft { get; init; }
    public int? WindowTop { get; init; }
    public int? WindowWidth { get; init; }
    public int? WindowHeight { get; init; }
    public string? WindowState { get; init; }
    public bool WindowFocused { get; init; }
}

public sealed record BrowserTabSnapshot
{
    public string Browser { get; init; } = string.Empty;
    public int WindowCount { get; init; }
    public IReadOnlyList<BrowserTabData> Tabs { get; init; } = Array.Empty<BrowserTabData>();
    public DateTimeOffset CapturedAtUtc { get; init; }
}

public sealed record BrowserOperationResult
{
    public string ItemKey { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
