using System.Text;
using System.Text.Json;

/// <summary>
/// Что случилось за один прогон: сколько постов прочитано, куда вели ссылки,
/// что стало с ассетами, пришлось ли входить заново. Копится по ходу прогона,
/// из этого собирается суточная сводка бота.
/// </summary>
internal sealed class RunStats
{
    public int PostsRead { get; set; }
    public int AssetStoreLinks { get; set; }

    /// <summary>Посты без ссылок на Asset Store, но со ссылкой на Fab (магазин Epic).</summary>
    public int FabPosts { get; set; }

    /// <summary>Посты без ссылок на Asset Store, но со ссылкой на itch.io.</summary>
    public int ItchPosts { get; set; }

    /// <summary>Остальные посты без ссылок на Asset Store: новости, другие сайты.</summary>
    public int OtherPosts { get; set; }

    public int ChannelsFailed { get; set; }
    public int Added { get; set; }
    public int AlreadyOwned { get; set; }
    public int PromoFailed { get; set; }
    public int Failed { get; set; }
    public int Relogins { get; set; }
    public bool LoginFailed { get; set; }

    // --- Fab на сервере (FAB=on): что делал этап после прогона Unity.

    /// <summary>Сколько раз этап Fab запускался (в «тихих» прогонах он ничего не открывает).</summary>
    public int FabStages { get; set; }

    /// <summary>Сколько раз ради него открывался браузер.</summary>
    public int FabSessions { get; set; }

    public int FabAdded { get; set; }

    /// <summary>Ассеты, которые получает только человек (раздача −100 %).</summary>
    public int FabNeedsHuman { get; set; }

    public int FabFailed { get; set; }
    public int FabCloudflareByHuman { get; set; }
    public int FabCloudflareSelf { get; set; }

    /// <summary>Сколько раз звали человека к окну и сколько из этих просьб остались без результата.</summary>
    public int FabHumanCalls { get; set; }

    public int FabHumanUnresolved { get; set; }

    /// <summary>Зачем звали человека: «галочка Cloudflare», «вход в Epic», «лицензия или кнопка» → сколько раз.</summary>
    public Dictionary<string, int> FabHumanReasons { get; set; } = new();

    // --- Память (только Linux): по этим числам видно, хватает ли её на общем сервере и нужен ли `mem_limit`.

    /// <summary>Пик памяти контейнера за прогон, МБ (0 — не читалась). Без кэша файлов.</summary>
    public int MemPeakMb { get; set; }

    /// <summary>Пик памяти контейнера за сеанс Fab (пока открыт браузер), МБ.</summary>
    public int MemFabPeakMb { get; set; }

    /// <summary>Меньше всего было доступно в системе, МБ (0 — не читалась).</summary>
    public int MemMinAvailMb { get; set; }

    public int MemSwapUsedMaxMb { get; set; }
    public int MemSwapTotalMb { get; set; }

    /// <summary>Добавляет итог замера: за весь прогон или (fab) только за сеанс Fab.</summary>
    public void AddMemory(MemorySampler sampler, bool fab)
    {
        if (!sampler.HasData)
        {
            return;
        }

        if (fab)
        {
            MemFabPeakMb = Math.Max(MemFabPeakMb, sampler.PeakContainerMb);
        }
        else
        {
            MemPeakMb = Math.Max(MemPeakMb, sampler.PeakContainerMb);
        }

        if (sampler.MinAvailableMb is { } a)
        {
            MemMinAvailMb = MemMinAvailMb == 0 ? a : Math.Min(MemMinAvailMb, a);
        }

        MemSwapUsedMaxMb = Math.Max(MemSwapUsedMaxMb, sampler.MaxSwapUsedMb);
        MemSwapTotalMb = Math.Max(MemSwapTotalMb, sampler.SwapTotalMb);
    }

