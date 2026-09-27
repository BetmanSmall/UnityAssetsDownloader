using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Каталог библиотеки аккаунта: все ассеты с разделом, тегами, конвейерами, версиями Unity
/// и сутью — чтобы ИИ-агент находил нужное grep'ом за десятки токенов, а человек смотрел
/// одну HTML-страницу. Лежит в профиле: data/profiles/&lt;профиль&gt;/catalog/.
///
/// Главный файл — assets.jsonl (строка на ассет). Остальное каждый раз собирается из него:
/// INDEX.md (оглавление), categories/*.md (строка на ассет по разделам), ai_todo.jsonl
/// (что ещё не размечено ИИ), README.md и catalog.html.
/// </summary>
internal sealed class AssetCatalog
{
    public const string FolderName = "catalog";

    /// <summary>Данные ассета перечитываются из магазина раз в столько дней.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions LineJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions MetaJson = new(LineJson) { WriteIndented = true };

    private readonly Dictionary<string, AssetCatalogEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>Место каждого ассета в последнем списке «My Assets» (0 — получен последним).</summary>
    private Dictionary<string, int>? _libraryRank;

    public AssetCatalog(string directory)
    {
        Directory = directory;
    }

    public string Directory { get; }
    public string AssetsPath => Path.Combine(Directory, "assets.jsonl");
    public string HtmlPath => Path.Combine(Directory, "catalog.html");
    public string IndexPath => Path.Combine(Directory, "INDEX.md");
    private string MetaPath => Path.Combine(Directory, "meta.json");

    public CatalogMeta Meta { get; private set; } = new();
    public int Count => _entries.Count;
    public IEnumerable<AssetCatalogEntry> Entries => _entries.Values;
    public bool Changed { get; private set; }

    public static AssetCatalog Load(string profileDirectory)
    {
        var catalog = new AssetCatalog(Path.Combine(profileDirectory, FolderName));
        try
        {
            if (File.Exists(catalog.AssetsPath))
            {
                foreach (var line in File.ReadLines(catalog.AssetsPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var entry = JsonSerializer.Deserialize<AssetCatalogEntry>(line, LineJson);
                    if (entry is not null && entry.Id.Length > 0)
                    {
                        catalog._entries[entry.Id] = entry;
                    }
                }
            }

            if (File.Exists(catalog.MetaPath))
            {
                catalog.Meta = JsonSerializer.Deserialize<CatalogMeta>(File.ReadAllText(catalog.MetaPath), LineJson) ?? new CatalogMeta();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Битый каталог не повод падать: соберётся заново из магазина.
            catalog._entries.Clear();
            catalog.Meta = new CatalogMeta();
        }

        return catalog;
    }

    public bool ContainsId(string? id) => id is not null && _entries.ContainsKey(id);

    /// <summary>Ассет по адресу из поста или списка — есть ли он в библиотеке аккаунта.</summary>
    public bool ContainsUrl(string url) => ContainsId(UnityAssetAutomationApp.ExtractPackageId(url));

    public AssetCatalogEntry? Get(string id) => _entries.GetValueOrDefault(id);

    public void Upsert(AssetCatalogEntry entry)
    {
        entry.KeepHistoryFrom(_entries.GetValueOrDefault(entry.Id));
        if (_libraryRank is not null)
        {
            entry.Rank = _libraryRank.TryGetValue(entry.Id, out var r) ? r : entry.Rank;
        }

        _entries[entry.Id] = entry;
        Changed = true;
    }

    /// <summary>Эта программа только что добавила ассет: запоминаем как и когда.</summary>
    public void MarkAdded(string id, string how, string? promoCode, DateTime whenUtc)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            entry.How = how;
            entry.PromoCode = promoCode;
            entry.AddedUtc = whenUtc;
            entry.Rank ??= -1;
            Changed = true;
        }
    }

