using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>Нажатие кнопки (Text — её данные) или сообщение владельца (Text — текст); AtUtc — когда написано / получено.</summary>
internal sealed record BotEvent(bool IsButton, string Text, DateTime AtUtc);

/// <summary>
/// Бот слушает чат всё время, пока работает служба (--watch), а не только когда программа чего-то ждёт (этап B плана
/// «Окно Fab», 13.10). Опрос getUpdates — один на всех: Telegram отдаёт обновления одному читателю, второй одновременный
/// опрос получает 409 и теряет сообщения.
///
/// - Команды и кнопки — только из чата владельца (TELEGRAM_CHAT_ID или data/telegram_bot_chat.txt): /fab и кнопка
///   «Открыть окно» — окно Fab по просьбе человека; /status — что делает служба; /help.
/// - Ожидания внутри прогона (код входа Unity, «✅ Готово» окна Fab) ждут здесь (WaitAsync), а не опрашивают Telegram сами.
/// - Чужие сообщения пересылаются владельцу и не выполняются, чужим бот не отвечает. Слова пользователя 13.10: «Бот
///   работает с сообщениями только от меня. Другие сообщения от других людей если они ему написали бот пересылает мне».
/// - Другой процесс в том же контейнере (`docker compose exec … --fab-login`) Telegram не опрашивает, пока служба держит
///   data/bot_inbox.lock, а читает её журнал data/bot_inbox.jsonl — туда пишется то, что служба сама не разобрала.
/// </summary>
internal sealed class BotInbox : IAsyncDisposable
{
    public const string LockFileName = "bot_inbox.lock";
    public const string JournalFileName = "bot_inbox.jsonl";

    /// <summary>Данные кнопки «Открыть окно» (Fab по просьбе человека).</summary>
    public const string FabOpenButton = "fab-open";

    /// <summary>Данные кнопки «📱 С телефона» в сообщении «Fab ждёт вас»: окно и сразу ссылка для телефона (без туннеля).</summary>
    public const string FabOpenPhoneButton = "fab-open-phone";

    /// <summary>Сколько чужих сообщений в час пересылать владельцу; остальные — одной строкой «ещё N».</summary>
    public const int ForwardsPerHour = 10;

    /// <summary>Сообщения владельца старше этого при запуске (пока служба перезапускалась) — уже не команды.</summary>
    private static readonly TimeSpan StaleText = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Сколько секунд Telegram держит опрос, если писать нечего. 13.10 на сервере 50 с не доживали: молчащее соединение
    /// где-то по дороге обрывается; прежнее ожидание кода входа по 25 с там работало с сентября.
    /// </summary>
    internal const int PollSeconds = 25;

    /// <summary>Сколько опрос может не удаваться подряд, прежде чем сказать об этом в лог (дальше — раз в столько же).</summary>
    private static readonly TimeSpan SilenceWarnAfter = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new();

    /// <summary>Приёмник этого процесса (служба) или null.</summary>
    public static BotInbox? Active { get; private set; }

    private readonly TelegramNotifier _bot;
    private readonly string _owner;
    private readonly string _journalPath;
    private readonly FileStream _lock;
    private readonly Action<string> _log;
    private readonly object _sync = new();
    private readonly List<Waiter> _waiters = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private Task? _loop;
    private DateTime _forwardWindowUtc = DateTime.MinValue;
    private int _forwardedInWindow;
    private int _suppressedInWindow;

    private sealed record Waiter(Func<BotEvent, bool> Match, string? ButtonAnswer, TaskCompletionSource<BotEvent?> Done);

    private BotInbox(TelegramNotifier bot, string owner, FileStream lockFile, Action<string> log)
    {
        _bot = bot;
        _owner = owner;
        _lock = lockFile;
        _log = log;
        _journalPath = Path.Combine(bot.DataDirectory, JournalFileName);
    }

    /// <summary>Что ответить на /fab и кнопку «Открыть окно»; аргумент true — /phone и «📱 С телефона» (ссылка без туннеля).</summary>
    public Func<bool, string>? OnFab { get; init; }

    /// <summary>Что ответить на /status.</summary>
    public Func<string>? OnStatus { get; init; }

