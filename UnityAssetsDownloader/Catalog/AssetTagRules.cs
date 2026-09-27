using System.Text.RegularExpressions;

/// <summary>
/// Свои метки ассета — по правилам, без ИИ. Дополняют раздел и теги магазина тем, по чему
/// удобно отбирать: 2D или 3D, что это по сути, под какой конвейер, в каком стиле.
///
/// Метки короткие и одного вида («группа:значение»), чтобы их можно было искать grep'ом:
/// grep "style:low-poly" catalog/categories/*.md
/// </summary>
internal static class AssetTagRules
{
    private static readonly (string Mark, Regex Pattern)[] TextMarks =
    [
        ("style:low-poly", Words("low[ -]?poly", "lowpoly", "polyart", "poly art", "flat shad(ed|ing)")),
        ("style:pixel", Words("pixel[ -]?art", "pixel", "8[ -]?bit", "16[ -]?bit", "retro sprite")),
        ("style:stylized", Words("styli[sz]ed", "cartoon", "toon", "cel[ -]?shad(ed|ing)", "hand[ -]?painted", "chibi")),
        ("style:realistic", Words("realistic", "photo[ -]?real(istic)?", "photo[ -]?scan(ned)?", "scanned", "pbr")),
        ("style:voxel", Words("voxel")),
        ("style:anime", Words("anime", "manga")),
        ("theme:sci-fi", Words("sci[ -]?fi", "science fiction", "space ?ship", "cyberpunk", "futuristic", "robot")),
        ("theme:fantasy", Words("fantasy", "rpg", "magic", "dragon", "dungeon", "elf", "orc", "wizard")),
        ("theme:medieval", Words("medieval", "castle", "knight", "viking")),
        ("theme:horror", Words("horror", "zombie", "creepy", "haunted")),
        ("theme:military", Words("military", "army", "soldier", "tank", "weapon", "gun", "rifle", "pistol")),
        ("theme:modern", Words("modern", "city", "urban", "office", "street")),
        ("theme:nature", Words("nature", "forest", "tree", "grass", "rock", "terrain", "landscape")),
        ("theme:casual", Words("casual", "hyper[ -]?casual", "puzzle", "match[ -]?3", "idle (clicker|game|tycoon)", "clicker", "tycoon")),
        ("platform:mobile", Words("mobile", "android", "ios")),
        ("platform:vr", Words("vr", "xr", "oculus", "meta quest", "virtual reality", "openxr")),
    ];

    /// <summary>Раздел магазина → что это по сути. Первое совпадение по началу slug.</summary>
    private static readonly (string Prefix, string Kind)[] CategoryKinds =
    [
        ("3d/animations", "animation"),
        ("3d/characters", "character"),
        ("3d/environments", "environment"),
        ("3d/vegetation", "vegetation"),
        ("3d/vehicles", "vehicle"),
        ("3d/props", "prop"),
        ("3d/gui", "ui"),
        ("2d/characters", "character"),
        ("2d/environments", "environment"),
        ("2d/gui", "ui"),
        ("2d/fonts", "font"),
        ("2d/textures-materials", "texture"),
        ("3d/textures-materials", "texture"),
        ("2d/", "sprite"),
        ("3d/", "model"),
        ("vfx/shaders", "shader"),
        ("vfx/particles", "particles"),
        ("vfx/", "vfx"),
        ("audio/music", "music"),
        ("audio/sound-fx", "sfx"),
        ("audio/ambient", "ambient"),
        ("audio/", "audio"),
        ("tools/gui", "ui"),
        ("tools/", "tool"),
        ("templates/", "template"),
        ("essentials/", "essentials"),
        ("add-ons/", "add-on"),
    ];

    public static List<string> Compute(AssetCatalogEntry e)
    {
        var marks = new List<string>();
        var category = e.Category ?? string.Empty;
        var root = category.Split('/')[0];
        if (root.Length > 0)
        {
            marks.Add(root switch
            {
                "tools" => "tool",
                "templates" => "template",
                "add-ons" => "add-on",
                _ => root
            });
        }

        var kind = CategoryKinds.FirstOrDefault(k => category.StartsWith(k.Prefix, StringComparison.OrdinalIgnoreCase)).Kind;
        if (kind is not null && !marks.Contains(kind))
        {
            marks.Add("kind:" + kind);
        }

        marks.AddRange(e.Pipelines.Where(p => p != "custom").Select(p => "rp:" + p));

        // Стиль и тема — по имени, тегам и коротким описаниям. Полное описание не берём:
        // в нём издатели перечисляют всё подряд («подходит для fantasy, sci-fi, modern…»).
        var text = string.Join(" | ", new[] { e.Name, string.Join(", ", e.Tags), e.Pitch }.Where(s => !string.IsNullOrWhiteSpace(s)));
        foreach (var (mark, pattern) in TextMarks)
        {
            if (pattern.IsMatch(text))
            {
                marks.Add(mark);
            }
        }

        if (Regex.IsMatch(e.Name, @"\b(free|lite|sample|demo|starter|trial)\b", RegexOptions.IgnoreCase))
        {
            marks.Add("lite");
        }

        if (e.UnityVersions.Count > 0 && AssetCatalogEntry.ParseUnityVersion(e.UnityVersions[^1]) < AssetCatalogEntry.ParseUnityVersion("2021.0.0"))
        {
            marks.Add("old");
        }

        if (!e.IsAvailable)
        {
            marks.Add(e.State == "missing" ? "missing" : "deprecated");
        }

        if (e.AiMade)
        {
            marks.Add("ai-made");
        }

        return marks.Distinct().ToList();
    }

    private static Regex Words(params string[] alternatives) =>
        new($@"(?<![\p{{L}}\d])({string.Join("|", alternatives)})(?![\p{{L}}\d])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
