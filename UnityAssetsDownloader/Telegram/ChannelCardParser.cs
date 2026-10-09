using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>Что за пост в канале с ассетами.</summary>
internal enum ChannelPostKind
{
    /// <summary>Карточка ассета: название, издатель, ссылка на Asset Store (формат канала UnityAssets2D с 10.2025).</summary>
    Card,

    /// <summary>
    /// Раздача файла через бота («СКАЧАТЬ», t.me/…bot?start=file_… или start=download_…). Обычно это платные
    /// ассеты, отданные без оплаты: платные не берём и ссылку на бота нигде не храним. Ассет из такого поста
    /// берётся только если он бесплатен в самом магазине (цену проверяет TelegramSourceParser).
    /// </summary>
    DownloadBot,

    /// <summary>Остальное: пустые и служебные посты, посты без ссылки на магазин.</summary>
    Other
}

/// <summary>
/// Что канал сообщил об ассете. Почти всё остальное (раздел, теги, версии Unity, размер, картинки)
/// каталог берёт из магазина по номеру; из поста нужны номер и то, чего в магазине нет:
/// описание по-русски, темы канала и откуда мы об ассете узнали.
/// </summary>
internal sealed class ChannelAssetCard
{
    public string Channel { get; init; } = string.Empty;
    public int PostId { get; init; }
    public DateTime? PostedUtc { get; init; }

    public string AssetId { get; init; } = string.Empty;

    /// <summary>Адрес из поста, без параметров: https://assetstore.unity.com/packages/…</summary>
    public string StoreUrl { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string? Publisher { get; init; }

    /// <summary>Описание из поста: перевод слов издателя, обычно по-русски. Может не быть.</summary>
    public string? Description { get; init; }

    /// <summary>Темы канала (#2D #GUI) в одном написании: строчные, слова через дефис.</summary>
    public List<string> Topics { get; init; } = [];
}

internal sealed class ChannelPostParse
{
    public ChannelPostKind Kind { get; init; }

    /// <summary>
    /// Что пост рассказывает об ассете. Есть у карточки и, если пост удалось разобрать, у раздачи через бота
    /// (описание и темы — из поста, ссылки на бота в карточке нет).
    /// </summary>
    public ChannelAssetCard? Card { get; init; }

    /// <summary>Ассеты магазина, на которые ссылается пост: номер и адрес без параметров. Заполнено у раздач через бота.</summary>
    public List<(string Id, string Url)> StoreAssets { get; init; } = [];
}

/// <summary>
/// Разбор поста канала с ассетом. Три шаблона (одни и те же ленты — UnityAssets2D, UnityAssetsTools):
///
///   1. Карточка (с 10.2025):
///        🎮 Название (версия)          версии бывает нет
///        Описание по-русски            необязательно
///        ⭐⭐⭐⭐⭐ Рейтинг: 5.0/5        необязательно, в описание не входит
///        👤 Издатель: Имя
///        🌐 Перейти на Unity Asset Store      ← ссылка спрятана за этими словами
///        💬 Обсудить
///        #2D  #GUI  #Icons
///
///   2. Карточка с раздачей (с 10.2025): та же «🎮», но название — часть адреса («nodecanvas-tasks+-|-visual-scripting»),
///      издателя нет, а вместо «🌐 Перейти…» — «🔍 Ознакомиться с ассетом» и адрес магазина прямо в тексте.
///
///   3. Старый шаблон (03.2024 – 09.2025): сначала хэштеги, потом название и описание, потом адрес магазина
///      и «📥 СКАЧАТЬ». Это только разбор текста; раздача через бота определяется по ссылке на бота.
///
///   4. Шаблон «название сверху» (UnityAssetsNewFree, 09.2024 – 09.2025):
///        Название
///        Описание
///        🔗Источник   💬Обсудить   🗂Все архивы      ← ссылка на магазин спрятана за словом «Источник»
///        #3D  #Props
///
/// Эталон — настоящие страницы канала в harness/fixtures (сценарий CARD).
/// </summary>
internal static class ChannelCardParser
{
    // Строка с такой иконкой — уже не описание. ⭐ — рейтинг («⭐⭐⭐⭐⭐ Рейтинг: 5.0/5»): магазин отдаёт его сам.
    private static readonly string[] BlockMarkers = ["👤", "🌐", "💬", "🔍", "📥", "⭐", "🔗", "🗂", "☕"];

    // Конец описания в старом шаблоне: после него идут подписи канала.
    private static readonly string[] TailMarkers = ["👉", "🔹", "🌟", "📁"];

    // Хэштеги, которые канал ставит на все свои посты: темой ассета они не являются.
    private static readonly HashSet<string> ChannelHashtags = new(StringComparer.Ordinal)
        { "unitypackage", "unity", "package", "assets" };

