using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Ночной добор истории каналов — настройки (NIGHT_BACKFILL_* в .env) и расписание.
///
/// Служба в обычных прогонах читает только новые посты; старые посты каналов (UnityAssetsTools, UnityAssetsNewFree —
/// больше тысячи бесплатных ассетов) раньше проходились руками через ./backfill.sh. Пользователь (11.10): «остальное сервер
/// заберёт сам, не торопимся — можно растянуть». Поэтому каждую ночь служба берёт порцию истории одного канала той же
/// цепочкой, что backfill.sh (пачки по 30, известное пропускается, платное отсеивается по цене заранее), и останавливается
/// на лимите открытых страниц. Канал, история которого прочитана и проверена до конца, считается пройденным.
/// </summary>
internal sealed class NightBackfillSettings
{
    public const int DefaultPagesPerNight = 100;
    public const int DefaultStartHour = 1;
    public const int DefaultEndHour = 6;

    /// <summary>Каналы по порядку: сначала первый, пройден — следующий. Пусто — добор выключен.</summary>
    public IReadOnlyList<string> Channels { get; init; } = [];

    /// <summary>Сколько страниц ассетов открыть за ночь (≈20–25 с каждая на сервере): 100 — около 40 минут.</summary>
    public int PagesPerNight { get; init; } = DefaultPagesPerNight;

    /// <summary>Окно по местному времени (TZ контейнера): с StartHour:00 до EndHour:00; может переходить через полночь.</summary>
    public int StartHour { get; init; } = DefaultStartHour;

    public int EndHour { get; init; } = DefaultEndHour;

    public bool Enabled => Channels.Count > 0;

    public static NightBackfillSettings FromEnvironment(Action<string> warn) => Parse(
        Environment.GetEnvironmentVariable("NIGHT_BACKFILL_CHANNELS"),
        Environment.GetEnvironmentVariable("NIGHT_BACKFILL_PAGES"),
        Environment.GetEnvironmentVariable("NIGHT_BACKFILL_HOURS"),
        warn);

    internal static NightBackfillSettings Parse(string? channels, string? pages, string? hours, Action<string> warn)
    {
        var list = (channels ?? string.Empty)
            .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeChannel)
            .Where(c => c.Length > 0 && !c.Equals("off", StringComparison.OrdinalIgnoreCase) && c != "-")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var perNight = DefaultPagesPerNight;
        if (!string.IsNullOrWhiteSpace(pages))
        {
            if (int.TryParse(pages.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 2000)
            {
                perNight = p;
            }
            else
            {
                warn($"[Добор] Не понял NIGHT_BACKFILL_PAGES='{pages}' (нужно число от 1 до 2000). Берём {DefaultPagesPerNight}.");
            }
        }

        int start = DefaultStartHour, end = DefaultEndHour;
        if (!string.IsNullOrWhiteSpace(hours))
        {
            var m = Regex.Match(hours.Trim(), @"^(\d{1,2})(?::00)?\s*-\s*(\d{1,2})(?::00)?$");
            if (m.Success && int.Parse(m.Groups[1].Value) is var s and >= 0 and <= 23 &&
                int.Parse(m.Groups[2].Value) is var e and >= 0 and <= 23 && s != e)
            {
                start = s;
                end = e;
            }
            else
            {
                warn($"[Добор] Не понял NIGHT_BACKFILL_HOURS='{hours}' (пример: 1-6). Берём {DefaultStartHour}-{DefaultEndHour}.");
            }
        }

        return new NightBackfillSettings { Channels = list, PagesPerNight = perNight, StartHour = start, EndHour = end };
    }

    /// <summary>Имя канала из «@name», «t.me/name», «https://t.me/s/name».</summary>
    private static string NormalizeChannel(string raw)
    {
        var c = raw.Trim();
        foreach (var prefix in new[] { "https://", "http://", "t.me/", "telegram.me/", "s/", "@" })
        {
            if (c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                c = c[prefix.Length..];
            }
        }

        return c.Split('/', '?')[0];
    }

    public bool InWindow(DateTime local) =>
        StartHour < EndHour
            ? local.Hour >= StartHour && local.Hour < EndHour
            : local.Hour >= StartHour || local.Hour < EndHour;

