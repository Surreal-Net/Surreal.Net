using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Threading.Channels;

using Microsoft.IO;

using SurrealDB.Common;

namespace SurrealDB.Ws;

/// <summary>Receives messages from a websocket server and passes them to a channel</summary>
public sealed class WsTxProducer : IDisposable {
    private readonly ClientWebSocket _ws;
    private readonly ChannelWriter<WsMessageReader> _out;
    private readonly RecyclableMemoryStreamManager _memoryManager;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _execute;

    private readonly int _blockSize;
    private readonly int _messageSize;

    public WsTxProducer(ClientWebSocket ws, ChannelWriter<WsMessageReader> @out, RecyclableMemoryStreamManager memoryManager, int blockSize, int messageSize) {
        _ws = ws;
        _out = @out;
        _memoryManager = memoryManager;
        _blockSize = blockSize;
        _messageSize = messageSize;
    }

    private async Task Execute(CancellationToken ct) {
        Debug.Assert(ct.CanBeCanceled);
        while (!ct.IsCancellationRequested) {
            var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
            try {
                await Produce(ct, buffer).Inv();
            } finally {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private async Task Produce(CancellationToken ct, byte[] buffer) {
        // receive the first part
        var result = await _ws.ReceiveAsync(buffer, ct).Inv();
        // create a new message with a RecyclableMemoryStream
        // use buffer instead of the build the builtin IBufferWriter, bc of thread safely issues related to locking
        WsMessageReader msg = new(_memoryManager, _messageSize);
        // begin adding the message to the output
        await _out.WriteAsync(msg, ct).Inv();

        await msg.AppendResultAsync(buffer, result, ct).Inv();

        while (!result.EndOfMessage && !ct.IsCancellationRequested) {
            // receive more parts
            result = await _ws.ReceiveAsync(buffer, ct).Inv();
            await msg.AppendResultAsync(buffer, result, ct).Inv();
        }

        // finish adding the message to the output
        //await writeOutput;
    }


    [MemberNotNullWhen(true, nameof(_cts)), MemberNotNullWhen(true, nameof(_execute))]
    public bool Connected => _cts is not null & _execute is not null;

    public void Open() {
        ThrowIfConnected();
        lock (_lock) {
            if (Connected) {
                return;
            }
            _cts = new();
            _execute = Execute(_cts.Token);
        }
    }

    public async Task Close() {
        Task task;
        ThrowIfDisconnected();
        lock (_lock) {
            if (!Connected) {
                return;
            }
            task = _execute;
            _cts.Cancel();
            _cts.Dispose(); // not relly needed here
            _cts = null;
            _execute = null;
        }

        try {
            await task.Inv();
        } catch (OperationCanceledException) {
            // expected on close using cts
        } catch (WebSocketException) {
            // expected when the socket is closed before the receiver
        }
    }

    [MemberNotNull(nameof(_cts)), MemberNotNull(nameof(_execute))]
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

    public void Dispose() {
        _cts?.Cancel();
        _cts?.Dispose();
        _out.TryComplete();
    }
}