    // (1.0), (1.0.0.1), (v1.0.0 - LITE), (v1.0.0 Free). «(2D & 3D)» или «(FREE)» версией не считаются.
    private static readonly Regex VersionRegex = new(
        @"^[vV]?\d+(?:\.\d+)*(?:[\s\-–].{0,20})?$",
        RegexOptions.Compiled);

    // «Конечно! Пожалуйста, предоставьте текст…», «Готов перевести. Пришлите текст…», «Извините, я не могу получить доступ
    // к внешним ссылкам…» и подобное: начало такой фразы и слова про перевод или текст неподалёку (85 абзацев в 7 каналах).
    private static readonly Regex TranslatorRefusalRegex = new(
        @"^(?:Конечно|Готов|Извините|Пожалуйста|Присылайте|Если\s+(?:же\s+)?(?:это|нужно)|""[^""]{1,80}""\s+переводится)\b" +
        @"[\s\S]{0,200}?(?:перевод|перевести|предостав\w*\s+текст|пришлите|не\s+могу\s+получить\s+доступ)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Сырой «#» внутри названия в ссылке («…/3d-sci-fi-vegetation-#02/312938»): после него идёт номер ассета, это не
    // фрагмент адреса. Магазин пишет его как %23; без этого номер терялся и ассет в очередь не попадал.
    private static readonly Regex RawHashInSlugRegex = new(@"#(?=[^/#?]*/\d+/?(?:\?|$))", RegexOptions.Compiled);

    private static readonly Regex SingleWordTagRegex = new(@"#([\p{L}\p{N}_]+)", RegexOptions.Compiled);

    private static readonly Regex TrailingGroupRegex = new(@"\s*\(([^()]*)\)\s*$", RegexOptions.Compiled);

    private static readonly HashSet<string> TelegramHosts = new(StringComparer.OrdinalIgnoreCase)
        { "t.me", "telegram.me", "telegram.dog" };

    /// <summary>Все посты страницы канала (t.me/s/&lt;канал&gt;) в порядке страницы.</summary>
    public static List<ChannelPostParse> ParsePage(string channel, string? html) =>
        TelegramHtmlParser.ExtractRawPosts(html).Select(p => Parse(channel, p)).ToList();

    public static ChannelPostParse Parse(string channel, TelegramRawPost post)
    {
        var lines = post.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var storeAssets = post.Links
            .Select(StoreLink)
            .Where(s => s is not null)
            .Select(s => s!.Value)
            .DistinctBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
        var card = BuildCard(post, lines, storeAssets);

        // Раздача через бота важнее всего остального: даже если в посте есть ссылка на магазин.
        // Ассет из неё берётся, только если он бесплатен в магазине; решает это не разбор поста.
        if (post.Links.Any(IsDownloadBotLink))
        {
            return new ChannelPostParse { Kind = ChannelPostKind.DownloadBot, Card = card, StoreAssets = storeAssets };
        }

        return card is null
            ? new ChannelPostParse { Kind = ChannelPostKind.Other }
            : new ChannelPostParse { Kind = ChannelPostKind.Card, Card = card };
    }

    // Что пост говорит об ассете. null — пост не про один понятный ассет магазина.
    private static ChannelAssetCard? BuildCard(
        TelegramRawPost post, List<string> lines, List<(string Id, string Url)> storeAssets)
    {
        if (lines.Count == 0 || storeAssets.Count == 0)
        {
            return null;
        }

        var store = storeAssets[0];
        string title;
        string? version = null;
        string description;
        string? publisher = null;
        var topics = TopicsOf(lines);

        if (lines[0].StartsWith("🎮", StringComparison.Ordinal))
        {
            (title, version) = SplitTitle(lines[0][2..].TrimStart('️', ' '));
            description = string.Join(' ', lines.Skip(1)
                .TakeWhile(l => !l.StartsWith('#') && !BlockMarkers.Any(m => l.StartsWith(m, StringComparison.Ordinal))));
            publisher = PublisherOf(lines);
        }
        else if (lines[0].StartsWith('#') && storeAssets.Count == 1)
        {
            // Старый шаблон: хэштеги, [название], описание, адрес, «СКАЧАТЬ». Описание кончается на адресе или подписи.
            var body = lines
                .Where(l => !l.StartsWith('#'))
                .TakeWhile(l => !IsTail(l))
                .ToList();

            // Первая короткая строка при ещё одной за ней — название; длинная — уже описание.
            title = body.Count >= 2 && body[0].Length <= 80 ? body[0] : string.Empty;
            description = string.Join(' ', body.Skip(title.Length > 0 ? 1 : 0));

            // В старом шаблоне тема — одно слово после «#» («#Assets 1799» — тема assets и номер архива).
            topics = lines.Where(l => l.StartsWith('#'))
                .SelectMany(l => SingleWordTagRegex.Matches(l).Select(m => NormalizeTopic(m.Groups[1].Value)))
                .Where(t => t.Length > 0 && !ChannelHashtags.Contains(t))
                .Distinct()
                .ToList();
        }
        else if (storeAssets.Count == 1 && lines.Any(l => l.StartsWith("🔗", StringComparison.Ordinal)))
        {
            // «Название сверху»: название, описание, «🔗Источник …», хэштеги.
            (title, version) = SplitTitle(lines[0]);
            description = string.Join(' ', lines.Skip(1)
                .TakeWhile(l => !l.StartsWith('#') && !BlockMarkers.Any(m => l.StartsWith(m, StringComparison.Ordinal))));
        }
        else
        {
            return null;
        }

        // Канал переводит описания ИИ; когда переводить нечего, тот отвечает отказом. Это не описание ассета.
        if (TranslatorRefusalRegex.IsMatch(description.TrimStart()))
        {
            description = string.Empty;
        }

        return new ChannelAssetCard
        {
            Channel = post.PostId.Split('/')[0],
            PostId = int.TryParse(post.PostId.Split('/').Last(), out var number) ? number : 0,
            PostedUtc = post.PostedUtc,
            AssetId = store.Id,
            StoreUrl = store.Url,
            Title = title,
            Version = version,
            Publisher = publisher,
            Description = Regex.Replace(System.Net.WebUtility.HtmlDecode(description), @"\s+", " ").Trim() is { Length: > 0 } d ? d : null,
            Topics = topics
        };
    }

    // Строка старого шаблона после описания: адрес, «СКАЧАТЬ [версия]», подписи канала.
    private static bool IsTail(string line) =>
        line.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("СКАЧАТЬ", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("САЧАТЬ", StringComparison.OrdinalIgnoreCase) ||
        BlockMarkers.Concat(TailMarkers).Any(m => line.StartsWith(m, StringComparison.Ordinal));

    /// <summary>«Textures &amp; Materials» и «textures_materials» → textures-materials.</summary>
    public static string NormalizeTopic(string raw)
    {
        var topic = raw.Trim().ToLowerInvariant().Replace('&', ' ').Replace('_', ' ');
        return Regex.Replace(topic, @"[\s\-]+", "-").Trim('-');
    }

    private static (string Title, string? Version) SplitTitle(string line)
    {
        var group = TrailingGroupRegex.Match(line);
        return group.Success && VersionRegex.IsMatch(group.Groups[1].Value.Trim())
            ? (line[..group.Index].Trim(), group.Groups[1].Value.Trim())
            : (line, null);
    }

    private static string? PublisherOf(List<string> lines)
    {
        var line = lines.FirstOrDefault(l => l.StartsWith("👤", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var name = line[2..].Trim();
        var colon = name.IndexOf(':');
        name = (colon >= 0 ? name[(colon + 1)..] : name).Trim();
        return name.Length > 0 ? name : null;
    }

    private static List<string> TopicsOf(List<string> lines) =>
        lines.Where(l => l.StartsWith('#'))
            .SelectMany(l => l.Split('#', StringSplitOptions.RemoveEmptyEntries))
            .Select(NormalizeTopic)
            .Where(t => t.Length > 0)
            .Distinct()
            .ToList();

    // Ссылка на ассет магазина: номер и адрес без параметров.
    private static (string Id, string Url)? StoreLink(string href)
    {
        href = RawHashInSlugRegex.Replace(href, "%23");
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri) ||
            !(uri.Host.Equals("assetstore.unity.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("www.assetstore.unity.com", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var id = UnityAssetAutomationApp.ExtractPackageId(uri.AbsolutePath);
        return id is null ? null : (id, "https://assetstore.unity.com" + uri.AbsolutePath);
    }

    // Ссылка на бота, который отдаёт файл: t.me/<что-то>bot?start=file_… (LockBotChanel_bot) или start=download_…
    // (tg_game_market_bot). Любая другая ссылка на бота — не раздача: подпись «наш бот: …?start=channel» или
    // реферальная ссылка не должны отбрасывать пост с бесплатным ассетом.
    private static bool IsDownloadBotLink(string href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri) || !TelegramHosts.Contains(uri.Host))
        {
            return false;
        }

        var first = uri.AbsolutePath.Trim('/').Split('/')[0];
        if (!first.EndsWith("bot", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var start = System.Web.HttpUtility.ParseQueryString(uri.Query)["start"];
        return start is not null &&
               (start.StartsWith("file_", StringComparison.OrdinalIgnoreCase) ||
                start.StartsWith("download_", StringComparison.OrdinalIgnoreCase));
    }
}
