using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Сообщения от Telegram-бота: что добавлено, что куплено по промокоду, что пошло не так.
/// И обратно: бот умеет дождаться ответа — например, кода подтверждения входа в Unity,
/// когда программа работает на сервере и спросить в консоли некого.
///
/// Куда писать, бот узнаёт сам: достаточно один раз написать ему любое сообщение.
/// Номер чата запоминается в файле.
///
/// Связь с api.telegram.org пробивается перебором: напрямую, по закреплённым адресам
/// Telegram и через тот же прокси, что и чтение каналов. Первый путь, который ответил,
/// запоминается в файле и в следующий раз пробуется первым. Подробности, почему так —
/// в соседнем проекте LinuxServerWatcher, docs/telegram-access.md.
/// </summary>
internal sealed class TelegramNotifier
{
    /// <summary>
    /// Адрес Bot API. Переменной TELEGRAM_API_BASE его можно заменить — на зеркало
    /// Bot API или на макет в стенде (например, http://127.0.0.1:8081). Читается при каждом
    /// вызове: у сценариев стенда в одном процессе макеты на разных портах.
    /// </summary>
    private static string ApiBase =>
        (Environment.GetEnvironmentVariable("TELEGRAM_API_BASE") is { Length: > 0 } custom
            ? custom.Trim().TrimEnd('/')
            : "https://api.telegram.org") + "/bot";

    /// <summary>
    /// Адреса Bot API. DNS нередко отдаёт тот из них, до которого у сервера нет
    /// маршрута, хотя остальные отвечают. Порядок — по частоте успеха.
    /// Лишние адреса не вредят: они просто не ответят.
    /// </summary>
    private static readonly string[] PinnedApiAddresses =
    [
        "149.154.167.220",
        "149.154.175.50",
        "149.154.167.50",
        "149.154.171.5",
        "91.108.56.130"
    ];

    private readonly string _token;
    private readonly string _chatIdFile;
    private readonly string _routeFile;
    private readonly Func<string?> _proxyProvider;
    private readonly AppLogger _logger;
    private string? _chatId;
    private Route? _route;
    private bool _noChatWarned;
    private bool _tokenRejected;
    private bool _unreachableWarned;

    public TelegramNotifier(string token, string? chatId, string chatIdFile, Func<string?> proxyProvider, AppLogger logger)
    {
        _token = token.Trim();
        _chatId = string.IsNullOrWhiteSpace(chatId) ? null : chatId.Trim();
        _chatIdFile = chatIdFile;
        _routeFile = Path.Combine(Path.GetDirectoryName(chatIdFile) ?? ".", "telegram_bot_route.txt");
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

            var sent = await CallAsync("sendMessage", payload) is not null;
            if (sent)
            {
                // По логу должно быть видно, что бот написал: без этой строки молчание
                // бота снаружи не отличить от сломанной отправки.
                var firstLine = text.Split('\n', 2)[0].Trim();
                _logger.Info($"[Бот] Сообщение отправлено: {firstLine}");
            }

            return sent;
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Бот] Не удалось отправить сообщение: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Отправляет файл (например, страницу каталога). Ошибки не пробрасывает, как и SendAsync.
    /// Bot API принимает файлы до 50 МБ; на медленном пути отправка дольше, поэтому таймаут больше.
    /// </summary>
    public async Task<bool> SendDocumentAsync(string path, string caption)
    {
        if (!Enabled || !File.Exists(path))
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

            var bytes = await File.ReadAllBytesAsync(path);
            var fileName = Path.GetFileName(path);
            var sent = await CallAsync("sendDocument", () =>
            {
                var form = new MultipartFormDataContent
                {
                    { new StringContent(chat), "chat_id" },
                    { new StringContent(caption.Length > 1000 ? caption[..1000] + "…" : caption), "caption" }
                };
                var file = new ByteArrayContent(bytes);
                file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
                form.Add(file, "document", fileName);
                return form;
            }, TimeSpan.FromSeconds(120)) is not null;

            if (sent)
            {
                _logger.Info($"[Бот] Файл отправлен: {fileName} ({bytes.Length / 1024} КБ)");
            }

            return sent;
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Бот] Не удалось отправить файл: {ex.Message}");
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
    /// Вызывает метод Bot API, перебирая пути, пока какой-нибудь не ответит:
    /// путь из прошлого раза, напрямую, по каждому закреплённому адресу, через прокси.
    /// Сработавший путь запоминается. Возвращает поле result или null.
    ///
    /// Сетевая ошибка на пути значит, что запрос до Telegram не дошёл, — повтор
    /// по другому пути безопасен и сообщение не удвоится. Если Telegram ответил
    /// отказом (неверный токен, нет чата), перебор прекращается: путь тут не при чём.
    /// </summary>
    private Task<JsonElement?> CallAsync(string method, Dictionary<string, object> payload, TimeSpan? timeout = null) =>
        CallAsync(method, () => JsonContent.Create(payload), timeout);

