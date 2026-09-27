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
        if (run.LoginFailed)
        {
            Total.LoginFailed = true;
        }
    }

    public bool IsDue(TimeSpan period, DateTime nowUtc) => nowUtc - SinceUtc >= period;

    public string Describe(string profile, TimeSpan period, DateTime nowUtc)
    {
        var t = Total;
        var problems = Crashes > 0 || t.LoginFailed || t.ChannelsFailed > 0 || t.Failed > 0;
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