    /// <summary>Учитывает вызов человека; причина берётся из заголовка просьбы.</summary>
    public void AddFabHumanCall(string title)
    {
        FabHumanCalls++;
        var reason = ClassifyFabHumanCall(title);
        FabHumanReasons[reason] = FabHumanReasons.GetValueOrDefault(reason) + 1;
    }

    public static string ClassifyFabHumanCall(string title)
    {
        var t = title.ToLowerInvariant();
        return t.Contains("человек") ? "галочка Cloudflare"
            : t.Contains("войдите") ? "вход в Epic"
            : "лицензия или кнопка";
    }

    /// <summary>Учитывает одно чтение каналов. Посты без Asset Store раскладывает по сайтам.</summary>
    public void AddTelegram(TelegramParseResult result)
    {
        PostsRead += result.AllPosts.Count;
        AssetStoreLinks += result.AssetUrls.Count;
        ChannelsFailed += result.FailedChannels.Count;

        foreach (var post in result.AllPosts)
        {
            var text = post.Text;
            if (text.Contains("assetstore.unity.com", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (text.Contains("fab.com", StringComparison.OrdinalIgnoreCase))
            {
                FabPosts++;
            }
            else if (text.Contains("itch.io", StringComparison.OrdinalIgnoreCase))
            {
                ItchPosts++;
            }
            else
            {
                OtherPosts++;
            }
        }
    }
}

/// <summary>
/// Суточная сводка бота. Бот пишет, когда что-то добавлено, а пустые прогоны
/// пропускает, чтобы не спамить. Из-за этого молчание нельзя было отличить от
/// поломки: 19–26.09 служба работала, а казалось, что нет. Раз в период бот
/// коротко пишет, что жив и что видел, даже если добавлять было нечего.
///
/// Счётчики хранятся в файле: перезапуск службы их не теряет и лишнюю сводку не шлёт.
/// </summary>
internal sealed class DailyDigest
{
    public DateTime SinceUtc { get; set; } = DateTime.UtcNow;
    public int Runs { get; set; }
    public int Crashes { get; set; }
    public RunStats Total { get; set; } = new();

    /// <summary>Включён ли этап Fab на сервере: от этого зависит, что писать про Fab. В файл не пишется.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool FabOnServer { get; set; }

    /// <summary>Что сказать о Fab отдельной строкой (например «ждёт вас»). В файл не пишется.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? FabNote { get; set; }

    public static DailyDigest Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<DailyDigest>(File.ReadAllText(path));
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Испорченный файл — начинаем счёт заново.
        }

        return new DailyDigest();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Не сохранили — в худшем случае сводка придёт с неполными цифрами.
        }
    }

    public void Add(RunStats? run, bool crashed)
    {
        Runs++;
        if (crashed)
        {
            Crashes++;
        }

        if (run is null)
        {
            return;
        }

        Total.PostsRead += run.PostsRead;
        Total.AssetStoreLinks += run.AssetStoreLinks;
        Total.FabPosts += run.FabPosts;
        Total.ItchPosts += run.ItchPosts;
        Total.OtherPosts += run.OtherPosts;
        Total.ChannelsFailed += run.ChannelsFailed;
        Total.Added += run.Added;
        Total.AlreadyOwned += run.AlreadyOwned;
        Total.PromoFailed += run.PromoFailed;
        Total.Failed += run.Failed;
        Total.Relogins += run.Relogins;
        Total.FabStages += run.FabStages;
        Total.FabSessions += run.FabSessions;
        Total.FabAdded += run.FabAdded;
        Total.FabNeedsHuman += run.FabNeedsHuman;
        Total.FabFailed += run.FabFailed;
        Total.FabCloudflareByHuman += run.FabCloudflareByHuman;
        Total.FabCloudflareSelf += run.FabCloudflareSelf;
        Total.FabHumanCalls += run.FabHumanCalls;
        Total.FabHumanUnresolved += run.FabHumanUnresolved;
        Total.MemPeakMb = Math.Max(Total.MemPeakMb, run.MemPeakMb);
        Total.MemFabPeakMb = Math.Max(Total.MemFabPeakMb, run.MemFabPeakMb);
        if (run.MemMinAvailMb > 0)
        {
            Total.MemMinAvailMb = Total.MemMinAvailMb == 0 ? run.MemMinAvailMb : Math.Min(Total.MemMinAvailMb, run.MemMinAvailMb);
        }

        Total.MemSwapUsedMaxMb = Math.Max(Total.MemSwapUsedMaxMb, run.MemSwapUsedMaxMb);
        Total.MemSwapTotalMb = Math.Max(Total.MemSwapTotalMb, run.MemSwapTotalMb);
        foreach (var (reason, count) in run.FabHumanReasons)
        {
            Total.FabHumanReasons[reason] = Total.FabHumanReasons.GetValueOrDefault(reason) + count;
        }

        if (run.LoginFailed)
        {
            Total.LoginFailed = true;
        }
    }

    public bool IsDue(TimeSpan period, DateTime nowUtc) => nowUtc - SinceUtc >= period;

    public string Describe(string profile, TimeSpan period, DateTime nowUtc)
    {
        var t = Total;
        var problems = Crashes > 0 || t.LoginFailed || t.ChannelsFailed > 0 || t.Failed > 0 ||
                       (FabOnServer && (t.FabFailed > 0 || t.FabHumanUnresolved > 0));
        var sb = new StringBuilder();

        sb.AppendLine($"{(problems ? "🟡" : "🟢")} Сводка за {DescribePeriod(nowUtc - SinceUtc, period)} — профиль {profile}");
        sb.AppendLine($"Прогонов: {Runs}{(Crashes > 0 ? $", из них упало: {Crashes}" : string.Empty)}");
        sb.AppendLine($"Новых постов в каналах: {t.PostsRead}");

        if (t.AssetStoreLinks > 0)
        {
            var parts = new List<string>();
            if (t.Added > 0) parts.Add($"добавлено {t.Added}");
            if (t.AlreadyOwned > 0) parts.Add($"уже были {t.AlreadyOwned}");
            if (t.PromoFailed > 0) parts.Add($"промокод не сработал {t.PromoFailed}");
            if (t.Failed > 0) parts.Add($"ошибок {t.Failed}");
            sb.AppendLine($"Ссылок на Asset Store: {t.AssetStoreLinks}{(parts.Count > 0 ? " — " + string.Join(", ", parts) : string.Empty)}");
        }
        else
        {
            sb.AppendLine("Ссылок на Asset Store: 0");
        }

        var elsewhere = new List<string>();
        if (t.FabPosts > 0) elsewhere.Add($"Fab {t.FabPosts}");
        if (t.ItchPosts > 0) elsewhere.Add($"itch.io {t.ItchPosts}");
        if (t.OtherPosts > 0) elsewhere.Add($"новости и прочее {t.OtherPosts}");
        if (elsewhere.Count > 0)
        {
            sb.AppendLine($"Не Asset Store, пропущены: {string.Join(", ", elsewhere)}");
        }

        if (FabOnServer)
        {
            sb.AppendLine(DescribeFab(t));
            if (t.FabCloudflareByHuman + t.FabCloudflareSelf + t.FabHumanCalls > 0)
            {
                var human = t.FabHumanCalls == 0
                    ? "человека не звали"
                    : $"человека звали {t.FabHumanCalls} раз" +
                      (t.FabHumanReasons.Count > 0 ? $" ({string.Join(", ", t.FabHumanReasons.Select(r => $"{r.Key} — {r.Value}"))})" : string.Empty) +
                      (t.FabHumanUnresolved > 0 ? $", без результата: {t.FabHumanUnresolved}" : string.Empty);
                sb.AppendLine($"Cloudflare: галочек вами {t.FabCloudflareByHuman}, прошли сами {t.FabCloudflareSelf}; {human}");
            }
        }
        else if (t.FabPosts > 0)
        {
            // Без FAB=on сервер Fab не открывает — ассеты забирает Deck или ПК.
            sb.AppendLine("Fab на аккаунт Epic: на Deck или ПК пункт F в меню");
        }

        if (!string.IsNullOrWhiteSpace(FabNote))
        {
            sb.AppendLine(FabNote);
        }

        if (t.MemPeakMb > 0 || t.MemFabPeakMb > 0)
        {
            var parts = new List<string> { $"контейнер до {Math.Max(t.MemPeakMb, t.MemFabPeakMb)} МБ" };
            if (t.MemFabPeakMb > 0) parts.Add($"в сеансе Fab до {t.MemFabPeakMb}");
            if (t.MemMinAvailMb > 0) parts.Add($"в системе доступно не меньше {t.MemMinAvailMb} МБ");
            if (t.MemSwapTotalMb > 0) parts.Add($"подкачка до {t.MemSwapUsedMaxMb} из {t.MemSwapTotalMb} МБ");
            sb.AppendLine("Память: " + string.Join(", ", parts));
            if (FabOnServer && t.FabSessions > 0)
            {
                sb.AppendLine(DescribeMemLimit(t));
            }
        }

        if (t.Relogins > 0)
        {
            sb.AppendLine($"Входов в Unity заново: {t.Relogins}{(t.LoginFailed ? " (был неудачный)" : string.Empty)}");
        }

        if (t.ChannelsFailed > 0)
        {
            sb.AppendLine($"Каналы не открылись: {t.ChannelsFailed} раз");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Нужен ли контейнеру `mem_limit` и какой. Соседей на общем сервере уже защищают подкачка и `oom_score_adj`,
    /// поэтому лимит — необязательная страховка; совет считается от пика с запасом в полтора раза.
    /// </summary>
    internal static string DescribeMemLimit(RunStats t)
    {
        var peak = Math.Max(t.MemPeakMb, t.MemFabPeakMb);
        var advised = Math.Max(400, (int)Math.Ceiling(peak * 1.5 / 50) * 50);
        var text = $"Лимит памяти контейнеру (`mem_limit` в docker-compose.yml) не обязателен: соседей защищают подкачка и oom_score_adj. " +
                   $"Если захотите — не ниже {advised} МБ (пик {peak} МБ × 1,5).";
        if (t.MemMinAvailMb > 0 && t.MemMinAvailMb < 60)
        {
            text += $" ⚠ В системе оставалось меньше 60 МБ ({t.MemMinAvailMb}) — соседям было тесно.";
        }

        return text;
    }

    private static string DescribeFab(RunStats t)
    {
        var parts = new List<string> { $"этап {t.FabStages}×, браузер открывали {t.FabSessions}" };
        if (t.FabAdded > 0) parts.Add($"добавлено {t.FabAdded}");
        if (t.FabNeedsHuman > 0) parts.Add($"ждут вас (раздача) {t.FabNeedsHuman}");
        if (t.FabFailed > 0) parts.Add($"ошибок {t.FabFailed}");
        return "Fab (Epic): " + string.Join(", ", parts);
    }

    public void Reset(DateTime nowUtc)
    {
        SinceUtc = nowUtc;
        Runs = 0;
        Crashes = 0;
        Total = new RunStats();
    }

    private static string DescribePeriod(TimeSpan actual, TimeSpan period)
    {
        // Сводка приходит на первом прогоне после срока, поэтому реальный отрезок чуть длиннее.
        var shown = actual > period && actual - period < TimeSpan.FromHours(12) ? period : actual;
        return shown.TotalHours >= 23.5 && Math.Abs(shown.TotalDays - Math.Round(shown.TotalDays)) < 0.05
            ? Math.Round(shown.TotalDays) == 1 ? "сутки" : $"{Math.Round(shown.TotalDays):0} сут."
            : $"{Math.Max(1, Math.Round(shown.TotalHours)):0} ч";
    }
}