    /// <param name="content">Тело запроса. Фабрика — потому что на каждом пути нужно новое: отправленное не переиспользуется.</param>
    private async Task<JsonElement?> CallAsync(string method, Func<HttpContent> content, TimeSpan? timeout = null)
    {
        var routes = BuildRoutes();
        Exception? lastError = null;

        foreach (var route in routes)
        {
            JsonElement result;
            try
            {
                using var client = CreateClient(route, timeout ?? TimeSpan.FromSeconds(20));
                using var body = content();
                using var response = await client.PostAsync($"{ApiBase}{_token}/{method}", body);
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                {
                    // Telegram ответил, но отказал (неверный токен, чат не найден): другой путь тут не поможет.
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

                result = root.GetProperty("result").Clone();
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.Debug($"[Бот] Путь не сработал ({route.Describe}): {ex.Message}");
                continue;
            }

            // Запрос дошёл. Всё, что дальше, — вне перебора путей: любая ошибка здесь
            // не должна отправить то же сообщение ещё раз другим путём.
            try
            {
                RememberRoute(route);
            }
            catch
            {
                // Путь не запомнили — в следующий раз найдём заново.
            }

            return result;
        }

        // Путь, который работал, мог перестать: пусть следующий вызов начнёт перебор заново.
        _route = null;
        _logger.Warn($"[Бот] Telegram не отвечает ({method}). Пробовали путей: {routes.Count}. Последняя ошибка: {lastError?.Message}");

        if (!_unreachableWarned)
        {
            _unreachableWarned = true;
            _logger.Warn("[Бот] Ни один путь до api.telegram.org не открылся: ни напрямую, ни по закреплённым адресам, ни через прокси.");
            _logger.Warn("[Бот] Проверьте на сервере: curl -s -m 8 -o /dev/null -w '%{http_code}\n' https://api.telegram.org/");
        }

        return null;
    }

    /// <summary>
    /// Пути до Bot API по порядку проверки. Первым идёт тот, что работал недавно,
    /// дальше — все остальные, чтобы смерть одного пути не оставила бота без связи.
    /// </summary>
    private List<Route> BuildRoutes()
    {
        var routes = new List<Route>();

        void Add(Route route)
        {
            if (!routes.Any(existing => existing.Id == route.Id))
            {
                routes.Add(route);
            }
        }

        if (_route is not null)
        {
            Add(_route);
        }

        var saved = Route.Parse(ReadSavedRoute());
        if (saved is not null)
        {
            Add(saved);
        }

        Add(new Route(null, null));

        foreach (var address in PinnedApiAddresses)
        {
            Add(new Route(null, address));
        }

        var proxy = _proxyProvider();
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            Add(new Route(proxy.Trim(), null));
        }

        return routes;
    }

    /// <summary>Запоминает сработавший путь — в памяти и в файле, для следующего запуска.</summary>
    private void RememberRoute(Route route)
    {
        if (_route?.Id == route.Id)
        {
            return;
        }

        _route = route;
        _unreachableWarned = false;
        _logger.Info($"[Бот] Связь с Telegram есть: {route.Describe}.");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_routeFile)!);
            File.WriteAllText(_routeFile, route.Id);
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Бот] Не удалось запомнить путь: {ex.Message}");
        }
    }

    private string? ReadSavedRoute()
    {
        try
        {
            return File.Exists(_routeFile) ? File.ReadAllText(_routeFile).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Клиент для одного пути. При закреплённом адресе подменяется только адрес
    /// назначения: имя в проверке сертификата и в заголовке Host остаётся прежним.
    /// Соединение ждём не дольше 8 секунд — мёртвый путь должен уступить место следующему,
    /// а не съесть весь таймаут запроса.
    /// </summary>
    private static HttpClient CreateClient(Route route, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(8) };

        if (!string.IsNullOrWhiteSpace(route.Proxy))
        {
            handler.Proxy = new WebProxy(new Uri(route.Proxy));
            handler.UseProxy = true;
        }
        else if (!string.IsNullOrWhiteSpace(route.PinnedIp))
        {
            var address = IPAddress.Parse(route.PinnedIp);

            // Свой ConnectCallback отменяет ConnectTimeout, поэтому ограничиваем время сами.
            handler.ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(TimeSpan.FromSeconds(8));

                try
                {
                    await socket.ConnectAsync(address, context.DnsEndPoint.Port, limit.Token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        return new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>Путь до Bot API: напрямую, по закреплённому адресу или через прокси.</summary>
    private sealed record Route(string? Proxy, string? PinnedIp)
    {
        public string Id =>
            !string.IsNullOrWhiteSpace(Proxy) ? $"proxy:{Proxy}"
            : !string.IsNullOrWhiteSpace(PinnedIp) ? $"ip:{PinnedIp}"
            : "direct";

        public string Describe =>
            !string.IsNullOrWhiteSpace(Proxy) ? $"через прокси {Proxy}"
            : !string.IsNullOrWhiteSpace(PinnedIp) ? $"по закреплённому адресу {PinnedIp}"
            : "напрямую";

        public static Route? Parse(string? saved)
        {
            if (string.IsNullOrWhiteSpace(saved))
            {
                return null;
            }

            saved = saved.Trim();

            if (saved == "direct")
            {
                return new Route(null, null);
            }

            if (saved.StartsWith("ip:", StringComparison.OrdinalIgnoreCase))
            {
                var value = saved[3..].Trim();
                return IPAddress.TryParse(value, out _) ? new Route(null, value) : null;
            }

            if (saved.StartsWith("proxy:", StringComparison.OrdinalIgnoreCase))
            {
                var value = saved[6..].Trim();
                return Uri.TryCreate(value, UriKind.Absolute, out _) ? new Route(value, null) : null;
            }

            return null;
        }
    }
}