    /// <summary>
    /// Применяет список «My Assets»: порядок (0 — получен последним) и отметку времени.
    /// Возвращает номера, данные которых надо взять из магазина: новые и устаревшие.
    /// </summary>
    public List<string> ApplyLibrary(IReadOnlyList<string> ids, DateTime nowUtc, bool refreshAll = false)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ids.Count; i++)
        {
            rank.TryAdd(ids[i], i);
        }

        _libraryRank = rank;

        foreach (var entry in _entries.Values)
        {
            entry.Rank = rank.TryGetValue(entry.Id, out var r) ? r : null;
        }

        Meta.LibrarySyncedUtc = nowUtc;
        Meta.LibraryCount = ids.Count;
        Changed = true;

        return ids
            .Where(id => refreshAll || !_entries.TryGetValue(id, out var e) || nowUtc - e.FetchedUtc > StaleAfter || e.State == "missing")
            .Distinct()
            .ToList();
    }

    public bool LibrarySyncDue(TimeSpan period, DateTime nowUtc) =>
        Meta.LibrarySyncedUtc is not { } synced || nowUtc - synced >= period;

    /// <summary>
    /// Пересобирает все файлы каталога. Метки правил и разметка ИИ пересчитываются каждый
    /// раз: поправили правила или дописали разметку — достаточно пересобрать.
    ///
    /// copyTo — куда ещё положить копию (--catalog-dir). Папка запоминается в meta.json, и
    /// дальше копия обновляется при каждой записи сама; «-» — перестать копировать.
    /// </summary>
    public void Write(IReadOnlyDictionary<string, AssetAiTag> ai, string title, string? copyTo = null)
    {
        Meta.CopyDir = copyTo switch
        {
            "-" => null,
            { Length: > 0 } dir => Path.GetFullPath(dir),
            _ => Meta.CopyDir
        };

        System.IO.Directory.CreateDirectory(Directory);
        var nowUtc = DateTime.UtcNow;
        foreach (var e in _entries.Values)
        {
            e.Marks = AssetTagRules.Compute(e);
            e.Ai = ai.GetValueOrDefault(e.Id);
        }

        var ordered = _entries.Values
            .OrderBy(e => e.Rank ?? int.MaxValue)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Meta.Title = title;
        Meta.WrittenUtc = nowUtc;
        Meta.Total = ordered.Count;
        Meta.WithAi = ordered.Count(e => e.Ai is not null);

        WriteAtomically(AssetsPath, string.Join('\n', ordered.Select(e => JsonSerializer.Serialize(e, LineJson))) + "\n");
        var sections = WriteCategoryFiles(ordered);
        WriteAtomically(IndexPath, BuildIndex(ordered, sections));
        WriteAtomically(Path.Combine(Directory, "ai_todo.jsonl"), BuildAiTodo(ordered));
        WriteAtomically(Path.Combine(Directory, "README.md"), BuildReadme());
        WriteAtomically(HtmlPath, CatalogHtml.Build(ordered, Meta));
        WriteAtomically(MetaPath, JsonSerializer.Serialize(Meta, MetaJson));
        Changed = false;

        if (Meta.CopyDir is { } target)
        {
            CopyTo(target);
        }
    }

    /// <summary>Раздел для файла: первые две части slug. 3d/props/weapons → 3d-props.</summary>
    public static string SectionOf(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return "other";
        }

        return string.Join('-', category.Split('/', StringSplitOptions.RemoveEmptyEntries).Take(2));
    }

    /// <summary>Строка ассета для categories/*.md. Цель — около 50 токенов.</summary>
    public static string FormatLine(AssetCatalogEntry e)
    {
        var marks = e.Marks.Where(m => !IsObviousMark(m, e)).ToList();
        var tags = e.Tags.Concat(e.Ai?.Tags ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Take(6);
        var labels = string.Join(" ", marks);
        var tagText = string.Join(", ", tags);
        var summary = AssetCatalogEntry.Truncate(e.Summary, 150);
        if (e.Ai?.Use is { Count: > 0 } use)
        {
            summary += $" [{string.Join("; ", use)}]";
        }

        if (e.Ai?.Note is { Length: > 0 } note)
        {
            summary += $" ⚠ {note}";
        }

        return string.Join(" | ", new[]
        {
            e.Id,
            e.Name.Replace('|', '/'),
            e.Category ?? "?",
            e.Pipelines.Count > 0 ? string.Join(",", e.Pipelines) : "rp?",
            e.UnityMin is { } u ? "u" + u : "u?",
            FormatSize(e.SizeBytes),
            (labels + (labels.Length > 0 && tagText.Length > 0 ? " · " : "") + tagText).Replace('|', '/'),
            (summary ?? string.Empty).Replace('|', '/')
        });
    }

    public static string FormatSize(long? bytes) => bytes switch
    {
        null => "?",
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.#}GB".Replace(',', '.'),
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0}MB",
        _ => $"{Math.Max(1, bytes.Value / 1024)}KB"
    };

    // Раздел и конвейеры и так стоят в строке отдельными полями — их метки не повторяем.
    private static bool IsObviousMark(string mark, AssetCatalogEntry e) =>
        mark.StartsWith("rp:", StringComparison.Ordinal) ||
        mark == (e.Category ?? string.Empty).Split('/')[0] ||
        mark is "tool" or "template" or "add-on";

    private Dictionary<string, List<AssetCatalogEntry>> WriteCategoryFiles(List<AssetCatalogEntry> ordered)
    {
        var folder = Path.Combine(Directory, "categories");
        System.IO.Directory.CreateDirectory(folder);
        var sections = ordered
            .GroupBy(e => SectionOf(e.Category))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var old in System.IO.Directory.GetFiles(folder, "*.md"))
        {
            if (!sections.ContainsKey(Path.GetFileNameWithoutExtension(old)))
            {
                File.Delete(old);
            }
        }

        foreach (var (section, items) in sections)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {section} — {items.Count} ассетов");
            sb.AppendLine();
            sb.AppendLine("id | название | раздел | конвейеры | Unity от | размер | метки · теги | о чём");
            sb.AppendLine();
            // Сначала то, что можно скачать, от полученных последним; снятые с продажи — в конце.
            foreach (var e in items.OrderBy(e => e.IsAvailable ? 0 : 1).ThenBy(e => e.Rank ?? int.MaxValue))
            {
                sb.AppendLine(FormatLine(e));
            }

            WriteAtomically(Path.Combine(folder, section + ".md"), sb.ToString());
        }

        return sections;
    }

    private string BuildIndex(List<AssetCatalogEntry> ordered, Dictionary<string, List<AssetCatalogEntry>> sections)
    {
        var available = ordered.Count(e => e.IsAvailable);
        var sb = new StringBuilder();
        sb.AppendLine($"# Каталог ассетов Unity — {Meta.Title}");
        sb.AppendLine();
        sb.AppendLine($"Обновлён {Meta.WrittenUtc:yyyy-MM-dd HH:mm} UTC. Ассетов: **{ordered.Count}**, из них в продаже {available}, " +
                      $"сняты с продажи {ordered.Count - available} (у владельца скачиваются). С разметкой ИИ: {Meta.WithAi}.");
        if (Meta.LibrarySyncedUtc is { } synced)
        {
            sb.AppendLine($"Список «My Assets» сверен {synced:yyyy-MM-dd HH:mm} UTC.");
        }
        else
        {
            sb.AppendLine("Здесь только ассеты, добавленные программой. Вся библиотека — `--build-catalog` (пункт K в меню).");
        }

        sb.AppendLine();
        sb.AppendLine("## Как искать (для ИИ-агента)");
        sb.AppendLine();
        sb.AppendLine("Этот файл — только оглавление. Не читайте разделы целиком: ищите grep'ом, строка = ассет.");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine("grep -i \"inventory\" categories/*.md                 # по слову в названии, тегах, сути");
        sb.AppendLine("grep \"kind:character\" categories/3d-*.md | grep \"urp\"  # 3D-персонажи под URP");
        sb.AppendLine("grep \"style:low-poly\" categories/*.md | grep -v deprecated");
        sb.AppendLine("grep '\"id\":\"154271\"' assets.jsonl                 # всё об одном ассете");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Строка раздела: `id | название | раздел магазина | конвейеры | Unity от | размер | метки · теги магазина | о чём`.");
        sb.AppendLine("Страница ассета: https://assetstore.unity.com/packages/package/<id>. Все ассеты уже на аккаунте — ");
        sb.AppendLine("ставятся через Package Manager → My Assets.");
        sb.AppendLine();
        sb.AppendLine("## Метки");
        sb.AppendLine();
        sb.AppendLine("- `kind:` что это: animation, character, environment, prop, vegetation, vehicle, model, sprite, texture, ui, font, shader, particles, vfx, music, sfx, ambient, audio, essentials");
        sb.AppendLine("- конвейеры: builtin, urp, hdrp (`rp?` — магазин не знает); `u2021.3` — самая старая поддерживаемая Unity");
        sb.AppendLine("- `style:` low-poly, pixel, stylized, realistic, voxel, anime; `theme:` sci-fi, fantasy, medieval, horror, military, modern, nature, casual; `platform:` mobile, vr");
        sb.AppendLine("- `lite` — бесплатная урезанная версия (Free/Lite/Sample/Demo в названии); `old` — только Unity до 2021;");
        sb.AppendLine("  `deprecated` — снят с продажи; `missing` — магазин о нём не знает; `ai-made` — издатель указал контент от ИИ");
        sb.AppendLine("- в конце строки из разметки ИИ: суть по-русски, `[для чего годится]`, после `⚠` — подвох");
        sb.AppendLine();
        sb.AppendLine("## Разделы");
        sb.AppendLine();
        sb.AppendLine("| Файл | Ассетов | Подразделы (сколько) |");
        sb.AppendLine("|---|---|---|");
        foreach (var (section, items) in sections.OrderByDescending(s => s.Value.Count))
        {
            var subs = items
                .GroupBy(e => (e.Category ?? "?").Split('/').Skip(2).FirstOrDefault() ?? "—")
                .OrderByDescending(g => g.Count())
                .Take(8)
                .Select(g => $"{g.Key} {g.Count()}");
            sb.AppendLine($"| categories/{section}.md | {items.Count} | {string.Join(", ", subs)} |");
        }

        sb.AppendLine();
        sb.AppendLine("Для человека — catalog.html: карточки с картинками, поиск и фильтры.");
        return sb.ToString();
    }

    private static string BuildAiTodo(List<AssetCatalogEntry> ordered)
    {
        var sb = new StringBuilder();
        foreach (var e in ordered.Where(e => e.Ai is null && e.State != "missing").OrderBy(e => e.IsAvailable ? 0 : 1).ThenBy(e => e.Rank ?? int.MaxValue))
        {
            sb.Append(JsonSerializer.Serialize(new
            {
                id = e.Id,
                name = e.Name,
                category = e.Category,
                tags = e.Tags.Take(10),
                rp = e.Pipelines,
                unity = e.UnityMin,
                pitch = e.Pitch,
                features = AssetCatalogEntry.Truncate(e.KeyFeatures, 250),
                desc = AssetCatalogEntry.Truncate(e.Description, 400),
                state = e.IsAvailable ? null : e.State
            }, LineJson)).Append('\n');
        }

        return sb.ToString();
    }

    private static string BuildReadme() =>
        """
        # Каталог ассетов — что здесь и как этим пользоваться

        Собирает UnityAssetsDownloader: `--build-catalog` (пункт K в меню) сверяет список «My Assets»
        аккаунта и берёт данные каждого ассета из магазина. Обычный прогон дописывает сюда добавленные
        ассеты сам; в режиме сервера весь список сверяется раз в неделю (`CATALOG_REFRESH`).

        | Файл | Для кого | Что внутри |
        |---|---|---|
        | INDEX.md | ИИ-агент, начать отсюда | Оглавление: разделы, метки, как искать |
        | categories/*.md | ИИ-агент (grep) | Строка на ассет: id, название, раздел, конвейеры, Unity, размер, метки, суть |
        | assets.jsonl | ИИ-агент (grep по id) | Всё об ассете одной строкой JSON |
        | catalog.html | Человек | Карточки с картинками, поиск, фильтры. Открывается двойным щелчком |
        | ai_todo.jsonl | Разметка ИИ | Ассеты, ещё не размеченные ИИ, в сжатом виде |
        | meta.json | Программа | Когда сверялся список, сколько ассетов |

        Файлы пересобираются программой — правки в них пропадут. Разметка ИИ хранится отдельно,
        в `asset_ai_tags.json` в корне репозитория: номер ассета один для всех аккаунтов.

        ## Разметка ИИ

        В чате Claude Code: «разметь ассеты» (навык `tag-assets`). Claude читает ai_todo.jsonl
        пачками и дописывает в asset_ai_tags.json:

        ```json
        { "154271": { "sum": "Базовые анимации гуманоида: ходьба, бег, прыжки, idle", "use": ["персонаж игрока", "прототип"], "tags": ["mecanim", "retarget"], "note": "только анимации, без модели" } }
        ```

        Затем `--build-catalog` (или любой прогон) вливает разметку в каталог.
        """;

    private void CopyTo(string target)
    {
        System.IO.Directory.CreateDirectory(Path.Combine(target, "categories"));
        foreach (var file in System.IO.Directory.GetFiles(Directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Directory, file);
            var destination = Path.Combine(target, relative);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }

        foreach (var stale in System.IO.Directory.GetFiles(Path.Combine(target, "categories"), "*.md"))
        {
            if (!File.Exists(Path.Combine(Directory, "categories", Path.GetFileName(stale))))
            {
                File.Delete(stale);
            }
        }
    }

    // Сначала во временный файл, потом подмена: обрыв посреди записи не оставит полфайла.
    private static void WriteAtomically(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}

