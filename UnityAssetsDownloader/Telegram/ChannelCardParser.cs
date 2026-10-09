using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>Что за пост в канале с ассетами.</summary>
internal enum ChannelPostKind
{
    /// <summary>Карточка ассета: название, издатель, ссылка на Asset Store (формат канала UnityAssets2D с 10.2025).</summary>
    Card,

    /// <summary>
    /// Раздача файла через бота («СКАЧАТЬ», t.me/…bot?start=file_… или start=download_…). По ценам магазина
    /// это платные ассеты, отданные без оплаты: такие посты в каталог не берём и ссылки на бота нигде не храним.
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
    public ChannelAssetCard? Card { get; init; }
}

/// <summary>
/// Разбор карточки ассета из поста канала. Формат (UnityAssets2D, с 10.2025):
///
///   🎮 Название (версия)          версии бывает нет
///   Описание по-русски            необязательно
///   👤 Издатель: Имя
///   🌐 Перейти на Unity Asset Store      ← ссылка спрятана за этими словами
///   💬 Обсудить
///   #2D  #GUI  #Icons
///
/// Эталон — настоящие страницы канала в harness/fixtures (сценарий CARD).
/// </summary>
internal static class ChannelCardParser
{
    private static readonly string[] BlockMarkers = ["👤", "🌐", "💬", "🔍", "📥"];

    // (1.0), (1.0.0.1), (v1.0.0 - LITE), (v1.0.0 Free). «(2D & 3D)» или «(FREE)» версией не считаются.
    private static readonly Regex VersionRegex = new(
        @"^[vV]?\d+(?:\.\d+)*(?:[\s\-–].{0,20})?$",
        RegexOptions.Compiled);

    private static readonly Regex TrailingGroupRegex = new(@"\s*\(([^()]*)\)\s*$", RegexOptions.Compiled);

    private static readonly HashSet<string> TelegramHosts = new(StringComparer.OrdinalIgnoreCase)
        { "t.me", "telegram.me", "telegram.dog" };

    /// <summary>Все посты страницы канала (t.me/s/&lt;канал&gt;) в порядке страницы.</summary>
    public static List<ChannelPostParse> ParsePage(string channel, string? html) =>
        TelegramHtmlParser.ExtractRawPosts(html).Select(p => Parse(channel, p)).ToList();

    public static ChannelPostParse Parse(string channel, TelegramRawPost post)
    {
        // Раздача через бота важнее всего остального: даже если в посте есть ссылка на магазин.
        if (post.Links.Any(IsDownloadBotLink))
        {
            return new ChannelPostParse { Kind = ChannelPostKind.DownloadBot };
        }

        var lines = post.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || !lines[0].StartsWith("🎮", StringComparison.Ordinal))
        {
            return new ChannelPostParse { Kind = ChannelPostKind.Other };
        }

        var store = post.Links
            .Select(StoreLink)
            .FirstOrDefault(s => s is not null);
        if (store is null)
        {
            return new ChannelPostParse { Kind = ChannelPostKind.Other };
        }

        var (title, version) = SplitTitle(lines[0][2..].TrimStart('️', ' '));
        var description = string.Join(' ', lines.Skip(1)
            .TakeWhile(l => !l.StartsWith('#') && !BlockMarkers.Any(m => l.StartsWith(m, StringComparison.Ordinal))));

        return new ChannelPostParse
        {
            Kind = ChannelPostKind.Card,
            Card = new ChannelAssetCard
            {
                Channel = post.PostId.Split('/')[0],
                PostId = int.TryParse(post.PostId.Split('/').Last(), out var number) ? number : 0,
                PostedUtc = post.PostedUtc,
                AssetId = store.Value.Id,
                StoreUrl = store.Value.Url,
                Title = title,
                Version = version,
                Publisher = PublisherOf(lines),
                Description = Regex.Replace(description, @"\s+", " ").Trim() is { Length: > 0 } d ? d : null,
                Topics = TopicsOf(lines)
            }
        };
    }

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
