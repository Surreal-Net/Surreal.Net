using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

using Microsoft.IO;

using SurrealDB.Common;
using SurrealDB.Json;

namespace SurrealDB.Ws;

/// <summary>The client used to connect to the Surreal server via JSON RPC.</summary>
public sealed class WsClient : IDisposable {
    private static readonly Lazy<RecyclableMemoryStreamManager> s_manager = new(static () => new());
    // Do not get any funny ideas and fill this fucker up.
    private static readonly List<object?> s_emptyList = new();

    private readonly ClientWebSocket _ws = new();
    private readonly RecyclableMemoryStreamManager _memoryManager;
    private readonly WsRxClient _rx;
    private readonly WsTxClient _tx;

    private readonly int _idBytes;

    public WsClient()
        : this(WsClientOptions.Default) {
    }

    public WsClient(WsClientOptions options) {
        _memoryManager = options.MemoryManager ?? new();
        _rx = new(_ws, Channel.CreateBounded<BufferStreamReader>(options.ChannelRxMessagesMax));
        _tx = new(_ws, Channel.CreateBounded<WsMessageReader>(options.ChannelTxMessagesMax), _memoryManager, options.HeaderBytesMax);
        _idBytes = options.IdBytes;
    }

    /// <summary>Indicates whether the client is connected or not.</summary>
    public bool Connected => _ws.State == WebSocketState.Open;

    public WebSocketState State => _ws.State;

    /// <summary>Opens the connection to the Surreal server.</summary>
    public async Task OpenAsync(Uri url, CancellationToken ct = default) {
        ThrowIfConnected();
        await _ws.ConnectAsync(url, ct);
    }

    /// <summary>
    ///     Closes the connection to the Surreal server.
    /// </summary>
    public async Task CloseAsync(CancellationToken ct = default) {
        await _ws.CloseAsync(WebSocketCloseStatus.Empty, "Orderly connection close", ct);
    }

    /// <inheritdoc cref="IDisposable" />
    public void Dispose() {
        _rx.Dispose();
        _tx.Dispose();
        _ws.Dispose();
    }

    /// <summary>
    ///     Sends the specified request to the Surreal server, and returns the response.
    /// </summary>
    public async Task<Response> Send(Request req, CancellationToken ct = default) {
        ThrowIfDisconnected();
        req.id ??= HeaderHelper.GetRandomId(_idBytes);
        req.parameters ??= s_emptyList;

        // listen for the response
        ResponseHandler handler = new(req.id, ct);
        if (!_tx.TryRegister(handler)) {
            return default;
        }
        // send request
        await using (var stream = await SerializeAsync(req, ct).Inv()) {
            await _rx.SendAsync(stream);
        }
        // await response
        var response = await handler.Task.Inv();
        // validate header
        var header = response.Header.Response;
        if (!response.Header.Notify.IsDefault) {
            ThrowExpectResponseGotNotify();
        }
        if (header.IsDefault) {
            ThrowInvalidResponse();
        }

        // move position stream beyond header and deserialize message body
        response.Message.Position = response.Header.BytesLength;
        // deserialize body
        var body = await JsonSerializer.DeserializeAsync<JsonDocument>(response.Message, SerializerOptions.Shared, ct).Inv();
        if (body is null) {
            ThrowInvalidResponse();
        }
        return new(header.id, header.err, ExtractResult(body));
    }

    private static async Task<RecyclableMemoryStream> SerializeAsync(Request req, CancellationToken ct) {
        RecyclableMemoryStream stream = new(s_manager.Value);

        await JsonSerializer.SerializeAsync(stream, req, SerializerOptions.Shared, ct).Inv();
        // position = Length = EndOfMessage -> position = 0
        stream.Position = 0;
        return stream;
    }

    private static JsonElement ExtractResult(JsonDocument root) {
        return root.RootElement.TryGetProperty("result", out JsonElement res) ? res : default;
    }

    private void ThrowIfDisconnected() {
        if (!Connected) {
            throw new InvalidOperationException("The connection is not open.");
        }
    }

    private void ThrowIfConnected() {
        if (Connected) {
            throw new InvalidOperationException("The connection is already open");
        }
    }

    [DoesNotReturn]
    private static void ThrowExpectResponseGotNotify() {
        throw new InvalidOperationException("Expected a response, got a notification");
    }

    [DoesNotReturn]
    private static void ThrowInvalidResponse() {
        throw new InvalidOperationException("Invalid response");
    }

    public record struct Request(
        string? id,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault),]
        bool async,
        string? method,
        [property: JsonPropertyName("params"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault),]
        List<object?>? parameters);

    public readonly record struct Response(
        string? id,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault),]
        Error error,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault),]
        JsonElement result);

    public readonly record struct Error(
        int code,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault),]
        string? message);


    public record struct Notify(
        string? id,
        string? method,
        [property: JsonPropertyName("params"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault),]
        List<object?>? parameters);
}
