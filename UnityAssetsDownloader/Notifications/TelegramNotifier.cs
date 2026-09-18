using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Сообщения от Telegram-бота: что добавлено, что куплено по промокоду, что пошло не так.
/// И обратно: бот умеет дождаться ответа — например, кода подтверждения входа в Unity,
/// когда программа работает на сервере и спросить в консоли некого.
///
/// Куда писать, бот узнаёт сам: достаточно один раз написать ему любое сообщение.
/// Номер чата запоминается в файле. Если api.telegram.org заблокирован, запросы идут
/// через тот же прокси, что и чтение каналов.
/// </summary>
internal sealed class TelegramNotifier
{
    private const string ApiBase = "https://api.telegram.org/bot";

    private readonly string _token;
    private readonly string _chatIdFile;
    private readonly Func<string?> _proxyProvider;
    private readonly AppLogger _logger;
    private string? _chatId;
    private string? _workingProxy;
    private bool _noChatWarned;
    private bool _tokenRejected;

    public TelegramNotifier(string token, string? chatId, string chatIdFile, Func<string?> proxyProvider, AppLogger logger)
    {
        _token = token.Trim();
        _chatId = string.IsNullOrWhiteSpace(chatId) ? null : chatId.Trim();
        _chatIdFile = chatIdFile;
        _proxyProvider = proxyProvider;
        _logger = logger;
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_token);

    /// <summary>Отправляет сообщение. Ошибки не пробрасывает: оповещение не должно ронять программу.</summary>
    public async Task<bool> SendAsync(string text)
    {
        if (!Enabled)
        {
            return false;
        }

        try
        {
            var chat = await ResolveChatIdAsync();
            if (chat is null)
            {
                return false;
            }

            var payload = new Dictionary<string, object>
            {
                ["chat_id"] = chat,
                ["text"] = text.Length > 4000 ? text[..4000] + "…" : text,
                ["disable_web_page_preview"] = true
            };

            return await CallAsync("sendMessage", payload) is not null;
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Бот] Не удалось отправить сообщение: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Ждёт от пользователя сообщение, подходящее под pattern (например, код из цифр).
    /// Сообщения, пришедшие раньше вызова, ответом не считаются.
    /// Возвращает совпавший текст или null, если за timeout ответа не было.
    /// </summary>
    public async Task<string?> WaitForReplyAsync(Regex pattern, TimeSpan timeout)
    {
        if (!Enabled)
        {
            return null;
        }

        var chat = await ResolveChatIdAsync();
        if (chat is null)
        {
            return null;
        }

        // Пропускаем всё, что пришло до вопроса.
        long offset = 0;
        var latest = await CallAsync("getUpdates", new Dictionary<string, object> { ["offset"] = -1, ["timeout"] = 0 });
        if (latest is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() > 0)
        {
            offset = arr[arr.GetArrayLength() - 1].GetProperty("update_id").GetInt64() + 1;
        }

        var stopAt = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < stopAt)
        {
            var updates = await CallAsync(
                "getUpdates",
                new Dictionary<string, object> { ["offset"] = offset, ["timeout"] = 25 },
                TimeSpan.FromSeconds(40));

            if (updates is not { ValueKind: JsonValueKind.Array } list)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                continue;
            }

            foreach (var update in list.EnumerateArray())
            {
                offset = update.GetProperty("update_id").GetInt64() + 1;
                if (!update.TryGetProperty("message", out var message) ||
                    !message.TryGetProperty("text", out var textElement) ||
                    ChatIdOf(message) != chat)
                {
                    continue;
                }

                var match = pattern.Match(textElement.GetString() ?? string.Empty);
                if (match.Success)
                {
                    return match.Value;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Номер чата: из настроек, из файла или из последнего сообщения, которое написали боту.
    /// </summary>
    private async Task<string?> ResolveChatIdAsync()
    {
        if (_chatId is not null)
        {
            return _chatId;
        }

        try
        {
            if (File.Exists(_chatIdFile))
            {
                var saved = (await File.ReadAllTextAsync(_chatIdFile)).Trim();
                if (saved.Length > 0)
                {
                    return _chatId = saved;
                }
            }
        }
        catch
        {
            // Не прочитали файл — спросим у Telegram.
        }

        var updates = await CallAsync("getUpdates", new Dictionary<string, object> { ["timeout"] = 0 });
        if (updates is { ValueKind: JsonValueKind.Array } list)
        {
            string? found = null;
            foreach (var update in list.EnumerateArray())
            {
                if (update.TryGetProperty("message", out var message))
                {
                    found = ChatIdOf(message) ?? found;
                }
            }

            if (found is not null)
            {
                _chatId = found;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_chatIdFile)!);
                    await File.WriteAllTextAsync(_chatIdFile, found);
                }
                catch
                {
                    // Не сохранили — узнаем заново в следующий раз.
                }

                _logger.Info($"[Бот] Запомнили чат для сообщений: {found}.");
                return _chatId;
            }
        }

        if (!_noChatWarned && !_tokenRejected)
        {
            _noChatWarned = true;
            _logger.Warn("[Бот] Бот не знает, куда писать. Напишите ему в Telegram любое сообщение (например, /start) — дальше он запомнит чат сам.");
        }

        return null;
    }

    private static string? ChatIdOf(JsonElement message) =>
        message.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var id)
            ? id.GetRawText()
            : null;

    /// <summary>
    /// Вызывает метод Bot API. Сначала напрямую, при сетевой ошибке — через прокси Telegram.
    /// Сработавший путь запоминается. Возвращает поле result или null.
    /// </summary>
    private async Task<JsonElement?> CallAsync(string method, Dictionary<string, object> payload, TimeSpan? timeout = null)
    {
        var routes = new List<string?>();
        if (_workingProxy is not null)
        {
            routes.Add(_workingProxy.Length == 0 ? null : _workingProxy);
        }
        else
        {
            routes.Add(null);
            var proxy = _proxyProvider();
            if (!string.IsNullOrWhiteSpace(proxy))
            {
                routes.Add(proxy);
            }
        }

        Exception? lastError = null;
        foreach (var proxy in routes)
        {
            try
            {
                using var client = CreateClient(proxy, timeout ?? TimeSpan.FromSeconds(20));
                using var response = await client.PostAsJsonAsync($"{ApiBase}{_token}/{method}", payload);
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                {
                    _workingProxy = proxy ?? string.Empty;
                    return root.GetProperty("result").Clone();
                }

                // Telegram ответил, но отказал (неверный токен, чат не найден): прокси тут не поможет.
                var description = root.TryGetProperty("description", out var d) ? d.GetString() : response.StatusCode.ToString();
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (!_tokenRejected)
                    {
                        _tokenRejected = true;
                        _logger.Warn("[Бот] Токен бота не подходит (Telegram ответил Unauthorized). Проверьте TELEGRAM_BOT_TOKEN: его выдаёт @BotFather.");
                    }

                    return null;
                }

                _logger.Warn($"[Бот] Telegram отказал ({method}): {description}");
                return null;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        _logger.Warn($"[Бот] Telegram не отвечает ({method}): {lastError?.Message}");
        return null;
    }

    private static HttpClient CreateClient(string? proxy, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler();
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new WebProxy(new Uri(proxy));
            handler.UseProxy = true;
        }

        return new HttpClient(handler) { Timeout = timeout };
    }
}
