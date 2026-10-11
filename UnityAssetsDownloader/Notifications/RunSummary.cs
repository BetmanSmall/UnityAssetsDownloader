using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Предупреждение или ошибка из лога. Одинаковые по смыслу сложены вместе: числа и адреса при сравнении не
/// считаются, поэтому «не открылась страница #123» и «не открылась страница #456» — одна запись с счётчиком 2.
/// </summary>
internal sealed record LogDigestEntry(string Level, int Count, string Example);

/// <summary>Сколько постов прочитано в канале и какие у них номера.</summary>
internal sealed class ChannelPostsInfo
{
    public int Posts { get; set; }
    public int MinId { get; set; }
    public int MaxId { get; set; }
}

/// <summary>Всё, что нужно для сводки прогона. Собирается в конце прогона из отчёта, счётчиков и логгера.</summary>
internal sealed class RunSummaryData
{
    public string Version { get; init; } = string.Empty;
    public string Profile { get; init; } = string.Empty;

    /// <summary>Что за прогон: служба или разовый, вся история каналов или только новые посты.</summary>
    public string Mode { get; init; } = string.Empty;

    /// <summary>Каналы, пачки, лимиты, пауза — одной строкой.</summary>
    public string Settings { get; init; } = string.Empty;

    public DateTime StartedLocal { get; init; }
    public DateTime FinishedLocal { get; init; }
    public bool DryRun { get; init; }
    public bool Crashed { get; init; }

    /// <summary>Прогон остановили (docker stop, Ctrl+C): сделанное сохранено, остальное — в следующий раз.</summary>
    public bool Interrupted { get; init; }

    public RunReport Report { get; init; } = new();
    public RunStats Stats { get; init; } = new();
    public IReadOnlyList<LogDigestEntry> Warnings { get; init; } = [];

    /// <summary>Сколько ассетов пропущено без открытия страницы: уже добавленные, не добавляемые магазином, удалённые.</summary>
    public int SkippedKnown { get; init; }

    public int SkippedUnaddable { get; init; }
    public int SkippedDeprecated { get; init; }

    /// <summary>Строка замера памяти (<see cref="MemorySampler.Describe"/>) или пусто.</summary>
    public string? Memory { get; init; }

    /// <summary>Сколько раз восстанавливали вкладку и браузер («вкладка заменялась 2 раза…») или пусто.</summary>
    public string? Browser { get; init; }

    /// <summary>Почему прогон остановлен раньше времени из-за браузера, или пусто.</summary>
    public string? BrowserStopReason { get; init; }

    /// <summary>Названия ассетов по номерам (каталог, карточки каналов) — чтобы в списках были не голые номера.</summary>
    public IReadOnlyDictionary<string, string> Names { get; init; } = new Dictionary<string, string>();

    public int CatalogCount { get; init; }
    public int CatalogWithSources { get; init; }
    public string RunLogPath { get; init; } = string.Empty;
    public string ReportPath { get; init; } = string.Empty;
    public string ErrorsPath { get; init; } = string.Empty;
}

/// <summary>
/// Короткая сводка прогона для человека и для ассистента: что читали, что вышло с ассетами, что не получилось и
/// какие предупреждения были. Полный лог прогона — тысячи строк; этого файла достаточно, чтобы понять прогон
/// целиком, и он не растёт вместе с числом ассетов: длинные списки обрезаны, остальное — числом.
/// </summary>
internal static class RunSummary
{
    public const int MaxAdded = 100;
    public const int MaxFailures = 60;
    public const int MaxWarnings = 15;

