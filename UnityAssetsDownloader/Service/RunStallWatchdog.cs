using System.Globalization;

/// <summary>
/// Сторож прогона службы (--watch). 12.10 ночной добор встал на закрытии вкладки Telegram (PuppeteerSharp ждёт
/// подтверждения без срока), и служба простояла 27 ч: ни обычных прогонов, ни суточной сводки бота. Закрытие с тех пор
/// ограничено по времени, но мест «ждать ответа браузера» в программе много, поэтому служба следит за логом: если занятая
/// служба дольше Limit не пишет ни строки, прогон считается зависшим. Зависшую задачу изнутри не прервать (она ждёт
/// ответа, который не придёт), поэтому служба записывает, что успела, и выходит с ошибкой, а Docker
/// (restart: unless-stopped) или systemd (Restart=on-failure) поднимают её заново — новый процесс начинает с обычного прогона.
/// </summary>
internal static class RunStallWatchdog
{
    /// <summary>
    /// Самое долгое законное молчание в логе — ожидание человека для Fab (до 30 мин), код входа от бота — до 10 мин.
    /// 45 мин — с запасом над ними.
    /// </summary>
    public const int DefaultMinutes = 45;

    /// <summary>Код выхода зависшей службы: не 0, чтобы systemd с Restart=on-failure тоже поднял её заново.</summary>
    public const int ExitCode = 3;

    /// <summary>О зависании — отдельным сообщением боту не чаще раза в столько; остальное — в суточной сводке.</summary>
    public static readonly TimeSpan NotifyEvery = TimeSpan.FromHours(6);

    /// <summary>RUN_STALL_MINUTES из окружения; null — сторож выключен.</summary>
    public static TimeSpan? LimitFromEnvironment(Action<string> warn) =>
        ParseLimit(Environment.GetEnvironmentVariable("RUN_STALL_MINUTES"), warn);

    /// <summary>Пусто — 45 мин; 0 или off — выключен; число минут от 10 до 1440; иначе — 45 с предупреждением.</summary>
    internal static TimeSpan? ParseLimit(string? value, Action<string> warn)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeSpan.FromMinutes(DefaultMinutes);
        }

        var v = value.Trim();
        if (v == "0" || v.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes is >= 10 and <= 1440)
        {
            return TimeSpan.FromMinutes(minutes);
        }

        warn($"[Сервер] Не понял RUN_STALL_MINUTES='{value}' (нужно число минут от 10 до 1440 или 0 — выключить). Берём {DefaultMinutes}.");
        return TimeSpan.FromMinutes(DefaultMinutes);
    }

    public static bool IsStalled(DateTime lastWriteUtc, DateTime nowUtc, TimeSpan limit) => nowUtc - lastWriteUtc >= limit;

    /// <summary>Что случилось — для лога, сводки прогона и бота.</summary>
    public static string Describe(string what, TimeSpan silent, string? lastLine) =>
        $"{what} завис: {silent.TotalMinutes:0} мин в лог не пишется ни строки. Последняя строка: {Shorten(lastLine, 300)}. " +
        "Сделанное сохранено; служба перезапускается сама (её поднимут Docker или systemd), дальше — по расписанию.";

    /// <summary>Коротко — для строки суточной сводки.</summary>
    public static string Note(string what, DateTime local, string? lastLine) =>
        $"{local:dd.MM HH:mm} — {what}, последняя строка: {Shorten(lastLine, 160)}";

    /// <summary>Ждёт задачу не дольше timeout; ошибки и опоздание глотает: сторож обязан дойти до выхода.</summary>
    public static async Task WithinAsync(Func<Task> start, TimeSpan timeout)
    {
        try
        {
            var task = start();
            await Task.WhenAny(task, Task.Delay(timeout));
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch
        {
            // Не вышло — идём дальше к выходу.
        }
    }

    private static string Shorten(string? line, int max)
    {
        var text = string.IsNullOrWhiteSpace(line) ? "—" : line.Trim();
        return text.Length > max ? text[..max] + "…" : text;
    }
}