internal sealed class CatalogMeta
{
    public string? Title { get; set; }
    public DateTime? WrittenUtc { get; set; }
    public DateTime? LibrarySyncedUtc { get; set; }
    public int LibraryCount { get; set; }
    public int Total { get; set; }
    public int WithAi { get; set; }

    /// <summary>Куда каждый раз класть копию каталога (запомнено из --catalog-dir).</summary>
    public string? CopyDir { get; set; }
}

/// <summary>catalog.html: шаблон из ресурсов сборки + данные каталога, вшитые в страницу как JSON.</summary>
internal static class CatalogHtml
{
    public const string ResourceName = "catalog-template.html";
    private const string DataPlaceholder = "/*CATALOG_DATA*/";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Template()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"В сборке нет ресурса {ResourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static string Build(IEnumerable<AssetCatalogEntry> entries, CatalogMeta meta)
    {
        var data = new
        {
            title = meta.Title,
            written = meta.WrittenUtc?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            synced = meta.LibrarySyncedUtc?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            items = entries.Select(e => new
            {
                i = e.Id,
                n = e.Name,
                u = e.Url,
                p = e.Publisher,
                c = e.Category,
                t = e.Tags.Count > 0 ? e.Tags.Take(12) : null,
                m = e.Marks,
                rp = e.Pipelines.Count > 0 ? e.Pipelines : null,
                uv = e.UnityMin,
                ux = e.UnityMax,
                v = e.Version,
                up = e.UpdatedUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                s = e.SizeBytes,
                f = e.FileCount,
                r = e.Rating,
                rc = e.RatingCount,
                im = e.Image,
                ib = e.ImageBig,
                d = AssetCatalogEntry.Truncate(e.StoreSummary, 220),
                x = AssetCatalogEntry.Truncate(e.Description, 500),
                ai = e.Ai,
                st = e.IsAvailable ? null : e.State,
                pr = e.Price,
                h = e.How,
                pc = e.PromoCode,
                a = e.AddedUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                k = e.Rank
            })
        };

        // «<» внутри строк JSON → <: ни «</script>», ни «<!--» из описания не сломают страницу.
        var json = JsonSerializer.Serialize(data, Json).Replace("<", "\\u003c");
        return Template().Replace(DataPlaceholder, json);
    }
}