    /// <summary>
    /// Запускает приёмник службы. null — бот не настроен, чат владельца неизвестен (тогда команды не нужны: некому их
    /// слать) или журнал уже держит другой процесс.
    /// </summary>
    public static async Task<BotInbox?> StartAsync(TelegramNotifier bot, Action<string> log, Func<bool, string>? onFab, Func<string>? onStatus)
    {
        if (!bot.Enabled)
        {
            return null;
        }

        var owner = await bot.ResolveChatIdAsync();
        if (owner is null)
        {
            log("[Бот] Чат владельца ещё неизвестен — команды боту заработают после первого сообщения ему и перезапуска службы.");
            return null;
        }

        FileStream lockFile;
        try
        {
            Directory.CreateDirectory(bot.DataDirectory);
            lockFile = new FileStream(Path.Combine(bot.DataDirectory, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            log("[Бот] Бота уже слушает другой процесс — команды обработает он.");
            return null;
        }

        var inbox = new BotInbox(bot, owner, lockFile, log) { OnFab = onFab, OnStatus = onStatus };
        Active = inbox;
        inbox._loop = Task.Run(() => inbox.RunAsync(inbox._stop.Token));
        return inbox;
    }

    /// <summary>
    /// Ждёт событие владельца, подходящее под match, — только пришедшее после вызова. buttonAnswer — всплывающий ответ на
    /// нажатие кнопки. null — время вышло или ожидание отменили.
    /// </summary>
    public async Task<BotEvent?> WaitAsync(Func<BotEvent, bool> match, string? buttonAnswer, TimeSpan timeout, CancellationToken cancel)
    {
        var waiter = new Waiter(match, buttonAnswer, new TaskCompletionSource<BotEvent?>(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_sync)
        {
            _waiters.Add(waiter);
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeoutCts.CancelAfter(timeout);
            await using (timeoutCts.Token.Register(() => waiter.Done.TrySetResult(null)))
            {
                return await waiter.Done.Task;
            }
        }
        finally
        {
            lock (_sync)
            {
                _waiters.Remove(waiter);
            }
        }
    }

    /// <summary>Держит ли бота служба в другом процессе (её замок занят). В самой службе не зовётся — там Active.</summary>
    public static bool HeldElsewhere(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, LockFileName);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Другой процесс: ждёт событие в журнале службы (записи новее начала ожидания). null — не дождались.</summary>
    public static async Task<BotEvent?> WaitInJournalAsync(string dataDirectory, Func<BotEvent, bool> match, TimeSpan timeout, CancellationToken cancel)
    {
        var path = Path.Combine(dataDirectory, JournalFileName);
        var since = DateTime.UtcNow;
        var stopAt = since + timeout;
        while (DateTime.UtcNow < stopAt && !cancel.IsCancellationRequested)
        {
            foreach (var (seenUtc, e) in ReadJournal(path))
            {
                if (seenUtc >= since && match(e))
                {
                    return e;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancel);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return null;
    }

    private async Task RunAsync(CancellationToken stop)
    {
        await _bot.SetCommandsAsync([
            ("fab", "Открыть окно Fab: галочка Cloudflare, вход в Epic"),
            ("phone", "Окно Fab с телефона, без туннеля (если включено)"),
            ("status", "Что сейчас делает служба"),
            ("help", "Что умеет бот")
        ]);
        _log("[Бот] Служба слушает бота: /fab, /phone, /status, кнопки «Открыть окно»; чужие сообщения пересылаются вам.");

        long offset = 0;
        var pause = TimeSpan.FromSeconds(10);
        DateTime? silentSince = null;
        var silenceWarned = DateTime.MinValue;
        while (!stop.IsCancellationRequested)
        {
            JsonElement? updates;
            try
            {
                updates = await _bot.GetUpdatesAsync(offset, PollSeconds);
            }
            catch (Exception ex)
            {
                _log($"[Бот] Опрос не удался: {ex.Message}");
                updates = null;
            }

            if (stop.IsCancellationRequested)
            {
                break;
            }

            if (updates is not { } list)
            {
                // Нет связи или опрашивает кто-то ещё (409) — подождём, не долбим Telegram. Один сорвавшийся опрос — обычное
                // дело; в лог — только если молчание затянулось.
                silentSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - silentSince >= SilenceWarnAfter && DateTime.UtcNow - silenceWarned >= SilenceWarnAfter)
                {
                    silenceWarned = DateTime.UtcNow;
                    _log($"[Бот] Не слышу Telegram уже {(DateTime.UtcNow - silentSince.Value).TotalMinutes:0} мин — команды и кнопки " +
                         "пока не доходят (сообщения бот отправляет как обычно).");
                }

                try
                {
                    await Task.Delay(pause, stop);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                pause = TimeSpan.FromSeconds(Math.Min(60, pause.TotalSeconds * 2));
                continue;
            }

            if (silentSince is { } since && DateTime.UtcNow - since >= SilenceWarnAfter)
            {
                _log("[Бот] Связь с Telegram снова есть — команды доходят.");
            }

            silentSince = null;
            pause = TimeSpan.FromSeconds(10);
            foreach (var update in list.EnumerateArray())
            {
                offset = update.GetProperty("update_id").GetInt64() + 1;
                try
                {
                    await HandleAsync(update);
                }
                catch (Exception ex)
                {
                    _log($"[Бот] Сообщение не разобралось: {ex.Message}");
                }
            }
        }
    }

    internal async Task HandleAsync(JsonElement update)
    {
        if (update.TryGetProperty("callback_query", out var callback))
        {
            await HandleButtonAsync(callback);
        }
        else if (update.TryGetProperty("message", out var message))
        {
            await HandleMessageAsync(message);
        }
    }

    private async Task HandleButtonAsync(JsonElement callback)
    {
        var callbackId = callback.TryGetProperty("id", out var id) ? id.GetString() : null;
        if (!callback.TryGetProperty("message", out var message) || TelegramNotifier.ChatIdOf(message) != _owner)
        {
            await _bot.AnswerCallbackAsync(callbackId, "Недоступно.");
            return;
        }

        var data = callback.TryGetProperty("data", out var d) ? d.GetString() ?? string.Empty : string.Empty;
        var e = new BotEvent(true, data, DateTime.UtcNow);
        if (TakeWaiter(e) is { } waiter)
        {
            await _bot.AnswerCallbackAsync(callbackId, waiter.ButtonAnswer ?? "Принято.");
            waiter.Done.TrySetResult(e);
            return;
        }

        if (data is FabOpenButton or FabOpenPhoneButton)
        {
            var reply = OnFab?.Invoke(data == FabOpenPhoneButton) ?? "Окно Fab здесь открыть нельзя.";
            await _bot.AnswerCallbackAsync(callbackId, Shorten(reply, 190));
            await _bot.SendAsync(reply);
            return;
        }

        if (data.StartsWith("fab-done:", StringComparison.Ordinal) && !HasButtonWaiters())
        {
            // Кнопку мог прислать другой процесс (команда в окне сервера) — он ждёт её в журнале.
            Journal(e);
            await _bot.AnswerCallbackAsync(callbackId, "Принято.");
            return;
        }

        await _bot.AnswerCallbackAsync(callbackId, "Эта кнопка уже не действует.");
    }

    private async Task HandleMessageAsync(JsonElement message)
    {
        var chat = TelegramNotifier.ChatIdOf(message);
        if (chat is null)
        {
            return;
        }

        if (chat != _owner)
        {
            await ForwardStrangerAsync(message, chat);
            return;
        }

        var text = message.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        var at = message.TryGetProperty("date", out var date) ? DateTimeOffset.FromUnixTimeSeconds(date.GetInt64()).UtcDateTime : DateTime.UtcNow;
        if (text.Length == 0)
        {
            return;
        }

        var e = new BotEvent(false, text, at);
        if (TakeWaiter(e) is { } waiter)
        {
            waiter.Done.TrySetResult(e);
            return;
        }

        if (at < _startedUtc - StaleText)
        {
            return; // написано, пока служба стояла: не выполняем задним числом
        }

        switch (CommandOf(text))
        {
            case "fab":
            case "phone":
                await _bot.SendAsync(OnFab?.Invoke(CommandOf(text) == "phone") ?? "Окно Fab здесь открыть нельзя.");
                break;
            case "status":
                await _bot.SendAsync(OnStatus?.Invoke() ?? "Служба работает.");
                break;
            case "help":
            case "start":
                await _bot.SendAsync(HelpText);
                break;
            case { } unknown:
                await _bot.SendAsync($"Не знаю команду /{unknown}.\n{HelpText}");
                break;
            default:
                Journal(e); // не команда: может, это ответ процессу в окне сервера (код входа, «готово»)
                break;
        }
    }

    internal const string HelpText =
        "Команды:\n/fab — открыть окно Fab (галочка Cloudflare, вход в Epic): сервер откроет Fab и, если понадобится, пришлёт " +
        "ссылку на окно и кнопку «Готово»;\n/phone — то же, но сразу со ссылкой для телефона без туннеля (если она включена);\n" +
        "/status — что сейчас делает служба.\nКнопки «🔓 Открыть окно» и «📱 С телефона» в сообщениях — то же, что /fab и /phone.";

    /// <summary>«/fab», «/fab@UnityAssetsTGBot» → «fab»; не команда — null.</summary>
    internal static string? CommandOf(string text)
    {
        var m = Regex.Match(text.Trim(), @"^/([A-Za-z_]+)(?:@\w+)?(?:\s|$)");
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    private async Task ForwardStrangerAsync(JsonElement message, string chat)
    {
        var now = DateTime.UtcNow;
        string? summary = null;
        bool forward;
        lock (_sync)
        {
            if (now - _forwardWindowUtc >= TimeSpan.FromHours(1))
            {
                if (_suppressedInWindow > 0)
                {
                    summary = $"✉️ За прошлый час боту писали ещё {_suppressedInWindow} раз — не пересылала, чтобы не засыпать чат.";
                }

                _forwardWindowUtc = now;
                _forwardedInWindow = 0;
                _suppressedInWindow = 0;
            }

            forward = _forwardedInWindow < ForwardsPerHour;
            if (forward)
            {
                _forwardedInWindow++;
            }
            else
            {
                _suppressedInWindow++;
            }
        }

        if (summary is not null)
        {
            await _bot.SendAsync(summary, silent: true);
        }

        if (!forward)
        {
            return;
        }

        var who = message.TryGetProperty("from", out var from) ? DescribeSender(from) : $"чат {chat}";
        _log($"[Бот] Боту написал не владелец ({who}) — пересылаю владельцу.");
        await _bot.SendAsync($"✉️ Боту написал не владелец: {who}. Пересылаю; ему бот не отвечает и команды от него не выполняет.", silent: true);
        if (message.TryGetProperty("message_id", out var messageId))
        {
            await _bot.ForwardAsync(chat, messageId.GetInt64());
        }
    }

    private static string DescribeSender(JsonElement from)
    {
        string? Get(string name) => from.TryGetProperty(name, out var v) ? v.ToString() : null;
        var name = string.Join(" ", new[] { Get("first_name"), Get("last_name") }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var user = Get("username") is { Length: > 0 } u ? $" @{u}" : string.Empty;
        return $"{(name.Length > 0 ? name : "без имени")}{user} (id {Get("id")})";
    }

    private Waiter? TakeWaiter(BotEvent e)
    {
        lock (_sync)
        {
            var waiter = _waiters.FirstOrDefault(w => !w.Done.Task.IsCompleted && w.Match(e));
            if (waiter is not null)
            {
                _waiters.Remove(waiter);
            }

            return waiter;
        }
    }

    private bool HasButtonWaiters()
    {
        lock (_sync)
        {
            return _waiters.Any(w => w.ButtonAnswer is not null);
        }
    }

    /// <summary>Пишет в журнал для других процессов; держит в нём не больше ~200 записей.</summary>
    private void Journal(BotEvent e)
    {
        try
        {
            var line = JsonSerializer.Serialize(new JournalLine(DateTime.UtcNow, e.IsButton, e.Text, e.AtUtc), Json);
            File.AppendAllText(_journalPath, line + "\n");
            var lines = File.ReadAllLines(_journalPath);
            if (lines.Length > 400)
            {
                File.WriteAllLines(_journalPath, lines[^200..]);
            }
        }
        catch (Exception ex)
        {
            _log($"[Бот] Журнал не записался: {ex.Message}");
        }
    }

    private sealed record JournalLine(DateTime SeenUtc, bool IsButton, string Text, DateTime AtUtc);

    private static IEnumerable<(DateTime SeenUtc, BotEvent Event)> ReadJournal(string path)
    {
        string[] lines;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var line in lines)
        {
            JournalLine? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<JournalLine>(line, Json);
            }
            catch (JsonException)
            {
                continue;
            }

            if (parsed is not null)
            {
                yield return (parsed.SeenUtc, new BotEvent(parsed.IsButton, parsed.Text, parsed.AtUtc));
            }
        }
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    public async ValueTask DisposeAsync()
    {
        if (ReferenceEquals(Active, this))
        {
            Active = null;
        }

        await _stop.CancelAsync();
        lock (_sync)
        {
            foreach (var w in _waiters)
            {
                w.Done.TrySetResult(null);
            }

            _waiters.Clear();
        }

        // Длинный опрос не прервать — не ждём его дольше пары секунд.
        if (_loop is not null)
        {
            await Task.WhenAny(_loop, Task.Delay(TimeSpan.FromSeconds(2)));
        }

        await _lock.DisposeAsync();
        _stop.Dispose();
    }
}

/// <summary>
/// Тихие часы бота (BOT_QUIET_HOURS, по умолчанию 23-8 по времени службы): сообщения, которые могут подождать до утра
/// (например, «Fab ждёт вас»), приходят без звука. off — без тихих часов.
/// </summary>
internal static class QuietHours
{
    public static bool Now(DateTime? local = null) => Parse(Environment.GetEnvironmentVariable("BOT_QUIET_HOURS")) is { } h &&
                                                      In(h, (local ?? DateTime.Now).Hour);

    internal static (int Start, int End)? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (23, 8);
        }

        var v = value.Trim();
        if (v.Equals("off", StringComparison.OrdinalIgnoreCase) || v == "-" || v == "0")
        {
            return null;
        }

        var m = Regex.Match(v, @"^(\d{1,2})\s*-\s*(\d{1,2})$");
        return m.Success && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) is var s and <= 23 &&
               int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) is var e and <= 23 && s != e
            ? (s, e)
            : (23, 8);
    }

    internal static bool In((int Start, int End) h, int hour) =>
        h.Start < h.End ? hour >= h.Start && hour < h.End : hour >= h.Start || hour < h.End;
}
