using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

/// <summary>Пост канала, из которого мы узнали об ассете.</summary>
internal sealed class AssetSource
{
    /// <summary>Откуда: telegram.</summary>
    public string Kind { get; set; } = "telegram";

    public string Channel { get; set; } = string.Empty;
    public int Post { get; set; }
    public DateTime? PostedUtc { get; set; }

    /// <summary>Темы канала в одном написании: 2d, gui, textures-materials.</summary>
    public List<string>? Topics { get; set; }

    /// <summary>Описание из поста, как в канале (обычно по-русски).</summary>
    public string? Text { get; set; }

    public static AssetSource FromCard(ChannelAssetCard card) => new()
    {
        Channel = card.Channel,
        Post = card.PostId,
        PostedUtc = card.PostedUtc,
        Topics = card.Topics.Count > 0 ? card.Topics : null,
        Text = AssetCatalogEntry.Truncate(card.Description, 500)
    };
}

/// <summary>
/// Один ассет в каталоге библиотеки: то, что нужно ИИ-агенту и человеку, чтобы понять,
/// что это за ассет, не открывая его страницу в магазине.
///
/// Данные берутся из GraphQL магазина (<see cref="AssetStoreProductApi"/>). Поля — плоские
/// и с понятными именами: assets.jsonl читают grep'ом, строка ассета должна читаться сама.
/// </summary>
internal sealed class AssetCatalogEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Publisher { get; set; }

    /// <summary>Раздел магазина, например 3d/animations или tools/utilities.</summary>
    public string? Category { get; set; }

    /// <summary>Теги магазина (popularTags): walk, run, humanoid…</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Конвейеры рендера: builtin, urp, hdrp, custom. Пусто — магазин не знает.</summary>
    public List<string> Pipelines { get; set; } = [];

    /// <summary>Версии Unity, под которые загружен пакет (supportedUnityVersions).</summary>
    public List<string> UnityVersions { get; set; } = [];

    /// <summary>Версия самого ассета и дата её выхода.</summary>
    public string? Version { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public DateTime? FirstPublishedUtc { get; set; }

    public long? SizeBytes { get; set; }
    public int? FileCount { get; set; }
    public double? Rating { get; set; }
    public int? RatingCount { get; set; }

    public string? Image { get; set; }
    public string? ImageBig { get; set; }

    /// <summary>Короткое описание издателя (elevatorPitch), без HTML.</summary>
    public string? Pitch { get; set; }

    /// <summary>Начало полного описания без HTML — для разметки ИИ и подробной карточки.</summary>
    public string? Description { get; set; }

    public string? KeyFeatures { get; set; }

    /// <summary>published, deprecated (снят с продажи, но у владельцев остаётся), disabled, missing.</summary>
    public string State { get; set; } = "published";

    /// <summary>Бесплатный ли в магазине сейчас. null — цена неизвестна (снятые с продажи).</summary>
    public bool? Free { get; set; }
    public string? Price { get; set; }

    /// <summary>Издатель отметил, что в ассете есть контент, сделанный ИИ.</summary>
    public bool AiMade { get; set; }

    /// <summary>Место в библиотеке: 0 — получен последним. null — в списке «My Assets» не нашёлся.</summary>
    public int? Rank { get; set; }

    /// <summary>Как получен этой программой: free, promo. null — был на аккаунте раньше.</summary>
    public string? How { get; set; }
    public string? PromoCode { get; set; }
    public DateTime? AddedUtc { get; set; }

    /// <summary>
    /// Откуда мы узнали об ассете помимо списка «My Assets»: посты каналов с темами и описанием
    /// по-русски. null — таких нет (писать пустой список в каждую строку jsonl незачем).
    /// </summary>
    public List<AssetSource>? Sources { get; set; }

    /// <summary>Свои метки по правилам (<see cref="AssetTagRules"/>): 3d, style:low-poly, rp:urp…</summary>
    public List<string> Marks { get; set; } = [];

    /// <summary>Разметка ИИ — копия из ai-тегов на момент записи, чтобы jsonl был самодостаточным.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AssetAiTag? Ai { get; set; }

    public DateTime FetchedUtc { get; set; }

    public static AssetCatalogEntry FromProduct(string id, JsonElement? product, DateTime nowUtc)
    {
        var entry = new AssetCatalogEntry { Id = id, FetchedUtc = nowUtc };
        if (product is not { ValueKind: JsonValueKind.Object } p)
        {
            // Магазин о таком номере не знает вовсе: ассет удалён совсем.
            entry.State = "missing";
            entry.Name = $"#{id}";
            entry.Url = $"https://assetstore.unity.com/packages/package/{id}";
            return entry;
        }

        entry.Name = Str(p, "name") ?? $"#{id}";
        entry.State = Str(p, "state") ?? "published";
        entry.Category = Str(Obj(p, "category"), "slug");
        var slug = Str(p, "slug");
        entry.Url = entry.Category is not null && slug is not null
            ? $"https://assetstore.unity.com/packages/{entry.Category}/{slug}"
            : $"https://assetstore.unity.com/packages/package/{id}";
        entry.Publisher = Str(Obj(p, "publisher"), "name");

        if (Obj(p, "popularTags") is { ValueKind: JsonValueKind.Array } tags)
        {
            entry.Tags = tags.EnumerateArray()
                .Select(t => Str(t, "name")?.Trim())
                .Where(t => !string.IsNullOrEmpty(t))
                .Select(t => t!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (Obj(p, "srps") is { ValueKind: JsonValueKind.Array } srps)
        {
            entry.Pipelines = srps.EnumerateArray()
                .Where(s => s.TryGetProperty("types", out var t) && t.ValueKind == JsonValueKind.Array)
                .SelectMany(s => s.GetProperty("types").EnumerateArray())
                .Select(t => PipelineName(t.GetString()))
                .Where(t => t is not null)
                .Select(t => t!)
                .Distinct()
                .OrderBy(PipelineOrder)
                .ToList();
        }

        if (Obj(p, "supportedUnityVersions") is { ValueKind: JsonValueKind.Array } versions)
        {
            entry.UnityVersions = versions.EnumerateArray()
                .Select(v => v.GetString())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .Distinct()
                .OrderBy(ParseUnityVersion)
                .ToList();
        }

        var current = Obj(p, "currentVersion");
        entry.Version = Str(current, "name");
        entry.UpdatedUtc = Date(Str(current, "publishedDate"));
        entry.FirstPublishedUtc = Date(Str(p, "firstPublishedDate"));

        entry.SizeBytes = long.TryParse(Str(p, "downloadSize"), out var size) && size > 0 ? size : null;
        entry.FileCount = Obj(p, "assetCount") is { ValueKind: JsonValueKind.Number } count ? count.GetInt32() : null;

        var rating = Obj(p, "rating");
        if (Obj(rating, "count") is { ValueKind: JsonValueKind.Number } ratingCount && ratingCount.GetInt32() > 0)
        {
            entry.RatingCount = ratingCount.GetInt32();
            entry.Rating = Obj(rating, "average") is { ValueKind: JsonValueKind.Number } avg ? Math.Round(avg.GetDouble(), 1) : null;
        }

        var image = Obj(p, "mainImage");
        entry.Image = AbsoluteUrl(Str(image, "small") ?? Str(image, "icon"));
        entry.ImageBig = AbsoluteUrl(Str(image, "big"));

        entry.Pitch = Truncate(HtmlToText(Str(p, "elevatorPitch")), 300);
        entry.Description = Truncate(HtmlToText(Str(p, "description")), 700);
        entry.KeyFeatures = Truncate(HtmlToText(Str(p, "keyFeatures")), 400);
        entry.AiMade = !string.IsNullOrWhiteSpace(HtmlToText(Str(p, "aiDescription")));

        var price = Obj(p, "originalPrice");
        if (price is { ValueKind: JsonValueKind.Object })
        {
            entry.Free = Obj(price, "isFree") is { ValueKind: JsonValueKind.True or JsonValueKind.False } isFree ? isFree.GetBoolean() : null;
            var final = Str(price, "finalPrice");
            if (entry.Free == false && !string.IsNullOrWhiteSpace(final))
            {
                entry.Price = $"{final} {Str(price, "currency")}".Trim();
            }
        }

        return entry;
    }

    /// <summary>Переносит то, что знает только эта программа (как и когда получен), из старой записи.</summary>
    public void KeepHistoryFrom(AssetCatalogEntry? old)
    {
        if (old is null)
        {
            return;
        }

        How ??= old.How;
        PromoCode ??= old.PromoCode;
        AddedUtc ??= old.AddedUtc;
        Rank ??= old.Rank;

        // Данные магазина обновились, а откуда мы про ассет узнали — нет: источники старой записи сохраняются.
        foreach (var source in old.Sources ?? [])
        {
            AddSource(source);
        }
    }

    /// <summary>Добавляет источник, если такого поста у ассета ещё нет. true — запись изменилась.</summary>
    public bool AddSource(AssetSource source)
    {
        Sources ??= [];
        if (Sources.Any(s => s.Channel == source.Channel && s.Post == source.Post))
        {
            return false;
        }

        Sources.Add(source);
        Sources = Sources.OrderByDescending(s => s.PostedUtc).ToList();
        return true;
    }

    /// <summary>Описание из поста канала (перевод слов издателя, обычно по-русски), самое свежее.</summary>
    [JsonIgnore]
    public string? PostText => Sources?.Select(s => s.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

    /// <summary>Темы каналов (#2D #GUI → 2d, gui) со всех постов, без повторов.</summary>
    [JsonIgnore]
    public IEnumerable<string> Topics => (Sources ?? []).SelectMany(s => s.Topics ?? []).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Самая старая версия Unity из поддерживаемых, коротко: 2021.3, 6000.0.</summary>
    [JsonIgnore]
    public string? UnityMin => UnityVersions.Count == 0 ? null : ShortUnityVersion(UnityVersions[0]);

    [JsonIgnore]
    public string? UnityMax => UnityVersions.Count == 0 ? null : ShortUnityVersion(UnityVersions[^1]);

    [JsonIgnore]
    public bool IsAvailable => State == "published";

    /// <summary>Строка «о чём ассет»: разметка ИИ, иначе слова издателя, иначе начало описания, иначе описание из поста канала.</summary>
    [JsonIgnore]
    public string Summary => FirstNonEmpty(Ai?.Sum, StoreSummary, PostText) ?? string.Empty;

    /// <summary>То же, но только словами издателя — для страницы, где разметка ИИ показана отдельно.</summary>
    [JsonIgnore]
    public string? StoreSummary => FirstNonEmpty(Pitch, FirstSentence(KeyFeatures), FirstSentence(Description));

    public static string? PipelineName(string? storeType) => storeType?.ToLowerInvariant() switch
    {
        "standard" => "builtin",
        "lightweight" or "universal" => "urp",
        "hd" => "hdrp",
        "custom" => "custom",
        null or "" => null,
        var other => other
    };

    private static int PipelineOrder(string name) => name switch
    {
        "builtin" => 0,
        "urp" => 1,
        "hdrp" => 2,
        _ => 3
    };

    /// <summary>6000.0.59f2 → 6000.0, 2019.4.0 → 2019.4, 5.6.3 → 5.6.</summary>
    public static string ShortUnityVersion(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{new string(parts[1].TakeWhile(char.IsDigit).ToArray())}" : version;
    }

    /// <summary>Номер для сравнения версий Unity: 2019.4.1 → 2019_004_001.</summary>
    public static long ParseUnityVersion(string version)
    {
        var parts = version.Split('.');
        long Part(int i) => i < parts.Length && long.TryParse(new string(parts[i].TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0;
        return Part(0) * 1_000_000 + Part(1) * 1_000 + Part(2);
    }

    /// <summary>HTML описания магазина → одна строка текста: абзацы и пункты списка через «; ».</summary>
    public static string? HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = Regex.Replace(html, @"<\s*(br|/p|/li|/h\d|/div)\b[^>]*>", "; ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]*>", " ");
        text = WebUtility.HtmlDecode(text);
        // Издатели пишут внутри HTML ещё и markdown: **жирный**, «* пункт».
        text = text.Replace("**", string.Empty).Replace("__", string.Empty);
        text = Regex.Replace(text, @"(^|;)\s*[*•\-–]\s+", "$1 ");
        text = Regex.Replace(text, @"\s+", " ");
        text = Regex.Replace(text, @"(\s*;\s*)+", "; ");
        text = text.Trim(' ', ';');
        return text.Length == 0 ? null : text;
    }

    public static string? Truncate(string? text, int max)
    {
        if (text is null || text.Length <= max)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', max - 1);
        return (cut > max / 2 ? text[..cut] : text[..(max - 1)]).TrimEnd(' ', ';', ',', '.') + "…";
    }

    private static string? FirstSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Regex.Match(text, @"^.{20,200}?[.!?;](\s|$)");
        return Truncate(match.Success ? match.Value.Trim().TrimEnd(';') : text, 200);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? AbsoluteUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url;

    private static DateTime? Date(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;

    private static JsonElement? Obj(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    private static string? Str(JsonElement? element, string name) =>
        Obj(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
