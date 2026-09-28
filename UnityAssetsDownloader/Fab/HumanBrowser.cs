using System.Diagnostics;
using System.Text.Json;
using PuppeteerSharp;

/// <summary>
/// Обычный браузер человека — для сайтов за Cloudflare (fab.com).
///
/// Chrome, который запускает Puppeteer, Cloudflare узнаёт сразу: у него десяток служебных
/// ключей запуска, включённые домены DevTools и часто старая сборка «Chrome for Testing».
/// Проверено 28.09 на Deck: такой браузер получал галочку «Verify you are human», а настоящий
/// Chrome 154, запущенный как обычная программа, открыл fab.com сам за 4 секунды.
///
/// Поэтому здесь: настоящий Chrome (или Edge) из системы, своя постоянная папка профиля,
/// никаких ключей автоматизации, и только разовые команды DevTools.
///
/// Окно, которым управляет программа, честно сообщает сайту об этом (navigator.webdriver),
/// и проверка Cloudflare «я человек» ему не верит даже после клика человека — просит снова,
/// по кругу (Windows, 28.09). Поэтому всё, что делает человек, он делает в обычном окне без
/// программы: HandOverToHumanAsync закрывает окно программы и открывает тот же профиль как
/// обычный Chrome; человек закрывает его — программа открывает своё и продолжает.
/// Скрывать от сайта, что окном управляет программа, мы не стали: это уже обход защиты.
/// </summary>
internal sealed class HumanBrowser : IAsyncDisposable
{
    internal sealed class LaunchSettings
    {
        /// <summary>Папка профиля браузера: вход и cookies живут здесь между запусками.</summary>
        public required string UserDataDir { get; init; }

        /// <summary>Свой браузер (--fab-browser): путь к exe или «flatpak:com.google.Chrome».</summary>
        public string? ExplicitBrowser { get; init; }

        /// <summary>Куда качать запасной Chromium, если настоящего браузера нет (сборка в один файл).</summary>
        public string? DownloadDir { get; init; }

        /// <summary>Положение и размер окна. null — обычное окно 1280×900.</summary>
        public (int X, int Y, int Width, int Height)? Window { get; init; }

        /// <summary>Только для стенда на макетах: fab.com невидимый браузер не пускает.</summary>
        public bool Headless { get; init; }

        /// <summary>
        /// Только для стенда: «человек» в окне без программы. Вызывается вместо ожидания, пока
        /// человек закроет окно; потом окно закрывается само.
        /// </summary>
        public Func<Task>? HumanStandIn { get; init; }

        public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(60);
    }

    private sealed record Candidate(string Title, string FileName, IReadOnlyList<string> Prefix, string? FlatpakApp, bool Real);

    private static readonly HttpClient Local = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static readonly bool InsideFlatpak = File.Exists("/.flatpak-info");

    private readonly AppLogger _logger;
    private readonly LaunchSettings _settings;
    private Candidate? _candidate;
    private bool _noSandbox;
    private Process? _process;
    private int _port;
    private CdpSession? _pageSession;

    private CdpSession Tab => _pageSession ?? throw new CdpException("окно браузера не открыто", disconnected: true);

    /// <summary>Что за браузер: «Google Chrome 154 (Flatpak)».</summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>Настоящий браузер, а не запасной Chromium для тестов.</summary>
    public bool IsRealBrowser { get; private set; }

    /// <summary>Окно браузера на месте: его не закрыли и связь с ним есть.</summary>
    public bool IsAlive => _pageSession is { IsOpen: true } && (_process is null || !_process.HasExited);