    public static string Build(RunSummaryData d)
    {
        var items = d.Report.Items;
        var finished = d.FinishedLocal < d.StartedLocal ? d.StartedLocal : d.FinishedLocal;
        var duration = finished - d.StartedLocal;
        var sb = new StringBuilder();

        sb.AppendLine($"=== СВОДКА ПРОГОНА {d.StartedLocal:yyyy-MM-dd HH:mm} – {finished:HH:mm} ({FormatDuration(duration)}) ===");
        if (d.Crashed)
        {
            sb.AppendLine("‼ ПРОГОН ЗАВЕРШИЛСЯ ПАДЕНИЕМ. Причина — в errors.log; ниже то, что успело записаться.");
        }

        if (!string.IsNullOrWhiteSpace(d.BrowserStopReason))
        {
            sb.AppendLine($"‼ ПРОГОН ОСТАНОВЛЕН: {d.BrowserStopReason}. Сделанное сохранено; следующий прогон продолжит с этого места.");
        }

        if (d.Interrupted)
        {
            sb.AppendLine("⏹ ПРОГОН ПРЕРВАН (остановка или Ctrl+C). Сделанное сохранено в памяти профиля; следующий прогон не будет проверять это заново.");
        }

        if (d.DryRun)
        {
            sb.AppendLine("Проверочный прогон: аккаунт не менялся, «добавились бы» — только расчёт.");
        }

        sb.AppendLine($"Версия: {d.Version}");
        sb.AppendLine($"Профиль: {d.Profile} | {d.Mode}");
        if (d.Settings.Length > 0)
        {
            sb.AppendLine($"Настройки: {d.Settings}");
        }

        AppendTelegram(sb, d.Stats);
        AppendAssets(sb, d, items, duration);
        AppendFailures(sb, items, d.Names);
        AppendAdded(sb, items, d.Names);
        AppendWarnings(sb, d.Warnings);

        sb.AppendLine();
        sb.AppendLine("-- Каталог и ресурсы --");
        if (d.CatalogCount > 0)
        {
            sb.AppendLine($"В каталоге библиотеки {d.CatalogCount} ассетов, у {d.CatalogWithSources} есть источник из каналов.");
        }

        if (!string.IsNullOrWhiteSpace(d.Memory))
        {
            sb.AppendLine($"Память: {d.Memory}.");
        }

        if (!string.IsNullOrWhiteSpace(d.Browser))
        {
            sb.AppendLine($"Браузер: {d.Browser}.");
        }

        sb.AppendLine();
        sb.AppendLine("-- Файлы --");
        sb.AppendLine($"Полный лог прогона: {d.RunLogPath}");
        sb.AppendLine($"Отчёт по каждому ассету: {d.ReportPath}");
        sb.AppendLine($"Все предупреждения и ошибки: {d.ErrorsPath}");
        return sb.ToString();
    }

