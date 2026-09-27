using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Разметка ассета, сделанная ИИ один раз (в чате Claude Code), чтобы потом не разбираться заново.</summary>
internal sealed class AssetAiTag
{
    /// <summary>Суть одной строкой по-русски: что внутри и чем полезно.</summary>
    [JsonPropertyName("sum")]
    public string? Sum { get; set; }

    /// <summary>Для чего годится: жанры, роли в игре, «прототип» / «финальная графика».</summary>
    [JsonPropertyName("use")]
    public List<string> Use { get; set; } = [];

    /// <summary>Свои теги ИИ, которых нет у магазина.</summary>
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    /// <summary>Подводные камни: «только HDRP», «нужен Input System», «старый, под 2018».</summary>
    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
}

/// <summary>
/// Разметка ИИ по номерам ассетов. Номер ассета один для всех аккаунтов, поэтому разметка
/// общая: файл asset_ai_tags.json лежит в репозитории и едет на сервер и в сборку для класса
/// вместе с программой. Рядом с данными можно положить свой такой же файл — он главнее.
/// </summary>
internal static class AssetAiTags
{
    public const string FileName = "asset_ai_tags.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Рядом с программой (и выше — для запуска из bin/Debug), затем в папке данных.</summary>
    public static List<string> CandidatePaths(string dataDirectory)
    {
        var nearProgram = new[]
            {
                Path.Combine(Directory.GetCurrentDirectory(), FileName),
                Path.Combine(AppContext.BaseDirectory, FileName),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", FileName),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", FileName)
            }
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);

        var paths = new List<string>();
        if (nearProgram is not null)
        {
            paths.Add(nearProgram);
        }

        paths.Add(Path.GetFullPath(Path.Combine(dataDirectory, FileName)));
        return paths;
    }

    public static Dictionary<string, AssetAiTag> Load(IEnumerable<string> paths, Action<string>? warn = null)
    {
        var result = new Dictionary<string, AssetAiTag>(StringComparer.Ordinal);
        foreach (var path in paths.Where(File.Exists))
        {
            try
            {
                var part = JsonSerializer.Deserialize<Dictionary<string, AssetAiTag>>(File.ReadAllText(path), JsonOptions);
                foreach (var (id, tag) in part ?? [])
                {
                    if (!id.StartsWith('_') && tag is not null)
                    {
                        result[id] = tag;
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                warn?.Invoke($"[Каталог] Файл разметки ИИ не читается, пропускаем: {path} ({ex.Message})");
            }
        }

        return result;
    }
}