    private HumanBrowser(AppLogger logger, LaunchSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public static async Task<HumanBrowser> LaunchAsync(LaunchSettings settings, AppLogger logger)
    {
        Directory.CreateDirectory(settings.UserDataDir);
        var browser = new HumanBrowser(logger, settings);

        // Окно от прошлого запуска ещё открыто (программу прервали) — работаем в нём же:
        // второй браузер на ту же папку профиля всё равно не запустится.
        if (await browser.TryAttachAsync(null, "окно от прошлого запуска", real: true))
        {
            logger.Info("[Браузер] Окно Fab от прошлого запуска ещё открыто — продолжаем в нём.");
            return browser;
        }

        var failures = new List<string>();
        foreach (var candidate in await FindCandidatesAsync(settings, logger))
        {
            if (!candidate.Real)
            {
                logger.Warn("[Браузер] Настоящего Chrome или Edge не нашлось, берём Chromium для тестов.");
                logger.Warn("[Браузер] Его Cloudflare узнаёт чаще: проверка «я человек» будет появляться чаще.");
                logger.Warn("[Браузер] Лучше поставить Google Chrome (на Steam Deck — из Discover, это Flatpak).");
            }

            var noSandbox = NeedsNoSandbox(candidate);
            if (await browser.TryStartAsync(candidate, noSandbox, failures))
            {
                return browser;
            }

            // Песочница Chrome не везде доступна (контейнеры, вложенные песочницы) — второй раз без неё.
            if (candidate.FlatpakApp is null && OperatingSystem.IsLinux() && !noSandbox &&
                await browser.TryStartAsync(candidate, noSandbox: true, failures))
            {
                return browser;
            }
        }

        throw new InvalidOperationException(
            "Не удалось запустить браузер для Fab. " +
            (failures.Count > 0 ? string.Join(" | ", failures) : "Ни Chrome, ни Edge, ни Chromium не найдены.") +
            " Можно указать браузер явно: --fab-browser <путь к chrome> (или flatpak:com.google.Chrome).");
    }

    private static bool NeedsNoSandbox(Candidate candidate) =>
        OperatingSystem.IsLinux() && candidate.FlatpakApp is null &&
        (InsideFlatpak || File.Exists("/.dockerenv") || Environment.UserName == "root");

    private string UserDataDir => Path.GetFullPath(_settings.UserDataDir);

    /// <summary>
    /// Ключи запуска. debug — окно программы (с портом DevTools); без него — обычное окно для
    /// человека: тот же профиль, тот же браузер, никакой связи с программой.
    /// </summary>
    private List<string> BuildArgs(Candidate candidate, bool noSandbox, bool debug, string url)
    {
        var udd = UserDataDir;
        var args = new List<string>(candidate.Prefix);
        if (candidate.FlatpakApp is not null)
        {
            // Flatpak-браузер видит только свои папки — открываем ему папку профиля.
            args.AddRange(["run", $"--filesystem={udd}", candidate.FlatpakApp]);
        }

        args.Add($"--user-data-dir={udd}");
        if (debug)
        {
            // Порт выбирает сам браузер и пишет его в DevToolsActivePort: у каждого запуска свой,
            // постоянный порт на машине не висит.
            args.Add("--remote-debugging-port=0");
        }

        args.Add("--no-first-run");
        args.Add("--no-default-browser-check");
        args.Add("--disable-search-engine-choice-screen");

        if (OperatingSystem.IsLinux())
        {
            // Без этого Chrome на Linux спрашивает пароль связки ключей (KWallet на Deck) —
            // окно, которого никто не ждёт. Папка профиля и так только ваша.
            args.Add("--password-store=basic");

            if (candidate.FlatpakApp is null && !_settings.Headless &&
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            {
                args.Add("--ozone-platform=wayland");
            }

            if (noSandbox)
            {
                args.Add("--no-sandbox");
            }
        }

        if (_settings.Window is { } w)
        {
            args.Add($"--window-position={w.X},{w.Y}");
            args.Add($"--window-size={w.Width},{w.Height}");
        }
        else
        {
            args.Add("--window-size=1280,900");
        }

        if (_settings.Headless)
        {
            args.Add("--headless=new");
        }

        args.Add(url);
        return args;
    }

    private Process StartProcess(Candidate candidate, List<string> args, Queue<string> errorTail)
    {
        _logger.Debug($"[Браузер] {candidate.FileName} {string.Join(" ", args)}");
        var psi = new ProcessStartInfo(candidate.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException("процесс не создан");
        // Chrome много пишет в консоль; не читать — значит однажды повиснуть на полном буфере.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (errorTail)
            {
                errorTail.Enqueue(e.Data);
                while (errorTail.Count > 12) errorTail.Dequeue();
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>Запускает окно программы и подключается к нему. false — не вышло (причина в failures).</summary>
    private async Task<bool> TryStartAsync(Candidate candidate, bool noSandbox, List<string> failures)
    {
        var portFile = Path.Combine(UserDataDir, "DevToolsActivePort");
        TryDelete(portFile);

        _logger.Info($"[Браузер] Запускаем: {candidate.Title}");
        var errorTail = new Queue<string>();
        Process process;
        try
        {
            process = StartProcess(candidate, BuildArgs(candidate, noSandbox, debug: true, "about:blank"), errorTail);
        }
        catch (Exception ex)
        {
            failures.Add($"{candidate.Title}: не запустился ({ex.Message})");
            return false;
        }

        var started = Stopwatch.StartNew();
        while (started.Elapsed < _settings.StartTimeout)
        {
            await Task.Delay(250);
            if (File.Exists(portFile) && await TryAttachAsync(process, candidate.Title, candidate.Real))
            {
                _candidate = candidate;
                _noSandbox = noSandbox;
                return true;
            }

            if (process.HasExited)
            {
                await Task.Delay(300);
                string tail;
                lock (errorTail) tail = string.Join(" / ", errorTail);
                failures.Add($"{candidate.Title}: закрылся сразу (код {process.ExitCode})" +
                             (tail.Length > 0 ? $": {Shorten(tail, 400)}" : string.Empty));
                if (process.ExitCode == 0 && started.Elapsed < TimeSpan.FromSeconds(10) && candidate.FlatpakApp is null)
                {
                    // Chrome с этой папкой профиля уже открыт и забрал запуск себе.
                    failures.Add("похоже, окно браузера Fab уже открыто без связи с программой — закройте его и запустите снова");
                }

                process.Dispose();
                return false;
            }
        }

        failures.Add($"{candidate.Title}: не ответил за {_settings.StartTimeout.TotalSeconds:0} с");
        TryKill(process);
        process.Dispose();
        return false;
    }

    /// <summary>Подключается к браузеру, который держит эту папку профиля (по DevToolsActivePort).</summary>
    private async Task<bool> TryAttachAsync(Process? process, string title, bool real)
    {
        var portFile = Path.Combine(UserDataDir, "DevToolsActivePort");
        int port;
        try
        {
            if (!File.Exists(portFile) ||
                !int.TryParse(File.ReadLines(portFile).FirstOrDefault()?.Trim(), out port) || port <= 0)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        try
        {
            var version = JsonDocument.Parse(await Local.GetStringAsync($"http://127.0.0.1:{port}/json/version")).RootElement;
            var product = version.TryGetProperty("Browser", out var b) ? b.GetString() : null;

            var pageUrl = await FindPageTargetAsync(port);
            if (pageUrl is null)
            {
                return false;
            }

            _pageSession = await CdpSession.ConnectAsync(pageUrl, TimeSpan.FromSeconds(10));
            _process = process;
            _port = port;
            Description = string.IsNullOrWhiteSpace(product) ? title : $"{title} — {product}";
            IsRealBrowser = real;
            _logger.Info($"[Браузер] Готов: {Description}");
            return true;
        }
        catch (Exception ex) when (process is null)
        {
            // Старый файл порта от закрытого браузера — это нормально, запустим новый.
            _logger.Debug($"[Браузер] К прошлому окну не подключились: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Браузер] Порт {port} ещё не отвечает: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Отдаёт браузер человеку: закрывает окно программы, открывает тот же профиль как обычный
    /// Chrome на странице url и ждёт, пока человек закроет окно (или timeout). Потом снова
    /// открывает окно программы — с тем же профилем, где уже пройдена проверка или выполнен вход.
    /// true — человек закрыл окно сам.
    /// </summary>
    public async Task<bool> HandOverToHumanAsync(string url, TimeSpan timeout)
    {
        var candidate = _candidate ?? (await FindCandidatesAsync(_settings, _logger)).FirstOrDefault()
            ?? throw new CdpException("не найден браузер для окна человека", disconnected: true);
        var noSandbox = _candidate is null ? NeedsNoSandbox(candidate) : _noSandbox;

        await CloseBrowserAsync();
        TryDelete(Path.Combine(UserDataDir, "DevToolsActivePort"));

        bool closedByHuman;
        var errorTail = new Queue<string>();
        using (var human = StartProcess(candidate, BuildArgs(candidate, noSandbox, debug: false, url), errorTail))
        {
            var opened = Stopwatch.StartNew();
            if (_settings.HumanStandIn is { } standIn)
            {
                await standIn();
                closedByHuman = true;
                await CloseGracefullyAsync(human, candidate);
            }
            else
            {
                closedByHuman = await WaitForExitAsync(human, timeout);
                if (!closedByHuman)
                {
                    await CloseGracefullyAsync(human, candidate);
                }
                else if (opened.Elapsed < TimeSpan.FromSeconds(3))
                {
                    _logger.Warn("[Браузер] Обычное окно закрылось сразу — возможно, Chrome с этой папкой ещё не закрылся.");
                }
            }
        }

        var failures = new List<string>();
        if (!await TryStartAsync(candidate, noSandbox, failures))
        {
            throw new CdpException("окно программы не открылось снова: " + string.Join(" | ", failures), disconnected: true);
        }

        return closedByHuman;
    }

    /// <summary>Закрывает окно программы штатно (Chrome записывает cookies на диск) и ждёт, пока оно закроется.</summary>
    private async Task CloseBrowserAsync()
    {
        try
        {
            using var version = JsonDocument.Parse(await Local.GetStringAsync($"http://127.0.0.1:{_port}/json/version"));
            if (version.RootElement.TryGetProperty("webSocketDebuggerUrl", out var ws) && ws.GetString() is { } url)
            {
                await using var browser = await CdpSession.ConnectAsync(url, TimeSpan.FromSeconds(5));
                try
                {
                    await browser.SendAsync("Browser.close", timeout: TimeSpan.FromSeconds(5));
                }
                catch (CdpException)
                {
                    // Браузер закрывается и рвёт соединение, не успев ответить, — так и должно быть.
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Браузер] Штатно закрыть не вышло: {ex.Message}");
        }

        if (_pageSession is not null)
        {
            await _pageSession.DisposeAsync();
            _pageSession = null;
        }

        if (_process is not null)
        {
            if (!await WaitForExitAsync(_process, TimeSpan.FromSeconds(10)) && _candidate is not null)
            {
                await CloseGracefullyAsync(_process, _candidate);
            }

            TryKill(_process);
            _process.Dispose();
            _process = null;
        }
        else
        {
            // Окно от прошлого запуска: процесса у нас нет — ждём, пока порт перестанет отвечать.
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                try
                {
                    await Local.GetStringAsync($"http://127.0.0.1:{_port}/json/version");
                    await Task.Delay(300);
                }
                catch
                {
                    break;
                }
            }
        }

        // Chrome дописывает профиль ещё мгновение после выхода процесса.
        await Task.Delay(500);
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Просит окно закрыться, как будто нажали крестик; не закрылось за 10 с — закрывает силой.
    ///
    /// Flatpak-браузер — отдельный процесс хоста: сигнал процессу flatpak/flatpak-spawn до него
    /// не доходит (проверено на Deck 28.09 — Chrome оставался). Поэтому сигнал идёт самому браузеру,
    /// найденному по папке профиля. Шаблон начинается с /app/ — так он не заденет ни личный Chrome
    /// человека, ни терминал, в строке запуска которого случайно встретится этот путь.
    /// </summary>
    private async Task CloseGracefullyAsync(Process process, Candidate candidate)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                process.CloseMainWindow();
            }
            else if (candidate.FlatpakApp is not null)
            {
                await SignalFlatpakBrowserAsync("-TERM");
            }
            else
            {
                await RunQuietAsync("kill", ["-TERM", process.Id.ToString()]);
            }
        }
        catch
        {
            // Уже закрылось.
        }

        if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(10)))
        {
            if (candidate.FlatpakApp is not null)
            {
                await SignalFlatpakBrowserAsync("-KILL");
            }

            TryKill(process);
        }
    }

    private Task SignalFlatpakBrowserAsync(string signal)
    {
        var pattern = $"^/app/\\S+ .*--user-data-dir={EscapeRegex(UserDataDir)}( |$)";
        return InsideFlatpak
            ? RunQuietAsync("/usr/bin/flatpak-spawn", ["--host", "pkill", signal, "-f", pattern])
            : RunQuietAsync("pkill", [signal, "-f", pattern]);
    }

    private static string EscapeRegex(string text) =>
        string.Concat(text.Select(c => "\\.^$|?*+()[]{}".Contains(c) ? "\\" + c : c.ToString()));

    private static async Task RunQuietAsync(string file, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is not null)
            {
                await WaitForExitAsync(p, TimeSpan.FromSeconds(5));
            }
        }
        catch
        {
            // Нет pkill/kill — закроем силой ниже.
        }
    }

    /// <summary>Первая обычная вкладка; если вкладок нет — открывает пустую.</summary>
    private static async Task<string?> FindPageTargetAsync(int port)
    {
        using var list = JsonDocument.Parse(await Local.GetStringAsync($"http://127.0.0.1:{port}/json/list"));
        foreach (var target in list.RootElement.EnumerateArray())
        {
            if (target.TryGetProperty("type", out var type) && type.GetString() == "page" &&
                target.TryGetProperty("webSocketDebuggerUrl", out var ws))
            {
                return ws.GetString();
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{port}/json/new?about:blank");
        using var response = await Local.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return created.RootElement.TryGetProperty("webSocketDebuggerUrl", out var url) ? url.GetString() : null;
    }

    private static async Task<List<Candidate>> FindCandidatesAsync(LaunchSettings settings, AppLogger logger)
    {
        var result = new List<Candidate>();

        if (!string.IsNullOrWhiteSpace(settings.ExplicitBrowser))
        {
            var chosen = settings.ExplicitBrowser.Trim();
            if (chosen.StartsWith("flatpak:", StringComparison.OrdinalIgnoreCase))
            {
                var flatpak = FlatpakCommand();
                if (flatpak is not null)
                {
                    var app = chosen["flatpak:".Length..];
                    result.Add(new Candidate($"{app} (Flatpak, задан явно)", flatpak.Value.File, flatpak.Value.Prefix, app, true));
                }
            }
            else
            {
                result.Add(new Candidate($"{Path.GetFileName(chosen)} (задан явно)", chosen, [], null, true));
            }

            return result;
        }

        void AddFirstExisting(string title, IEnumerable<string> paths)
        {
            var found = paths.FirstOrDefault(File.Exists);
            if (found is not null)
            {
                result.Add(new Candidate(title, found, [], null, true));
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            AddFirstExisting("Google Chrome",
            [
                Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(local, @"Google\Chrome\Application\chrome.exe")
            ]);
            AddFirstExisting("Microsoft Edge",
            [
                Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe")
            ]);
        }
        else if (OperatingSystem.IsMacOS())
        {
            AddFirstExisting("Google Chrome", ["/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"]);
            AddFirstExisting("Microsoft Edge", ["/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"]);
            AddFirstExisting("Chromium", ["/Applications/Chromium.app/Contents/MacOS/Chromium"]);
        }
        else
        {
            var flatpak = FlatpakCommand();
            async Task AddFlatpakAsync(string title, string app)
            {
                if (flatpak is not null && await FlatpakHasAppAsync(flatpak.Value.File, flatpak.Value.Prefix, app))
                {
                    result.Add(new Candidate($"{title} (Flatpak)", flatpak.Value.File, flatpak.Value.Prefix, app, true));
                }
            }

            AddFirstExisting("Google Chrome", ["/usr/bin/google-chrome-stable", "/usr/bin/google-chrome", "/opt/google/chrome/chrome"]);
            await AddFlatpakAsync("Google Chrome", "com.google.Chrome");
            AddFirstExisting("Microsoft Edge", ["/usr/bin/microsoft-edge-stable", "/usr/bin/microsoft-edge", "/opt/microsoft/msedge/msedge"]);
            await AddFlatpakAsync("Microsoft Edge", "com.microsoft.Edge");
            AddFirstExisting("Chromium", ["/usr/bin/chromium", "/usr/bin/chromium-browser", "/snap/bin/chromium"]);
            await AddFlatpakAsync("Chromium", "org.chromium.Chromium");
        }

        // Запасной вариант — Chromium, который программа и так качает для Unity.
        try
        {
            var fetcher = settings.DownloadDir is null
                ? new BrowserFetcher()
                : new BrowserFetcher(new BrowserFetcherOptions { Path = settings.DownloadDir });
            var installed = fetcher.GetInstalledBrowsers().FirstOrDefault(b => b.Browser == SupportedBrowser.Chrome)
                            ?? (result.Count == 0 ? await fetcher.DownloadAsync() : null);
            if (installed is not null)
            {
                result.Add(new Candidate($"Chromium для тестов {installed.BuildId}", installed.GetExecutablePath(), [], null, false));
            }
        }
        catch (Exception ex)
        {
            logger.Debug($"[Браузер] Запасной Chromium недоступен: {ex.Message}");
        }

        return result;
    }

    /// <summary>Как запускать flatpak: изнутри песочницы (терминал VS Code на Deck) — через flatpak-spawn.</summary>
    private static (string File, string[] Prefix)? FlatpakCommand()
    {
        if (InsideFlatpak)
        {
            return File.Exists("/usr/bin/flatpak-spawn") ? ("/usr/bin/flatpak-spawn", ["--host", "flatpak"]) : null;
        }

        foreach (var path in new[] { "/usr/bin/flatpak", "/usr/local/bin/flatpak" })
        {
            if (File.Exists(path))
            {
                return (path, []);
            }
        }

        return null;
    }

    private static async Task<bool> FlatpakHasAppAsync(string file, string[] prefix, string app)
    {
        if (!InsideFlatpak)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Directory.Exists($"/var/lib/flatpak/app/{app}") ||
                   Directory.Exists(Path.Combine(home, ".local/share/flatpak/app", app));
        }

        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in prefix) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("info");
            psi.ArgumentList.Add(app);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            var errors = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await p.WaitForExitAsync(cts.Token);
            await Task.WhenAll(output, errors);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ вкладка

    /// <summary>
    /// Открывает адрес и ждёт, пока новая страница загрузится. Новую страницу от старой
    /// отличаем по performance.timeOrigin — у каждого документа он свой, а метки на
    /// страницу ставить не нужно.
    /// </summary>
    public async Task NavigateAsync(string url, TimeSpan timeout)
    {
        var before = await TryEvaluateAsync("performance.timeOrigin");
        var reply = await Tab.SendAsync("Page.navigate", new Dictionary<string, object?> { ["url"] = url }, timeout);
        if (reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("errorText", out var error) &&
            error.GetString() is { Length: > 0 } text && !text.Contains("ERR_ABORTED", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"страница {url} не открылась: {text}");
        }

        var started = Stopwatch.StartNew();
        while (started.Elapsed < timeout)
        {
            await Task.Delay(300);
            var now = await TryEvaluateAsync("[performance.timeOrigin, document.readyState]");
            if (now is not { ValueKind: JsonValueKind.Array } pair)
            {
                continue;
            }

            var origin = pair[0];
            var state = pair[1].GetString();
            var fresh = before is null || origin.ToString() != before.Value.ToString();
            if (fresh && state == "complete")
            {
                return;
            }

            // Тяжёлая страница может долго догружать картинки — для работы хватает готового DOM.
            if (fresh && state == "interactive" && started.Elapsed > TimeSpan.FromSeconds(15))
            {
                return;
            }
        }

        _logger.Debug($"[Браузер] {url} грузится дольше {timeout.TotalSeconds:0} с — работаем с тем, что есть.");
    }

    /// <summary>Выполняет JavaScript на странице и возвращает значение (объекты — как JSON).</summary>
    public async Task<JsonElement> EvaluateAsync(string expression, TimeSpan? timeout = null)
    {
        var reply = await Tab.SendAsync("Runtime.evaluate", new Dictionary<string, object?>
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
            ["awaitPromise"] = true
        }, timeout ?? TimeSpan.FromSeconds(45));

        if (reply.TryGetProperty("exceptionDetails", out var details))
        {
            var text = details.TryGetProperty("exception", out var ex) && ex.TryGetProperty("description", out var d)
                ? d.GetString()
                : details.TryGetProperty("text", out var t) ? t.GetString() : details.ToString();
            throw new CdpException($"ошибка скрипта на странице: {Shorten(text ?? "?", 300)}");
        }

        return reply.TryGetProperty("result", out var result) && result.TryGetProperty("value", out var value)
            ? value.Clone()
            : default;
    }

    /// <summary>То же, но во время перехода между страницами ошибка — не ошибка, а null.</summary>
    public async Task<JsonElement?> TryEvaluateAsync(string expression, TimeSpan? timeout = null)
    {
        try
        {
            return await EvaluateAsync(expression, timeout);
        }
        catch (CdpException ex) when (!ex.IsDisconnected)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public async Task<string> UrlAsync() =>
        (await TryEvaluateAsync("location.href"))?.GetString() ?? string.Empty;

    /// <summary>Поднимает окно браузера наверх, когда нужен человек.</summary>
    public async Task BringToFrontAsync()
    {
        try
        {
            await Tab.SendAsync("Page.bringToFront");
        }
        catch (CdpException ex) when (!ex.IsDisconnected)
        {
            // Не поднялось — не беда, человек найдёт окно сам.
        }
    }

    public async Task SaveScreenshotAsync(string path)
    {
        try
        {
            var shot = await Tab.SendAsync("Page.captureScreenshot",
                new Dictionary<string, object?> { ["format"] = "png" }, TimeSpan.FromSeconds(20));
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(shot.GetProperty("data").GetString()!));
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Браузер] Скриншот не сохранился: {ex.Message}");
        }
    }

    public async Task SaveHtmlAsync(string path)
    {
        try
        {
            var html = await EvaluateAsync("document.documentElement.outerHTML");
            await File.WriteAllTextAsync(path, html.GetString());
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Браузер] HTML страницы не сохранился: {ex.Message}");
        }
    }

    /// <summary>Имена cookies сайта и срок жизни: для лога, чтобы видеть, запомнился ли вход.</summary>
    public async Task<List<(string Name, bool Session)>> CookieNamesAsync(string url)
    {
        var result = new List<(string, bool)>();
        try
        {
            var reply = await Tab.SendAsync("Network.getCookies",
                new Dictionary<string, object?> { ["urls"] = new[] { url } });
            foreach (var c in reply.GetProperty("cookies").EnumerateArray())
            {
                result.Add((c.GetProperty("name").GetString() ?? "?", c.TryGetProperty("session", out var s) && s.GetBoolean()));
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Браузер] Cookies не прочитались: {ex.Message}");
        }

        return result;
    }

    /// <summary>Закрывает браузер штатно: так Chrome успевает записать cookies на диск.</summary>
    public async ValueTask DisposeAsync() => await CloseBrowserAsync();

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Уже закрылся.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Файл держит живой браузер — к нему и подключимся.
        }
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