    private static void AppendTelegram(StringBuilder sb, RunStats s)
    {
        if (s.Channels.Count == 0 && s.PostsRead == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("-- Telegram --");
        foreach (var (name, info) in s.Channels.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
        {
            var range = info.MinId > 0 ? $" (посты #{info.MinId}…#{info.MaxId})" : string.Empty;
            sb.AppendLine($"{name}: прочитано постов {info.Posts}{range}");
        }

        sb.AppendLine($"Ссылок на ассеты магазина: {s.AssetStoreLinks} | постов с описанием ассета: {s.Cards}");
        if (s.DownloadPosts > 0)
        {
            sb.AppendLine($"Раздачи через бота: постов {s.DownloadPosts}; бесплатных в магазине взято ассетов {s.DownloadAssetsFree}, " +
                          $"платных пропущено {s.DownloadAssetsPaid} (файлы из бота не берём, ссылки на бота не храним)");
        }

        if (s.FabPosts + s.ItchPosts + s.OtherPosts > 0)
        {
            sb.AppendLine($"Постов без ссылки на магазин: Fab {s.FabPosts}, itch.io {s.ItchPosts}, прочие {s.OtherPosts}");
        }

        if (s.ChannelsFailed > 0)
        {
            sb.AppendLine($"Каналов, которые не открылись: {s.ChannelsFailed}");
        }
    }

    private static void AppendAssets(StringBuilder sb, RunSummaryData d, List<ProcessResult> items, TimeSpan duration)
    {
        sb.AppendLine();
        sb.AppendLine($"-- Ассеты (обработано {items.Count}) --");
        var groups = items.GroupBy(i => i.Status).ToDictionary(g => g.Key, g => g.Count());
        foreach (AssetProcessStatus status in Enum.GetValues<AssetProcessStatus>())
        {
            if (groups.TryGetValue(status, out var count) && count > 0)
            {
                sb.AppendLine($"{UnityAssetAutomationApp.DescribeStatus(status)}: {count}");
            }
        }

        if (items.Count == 0)
        {
            sb.AppendLine("Ни одного ассета не обработано.");
        }

        if (d.SkippedKnown + d.SkippedUnaddable + d.SkippedDeprecated > 0)
        {
            sb.AppendLine($"Пропущено без открытия страницы: уже добавленных {d.SkippedKnown}, " +
                          $"не добавляются магазином {d.SkippedUnaddable}, удалённых из магазина {d.SkippedDeprecated}");
        }

        // Скорость — по открытым страницам: ассеты, пропущенные без открытия, времени почти не берут (11.10 из-за них
        // вышло «2,5 с на ассет», хотя страница шла ~26 с).
        var opened = items.Count - d.SkippedKnown - d.SkippedDeprecated;
        if (opened > 0 && duration.TotalSeconds >= 1)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"Скорость: открыто страниц {opened}, в среднем {duration.TotalSeconds / opened:0.0} с на страницу (вместе со входом, ожиданием и чтением каналов)"));
        }
    }

    private static void AppendFailures(StringBuilder sb, List<ProcessResult> items, IReadOnlyDictionary<string, string> names)
    {
        var bad = items.Where(i => i.Status is AssetProcessStatus.Failed or AssetProcessStatus.UnknownAfterClick
            or AssetProcessStatus.PromoNotApplied or AssetProcessStatus.NeedsHuman).ToList();
        if (bad.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine($"-- Не вышло ({bad.Count}) --");
        foreach (var item in bad.Take(MaxFailures))
        {
            sb.AppendLine($"• {UnityAssetAutomationApp.DescribeStatus(item.Status)} | {ShortName(item.Url, names)} | {Clip(WithoutUrls(item.Message), 160)}");
        }

        if (bad.Count > MaxFailures)
        {
            sb.AppendLine($"…и ещё {bad.Count - MaxFailures} (все — в отчёте по ассетам)");
        }
    }

    private static void AppendAdded(StringBuilder sb, List<ProcessResult> items, IReadOnlyDictionary<string, string> names)
    {
        var added = items.Where(i => i.Status is AssetProcessStatus.Added or AssetProcessStatus.WouldAddInDryRun).ToList();
        if (added.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine($"-- Добавлено (или добавилось бы) — {added.Count} --");
        foreach (var item in added.Take(MaxAdded))
        {
            sb.AppendLine($"• {ShortName(item.Url, names)}{(item.PromoCode is null ? string.Empty : $" (промокод {item.PromoCode})")}");
        }

        if (added.Count > MaxAdded)
        {
            sb.AppendLine($"…и ещё {added.Count - MaxAdded} (все — в отчёте по ассетам)");
        }
    }

    private static void AppendWarnings(StringBuilder sb, IReadOnlyList<LogDigestEntry> warnings)
    {
        sb.AppendLine();
        if (warnings.Count == 0)
        {
            sb.AppendLine("-- Предупреждения и ошибки в логе: нет --");
            return;
        }

        sb.AppendLine($"-- Предупреждения и ошибки в логе: видов {warnings.Count}, всего {warnings.Sum(w => w.Count)} --");
        foreach (var w in warnings.OrderByDescending(w => w.Level == "ERROR").ThenByDescending(w => w.Count).Take(MaxWarnings))
        {
            sb.AppendLine($"[{w.Count}×] {w.Level} {Clip(w.Example, 260)}");
        }

        if (warnings.Count > MaxWarnings)
        {
            sb.AppendLine($"…и ещё {warnings.Count - MaxWarnings} видов (все — в errors.log)");
        }
    }

    /// <summary>
    /// Короткое имя ассета: «Название (123456)», если название известно (каталог, карточка канала), иначе хвост адреса —
    /// «foo-123456» у обычного адреса, «foo (123456)» у адреса из каналов /packages/foo/123456.
    /// </summary>
    internal static string ShortName(string url, IReadOnlyDictionary<string, string>? names = null)
    {
        var id = UnityAssetAutomationApp.ExtractPackageId(url);
        if (id is not null && names is not null && names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name))
        {
            return $"{name.Trim()} ({id})";
        }

        var trimmed = url.TrimEnd('/');
        var cut = trimmed.IndexOf('?');
        if (cut > 0)
        {
            trimmed = trimmed[..cut];
        }

        var parts = trimmed.Split('/');
        var last = parts[^1];
        if (id is not null && last == id && parts.Length >= 2 && parts[^2] is not ("package" or "packages"))
        {
            return $"{parts[^2]} ({id})";
        }

        return last.Length > 0 ? last : url;
    }

    private static readonly Regex UrlRegex = new(@"https?://\S+", RegexOptions.Compiled);

    /// <summary>Сообщение без адресов: ассет уже назван в начале строки, адрес в сообщении только удлиняет её.</summary>
    internal static string WithoutUrls(string? text) =>
        UrlRegex.Replace(text ?? string.Empty, "…").Replace("( …)", string.Empty).Replace("(…)", string.Empty);

    private static string Clip(string? text, int max)
    {
        var flat = (text ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return flat.Length > max ? flat[..max] + "…" : flat;
    }

    internal static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} ч {t.Minutes} мин"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} мин {t.Seconds} с"
        : $"{(int)t.TotalSeconds} с";

    /// <summary>Оставляет в папке последние keep сводок (run-summary-*.txt), остальные удаляет. Ошибок не бросает.</summary>
    public static void Prune(string logsDirectory, int keep)
    {
        try
        {
            var old = Directory.GetFiles(logsDirectory, "run-summary-*.txt")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .Skip(Math.Max(0, keep));
            foreach (var file in old)
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Лишние сводки не мешают.
        }
    }
}
