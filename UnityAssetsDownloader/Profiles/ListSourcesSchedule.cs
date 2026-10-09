using System.Globalization;

/// <summary>
/// Как часто на сервере читать списки магазина («топ бесплатных» и подобные), в отличие от Telegram-каналов.
/// Каналы проверяются каждый прогон (новые посты — это новые ассеты), а страница топа меняется медленно, и ради неё
/// каждый раз запускается браузер. Срок задаёт TOP_FREE_EVERY (3d по умолчанию, off — каждый прогон); время
/// последнего чтения лежит в папке профиля. Действует только в службе (--watch) при SOURCES=top-free или all:
/// ручной запуск на ПК читает всё, что попросили.
/// </summary>
internal static class ListSourcesSchedule
{
    public const string FileName = "list_sources_last.txt";
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromDays(3);

    /// <summary>Касается ли расписание этого запуска.</summary>
    public static bool Applies(CliOptions options) =>
        options.Watch && options.ListSourcesEvery is not null &&
        options.SourcesPreset is "top-free" or "all";

    /// <summary>
    /// Пора ли читать списки магазина в этом прогоне. Когда не пора, note объясняет почему и когда будет пора.
    /// Всё остальное (расписание не касается, срока ещё нет в файле) — пора.
    /// </summary>
    public static bool IsDue(CliOptions options, string profileDirectory, DateTime nowUtc, out string note)
    {
        note = string.Empty;
        if (!Applies(options))
        {
            return true;
        }

        var last = LastDone(profileDirectory);
        var period = options.ListSourcesEvery!.Value;
        if (last is null || nowUtc - last.Value >= period)
        {
            return true;
        }

        var next = last.Value + period;
        note = $"Списки магазина («топ бесплатных») читаются раз в {FormatPeriod(period)} (TOP_FREE_EVERY): " +
               $"в прошлый раз {last.Value.ToLocalTime():yyyy-MM-dd HH:mm}, в этот прогон пропускаем, следующий раз — после {next.ToLocalTime():yyyy-MM-dd HH:mm}. Каналы читаются как обычно.";
        return false;
    }

    public static DateTime? LastDone(string profileDirectory)
    {
        try
        {
            var path = Path.Combine(profileDirectory, FileName);
            return File.Exists(path) &&
                   DateTime.TryParse(File.ReadAllText(path).Trim(), CultureInfo.InvariantCulture,
                       DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)
                ? when
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static void MarkDone(string profileDirectory, DateTime nowUtc)
    {
        try
        {
            Directory.CreateDirectory(profileDirectory);
            File.WriteAllText(Path.Combine(profileDirectory, FileName), nowUtc.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не записалось — в следующий прогон списки прочитаются ещё раз. Это лишний, но не опасный проход.
        }
    }

    private static string FormatPeriod(TimeSpan t) =>
        t.TotalDays >= 1 && t.TotalDays % 1 == 0 ? $"{t.TotalDays:0} сут." : t.TotalHours >= 1 ? $"{t.TotalHours:0.#} ч" : $"{t.TotalMinutes:0} мин";
}
