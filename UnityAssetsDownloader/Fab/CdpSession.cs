using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

/// <summary>
/// Самый простой клиент протокола DevTools (CDP): отправить команду — дождаться ответа.
///
/// Нарочно без Puppeteer. Puppeteer при подключении к вкладке включает домены Runtime,
/// Page и Network и держит их включёнными, а это видно самой странице. Здесь вкладке
/// посылаются только разовые команды (перейти по адресу, выполнить скрипт, нажать мышью),
/// и браузер остаётся тем самым обычным Chrome, который человек открыл бы сам.
/// </summary>
internal sealed class CdpSession : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _receiveLoop;
    private int _nextId;
    private string? _closeReason;

    /// <summary>Соединение живо: браузер не закрыт и вкладка на месте.</summary>
    public bool IsOpen => _socket.State == WebSocketState.Open && _closeReason is null;

    public static async Task<CdpSession> ConnectAsync(string webSocketUrl, TimeSpan timeout)
    {
        var session = new CdpSession();
        // Пока человек входит в Epic, программа минутами молчит — соединение не должно засыпать.
        session._socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var cts = new CancellationTokenSource(timeout);
        await session._socket.ConnectAsync(new Uri(webSocketUrl), cts.Token);
        session._receiveLoop = Task.Run(session.ReceiveLoopAsync);
        return session;
    }

    /// <summary>
    /// Команда CDP. Ответ — поле result (или пустой элемент). Ошибка браузера — CdpException,
    /// закрытое окно — CdpException с IsDisconnected.
    /// </summary>
    public async Task<JsonElement> SendAsync(string method, object? parameters = null, TimeSpan? timeout = null)
    {
        if (!IsOpen)
        {
            throw new CdpException($"{method}: связь с браузером потеряна ({_closeReason ?? "окно закрыто"})", disconnected: true);
        }

        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;

        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new Dictionary<string, object?>()
            });

            await _sendLock.WaitAsync();
            try
            {
                await _socket.SendAsync(payload, WebSocketMessageType.Text, true, _stop.Token);
            }
            finally
            {
                _sendLock.Release();
            }

            var limit = timeout ?? TimeSpan.FromSeconds(30);
            var finished = await Task.WhenAny(reply.Task, Task.Delay(limit));
            if (finished != reply.Task)
            {
                throw new TimeoutException($"{method}: браузер не ответил за {limit.TotalSeconds:0} с");
            }

            return await reply.Task;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            throw new CdpException($"{method}: связь с браузером потеряна ({ex.Message})", disconnected: true);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();

        try
        {
            while (!_stop.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult part;
                do
                {
                    part = await _socket.ReceiveAsync(buffer, _stop.Token);
                    if (part.MessageType == WebSocketMessageType.Close)
                    {
                        FailAll("браузер закрыл соединение");
                        return;
                    }

                    message.Write(buffer, 0, part.Count);
                }
                while (!part.EndOfMessage);

                Dispatch(message.GetBuffer().AsMemory(0, (int)message.Length));
            }
        }
        catch (Exception ex)
        {
            FailAll(ex.Message);
            return;
        }

        FailAll("соединение закрыто");
    }

    /// <summary>Ответы раздаются по номерам команд. События не нужны: домены не включаются.</summary>
    private void Dispatch(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id) ||
                !_pending.TryGetValue(id, out var reply))
            {
                return;
            }

            if (root.TryGetProperty("error", out var error))
            {
                var text = error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString();
                reply.TrySetException(new CdpException(text ?? "ошибка браузера"));
            }
            else
            {
                reply.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            }
        }
        catch (JsonException)
        {
            // Непонятное сообщение браузера пропускаем: ответ на свою команду всё равно дождёмся или нет.
        }
    }

    private void FailAll(string reason)
    {
        _closeReason ??= reason;
        foreach (var reply in _pending.Values)
        {
            reply.TrySetException(new CdpException($"связь с браузером потеряна ({reason})", disconnected: true));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _closeReason ??= "соединение закрыто программой";
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
            }
        }
        catch
        {
            // Браузер мог уже закрыться сам — это не ошибка.
        }

        _stop.Cancel();
        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Цикл приёма завершится сам вместе с сокетом.
            }
        }

        _socket.Dispose();
        _stop.Dispose();
        _sendLock.Dispose();
    }
}

internal sealed class CdpException(string message, bool disconnected = false) : Exception(message)
{
    /// <summary>Окно браузера закрыто или соединение с ним оборвалось — дальше работать не с чем.</summary>
    public bool IsDisconnected { get; } = disconnected;
}