    /// <summary>К какой ночи относится момент: дата начала окна (окно 23–5 в 02:00 — ночь вчерашней даты).</summary>
    public DateOnly NightOf(DateTime local) =>
        StartHour > EndHour && local.Hour < EndHour
            ? DateOnly.FromDateTime(local.AddDays(-1))
            : DateOnly.FromDateTime(local);

    /// <summary>Начало окна той ночи, к которой относится дата.</summary>
    public DateTime WindowStart(DateOnly night) => night.ToDateTime(new TimeOnly(StartHour, 0));

    /// <summary>
    /// Аргументы прогона добора из аргументов службы: без --watch и её расписания, только этот канал, вся история
    /// (не «только новые»), не больше pages страниц, без этапа Fab (он идёт только при --watch).
    /// </summary>
    public static string[] BackfillArgs(IReadOnlyList<string> serviceArgs, string channel, int pages)
    {
        var withValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "--watch-interval", "--sources", "--tg-channels", "--max-visited-assets", "--max-add-attempts" };
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--watch", "--tg-only-new", "--history-backfill" };

        var result = new List<string>();
        for (var i = 0; i < serviceArgs.Count; i++)
        {
            if (withValue.Contains(serviceArgs[i]))
            {
                i++;
                continue;
            }

            if (!flags.Contains(serviceArgs[i]))
            {
                result.Add(serviceArgs[i]);
            }
        }

        result.AddRange(["--sources", "telegram", "--tg-channels", channel,
            "--max-visited-assets", pages.ToString(CultureInfo.InvariantCulture), "--history-backfill"]);
        return result.ToArray();
    }
}

