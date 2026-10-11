using PuppeteerSharp;

/// <summary>
/// Браузер и вкладка магазина, которые переживают смерть вкладки и самого браузера.
///
/// 10.10 на сервере (1 ядро, ~1 ГБ) вкладка умерла посреди репетиции, а программа продолжала открывать ассеты в ней:
/// PuppeteerSharp отвечал за 0 мс «Object reference not set…», и 24 ассета подряд записались в ошибки. Теперь вкладку
/// проверяют перед каждым ассетом и после каждой ошибки: мёртвая заменяется новой в том же браузере, а если не отвечает
/// весь браузер — он перезапускается с той же папкой профиля (вход хранится в ней).
/// </summary>
internal sealed class BrowserKeeper : IAsyncDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<Task<IBrowser>> _launch;
    private readonly Func<IBrowser, IPage, Task> _prepare;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;

    private BrowserKeeper(IBrowser browser, Func<Task<IBrowser>> launch, Func<IBrowser, IPage, Task> prepare,
        Action<string> info, Action<string> warn)
    {
        Browser = browser;
        Page = null!;
        _launch = launch;
        _prepare = prepare;
        _info = info;
        _warn = warn;
    }

    public IBrowser Browser { get; private set; }
    public IPage Page { get; private set; }

    /// <summary>Сколько раз вкладку заменяли новой в том же браузере.</summary>
    public int TabsReplaced { get; private set; }

    /// <summary>Сколько раз перезапускали браузер. После перезапуска вход нужно проверить заново.</summary>
    public int BrowserRestarts { get; private set; }

    /// <summary>Что браузер сам сообщил о последнем сбое («вкладка упала», «браузер отключился») — для лога.</summary>
    public string? LastCrash { get; private set; }

    /// <summary>«--user-data-dir=…» текущего браузера: по нему находятся его процессы, оставшиеся после убийства.</summary>
    private string? _profileArg;

    /// <summary>
    /// Запускает браузер и открывает вкладку. launch вызывается и при перезапуске — он должен сам снимать
    /// блокировку папки профиля, оставшуюся от убитого браузера. prepare настраивает каждую новую вкладку.
    /// </summary>
    public static async Task<BrowserKeeper> StartAsync(Func<Task<IBrowser>> launch, Func<IBrowser, IPage, Task> prepare,
        Action<string> info, Action<string> warn)
    {
        var keeper = new BrowserKeeper(await launch(), launch, prepare, info, warn);
        keeper.Watch(keeper.Browser);
        keeper.Page = await keeper.OpenPageAsync(keeper.Browser);
        return keeper;
    }

    /// <summary>Вкладка жива и отвечает: браузер на связи, вкладка не закрыта и выполняет скрипт.</summary>
    public Task<bool> IsAliveAsync() => IsResponsiveAsync(Browser, Page);

    /// <summary>
    /// Мёртвую вкладку заменяет новой; если не вышло — перезапускает браузер. true — есть живая вкладка.
    /// Вызывать, когда <see cref="IsAliveAsync"/> вернул false.
    /// </summary>
    public async Task<bool> RecoverAsync(string reason)
    {
        _warn($"[Браузер] {reason}{(LastCrash is null ? string.Empty : $" Браузер сообщил: {LastCrash}.")}");
        LastCrash = null;

        if (Browser.IsConnected)
        {
            IPage? fresh = null;
            try
            {
                fresh = await WithTimeout(OpenPageAsync(Browser), TimeSpan.FromSeconds(30));
                if (await IsResponsiveAsync(Browser, fresh))
                {
                    var dead = Page;
                    Page = fresh;
                    TabsReplaced++;
                    await CloseQuietlyAsync(dead);
                    _info("[Браузер] Открыта новая вкладка, продолжаем.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _warn($"[Браузер] Новая вкладка не открылась: {ex.Message}");
            }

            if (fresh is not null)
            {
                await CloseQuietlyAsync(fresh);
            }
        }

        _warn("[Браузер] Браузер не отвечает — перезапускаем его (вход хранится в папке профиля).");
        await KillQuietlyAsync(Browser, _profileArg, _warn);
        try
        {
            var browser = await _launch();
            Browser = browser;
            Watch(browser);
            Page = await OpenPageAsync(browser);
            BrowserRestarts++;
            if (await IsAliveAsync())
            {
                _info("[Браузер] Браузер перезапущен, продолжаем.");
                return true;
            }

            _warn("[Браузер] Браузер перезапущен, но вкладка не отвечает.");
            return false;
        }
        catch (Exception ex)
        {
            _warn($"[Браузер] Браузер не перезапустился: {ex.Message}");
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseQuietlyAsync(Page);
        await KillQuietlyAsync(Browser, _profileArg, _warn);
    }

    private async Task<IPage> OpenPageAsync(IBrowser browser)
    {
        var page = await browser.NewPageAsync();
        // «Вкладка упала» (обычно нехватка памяти у процесса отрисовки) — запоминаем, чтобы написать в лог причину.
        page.Error += (_, e) =>
        {
            if (ReferenceEquals(page, Page))
            {
                LastCrash = $"вкладка упала ({e.Error})";
            }
        };
        await _prepare(browser, page);
        return page;
    }

    private void Watch(IBrowser browser)
    {
        _profileArg = ProfileArg(browser) ?? _profileArg;
        browser.Disconnected += (_, _) =>
        {
            // Старый браузер, который закрыли при перезапуске, — не сбой.
            if (ReferenceEquals(browser, Browser))
            {
                LastCrash ??= "браузер отключился (процесс закрылся или упал)";
            }
        };
    }

    private static async Task<bool> IsResponsiveAsync(IBrowser browser, IPage? page)
    {
        if (page is null || page.IsClosed || !browser.IsConnected)
        {
            return false;
        }

        try
        {
            return await WithTimeout(page.EvaluateExpressionAsync<int>("1 + 1"), ProbeTimeout) == 2;
        }
        catch
        {
            return false;
        }
    }

    private static async Task CloseQuietlyAsync(IPage? page)
    {
        if (page is null || page.IsClosed)
        {
            return;
        }

        try
        {
            await WithTimeout(page.CloseAsync(), CloseTimeout);
        }
        catch
        {
            // Мёртвую вкладку закрыть не всегда можно — она уйдёт вместе с браузером.
        }
    }

    private static async Task KillQuietlyAsync(IBrowser browser, string? profileArg, Action<string> warn)
    {
        try
        {
            if (browser.IsConnected)
            {
                await WithTimeout(browser.CloseAsync(), CloseTimeout);
            }
        }
        catch
        {
            // Зависший браузер не закрывается вежливо — ниже его процесс завершается.
        }

        try
        {
            if (browser.Process is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // Процесс уже завершился.
        }

        var leftovers = KillLeftovers(profileArg);
        if (leftovers > 0)
        {
            warn($"[Браузер] Убраны оставшиеся процессы прежнего браузера: {leftovers}.");
        }
    }

    /// <summary>
    /// Процессы Chrome, оставшиеся от убитого браузера. Его процессы отрисовки уходят вместе с ним не всегда: на Deck
    /// 11.10 такой «сирота» после убийства браузера крутился на 40–90 % процессора, пока его не убили руками. На сервере
    /// с одним ядром это съело бы весь прогон. Свои процессы узнаём по папке профиля в командной строке — у каждого
    /// браузера она своя. Только Linux. Возвращает, сколько процессов убито.
    /// </summary>
    internal static int KillLeftovers(string? profileArg)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(profileArg) || profileArg.Length <= "--user-data-dir=".Length + 1 ||
            !Directory.Exists("/proc"))
        {
            return 0;
        }

        var killed = 0;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == Environment.ProcessId)
            {
                continue;
            }

            try
            {
                if (!File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0').Contains(profileArg, StringComparer.Ordinal))
                {
                    continue;
                }

                using var process = System.Diagnostics.Process.GetProcessById(pid);
                process.Kill();
                killed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
            {
                // Процесс уже ушёл или чужой.
            }
        }

        return killed;
    }

    /// <summary>«--user-data-dir=…» из командной строки процесса браузера (Linux), иначе null.</summary>
    private static string? ProfileArg(IBrowser browser)
    {
        try
        {
            return OperatingSystem.IsLinux() && browser.Process is { } process
                ? File.ReadAllText($"/proc/{process.Id}/cmdline").Split('\0')
                    .FirstOrDefault(a => a.StartsWith("--user-data-dir=", StringComparison.Ordinal))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Задача с ограничением по времени. Брошенная задача не оставляет необработанного исключения.</summary>
    private static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
        {
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new TimeoutException($"нет ответа за {timeout.TotalSeconds:0} с");
        }

        return await task;
    }

    private static async Task WithTimeout(Task task, TimeSpan timeout)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
        {
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new TimeoutException($"нет ответа за {timeout.TotalSeconds:0} с");
        }

        await task;
    }
}
