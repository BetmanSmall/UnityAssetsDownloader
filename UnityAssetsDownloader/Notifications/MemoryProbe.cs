using System.Globalization;

/// <summary>Снимок памяти в мегабайтах. null — этот показатель прочитать не удалось.</summary>
internal sealed record MemorySnapshot(int? ContainerMb, int? AvailableMb, int? SwapUsedMb, int? SwapTotalMb);

/// <summary>
/// Чтение памяти: сколько занимает контейнер (без кэша файлов, он освобождается сам), сколько ещё доступно в
/// системе и сколько занято в подкачке. Нужно, чтобы по логу и суточной сводке было видно, хватает ли памяти на
/// тесном общем сервере и нужен ли контейнеру `mem_limit`, не гоняя `docker stats` руками. Только Linux; нет
/// файлов — вернёт пустой снимок и ничего не сломает.
/// </summary>
internal static class MemoryProbe
{
    public static MemorySnapshot Read(string cgroupRoot = "/sys/fs/cgroup", string meminfoPath = "/proc/meminfo")
    {
        int? container = null;
        int? available = null;
        int? swapUsed = null;
        int? swapTotal = null;

        try
        {
            // cgroup v2 (память контейнера лежит в корне его пространства имён) или v1 (подпапка memory).
            var v2 = Path.Combine(cgroupRoot, "memory.stat");
            var v1 = Path.Combine(cgroupRoot, "memory", "memory.stat");
            if (File.Exists(v2))
            {
                container = ToMb(ParseStatBytes(File.ReadAllText(v2), "anon", "shmem"));
            }
            else if (File.Exists(v1))
            {
                container = ToMb(ParseStatBytes(File.ReadAllText(v1), "total_rss", "total_shmem"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Нет доступа к cgroup — не страшно: останутся показатели системы.
        }

        try
        {
            if (File.Exists(meminfoPath))
            {
                var (availKb, totalKb, freeKb) = ParseMeminfo(File.ReadAllText(meminfoPath));
                available = availKb is { } a ? KbToMb(a) : null;
                swapTotal = totalKb is { } t ? KbToMb(t) : null;
                swapUsed = totalKb is { } tt && freeKb is { } ff ? KbToMb(tt - ff) : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return new MemorySnapshot(container, available, swapUsed, swapTotal);
    }

    private static int? ToMb(long? bytes) => bytes is { } b ? (int)Math.Round(b / 1048576.0) : null;

    private static int KbToMb(long kb) => (int)Math.Round(kb / 1024.0);

    /// <summary>Сумма значений (в байтах) перечисленных строк «имя число» из memory.stat. Нет ни одной — null.</summary>
    internal static long? ParseStatBytes(string text, params string[] keys)
    {
        long sum = 0;
        var found = false;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && keys.Contains(parts[0]) &&
                long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                sum += value;
                found = true;
            }
        }

        return found ? sum : null;
    }

    /// <summary>MemAvailable, SwapTotal и SwapFree из /proc/meminfo, в килобайтах.</summary>
    internal static (long? AvailableKb, long? SwapTotalKb, long? SwapFreeKb) ParseMeminfo(string text)
    {
        long? Get(string key)
        {
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith(key + ":", StringComparison.Ordinal))
                {
                    continue;
                }

                var number = new string(line[(key.Length + 1)..].Where(char.IsDigit).ToArray());
                return long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
            }

            return null;
        }

        return (Get("MemAvailable"), Get("SwapTotal"), Get("SwapFree"));
    }
}

/// <summary>
/// Пока идёт прогон (или сеанс Fab), раз в несколько секунд смотрит память и запоминает худшее: пик контейнера,
/// минимум свободного в системе, максимум подкачки. Ничего не бросает наружу.
/// </summary>
internal sealed class MemorySampler : IDisposable
{
    private readonly Func<MemorySnapshot> _read;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private Task _loop = Task.CompletedTask;

    private MemorySampler(Func<MemorySnapshot> read) => _read = read;

    /// <summary>Пик памяти контейнера, МБ (0 — не читалась).</summary>
    public int PeakContainerMb { get; private set; }

    /// <summary>Меньше всего было доступно в системе, МБ (null — не читалась).</summary>
    public int? MinAvailableMb { get; private set; }

    public int MaxSwapUsedMb { get; private set; }
    public int SwapTotalMb { get; private set; }

    /// <summary>Удалось прочитать хоть что-то.</summary>
    public bool HasData { get; private set; }

    public static MemorySampler Start(TimeSpan? interval = null, Func<MemorySnapshot>? read = null)
    {
        var sampler = new MemorySampler(read ?? (() => MemoryProbe.Read()));
        var period = interval ?? TimeSpan.FromSeconds(3);
        sampler.Sample();
        sampler._loop = Task.Run(async () =>
        {
            try
            {
                while (!sampler._cts.IsCancellationRequested)
                {
                    await Task.Delay(period, sampler._cts.Token);
                    sampler.Sample();
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
        return sampler;
    }

    /// <summary>Последний замер и остановка. Можно вызывать несколько раз.</summary>
    public MemorySampler Stop()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
        }

        Sample();
        return this;
    }

    private void Sample()
    {
        MemorySnapshot snapshot;
        try
        {
            snapshot = _read();
        }
        catch (Exception)
        {
            return;
        }

        lock (_sync)
        {
            if (snapshot.ContainerMb is { } c)
            {
                PeakContainerMb = Math.Max(PeakContainerMb, c);
                HasData = true;
            }

            if (snapshot.AvailableMb is { } a)
            {
                MinAvailableMb = MinAvailableMb is { } m ? Math.Min(m, a) : a;
                HasData = true;
            }

            if (snapshot.SwapUsedMb is { } s)
            {
                MaxSwapUsedMb = Math.Max(MaxSwapUsedMb, s);
                HasData = true;
            }

            if (snapshot.SwapTotalMb is { } t)
            {
                SwapTotalMb = t;
            }
        }
    }

    /// <summary>Одной строкой для лога: «контейнер до 412 МБ, в системе доступно не меньше 96 МБ, подкачка до 1450 из 2449 МБ».</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (PeakContainerMb > 0)
        {
            parts.Add($"контейнер до {PeakContainerMb} МБ");
        }

        if (MinAvailableMb is { } a)
        {
            parts.Add($"в системе доступно не меньше {a} МБ");
        }

        if (SwapTotalMb > 0)
        {
            parts.Add($"подкачка до {MaxSwapUsedMb} из {SwapTotalMb} МБ");
        }

        return string.Join(", ", parts);
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}