/// <summary>
/// Что ночной добор уже сделал (data/night_backfill.json): какая ночь отработана, какие каналы пройдены, итог прошлой ночи.
/// Ночь отмечается в начале прогона: упавший или прерванный прогон не повторяется до следующей ночи, а следующая
/// продолжает с того же места (уже проверенное пропускается по памяти профиля и каталогу) — повторный запуск безопасен.
/// Пока прогон идёт, в файле лежит Running: если служба поднялась, а отметка осталась, — прогон прервали снаружи
/// (перезапуск контейнера, перезагрузка, нехватка памяти), и это видно в сводке, а не итог прошлой ночи как последний.
/// </summary>
internal sealed class NightBackfillState
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Последняя ночь, в которую добор запускался («2026-10-12»).</summary>
    public string? LastNight { get; set; }

    public Dictionary<string, NightChannelProgress> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Итог последнего прогона добора — для суточной сводки.</summary>
    public NightRunResult? Last { get; set; }

    /// <summary>Прогон, который сейчас идёт (канал и начало); null — не идёт.</summary>
    public NightRunStart? Running { get; set; }

    public static NightBackfillState Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var state = JsonSerializer.Deserialize<NightBackfillState>(File.ReadAllText(path), Json) ?? new NightBackfillState();
                state.Channels = new Dictionary<string, NightChannelProgress>(state.Channels, StringComparer.OrdinalIgnoreCase);
                return state;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Битый файл — начнём заново: пройденные каналы быстро пройдутся ещё раз (всё известное пропускается).
        }

        return new NightBackfillState();
    }

    /// <summary>Сохраняет; false — не вышло (тогда служба не будит себя добором снова в ту же минуту).</summary>
    public bool Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Первый по порядку канал, история которого ещё не пройдена, или null.</summary>
    public string? NextChannel(NightBackfillSettings settings) =>
        settings.Channels.FirstOrDefault(c => !(Channels.TryGetValue(c, out var p) && p.Done));

    /// <summary>Пора ли добирать сейчас: включено, ночь, этой ночью ещё не добирали, есть непройденный канал.</summary>
    public bool IsDue(NightBackfillSettings settings, DateTime local) =>
        settings.Enabled && settings.InWindow(local) && LastNight != Key(settings.NightOf(local)) && NextChannel(settings) is not null;

    /// <summary>Когда службе проснуться ради добора: сейчас (если пора), начало ближайшей неотработанной ночи или null.</summary>
    public DateTime? NextWake(NightBackfillSettings settings, DateTime local)
    {
        if (!settings.Enabled || NextChannel(settings) is null)
        {
            return null;
        }

        if (IsDue(settings, local))
        {
            return local;
        }

        var night = settings.NightOf(local);
        for (var i = 0; i < 3; i++, night = night.AddDays(1))
        {
            var start = settings.WindowStart(night);
            if (start > local && Key(night) != LastNight)
            {
                return start;
            }
        }

        return null;
    }

    public void MarkStarted(NightBackfillSettings settings, DateTime local, string channel)
    {
        LastNight = Key(settings.NightOf(local));
        Running = new NightRunStart { Channel = channel, StartedAt = Stamp(local) };
    }

    /// <summary>
    /// Итог ночи: сколько добавлено и страниц открыто; finished — история канала пройдена до конца; hung — прогон завис,
    /// и сторож службы перезапустил её (числа — сколько успели).
    /// </summary>
    public void Record(string channel, int added, int pages, bool finished, bool crashed, DateTime local, bool hung = false)
    {
        var progress = ProgressOf(channel);
        progress.Nights++;
        progress.Added += added;
        progress.Pages += pages;
        if (finished)
        {
            progress.Done = true;
            progress.DoneOn = Key(DateOnly.FromDateTime(local));
        }

        Last = new NightRunResult
        {
            Channel = channel, Added = added, Pages = pages, Finished = finished, Crashed = crashed, Hung = hung,
            At = Stamp(local)
        };
        Running = null;
    }

    /// <summary>
    /// Прогон прервали снаружи: служба поднялась, а отметка «идёт» осталась. Записывает его как прерванный (итог не
    /// известен; канал продолжит следующая ночь) и возвращает строку для лога; отметки нет — null. Повторно ничего не делает.
    /// </summary>
    public string? ResolveInterrupted()
    {
        if (Running is not { } run)
        {
            return null;
        }

        ProgressOf(run.Channel).Nights++;
        Last = new NightRunResult { Channel = run.Channel, Interrupted = true, At = run.StartedAt };
        Running = null;
        return $"Ночной добор {run.StartedAt} ({run.Channel}) был прерван — служба перезапускалась, итог не записан. " +
               "Канал продолжит следующая ночь.";
    }

    private NightChannelProgress ProgressOf(string channel)
    {
        if (!Channels.TryGetValue(channel, out var progress))
        {
            Channels[channel] = progress = new NightChannelProgress();
        }

        return progress;
    }

    /// <summary>Строка для суточной сводки бота или null, если добор выключен.</summary>
    public string? Describe(NightBackfillSettings settings)
    {
        if (!settings.Enabled)
        {
            return null;
        }

        var done = settings.Channels.Count(c => Channels.TryGetValue(c, out var p) && p.Done);
        var next = NextChannel(settings);
        var last = Last switch
        {
            null => string.Empty,
            { Interrupted: true } l => $"{l.At}: {l.Channel} — прогон прерван (служба перезапускалась), итог не записан. ",
            var l => $"{l.At}: {l.Channel} — добавлено {l.Added}, открыто страниц {l.Pages}" +
                     (l.Crashed ? ", прогон упал"
                         : l.Hung ? ", прогон завис — служба перезапустилась сама"
                         : l.Finished ? ", канал пройден"
                         : string.Empty) + ". "
        };
        return next is null
            ? $"Ночной добор: {last}все каналы пройдены ({string.Join(", ", settings.Channels)})."
            : $"Ночной добор: {last}Пройдено каналов {done} из {settings.Channels.Count}, дальше — {next}.";
    }

    private static string Key(DateOnly night) => night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Stamp(DateTime local) => local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

internal sealed class NightChannelProgress
{
    public bool Done { get; set; }
    public string? DoneOn { get; set; }
    public int Nights { get; set; }
    public int Added { get; set; }
    public int Pages { get; set; }
}

internal sealed class NightRunResult
{
    public string Channel { get; set; } = string.Empty;
    public int Added { get; set; }
    public int Pages { get; set; }
    public bool Finished { get; set; }
    public bool Crashed { get; set; }

    /// <summary>Прогон завис, сторож службы её перезапустил (Added и Pages — сколько успели).</summary>
    public bool Hung { get; set; }

    /// <summary>Прогон прервали снаружи (служба перезапускалась): итог не известен, Added и Pages — 0.</summary>
    public bool Interrupted { get; set; }

    public string At { get; set; } = string.Empty;
}

internal sealed class NightRunStart
{
    public string Channel { get; set; } = string.Empty;
    public string StartedAt { get; set; } = string.Empty;
}
