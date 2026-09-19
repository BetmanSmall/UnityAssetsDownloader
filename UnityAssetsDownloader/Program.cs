using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PuppeteerSharp;

// Windows-консоль по умолчанию не в UTF-8 — без этого русские логи превращаются в кракозябры.
try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.InputEncoding = System.Text.Encoding.UTF8;
}
catch
{
    // В перенаправленном выводе смена кодировки может не поддерживаться. Это не критично.
}

var options = CliOptions.Parse(args);

if (options.ListProfiles)
{
    var listStore = new ProfileStore(options.DataDirectory);
    Console.WriteLine($"Каталог данных: {options.DataDirectory}");
    Console.WriteLine("Профили на этом компьютере:");
    Console.WriteLine(listStore.Describe());
    return;
}

if (options.Watch)
{
    await RunWatchLoopAsync();
    return;
}

try
{
    var app = new UnityAssetAutomationApp(options);
    await app.RunAsync();
}
catch (Exception ex)
{
    await ReportCrashAsync(ex, options);
}

// Режим сервера: прогон, пауза до следующего, снова прогон — пока службу не остановят.
// Каждый прогон начинается с чистого листа: параметры читаются заново (после первого
// входа профиль переименовывается), браузер и лог — новые.
async Task RunWatchLoopAsync()
{
    var interval = options.WatchInterval;
    Console.WriteLine($"[Сервер] Режим наблюдения: прогон раз в {DescribeInterval(interval)}. Остановить: Ctrl+C, docker stop или systemctl stop.");

    using var stop = new CancellationTokenSource();
    UnityAssetAutomationApp? current = null;
    var running = false;

    // docker stop / systemctl stop присылают SIGTERM. Между прогонами выходим штатно,
    // посреди прогона — сохраняем память профиля и завершаемся сразу: ждать конца
    // прогона служба не станет (через несколько секунд пришлёт SIGKILL).
    using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
    {
        Console.WriteLine("[Сервер] Получен сигнал остановки. Сохраняем память профиля и выходим.");
        current?.SaveCachesOnExit();
        ctx.Cancel = !running;
        stop.Cancel();
    });

    while (!stop.IsCancellationRequested)
    {
        var startedAt = DateTime.Now;
        var cycleOptions = CliOptions.Parse(args);

        try
        {
            running = true;
            // Неудачный прогон не должен помечать неудачей всю службу.
            Environment.ExitCode = 0;
            current = new UnityAssetAutomationApp(cycleOptions);
            await current.RunAsync();
        }
        catch (Exception ex)
        {
            await ReportCrashAsync(ex, cycleOptions);
            if (current is not null)
            {
                await current.NotifyAsync($"💥 Программа упала во время прогона: {ex.Message}\nСледующий прогон — по расписанию.");
            }
        }
        finally
        {
            running = false;
        }

        // Расписание от начала прогона: «раз в сутки» значит в одно и то же время.
        var next = startedAt + interval;
        if (next <= DateTime.Now)
        {
            next = DateTime.Now + TimeSpan.FromMinutes(1);
        }

        Console.WriteLine($"[Сервер] Следующий прогон: {next:yyyy-MM-dd HH:mm}.");
        try
        {
            await Task.Delay(next - DateTime.Now, stop.Token);
        }
        catch (TaskCanceledException)
        {
            break;
        }
    }
}

static string DescribeInterval(TimeSpan t) =>
    t.TotalDays >= 1 && t.TotalDays % 1 == 0 ? $"{t.TotalDays:0} сут."
    : t.TotalHours >= 1 && t.TotalHours % 1 == 0 ? $"{t.TotalHours:0} ч"
    : $"{t.TotalMinutes:0} мин";

async Task ReportCrashAsync(Exception ex, CliOptions crashOptions)
{
    // Любое необработанное падение сохраняем в отдельный файл, чтобы его можно было прислать целиком.
    var crashText =
        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] НЕОБРАБОТАННАЯ ОШИБКА{Environment.NewLine}" +
        $"ВЕРСИЯ ПРОГРАММЫ: {UnityAssetAutomationApp.BuildVersionLine()}{Environment.NewLine}" +
        $"ОС: {RuntimeInformation.OSDescription} | .NET: {RuntimeInformation.FrameworkDescription}{Environment.NewLine}" +
        $"Аргументы: {string.Join(" ", args)}{Environment.NewLine}" +
        ex;

    Console.Error.WriteLine(crashText);

    try
    {
        Directory.CreateDirectory(crashOptions.LogsDirectory);
        CliOptions.MigrateProblemsFile(crashOptions.LogsDirectory);
        var problemsPath = Path.Combine(crashOptions.LogsDirectory, CliOptions.ProblemsFileName);
        await File.AppendAllTextAsync(problemsPath, crashText + Environment.NewLine);
        Console.Error.WriteLine();
        Console.Error.WriteLine("============================================================");
        Console.Error.WriteLine($" ПРОГРАММА УПАЛА. ПРИШЛИТЕ ЭТОТ ФАЙЛ: {problemsPath}");
        Console.Error.WriteLine("============================================================");
    }
    catch (Exception writeEx)
    {
        Console.Error.WriteLine($"Не удалось сохранить файл с ошибкой: {writeEx.Message}");
    }

    Environment.ExitCode = 1;
}

internal sealed class UnityAssetAutomationApp
{
    private const string AssetStoreHomeUrl = "https://assetstore.unity.com/";
    public const string BaseTopFreeSource = "https://assetstore.unity.com/top-assets/top-free";

    private const string BaseFreeListFileName =
        "GreaterChinaUnityAssetArchive/free_list_GreaterChinaUnityAssetArchiveLinks.txt";

    private const string ExtendedSourcesFileName = "extended_sources.txt";

    private readonly CliOptions _options;
    private readonly string _signInUrl;
    private readonly ProfileStore _profileStore;
    private readonly string _profileName;
    private readonly string _credentialTarget;
    private string? _unityEmail;
    private string? _unityPassword;
    private bool? _savePasswordAnswer;
    private bool _credentialsAsked;
    private bool _googleWarningShown;
    private string? _lastWaitMessage;
    private string? _chromePath;
    private string? _unityAccount;
    private OwnedAssetsCache? _ownedCache;
    private OwnedAssetsCache? _deprecatedCache;
    private OwnedAssetsCache? _rejectedPromoCache;
    private readonly TelegramNotifier? _notifier;

    private bool HasCredentials =>
        !string.IsNullOrWhiteSpace(_unityEmail) && !string.IsNullOrWhiteSpace(_unityPassword);
    private readonly AppLogger _logger;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly JsonSerializerOptions _runtimeJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _dataDirectory;
    private readonly string _logsDirectory;
    private readonly string _cookiesPath;
    private readonly string _sessionStatePath;
    private readonly string _reportPath;
    private readonly HttpClient _httpClient = new();
    private DateTime? _lastFullAuthAttemptUtc;

    private static readonly TimeSpan FullAuthCooldown = TimeSpan.FromSeconds(25);

    private static readonly string[] SessionOrigins =
    [
        "https://assetstore.unity.com",
        "https://login.unity.com",
        "https://api.unity.com",
        "https://cloud.unity.com"
    ];

    private static readonly string[] LocalStorageOrigins =
    [
        "https://assetstore.unity.com",
        "https://login.unity.com"
    ];

    // Расширенные источники поиска теперь загружаются из отдельного файла

    public UnityAssetAutomationApp(CliOptions options)
    {
        _options = options;
        _signInUrl = options.SignInUrl;
        _dataDirectory = options.DataDirectory;
        _logsDirectory = options.LogsDirectory;
        _profileStore = new ProfileStore(_dataDirectory);
        _profileName = options.ProfileName;
        _credentialTarget = SecretStore.BuildCredentialTarget(_profileName);
        _unityEmail = options.UnityEmail;
        _unityPassword = options.UnityPassword;

        var profileDirectory = _profileStore.GetProfileDirectory(_profileName);
        _cookiesPath = Path.Combine(profileDirectory, "unity_cookies.json");
        _sessionStatePath = _profileStore.GetSessionPath(_profileName);
        _reportPath = Path.Combine(_logsDirectory, $"run-report-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var logFilePath = string.IsNullOrWhiteSpace(options.LogFilePath)
            ? Path.Combine(_logsDirectory, $"run-log-{DateTime.Now:yyyyMMdd-HHmmss}.log")
            : Path.GetFullPath(options.LogFilePath);
        CliOptions.MigrateProblemsFile(_logsDirectory);
        var errorsFilePath = Path.Combine(_logsDirectory, CliOptions.ProblemsFileName);
        _logger = new AppLogger(options.Verbose, options.TraceNetwork, logFilePath, errorsFilePath);
        _logger.Info($"ВЕРСИЯ ПРОГРАММЫ: {BuildVersionLine()}");
        _logger.Info($"Каталог логов: {_logsDirectory}");
        _logger.Info($"Каталог данных (cookies): {_dataDirectory}");
        _logger.Info($"Профиль аккаунта: {_profileName} | папка: {profileDirectory}");

        if (!string.IsNullOrWhiteSpace(options.SourcesPreset))
        {
            _logger.Info($"Источники (SOURCES={options.SourcesPreset}): {CliOptions.DescribeSourcesPreset(options.SourcesPreset)}.");
        }
        _logger.Info($"ЕСЛИ ЧТО-ТО ПОШЛО НЕ ТАК — ПРИШЛИТЕ ЭТОТ ФАЙЛ: {errorsFilePath}");

        if (!string.IsNullOrWhiteSpace(options.NotifyBotToken))
        {
            // Номер чата общий для всех профилей: бот пишет одному человеку.
            _notifier = new TelegramNotifier(
                options.NotifyBotToken,
                options.NotifyChatId,
                Path.Combine(_dataDirectory, "telegram_bot_chat.txt"),
                () => !string.IsNullOrWhiteSpace(options.TelegramProxy) ? options.TelegramProxy : ReadRememberedProxy(),
                _logger);
            _logger.Info("Telegram-бот для сообщений настроен.");
        }
    }

    public async Task RunAsync()
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            Directory.CreateDirectory(_logsDirectory);

            // Ctrl+C не должен стирать то, что программа уже успела выяснить.
            Console.CancelKeyPress += OnCancelRequested;

            if (_options.NotifyTest)
            {
                if (_notifier is null)
                {
                    _logger.Error("Бот не настроен: задайте TELEGRAM_BOT_TOKEN (или notify.telegramBotToken в config.json).");
                    Environment.ExitCode = 2;
                    return;
                }

                var sent = await _notifier.SendAsync($"👋 Проверка связи от UnityAssetsDownloader (профиль {_profileName}). Сообщения доходят.");
                Environment.ExitCode = sent ? 0 : 2;
                _logger.Info(sent
                    ? "Бот: проверочное сообщение отправлено. Проверьте Telegram."
                    : "Бот: сообщение не отправилось. Причина — в строках выше.");
                return;
            }

            _profileStore.Touch(_profileName, _unityEmail);
            if (_profileStore.TryMigrateLegacySession(_profileName, out var migrationMessage))
            {
                _logger.Info(migrationMessage);
            }
            else if (!string.IsNullOrWhiteSpace(migrationMessage))
            {
                _logger.Warn(migrationMessage);
            }

            ApplySavePasswordPolicy();

            // Вопрос про вход задаём до запуска браузера. Если спросить позже,
            // окно браузера перехватит внимание и вопрос в консоли останется незамеченным —
            // со стороны это выглядит как "программа зависла".
            if (!HasStoredSession())
            {
                TrySetupCredentialsInteractively();
            }

            _logger.Info($"ОС: {RuntimeInformation.OSDescription} | Arch: {RuntimeInformation.OSArchitecture} | .NET: {RuntimeInformation.FrameworkDescription}");

            string? chromePath = null;
            string[] potentialChromePaths;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                potentialChromePaths =
                [
                    @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                    @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"Google\Chrome\Application\chrome.exe")
                ];
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                potentialChromePaths =
                [
                    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                    "/Applications/Chromium.app/Contents/MacOS/Chromium"
                ];
            }
            else
            {
                // Linux: стандартные пути + Snap + Flatpak (system + user, Steam Deck / SteamOS)
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                potentialChromePaths =
                [
                    "/usr/bin/google-chrome",
                    "/usr/bin/google-chrome-stable",
                    "/usr/bin/chromium-browser",
                    "/usr/bin/chromium",
                    "/snap/bin/chromium",
                    "/snap/bin/google-chrome",
                    "/var/lib/flatpak/app/com.google.Chrome/current/active/files/chrome",
                    "/var/lib/flatpak/app/org.chromium.Chromium/current/active/files/chromium",
                    Path.Combine(home, ".local/share/flatpak/app/com.google.Chrome/current/active/files/chrome"),
                    Path.Combine(home, ".local/share/flatpak/app/org.chromium.Chromium/current/active/files/chromium"),
                    "/usr/lib/chromium-browser/chromium-browser",
                    "/usr/lib/chromium/chromium",
                    Path.Combine(home, ".local/bin/google-chrome")
                ];
            }

            foreach (var path in potentialChromePaths)
            {
                if (File.Exists(path))
                {
                    chromePath = path;
                    break;
                }
            }

            _chromePath = chromePath;

            if (chromePath != null)
            {
                _logger.Info($"Используем локальный браузер: {chromePath}");
            }
            else
            {
                _logger.Info("Локальный Chrome/Chromium не найден. Скачивание встроенного Chromium...");
                _logger.Debug($"Проверялись пути: {string.Join(", ", potentialChromePaths)}");
                await new BrowserFetcher().DownloadAsync();
            }

            var browserArgs = new List<string>
            {
                "--disable-blink-features=AutomationControlled",
                "--disable-infobars"
            };

            // Делим экран: консоль с логами слева, браузер справа.
            // Так видно и то, и другое, не переключая окна.
            var splitApplied = false;
            if (_options.SplitScreen && !_options.Headless)
            {
                var right = ScreenLayout.RightHalf();
                if (right is not null)
                {
                    var r = right.Value;
                    browserArgs.Add($"--window-position={r.X},{r.Y}");
                    browserArgs.Add($"--window-size={r.Width},{r.Height}");

                    var consoleMoved = ScreenLayout.MoveConsoleToLeftHalf();
                    splitApplied = true;

                    _logger.Info($"Экран поделён: браузер справа ({r.Width}x{r.Height}), " +
                                 (consoleMoved ? "консоль слева." : "консоль подвинуть не удалось."));
                }
                else
                {
                    _logger.Debug("Размер экрана не определяется, окна оставляем как есть.");
                }
            }

            if (!splitApplied)
            {
                browserArgs.Add("--start-maximized");
            }

            // В контейнерах Linux (Flatpak, Docker, Steam Deck) песочница Chrome недоступна,
            // и браузер просто не стартует. На Windows и macOS это не нужно и не добавляется.
            if (_options.UseSystemChromeProfile)
            {
                // Без явного указания Chrome может открыть чужой профиль внутри общей папки.
                browserArgs.Add("--profile-directory=Default");
            }

            if (OperatingSystem.IsLinux())
            {
                browserArgs.Add("--no-sandbox");
                browserArgs.Add("--disable-dev-shm-usage");
                _logger.Debug("Linux: добавлены --no-sandbox и --disable-dev-shm-usage для запуска в контейнере.");

                // Рабочий стол Steam Deck и многие другие работают на Wayland. Программам
                // из Flatpak (например, терминалу VS Code) там доступен только Wayland,
                // а Chrome по умолчанию ищет X11 и падает с "Missing X server or $DISPLAY".
                if (!_options.Headless &&
                    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
                    !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                {
                    browserArgs.Add("--ozone-platform=wayland");
                    _logger.Info("Экрана X11 нет, браузер запускается на Wayland.");
                }
            }

            if (_options.ProxyHost != null && _options.ProxyPort.HasValue)
            {
                var proxyArg = $"--proxy-server={_options.ProxyType ?? "socks5"}://{_options.ProxyHost}:{_options.ProxyPort}";
                browserArgs.Add(proxyArg);
                _logger.Info($"Прокси включён: {proxyArg}");
            }

            // Постоянная папка браузера. Без неё Chrome каждый раз стартует пустым,
            // как в режиме инкогнито: ни истории, ни расширений, ни сохранённого входа.
            var userDataDir = ResolveChromeUserDataDir();
            if (!_options.UseSystemChromeProfile)
            {
                RemoveStaleChromeLock(userDataDir);
            }

            _logger.Debug($"Аргументы браузера: {string.Join(" ", browserArgs)}");

            var launchOptions = new LaunchOptions
            {
                Headless = _options.Headless,
                DefaultViewport = null,
                IgnoredDefaultArgs = ["--enable-automation"],
                Args = [..browserArgs],
                UserDataDir = userDataDir
            };

            if (chromePath != null)
            {
                launchOptions.ExecutablePath = chromePath;
            }

            var browser = await LaunchBrowserWithRetryAsync(launchOptions);

            await using (browser)
            {
            var browserVersion = await browser.GetVersionAsync();
            _logger.Info($"Браузер запущен: {browserVersion} | headless={_options.Headless}");

            await using var page = await browser.NewPageAsync();
            page.DefaultNavigationTimeout = _options.NavigationTimeoutMs;
            page.DefaultTimeout = _options.NavigationTimeoutMs;

            // Скрываем признаки Puppeteer (чтобы пускал Google OAuth)
            await page.EvaluateFunctionOnNewDocumentAsync(@"() => {
                Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            }");

            var ua = await browser.GetUserAgentAsync();
            await page.SetUserAgentAsync(ua.Replace("HeadlessChrome", "Chrome"));

            AttachPageDiagnostics(page);

            if (_options.CheckLoginPage)
            {
                await CheckSignInPageAsync(page);
                return;
            }

            // Проверка Telegram идёт до входа в Unity: так можно настраивать прокси,
            // не трогая аккаунт и не дожидаясь авторизации.
            if (_options.CheckTelegram)
            {
                await CheckTelegramAsync(browser);
                return;
            }

            var authenticated = await EnsureAuthenticatedAsync(page);
            if (!authenticated)
            {
                _logger.Error("============================================================");
                _logger.Error(" НЕ ПОЛУЧИЛОСЬ ВОЙТИ");
                _logger.Error($" Профиль: {_profileName}");
                Environment.ExitCode = 2;
                _logger.Error(" Что делать написано выше. Обычно помогает вход по email и паролю.");
                _logger.Error("============================================================");
                await NotifyAsync(
                    $"🚫 Не удалось войти в Unity (профиль {_profileName}).\n" +
                    (_loginProblem ?? (HasCredentials
                        ? "Unity не подтвердила вход. Подробности в логе."
                        : "Сессия истекла, а email и пароль не заданы (UNITY_EMAIL / UNITY_PASSWORD).")));
                return;
            }

            if (_options.LoginOnly)
            {
                var whoami = await TryReadSignedInUserAsync(page);
                _logger.Info("============================================================");
                _logger.Info(" ГОТОВО. ВЫ ВОШЛИ В UNITY.");
                _logger.Info($" Профиль: {_profileName}");
                if (!string.IsNullOrWhiteSpace(whoami))
                {
                    _logger.Info($" Аккаунт: {whoami}");
                }

                _logger.Info(" Вход сохранён. В следующий раз программа войдёт сама, без окна браузера.");
                _logger.Info(" Теперь можно запускать пункты 1-8 в меню.");
                _logger.Info("============================================================");
                return;
            }

            var sources = ResolveSources();
            var assetUrls = await CollectAssetUrlsAsync(page, sources);
            var assetPromocodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Память профиля нужна ещё до Telegram: пачка из каналов набирается
            // только из ассетов, про которые ещё ничего не известно.
            var profileDirectory = _profileStore.GetProfileDirectory(_profileName);
            var ownedCache = new OwnedAssetsCache(profileDirectory);
            var deprecatedCache = new OwnedAssetsCache(
                profileDirectory, "deprecated_assets.txt", "Ассеты, удалённые издателем из магазина.");

            var rejectedPromoCache = new OwnedAssetsCache(
                profileDirectory, "rejected_promocodes.txt",
                "Промокоды, которые магазин уже не принял: адрес ассета и код через пробел.");

            _ownedCache = ownedCache;
            _deprecatedCache = deprecatedCache;
            _rejectedPromoCache = rejectedPromoCache;
            var skippedKnown = 0;
            var skippedDeprecated = 0;

            var report = new RunReport
            {
                StartedAtUtc = DateTime.UtcNow,
                DryRun = _options.DryRun,
                Sources = sources
            };

            // Ассеты, про которые всё известно с прошлых запусков: страницу не открываем.
            // Возвращает true, если ассет пропущен.
            bool SkipIfKnown(string url)
            {
                if (_options.RecheckOwned)
                {
                    return false;
                }

                if (ownedCache.Contains(url))
                {
                    skippedKnown++;
                    report.Items.Add(new ProcessResult
                    {
                        Url = url,
                        Status = AssetProcessStatus.AlreadyOwned,
                        Message = "Уже был на аккаунте (известно с прошлых запусков, страница не открывалась)."
                    });
                    return true;
                }

                if (deprecatedCache.Contains(url))
                {
                    skippedDeprecated++;
                    report.Items.Add(new ProcessResult
                    {
                        Url = url,
                        Status = AssetProcessStatus.Deprecated,
                        Message = "Удалён из магазина (известно с прошлых запусков, страница не открывалась)."
                    });
                    return true;
                }

                return false;
            }

            // Telegram: пачками (по умолчанию) — собрали пачку, проверили в магазине, собираем
            // следующую из более старых постов. С --tg-batch-size 0 — всё разом, как раньше.
            var batchSize = _options.TelegramBatchSize;
            List<TelegramChannelCursor>? telegramCursors = null;

            // «Только новые» (всегда на сервере): читаем посты, появившиеся после прошлого прогона.
            // В этом режиме всегда работаем пачками: только так известно, где остановились.
            var telegramState = TelegramChannelState.Load(profileDirectory);
            if (_options.TelegramOnlyNew && batchSize <= 0)
            {
                batchSize = 30;
            }

            if (_options.TelegramChannels.Count > 0)
            {
                if (batchSize > 0)
                {
                    telegramCursors = _options.TelegramChannels.Select(c =>
                    {
                        var cursor = new TelegramChannelCursor(c);
                        if (_options.TelegramOnlyNew)
                        {
                            cursor.StopAtId = telegramState.LastSeen(c);
                            // Канал впервые: ограничиваемся последними постами, историю не листаем.
                            cursor.MaxPosts = cursor.StopAtId > 0 ? int.MaxValue : _options.TelegramPostLimit;
                        }

                        return cursor;
                    }).ToList();

                    if (_options.TelegramOnlyNew)
                    {
                        foreach (var cursor in telegramCursors)
                        {
                            _logger.Info(cursor.StopAtId > 0
                                ? $"Telegram: канал {cursor.Name} — читаем посты новее #{cursor.StopAtId}."
                                : $"Telegram: канал {cursor.Name} читаем впервые — берём последние {cursor.MaxPosts} постов.");
                        }
                    }

                    _logger.Info(
                        $"Telegram: ассеты берутся пачками по {batchSize}. Собрали пачку — проверили в магазине — " +
                        (_options.TelegramOnlyNew
                            ? "собираем следующую."
                            : "собираем следующую из более старых постов. Лимит постов на канал при этом не действует."));
                }
                else
                {
                    var tgResult = await ReadTelegramOnceAsync(browser);
                    await ReportTelegramResultAsync(tgResult, assetPromocodes);
                    assetUrls.AddRange(tgResult.AssetUrls);
                }
            }

            _logger.Info(telegramCursors is null
                ? $"Найдено уникальных ассетов: {assetUrls.Distinct(StringComparer.OrdinalIgnoreCase).Count()}"
                : $"Найдено уникальных ассетов в списках: {assetUrls.Distinct(StringComparer.OrdinalIgnoreCase).Count()}, из Telegram — пачками дальше.");

            if (!_options.RecheckOwned && (ownedCache.Count > 0 || deprecatedCache.Count > 0))
            {
                _logger.Info(
                    $"В памяти профиля: {ownedCache.Count} уже добавленных ассетов, " +
                    $"{deprecatedCache.Count} удалённых из магазина. Их страницы открывать не будем.");
            }

            var newlyAddedCount = 0;
            if (_options.MaxAddAttempts.HasValue)
            {
                _logger.Info($"Включен лимит по новым добавленным ассетам: {_options.MaxAddAttempts.Value}");
            }

            if (_options.MaxVisitedAssets.HasValue)
            {
                _logger.Info($"Включен лимит по посещенным ассетам: {_options.MaxVisitedAssets.Value}");
            }

            // Очередь на проверку: сначала ссылки из списков, потом пачки из Telegram.
            var queue = new Queue<(string Url, string Label)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var listUrls = assetUrls.Where(seen.Add).ToList();
            for (var i = 0; i < listUrls.Count; i++)
            {
                queue.Enqueue((listUrls[i], $"{i + 1}/{listUrls.Count}"));
            }

            var telegramPending = new List<string>();
            var batchNo = 0;
            var telegramSilentReads = 0;

            // Следующая пачка из Telegram: читаем каналы, пока не наберётся batchSize новых
            // ассетов (не проверенных в этом запуске и не известных по памяти профиля).
            // Лишнее, что пришло с последней страницей, ждёт следующей пачки.
            async Task<List<string>> NextTelegramBatchAsync()
            {
                var need = batchSize - telegramPending.Count;
                if (need > 0 && telegramCursors!.Any(c => !c.Exhausted) && telegramSilentReads < 2)
                {
                    _logger.Info($"==== Telegram: собираем пачку №{batchNo + 1} (нужно ещё {need} новых ассетов) ====");
                    var tg = await ParseTelegramChannelsAsync(
                        browser, telegramCursors!, need,
                        url => !seen.Contains(url) && (_options.RecheckOwned || (!ownedCache.Contains(url) && !deprecatedCache.Contains(url))),
                        maxPostsPerChannel: int.MaxValue, maxPagesPerChannel: int.MaxValue);

                    await ReportTelegramResultAsync(tg, assetPromocodes);
                    foreach (var url in tg.AssetUrls)
                    {
                        if (seen.Add(url) && !SkipIfKnown(url))
                        {
                            telegramPending.Add(url);
                        }
                    }

                    // Два чтения подряд без единого поста — Telegram перестал открываться.
                    telegramSilentReads = tg.AllPosts.Count == 0 ? telegramSilentReads + 1 : 0;
                    if (telegramSilentReads >= 2)
                    {
                        _logger.Warn("Telegram два раза подряд не отдал ни одного поста. Дальше каналы не читаем.");
                    }
                }

                var batch = telegramPending.Take(batchSize).ToList();
                telegramPending.RemoveRange(0, batch.Count);

                if (batch.Count == 0)
                {
                    _logger.Info(telegramCursors!.All(c => c.Exhausted)
                        ? "Telegram: посты в каналах кончились, новых ассетов больше нет."
                        : "Telegram: новых ассетов больше не нашлось.");
                }

                return batch;
            }

            var index = 0;
            var stoppedEarly = false;

            bool LimitReached() =>
                (_options.MaxAddAttempts.HasValue && newlyAddedCount >= _options.MaxAddAttempts.Value) ||
                (_options.MaxVisitedAssets.HasValue && index >= _options.MaxVisitedAssets.Value);

            while (true)
            {
                if (queue.Count == 0)
                {
                    if (telegramCursors is null)
                    {
                        break;
                    }

                    if (LimitReached())
                    {
                        stoppedEarly = true;
                        break;
                    }

                    var batch = await NextTelegramBatchAsync();
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    batchNo++;
                    _logger.Info($"==== Пачка №{batchNo} из Telegram: {batch.Count} ассетов, проверяем их в магазине" +
                                 (telegramPending.Count > 0 ? $" (ещё {telegramPending.Count} ждут следующей пачки)" : string.Empty) +
                                 " ====");
                    for (var i = 0; i < batch.Count; i++)
                    {
                        queue.Enqueue((batch[i], $"пачка {batchNo}: {i + 1}/{batch.Count}"));
                    }
                }

                var (assetUrl, label) = queue.Dequeue();

                if (_options.MaxVisitedAssets.HasValue && index >= _options.MaxVisitedAssets.Value)
                {
                    _logger.Warn(
                        $"Достигнут лимит посещенных ассетов ({index}/{_options.MaxVisitedAssets.Value}). Обработка остановлена.");
                    stoppedEarly = true;
                    break;
                }

                if (_options.MaxAddAttempts.HasValue && newlyAddedCount >= _options.MaxAddAttempts.Value)
                {
                    _logger.Warn(
                        $"[Лимит] Достигнут лимит новых ассетов ({newlyAddedCount}/{_options.MaxAddAttempts.Value}). Обработка остановлена.");
                    stoppedEarly = true;
                    break;
                }

                // Ассеты, про которые уже всё известно, не открываем вовсе.
                if (SkipIfKnown(assetUrl))
                {
                    continue;
                }

                index++;
                _logger.Info($"[{label}] {assetUrl}");

                assetPromocodes.TryGetValue(assetUrl, out var promoCode);
                if (promoCode != null && !_options.RecheckOwned &&
                    rejectedPromoCache.Contains(PromoCacheKey(assetUrl, promoCode)))
                {
                    // Истёкший код заново не оживёт, а проверка стоит почти минуту.
                    _logger.Info($"[Промокод] Код '{promoCode}' для этого ассета магазин уже не принял раньше. Не пробуем.");
                    promoCode = null;
                }

                var result = await ProcessAssetAsync(page, assetUrl, promoCode);
                report.Items.Add(result);

                if (result.Status is AssetProcessStatus.Added or AssetProcessStatus.AlreadyOwned)
                {
                    ownedCache.Add(assetUrl);
                }
                else if (result.Status == AssetProcessStatus.Deprecated)
                {
                    deprecatedCache.Add(assetUrl);
                }
                else if (result.Status == AssetProcessStatus.PromoNotApplied && promoCode != null)
                {
                    rejectedPromoCache.Add(PromoCacheKey(assetUrl, promoCode));
                }

                // Сохраняем по ходу дела, а не только в конце: проход по сотням ассетов
                // занимает много минут, и обрыв не должен стирать уже узнанное.
                if (index % 10 == 0)
                {
                    SaveCaches();
                }

                // В лимит попадают только фактически добавленные ассеты.
                // AlreadyOwned / PaidSkipped / Failed не считаются.
                // В режиме --dry-run считаем то, что было бы добавлено, иначе лимит не сработает никогда.
                var countsAsNewlyAdded = result.Status == AssetProcessStatus.Added ||
                                         (_options.DryRun && result.Status == AssetProcessStatus.WouldAddInDryRun);
                result.CountsTowardsAddLimit = countsAsNewlyAdded;

                if (countsAsNewlyAdded)
                {
                    newlyAddedCount++;
                    _logger.Info(_options.MaxAddAttempts.HasValue
                        ? $"[Лимит] Добавлено новых ассетов: {newlyAddedCount}/{_options.MaxAddAttempts.Value}"
                        : $"[Лимит] Добавлено новых ассетов: {newlyAddedCount}");

                    if (_options.MaxAddAttempts.HasValue && newlyAddedCount >= _options.MaxAddAttempts.Value)
                    {
                        _logger.Info(
                            $"[Лимит] Достигнут лимит {_options.MaxAddAttempts.Value} новых ассетов. Завершение.");
                        stoppedEarly = true;
                        break;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(_options.DelayMs));
            }

            // Запоминаем, до какого поста дочитали. Только если каналы прочитаны до конца
            // и все собранные ассеты проверены: иначе часть новых постов потерялась бы.
            if (telegramCursors is not null && !stoppedEarly && !_options.DryRun && queue.Count == 0 && telegramPending.Count == 0)
            {
                var advanced = telegramCursors
                    .Where(c => c.Exhausted && c.NewestId > 0 && telegramState.Advance(c.Name, c.NewestId))
                    .Select(c => $"{c.Name} #{c.NewestId}")
                    .ToList();
                if (advanced.Count > 0)
                {
                    telegramState.Save();
                    _logger.Info($"Telegram: запомнили, где остановились: {string.Join(", ", advanced)}. " +
                                 "В режиме «только новые» следующий прогон начнёт отсюда.");
                }
            }

            SaveCaches();

            if (skippedKnown + skippedDeprecated > 0)
            {
                // Удалённый ассет обходится дороже: его страницу приходится долго ждать.
                var saved = skippedKnown * 4 + skippedDeprecated * 60;
                _logger.Info(
                    $"Пропущено без открытия страницы: {skippedKnown} уже добавленных, " +
                    $"{skippedDeprecated} удалённых из магазина. Сэкономлено примерно {saved} секунд.");
            }

            report.FinishedAtUtc = DateTime.UtcNow;
            await File.WriteAllTextAsync(_reportPath, JsonSerializer.Serialize(report, _jsonOptions));

            PrintSummary(report);
            _logger.Info($"Отчет сохранен: {_reportPath}");
            await NotifyRunSummaryAsync(report);
            }
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelRequested;
            SaveCaches();
            FinalizeProfileName();

            var problemsPath = Path.Combine(_logsDirectory, CliOptions.ProblemsFileName);
            _logger.Info("============================================================");
            _logger.Info($" ЕСЛИ ЧТО-ТО ПОШЛО НЕ ТАК — ПРИШЛИТЕ ЭТОТ ОДИН ФАЙЛ:");
            _logger.Info($" {problemsPath}");
            _logger.Info("============================================================");
            _logger.Dispose();
        }
    }

    /// <summary>
    /// Строка с версией, номером коммита и датой сборки.
    /// По ней видно, какой именно код запущен на компьютере пользователя.
    /// </summary>
    public static string BuildVersionLine()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "неизвестна";

        var built = "дата сборки неизвестна";
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                built = $"собрано {File.GetLastWriteTime(exePath):yyyy-MM-dd HH:mm}";
            }
        }
        catch
        {
            // Дата сборки — приятная мелочь, из-за неё падать нельзя.
        }

        return $"{version} | {built}";
    }

    /// <summary>
    /// Открывает страницу входа и проверяет, что на ней есть поля для автовхода.
    /// Ничего не нажимает и никуда не отправляет — только смотрит и делает скриншот.
    /// </summary>
    private async Task CheckSignInPageAsync(IPage page)
    {
        _logger.Info("============================================================");
        _logger.Info($" ПРОВЕРКА СТРАНИЦЫ ВХОДА: {_signInUrl}");
        _logger.Info("============================================================");

        await SafeGoToAsync(page, _signInUrl);
        await WaitForDocumentReadySoftAsync(page, TimeSpan.FromSeconds(15));
        await Task.Delay(3000);

        _logger.Info($"Адрес после загрузки: {ShortUrl(page.Url)}");
        _logger.Info($"Заголовок страницы: {await page.GetTitleAsync()}");

        // Если Unity сразу вернул нас в магазин — значит вход уже выполнен,
        // формы входа на странице просто нет, и искать её бессмысленно.
        if (page.Url.Contains("assetstore.unity.com", StringComparison.OrdinalIgnoreCase))
        {
            var user = await TryReadSignedInUserAsync(page);
            _logger.Info("============================================================");
            _logger.Info(" ВЫ УЖЕ ВОШЛИ. Unity сразу вернул в магазин, форма входа не нужна.");
            _logger.Info($" Профиль: {_profileName}");
            if (!string.IsNullOrWhiteSpace(user))
            {
                _logger.Info($" Аккаунт: {user}");
            }

            _logger.Info(" Можно запускать пункты 1-8 в меню.");
            _logger.Info("============================================================");
            await SaveErrorScreenshotAsync(page, "check-login-page");
            return;
        }

        if (!page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warn("Нас увели с login.unity.com. Проверьте адрес в --sign-in-url.");
        }

        var report = await page.EvaluateFunctionAsync<string>(@"() => {
            const find = (sel) => document.querySelector(sel);
            const email = find('input[type=""email""], input[name*=""email"" i], input[id*=""email"" i]');
            const pass = find('input[type=""password""], input[name*=""password"" i], input[id*=""password"" i]');
            const submit = (pass && pass.form && pass.form.querySelector('button[type=""submit""], input[type=""submit""]'))
                || find('button[type=""submit""], button[data-testid*=""sign"" i]');
            const describe = (el) => el
                ? `НАЙДЕНО (тег ${el.tagName.toLowerCase()}, name=""${el.name || ''}"", id=""${el.id || ''}"")`
                : 'НЕ НАЙДЕНО';
            const inputs = Array.from(document.querySelectorAll('input'))
                .map(el => `type=${el.type} name=${el.name || '-'} id=${el.id || '-'}`);
            return JSON.stringify({
                email: describe(email),
                password: describe(pass),
                submit: describe(submit),
                allInputs: inputs
            });
        }");

        using var parsed = JsonDocument.Parse(report);
        var root = parsed.RootElement;

        _logger.Info($"Поле email:  {root.GetProperty("email").GetString()}");
        _logger.Info($"Поле пароля: {root.GetProperty("password").GetString()}");
        _logger.Info($"Кнопка входа: {root.GetProperty("submit").GetString()}");

        foreach (var input in root.GetProperty("allInputs").EnumerateArray())
        {
            _logger.Info($"  поле ввода: {input.GetString()}");
        }

        var hasEmail = !root.GetProperty("email").GetString()!.StartsWith("НЕ");
        var hasSubmit = !root.GetProperty("submit").GetString()!.StartsWith("НЕ");

        // Форма Unity двухшаговая: на первом экране есть только email и кнопка,
        // поле пароля появляется после её нажатия. Это нормальное состояние.
        if (hasEmail && hasSubmit)
        {
            _logger.Info("ИТОГ: страница входа в порядке. Поле email и кнопка на месте, пароль спросят на следующем шаге.");
        }
        else
        {
            _logger.Warn("ИТОГ: на странице нет полей для входа. Автовход не сработает, входите руками.");
            Environment.ExitCode = 2;
        }

        await SaveErrorScreenshotAsync(page, "check-login-page");
        await SaveHtmlDumpAsync(page, "check-login-page");

        // Отдельно смотрим, какую ссылку на вход даёт сам Asset Store.
        // Именно она содержит служебные параметры, без которых Unity уводит на регистрацию.
        _logger.Info("------------------------------------------------------------");
        _logger.Info(" ССЫЛКА НА ВХОД С САМОГО ASSET STORE");
        _logger.Info("------------------------------------------------------------");

        await SafeGoToAsync(page, AssetStoreHomeUrl);
        await WaitForDocumentReadySoftAsync(page, TimeSpan.FromSeconds(15));
        await Task.Delay(4000);

        var links = await page.EvaluateFunctionAsync<string[]>(@"() => {
            const out = new Set();
            for (const a of document.querySelectorAll('a[href]')) {
                const href = a.href || '';
                if (/login\.unity\.com|id\.unity\.com|sign-in|signin|oauth/i.test(href)) {
                    out.add(href);
                }
            }
            return Array.from(out).slice(0, 20);
        }");

        if (links.Length == 0)
        {
            _logger.Warn("Ссылок на вход в разметке нет — кнопка рисуется скриптом уже после загрузки.");
        }

        foreach (var link in links)
        {
            _logger.Info($"  ссылка: {link}");
        }

        await SaveHtmlDumpAsync(page, "check-store-home");
    }

    /// <summary>
    /// Выбирает папку, в которой Chrome хранит профиль: историю, расширения и вход.
    ///
    /// По умолчанию — своя папка внутри профиля программы. Браузер выглядит обычным,
    /// вход в Unity запоминается между запусками, и при этом ваш личный Chrome не трогается.
    ///
    /// С ключом --use-system-chrome-profile берётся ваш настоящий профиль Chrome
    /// со всеми закладками и уже выполненными входами. Для этого Chrome должен быть закрыт.
    /// </summary>
    private string ResolveChromeUserDataDir()
    {
        if (!string.IsNullOrWhiteSpace(_options.ChromeUserDataDir))
        {
            var custom = Path.GetFullPath(_options.ChromeUserDataDir);
            _logger.Info($"Папка браузера задана вручную: {custom}");
            return custom;
        }

        if (_options.UseSystemChromeProfile)
        {
            var system = FindSystemChromeUserDataDir();
            if (system != null)
            {
                _logger.Info($"Используется ваш обычный профиль Chrome: {system}");
                _logger.Warn("Chrome должен быть полностью закрыт, иначе он не отдаст эту папку.");
                _logger.Warn(
                    "Если окно откроется пустым (about:blank) и ничего не произойдёт — значит Chrome всё ещё запущен. " +
                    "Закройте его через диспетчер задач и повторите, либо вернитесь к своей папке (пункт B).");
                return system;
            }

            _logger.Warn("Обычный профиль Chrome не найден. Используем собственную папку программы.");
        }

        var own = Path.Combine(_profileStore.GetProfileDirectory(_profileName), "chrome");
        Directory.CreateDirectory(own);
        _logger.Info($"Папка браузера профиля '{_profileName}': {own}");
        _logger.Info("Браузер запоминает вход между запусками. Личный Chrome не затрагивается.");
        return own;
    }

    /// <summary>Находит папку профиля обычного Chrome для текущей операционной системы.</summary>
    private static string? FindSystemChromeUserDataDir()
    {
        string candidate;

        if (OperatingSystem.IsWindows())
        {
            candidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "User Data");
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "Google", "Chrome");
        }
        else
        {
            candidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "google-chrome");
        }

        return Directory.Exists(candidate) ? candidate : null;
    }

    private readonly List<string> _telegramGitLinks = [];
    private readonly List<string> _telegramPromocodes = [];

    /// <summary>
    /// Пишет в лог, что принесло чтение Telegram, и копит найденное за весь запуск:
    /// промокоды — в общий словарь (код из более свежего поста не перетирается),
    /// git-ссылки и промокоды — в файлы в logs/.
    /// </summary>
    private async Task ReportTelegramResultAsync(TelegramParseResult tgResult, Dictionary<string, string> assetPromocodes)
    {
        if (tgResult.AssetUrls.Count > 0)
        {
            _logger.Info($"Telegram: найдено ссылок на ассеты: {tgResult.AssetUrls.Count}");
            foreach (var url in tgResult.AssetUrls)
            {
                _logger.Debug($"Telegram asset: {url}");
            }
        }

        foreach (var kvp in tgResult.AssetPromocodes)
        {
            assetPromocodes.TryAdd(kvp.Key, kvp.Value);
        }

        if (tgResult.GitLinks.Count > 0)
        {
            _telegramGitLinks.AddRange(tgResult.GitLinks.Except(_telegramGitLinks, StringComparer.OrdinalIgnoreCase).ToList());
            var gitLogPath = Path.Combine(_logsDirectory, "telegram_git_links.log");
            await File.WriteAllLinesAsync(gitLogPath, _telegramGitLinks);
            _logger.Info($"Telegram git-ссылки сохранены в: {gitLogPath} (всего: {_telegramGitLinks.Count})");
        }

        if (tgResult.Promocodes.Count > 0)
        {
            _telegramPromocodes.AddRange(tgResult.Promocodes);
            var promoLogPath = Path.Combine(_logsDirectory, "telegram_promocodes.log");
            await File.WriteAllLinesAsync(promoLogPath, _telegramPromocodes);
            _logger.Info($"Telegram промокоды сохранены в: {promoLogPath} (всего: {_telegramPromocodes.Count})");
        }

        if (tgResult.PostsWithoutLinks.Count > 0)
        {
            _logger.Info($"Telegram: постов без ссылок на Asset Store: {tgResult.PostsWithoutLinks.Count}. " +
                         "Их тексты есть в telegram_posts_raw.log.");
        }

        foreach (var err in tgResult.Errors)
        {
            _logger.Warn($"Telegram ошибка: {err}");
        }
    }

    /// <summary>Читает каналы за один раз: с каждого до telegram.postLimit последних постов.</summary>
    private Task<TelegramParseResult> ReadTelegramOnceAsync(IBrowser mainBrowser) =>
        ParseTelegramChannelsAsync(
            mainBrowser,
            _options.TelegramChannels.Select(c => new TelegramChannelCursor(c)).ToList(),
            wantAssets: int.MaxValue,
            isWanted: _ => true,
            maxPostsPerChannel: _options.TelegramPostLimit,
            maxPagesPerChannel: TelegramSourceParser.MaxPagesPerChannel);

    /// <summary>
    /// Разбирает Telegram-каналы. Страницы каналов качаются обычными запросами, без
    /// браузера: через прокси идут только они, Unity всё так же ходит напрямую своим
    /// браузером. Не вышло — запасной путь: отдельный браузер через прокси, как раньше.
    ///
    /// Курсоры помнят, где остановилось чтение каждого канала: при сборе пачками
    /// следующий вызов продолжает с более старых постов. Чтение идёт, пока не наберётся
    /// wantAssets ассетов, подходящих под isWanted, или пока не кончатся посты.
    /// </summary>
    private async Task<TelegramParseResult> ParseTelegramChannelsAsync(
        IBrowser mainBrowser,
        List<TelegramChannelCursor> cursors,
        int wantAssets,
        Func<string, bool> isWanted,
        int maxPostsPerChannel,
        int maxPagesPerChannel)
    {
        _logger.Info($"Запуск парсинга Telegram каналов: {string.Join(", ", cursors.Select(c => c.Name))}");

        IBrowser tgBrowser = mainBrowser;
        IBrowser? ownBrowser = null;

        try
        {
            // Порядок такой, чтобы ничего не приходилось настраивать руками:
            //   1. прокси, заданный явно;
            //   2. прокси, который сработал в прошлый раз (лежит в профиле);
            //   3. напрямую;
            //   4. если напрямую не вышло — сам ищем рабочий прокси и повторяем.
            var proxy = _options.TelegramProxy;

            var probeChannel = _options.TelegramChannels.FirstOrDefault() ?? "telegram";

            // Кандидаты пробуются по порядку: сначала заданный или запомненный адрес,
            // потом подобранные из списка. Первый, через который посты действительно
            // прочитались, становится рабочим и запоминается.
            var candidates = new List<string>();

            if (!string.IsNullOrWhiteSpace(_options.TelegramProxy))
            {
                candidates.Add(_options.TelegramProxy);
            }
            else
            {
                var remembered = ReadRememberedProxy();

                if (!string.IsNullOrWhiteSpace(remembered))
                {
                    _logger.Info($"Проверяем прокси из прошлого запуска: {remembered}");
                    if (await TestProxyAsync(remembered, probeChannel))
                    {
                        _logger.Info("Работает, берём его.");
                        candidates.Add(remembered);
                    }
                    else
                    {
                        _logger.Info("Не отвечает, ищем замену.");
                    }
                }
                else
                {
                    _logger.Info("Проверяем, открывается ли Telegram напрямую...");
                    if (await TestProxyAsync(null, probeChannel))
                    {
                        _logger.Info("Открывается. Прокси не нужен.");
                        candidates.Add(string.Empty);
                    }
                    else
                    {
                        _logger.Info("Не открывается — похоже на блокировку.");
                    }
                }

                // Прокси, найденные в этой же программе раньше: проверить их дешевле,
                // чем скачивать и перебирать список заново.
                if (candidates.Count == 0)
                {
                    foreach (var known in _telegramProxyPool.ToList())
                    {
                        // Запомненный прокси только что проверили выше — второй раз незачем.
                        if (string.Equals(known, remembered, StringComparison.OrdinalIgnoreCase))
                        {
                            _telegramProxyPool.Remove(known);
                            continue;
                        }

                        if (await TestProxyAsync(known, probeChannel))
                        {
                            _logger.Info($"Прокси из этого прогона ещё живой: {known}");
                            candidates.Add(known);
                            break;
                        }

                        _telegramProxyPool.Remove(known);
                    }
                }

                if (candidates.Count == 0 && _options.TelegramAutoProxy)
                {
                    candidates.AddRange(await AutoSelectTelegramProxiesAsync());
                }
            }

            if (candidates.Count == 0)
            {
                candidates.Add(string.Empty);
            }

            TelegramParseResult result = new();

            // Бесплатный прокси может открыть один канал и сорваться на другом.
            // Поэтому каналы, которые не открылись, пробуем через следующий прокси,
            // а уже прочитанные не перечитываем.
            var pending = cursors.Where(c => !c.Exhausted).ToList();
            var wantedFound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var proxySaved = false;

            // Сначала читаем каналы обычными запросами: страница t.me/s/<канал> приходит
            // готовой, браузер для неё не нужен. Так быстрее и не нужен второй Chrome.
            await TryCandidatesAsync(viaBrowser: false);

            // Не вышло — старый путь: отдельный браузер через прокси. Он медленнее,
            // но переживает смену вёрстки Telegram и работает там, где простой запрос отбивают.
            if (pending.Count > 0 && wantedFound.Count < wantAssets)
            {
                _logger.Info("Простым запросом каналы не открылись. Пробуем по-старому — браузером через прокси.");
                await TryCandidatesAsync(viaBrowser: true);
            }

            async Task TryCandidatesAsync(bool viaBrowser)
            {
                for (var i = 0; i < candidates.Count && i < 4 && pending.Count > 0 && wantedFound.Count < wantAssets; i++)
                {
                    var candidate = candidates[i];

                    if (ownBrowser is not null)
                    {
                        await ownBrowser.CloseAsync();
                        await ownBrowser.DisposeAsync();
                        ownBrowser = null;
                    }

                    if (i > 0)
                    {
                        _logger.Warn($"Пробуем запасной прокси: {candidate} (каналы: {string.Join(", ", pending.Select(c => c.Name))})");
                    }

                    // Кто качает страницы: обычный запрос или браузер.
                    HttpClient? tgClient = null;
                    Func<string, Task<string?>>? fetchHtml = null;
                    tgBrowser = mainBrowser;

                    try
                    {
                        if (!viaBrowser)
                        {
                            tgClient = CreateTelegramHttpClient(candidate);
                            fetchHtml = url => FetchTelegramPageAsync(tgClient, url);
                            _logger.Info(string.IsNullOrWhiteSpace(candidate)
                                ? "Telegram читаем напрямую, без браузера и без прокси."
                                : $"Telegram читаем без браузера, через прокси: {candidate}");
                        }
                        else if (string.IsNullOrWhiteSpace(candidate))
                        {
                            _logger.Info("Telegram открываем браузером напрямую, без прокси.");
                        }
                        else
                        {
                            // Запуск браузера под прокси иногда не проходит совсем. Это не повод
                            // бросать чтение каналов: берём следующий прокси из списка.
                            try
                            {
                                ownBrowser = await LaunchTelegramBrowserAsync(candidate);
                            }
                            catch (Exception ex)
                            {
                                _logger.Warn($"Прокси {candidate} пропускаем: браузер для Telegram не запустился ({ex.Message}).");
                                continue;
                            }

                            tgBrowser = ownBrowser;
                            _logger.Info($"Telegram идёт через отдельный прокси: {candidate}");
                            _logger.Info("Unity при этом работает напрямую, без прокси.");
                        }

                        foreach (var cursor in pending)
                        {
                            cursor.FailedThisRead = false;
                        }

                        var partial = await RunParserAsync(tgBrowser, pending, wantAssets - wantedFound.Count, fetchHtml);
                        MergeTelegramResults(result, partial);
                        foreach (var url in partial.AssetUrls.Where(isWanted))
                        {
                            wantedFound.Add(url);
                        }

                        var failed = pending.Where(c => c.FailedThisRead).ToList();
                        if (failed.Count < pending.Count && !proxySaved && !string.IsNullOrWhiteSpace(candidate))
                        {
                            await RememberProxyAsync(candidate);
                            proxySaved = true;
                        }

                        pending = failed;

                        if (pending.Count > 0 && !LooksBlocked(partial))
                        {
                            break;
                        }
                    }
                    finally
                    {
                        tgClient?.Dispose();
                    }
                }
            }

            if (pending.Count > 0 && result.AllPosts.Count > 0)
            {
                _logger.Warn($"Telegram: не открылись каналы: {string.Join(", ", pending.Select(c => c.Name))}. Их посты в этот раз пропущены.");
            }

            // Следующая пачка снова пробует все каналы.
            foreach (var cursor in cursors)
            {
                cursor.FailedThisRead = false;
            }

            await SaveTelegramPostsAsync(result);
            ExplainTelegramFailure(result);
            return result;

            async Task<TelegramParseResult> RunParserAsync(
                IBrowser browser,
                List<TelegramChannelCursor> channels,
                int want,
                Func<string, Task<string?>>? fetchHtml)
            {
                var parser = new TelegramSourceParser(
                    browser,
                    _logger,
                    _logsDirectory,
                    _options.NavigationTimeoutMs,
                    _options.TelegramPostLimit,
                    _options.TelegramScreenshotOnNoLinks,
                    fetchHtml);

                return await parser.ReadAsync(channels, want, isWanted, maxPostsPerChannel, maxPagesPerChannel);
            }
        }
        finally
        {
            if (ownBrowser is not null)
            {
                await ownBrowser.CloseAsync();
                await ownBrowser.DisposeAsync();
            }
        }
    }

    /// <summary>Путь к файлу, где лежит последний сработавший прокси профиля.</summary>
    private string RememberedProxyPath =>
        Path.Combine(_profileStore.GetProfileDirectory(_profileName), "telegram_proxy.txt");

    /// <summary>Читает прокси, сработавший в прошлый раз. Пусто, если такого нет.</summary>
    private string? ReadRememberedProxy()
    {
        try
        {
            if (!File.Exists(RememberedProxyPath))
            {
                return null;
            }

            var value = File.ReadAllText(RememberedProxyPath).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Дописывает к общему результату то, что прочиталось через очередной прокси.
    /// Ошибки берутся только последние: ошибки каналов, которые потом открылись, уже не важны.
    /// </summary>
    private static void MergeTelegramResults(TelegramParseResult total, TelegramParseResult partial)
    {
        total.AssetUrls = total.AssetUrls.Concat(partial.AssetUrls).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        total.GitLinks = total.GitLinks.Concat(partial.GitLinks).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        total.Promocodes.AddRange(partial.Promocodes);
        total.PostsWithoutLinks.AddRange(partial.PostsWithoutLinks);
        total.AllPosts.AddRange(partial.AllPosts);
        total.Errors = partial.Errors;
        total.FailedChannels = partial.FailedChannels;

        foreach (var kvp in partial.AssetPromocodes)
        {
            total.AssetPromocodes.TryAdd(kvp.Key, kvp.Value);
        }
    }

    /// <summary>Похожи ли ошибки на блокировку или мёртвый прокси.</summary>
    private static bool LooksBlocked(TelegramParseResult result)
    {
        string[] codes =
        [
            "ERR_CONNECTION_TIMED_OUT", "ERR_CONNECTION_RESET", "ERR_CONNECTION_CLOSED",
            "ERR_NAME_NOT_RESOLVED", "ERR_CONNECTION_REFUSED", "ERR_TIMED_OUT",
            "ERR_PROXY_CONNECTION_FAILED", "ERR_SOCKS_CONNECTION_FAILED", "ERR_ADDRESS_UNREACHABLE",
            "ERR_NETWORK_CHANGED", "ERR_EMPTY_RESPONSE", "ERR_TUNNEL_CONNECTION_FAILED",
            // Чтение без браузера: своих кодов ошибок у него нет, есть метка парсера.
            TelegramSourceParser.NetworkFailureMarker
        ];

        return result.Errors.Any(e => codes.Any(c => e.Contains(c, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Список бесплатных SOCKS5-прокси, обновляемый сообществом.</summary>
    private const string DefaultProxyListUrl =
        "https://raw.githubusercontent.com/hookzof/socks5_list/master/proxy.txt";

    /// <summary>
    /// Рабочие прокси, найденные в этом прогоне. Ассеты собираются пачками, и каждая
    /// пачка заново читает каналы. Без этого списка запомненный прокси умирал между
    /// пачками, и программа скачивала 12 тысяч адресов заново (19.09 на сервере — дважды).
    /// </summary>
    private readonly List<string> _telegramProxyPool = [];

    /// <summary>Скачанный список адресов: в одном прогоне он один и тот же.</summary>
    private List<string>? _proxyListCache;

    /// <summary>
    /// Подбирает рабочий прокси для Telegram.
    /// Сначала пробует тот, что сработал в прошлый раз, потом берёт список
    /// и проверяет адреса пачками, пока не найдёт живой.
    ///
    /// Через прокси идут только открытые страницы каналов Telegram.
    /// Вход в Unity и cookies через него не проходят никогда.
    /// </summary>
    private async Task<List<string>> AutoSelectTelegramProxiesAsync()
    {
        var testChannel = _options.TelegramChannels.FirstOrDefault() ?? "telegram";

        var candidates = await LoadProxyCandidatesAsync();
        if (candidates.Count == 0)
        {
            _logger.Warn("Не удалось получить список прокси. Задайте свой через --tg-proxy.");
            return [];
        }

        _logger.Info($"Список получен: {candidates.Count} адресов. Ищем рабочие, проверяем пачками по 25.");
        _logger.Info("Через прокси пойдут только открытые страницы Telegram. Unity — напрямую.");

        var shuffled = candidates.OrderBy(_ => Random.Shared.Next()).ToList();
        const int batchSize = 25;
        var checkedCount = 0;

        for (var offset = 0; offset < shuffled.Count; offset += batchSize)
        {
            var batch = shuffled.Skip(offset).Take(batchSize).ToList();
            var tasks = batch.Select(async candidate =>
                await TestProxyAsync(candidate, testChannel) ? candidate : null).ToList();

            var results = await Task.WhenAll(tasks);
            checkedCount += batch.Count;

            // Берём все рабочие из пачки: если первый подведёт в браузере,
            // сразу есть запасные, и не придётся искать заново.
            var working = results.Where(r => r != null).Select(r => r!).ToList();
            if (working.Count > 0)
            {
                _logger.Info(
                    $"Найдено рабочих прокси: {working.Count} (проверено адресов: {checkedCount}). " +
                    $"Основной: {working[0]}");

                foreach (var found in working.Where(found => !_telegramProxyPool.Contains(found)))
                {
                    _telegramProxyPool.Add(found);
                }

                return working;
            }

            _logger.Info($"Проверено {checkedCount} из {shuffled.Count}, рабочих пока нет...");
        }

        _logger.Warn("Ни один прокси из списка не подошёл. Попробуйте позже или задайте свой через --tg-proxy.");
        return [];
    }

    /// <summary>Запоминает рабочий прокси, чтобы следующий запуск начинался сразу с него.</summary>
    private async Task RememberProxyAsync(string proxy)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RememberedProxyPath)!);
            await File.WriteAllTextAsync(RememberedProxyPath, proxy);
            _logger.Info($"Запомнили {proxy} для профиля '{_profileName}'.");
        }
        catch (Exception ex)
        {
            _logger.Debug($"Не удалось запомнить прокси: {ex.Message}");
        }
    }

    /// <summary>Читает список прокси: по ссылке или из локального файла.</summary>
    private async Task<List<string>> LoadProxyCandidatesAsync()
    {
        var source = string.IsNullOrWhiteSpace(_options.TelegramProxyList)
            ? DefaultProxyListUrl
            : _options.TelegramProxyList;

        if (_proxyListCache is { Count: > 0 })
        {
            _logger.Info($"Список прокси уже получен в этом прогоне: {_proxyListCache.Count} адресов, скачивать заново не нужно.");
            return _proxyListCache;
        }

        try
        {
            string text;

            if (File.Exists(source))
            {
                _logger.Info($"Читаем список прокси из файла: {source}");
                text = await File.ReadAllTextAsync(source);
            }
            else
            {
                _logger.Info($"Скачиваем список прокси: {source}");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                text = await _httpClient.GetStringAsync(source, cts.Token);
            }

            return _proxyListCache = text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !line.StartsWith('#'))
                .Select(line => line.Contains("://", StringComparison.Ordinal) ? line : "socks5://" + line)
                .Where(line => Uri.TryCreate(line, UriKind.Absolute, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Список прокси не получен: {ex.Message}");
            _logger.Warn("Если GitHub тоже заблокирован — сохраните список в файл и укажите его в --tg-proxy-list.");
            return [];
        }
    }

    /// <summary>
    /// Обычная браузерная подпись: t.me отдаёт страницу и без неё, но так меньше
    /// шансов получить заглушку вместо постов.
    /// </summary>
    private const string TelegramUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/153.0.0.0 Safari/537.36";

    /// <summary>
    /// Клиент для чтения открытых страниц Telegram. Через него идут только они:
    /// Unity ходит напрямую и своим браузером.
    /// </summary>
    private static HttpClient CreateTelegramHttpClient(string? proxy)
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };

        if (string.IsNullOrWhiteSpace(proxy))
        {
            handler.UseProxy = false;
        }
        else
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        // 12 секунд на попытку, попыток три. При подборе прокси обязан ответить за 8,
        // поэтому дольше ждать смысла нет: лучше быстрее взять следующий прокси.
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(TelegramUserAgent);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru,en;q=0.9");
        return client;
    }

    /// <summary>
    /// Качает страницу канала. Бесплатный прокси часто срывается на ровном месте,
    /// поэтому три попытки. Не получилось — null, и канал пробуется другим путём.
    ///
    /// Отдельно разбираются два ответа Telegram: «слишком часто» (429) и его собственные
    /// ошибки (5xx). Оба лечатся ожиданием, а не сменой прокси, поэтому ждём и повторяем.
    /// </summary>
    private async Task<string?> FetchTelegramPageAsync(HttpClient client, string url)
    {
        const int attempts = 3;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(url);

                if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                    (int)response.StatusCode >= 500)
                {
                    if (attempt == attempts)
                    {
                        _logger.Warn($"[Telegram] {url}: Telegram отвечает {(int)response.StatusCode}, и после ожидания тоже. Пропускаем страницу.");
                        return null;
                    }

                    var wait = response.Headers.RetryAfter?.Delta
                               ?? TimeSpan.FromSeconds(response.StatusCode == HttpStatusCode.TooManyRequests ? 5 : 2);
                    _logger.Info($"[Telegram] Telegram просит подождать ({(int)response.StatusCode}). Ждём {wait.TotalSeconds:0} с и пробуем снова.");
                    await Task.Delay(wait);
                    continue;
                }

                // Прочие отказы (нет такого канала, 403) повтором не лечатся.
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warn($"[Telegram] {url}: ответ {(int)response.StatusCode}. Повторять нечего — проверьте имя канала.");
                    return null;
                }

                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                if (attempt == attempts)
                {
                    _logger.Debug($"[Telegram] Страница {url} не скачалась: {ex.Message}");
                    return null;
                }

                // Про паузу в логе должно быть видно, иначе она выглядит зависанием.
                _logger.Info($"[Telegram] Страница не пришла (попытка {attempt} из {attempts}): {ex.GetBaseException().Message}. Пробуем снова...");
                await Task.Delay(1500);
            }
        }

        return null;
    }

    /// <summary>
    /// Проверяет один прокси: открывается ли через него страница канала.
    ///
    /// Восемь секунд — намеренно немного. Живой прокси отдаёт страницу за одну-две,
    /// а тот, что думает дольше, будет мучительно медленным и в работе.
    /// </summary>
    private static async Task<bool> TestProxyAsync(string? proxy, string channel)
    {
        try
        {
            using var handler = new HttpClientHandler();

            if (string.IsNullOrWhiteSpace(proxy))
            {
                handler.UseProxy = false;
            }
            else
            {
                handler.Proxy = new WebProxy(proxy);
                handler.UseProxy = true;
            }

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            var html = await client.GetStringAsync($"https://t.me/s/{channel}");

            // Признак того, что страница действительно отдала посты, а не заглушку провайдера.
            return html.Contains("tgme_widget_message", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Складывает полные тексты постов в отдельный файл.
    /// В основном логе их нет намеренно: они занимают сотни строк и мешают читать.
    /// </summary>
    private async Task SaveTelegramPostsAsync(TelegramParseResult result)
    {
        var path = Path.Combine(_logsDirectory, "telegram_posts_raw.log");

        var lines = new List<string>
        {
            string.Empty,
            "############################################################",
            $"# ЗАПУСК: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"# КАНАЛЫ: {string.Join(", ", _options.TelegramChannels)}",
            $"# ПОСТОВ СОБРАНО: {result.AllPosts.Count}",
            "############################################################"
        };

        foreach (var post in result.AllPosts)
        {
            lines.Add("============================================================");
            lines.Add($"CHANNEL: {post.ChannelName} | POST ID: {post.PostId}");
            lines.Add("============================================================");
            lines.Add(post.Text);
            lines.Add(string.Empty);
        }

        await File.AppendAllLinesAsync(path, lines);
        _logger.Info($"Полные тексты постов ({result.AllPosts.Count}) дописаны в: {path}");
    }

    /// <summary>
    /// Запускает браузер, повторяя попытку при неудаче.
    ///
    /// Самая частая причина отказа — Chrome от прошлого запуска ещё не закрылся
    /// и держит папку профиля. Так бывает после Ctrl+C. Через несколько секунд
    /// он завершается сам, и вторая попытка проходит.
    /// </summary>
    private async Task<IBrowser> LaunchBrowserWithRetryAsync(LaunchOptions options)
    {
        const int attempts = 3;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await Puppeteer.LaunchAsync(options);
            }
            catch (Exception ex) when (attempt < attempts && !IsNoDisplayError(ex))
            {
                _logger.Warn($"Браузер не запустился (попытка {attempt} из {attempts}): {ex.Message}");
                _logger.Warn("Обычно это Chrome от прошлого запуска: он ещё держит папку профиля.");
                _logger.Warn("Ждём 6 секунд и пробуем снова...");
                await Task.Delay(6000);
            }
            catch (Exception ex)
            {
                // Последняя попытка или ошибка, которую повтор не исправит:
                // объясняем и отдаём ошибку дальше, чтобы её текст попал в файл.
                ExplainBrowserLaunchFailure(ex);
                throw;
            }
        }
    }

    /// <summary>
    /// Убирает блокировку папки браузера, оставшуюся от упавшего запуска.
    ///
    /// Chrome пишет в SingletonLock «имя-компьютера-номер-процесса». Если процесс жив,
    /// блокировка настоящая. Но в Docker у каждого нового контейнера своё имя, и чужую
    /// блокировку Chrome не снимает никогда: после любой аварийной остановки браузер
    /// перестаёт запускаться совсем. Поэтому снимаем её сами, если она с другого
    /// «компьютера» или её процесса уже нет. Только для своей папки браузера программы,
    /// обычный Chrome пользователя не трогаем.
    /// </summary>
    private void RemoveStaleChromeLock(string userDataDir)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var lockPath = Path.Combine(userDataDir, "SingletonLock");
            var info = new FileInfo(lockPath);
            if (info.LinkTarget is not { } target)
            {
                return;
            }

            var dash = target.LastIndexOf('-');
            var host = dash > 0 ? target[..dash] : target;
            var pidAlive = dash > 0 && int.TryParse(target[(dash + 1)..], out var pid) && IsProcessAlive(pid);
            if (host == Environment.MachineName && pidAlive)
            {
                return;
            }

            foreach (var name in new[] { "SingletonLock", "SingletonSocket", "SingletonCookie" })
            {
                File.Delete(Path.Combine(userDataDir, name));
            }

            _logger.Info($"Сняли блокировку папки браузера от прошлого запуска ({target}).");
        }
        catch (Exception ex)
        {
            _logger.Debug($"Блокировку папки браузера проверить не удалось: {ex.Message}");
        }

        static bool IsProcessAlive(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Браузеру негде показать окно: ни X11, ни Wayland ему не доступны.</summary>
    private static bool IsNoDisplayError(Exception ex) =>
        ex.Message.Contains("Missing X server", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("platform failed to initialize", StringComparison.OrdinalIgnoreCase);

    private void ExplainBrowserLaunchFailure(Exception ex)
    {
        _logger.Error("============================================================");
        _logger.Error(" БРАУЗЕР НЕ ЗАПУСКАЕТСЯ");
        _logger.Error("");

        if (IsNoDisplayError(ex))
        {
            _logger.Error(" Браузеру негде показать окно: программе не виден экран.");
            _logger.Error(" Так бывает, если запускать из терминала внутри Flatpak или по SSH.");
            _logger.Error("");
            _logger.Error(" Что сделать:");
            _logger.Error("   - запустите run.sh из обычного терминала рабочего стола (Konsole);");
            _logger.Error("   - или из терминала Flatpak так: flatpak-spawn --host ./run.sh");
        }
        else if (_options.UseSystemChromeProfile)
        {
            _logger.Error(" Включён режим вашего обычного Chrome, а Chrome сейчас запущен.");
            _logger.Error(" Закройте ВСЕ окна Chrome, включая значок у часов, и повторите.");
            _logger.Error(" Либо вернитесь к своей папке браузера — пункт B в меню.");
        }
        else
        {
            _logger.Error(" Скорее всего, окно Chrome от прошлого запуска ещё живо.");
            _logger.Error(" Закройте лишние окна Chrome и запустите снова.");
            _logger.Error(" Если не помогает — перезагрузите компьютер: зависший процесс уйдёт.");
        }

        _logger.Error("");
        _logger.Error($" Текст ошибки: {ex.Message}");
        _logger.Error("============================================================");
    }

    /// <summary>Поднимает отдельный браузер с прокси только для Telegram.</summary>
    private async Task<IBrowser> LaunchTelegramBrowserAsync(string proxy)
    {
        var args = new List<string>
        {
            $"--proxy-server={proxy}",
            "--disable-blink-features=AutomationControlled"
        };

        if (OperatingSystem.IsLinux())
        {
            args.Add("--no-sandbox");
            args.Add("--disable-dev-shm-usage");
        }

        var options = new LaunchOptions
        {
            Headless = true,
            DefaultViewport = null,
            IgnoredDefaultArgs = ["--enable-automation"],
            Args = [..args],
            // 19.09 на сервере запуск под мёртвым прокси висел 5,5 минуты, прежде чем
            // отказать. Ждём 45 секунд: дальше быстрее взять следующий прокси.
            Timeout = 45_000
        };

        if (_chromePath != null)
        {
            options.ExecutablePath = _chromePath;
        }

        return await LaunchBrowserWithRetryAsync(options);
    }

    /// <summary>
    /// Объясняет человеческими словами, почему Telegram не открылся.
    /// Самая частая причина — блокировка провайдером, и без прокси она не лечится.
    /// </summary>
    private void ExplainTelegramFailure(TelegramParseResult result)
    {
        if (result.AllPosts.Count > 0 || result.Errors.Count == 0)
        {
            return;
        }

        bool HasError(params string[] codes) =>
            result.Errors.Any(e => codes.Any(c => e.Contains(c, StringComparison.OrdinalIgnoreCase)));

        // Прокси задан, но сам не отвечает — это отдельная беда, и лечится она иначе.
        if (HasError("ERR_PROXY_CONNECTION_FAILED", "ERR_SOCKS_CONNECTION_FAILED",
                "ERR_PROXY_AUTH_UNSUPPORTED", "ERR_TUNNEL_CONNECTION_FAILED"))
        {
            _logger.Warn("============================================================");
            _logger.Warn(" ПРОКСИ ДЛЯ TELEGRAM НЕ ОТВЕЧАЕТ");
            _logger.Warn($" Указан: {_options.TelegramProxy}");
            _logger.Warn("");
            _logger.Warn(" До самого прокси достучаться не удалось. Проверьте:");
            _logger.Warn("   - запущена ли программа, которая его раздаёт;");
            _logger.Warn("   - верны ли адрес и порт;");
            _logger.Warn("   - тот ли вид: socks5:// или http://");
            _logger.Warn("");
            _logger.Warn(" Пример правильной записи: socks5://127.0.0.1:1080");
            _logger.Warn("============================================================");
            return;
        }

        var blocked = HasError("ERR_CONNECTION_TIMED_OUT", "ERR_CONNECTION_RESET",
            "ERR_NAME_NOT_RESOLVED", "ERR_CONNECTION_REFUSED", "ERR_CONNECTION_CLOSED",
            "ERR_TIMED_OUT", "ERR_ADDRESS_UNREACHABLE", TelegramSourceParser.NetworkFailureMarker);

        if (!blocked)
        {
            return;
        }

        _logger.Warn("============================================================");
        _logger.Warn(" TELEGRAM НЕ ОТКРЫВАЕТСЯ");
        _logger.Warn(" Соединение с t.me не устанавливается. Так выглядит блокировка");
        _logger.Warn(" со стороны провайдера: сайт недоступен ещё до начала обмена.");
        _logger.Warn("");
        _logger.Warn(" ЧТО ПОМОЖЕТ:");
        _logger.Warn("   1. Прокси только для Telegram (Unity останется напрямую):");
        _logger.Warn("      --tg-proxy socks5://127.0.0.1:1080");
        _logger.Warn("      или пункт T в меню run.bat");
        _logger.Warn("   2. VPN на весь компьютер. Тогда через него пойдёт и Unity.");
        _logger.Warn("");
        _logger.Warn(" ЧТО НЕ ПОМОЖЕТ: смена протокола, WebSocket, другие таймауты.");
        _logger.Warn(" Блокировка стоит на маршруте, а не в способе подключения.");
        _logger.Warn("");
        _logger.Warn(" Остальные источники (пункты 1-5) работают без Telegram.");
        _logger.Warn("============================================================");
    }

    /// <summary>Проверка Telegram без входа в Unity: удобно подбирать прокси.</summary>
    private async Task CheckTelegramAsync(IBrowser browser)
    {
        _logger.Info("============================================================");
        _logger.Info(" ПРОВЕРКА TELEGRAM");
        _logger.Info($" Каналы: {string.Join(", ", _options.TelegramChannels)}");
        var proxyLabel = !string.IsNullOrWhiteSpace(_options.TelegramProxy)
            ? _options.TelegramProxy
            : _options.TelegramAutoProxy
                ? "автоподбор из общего списка"
                : "не задан, идём напрямую";
        _logger.Info($" Прокси: {proxyLabel}");
        _logger.Info("============================================================");

        if (_options.TelegramChannels.Count == 0)
        {
            _logger.Warn("Каналы не заданы. Впишите их в telegram_sources.txt, по одному на строку.");
            return;
        }

        var result = await ReadTelegramOnceAsync(browser);

        _logger.Info("============================================================");
        _logger.Info(" ИТОГ ПРОВЕРКИ TELEGRAM");
        _logger.Info($" Постов прочитано: {result.AllPosts.Count}");
        _logger.Info($" Ссылок на ассеты: {result.AssetUrls.Count}");
        _logger.Info($" Промокодов найдено: {result.Promocodes.Count}");
        _logger.Info($" Ассетов с промокодом: {result.AssetPromocodes.Count}");
        _logger.Info($" Ошибок: {result.Errors.Count}");

        foreach (var pair in result.AssetPromocodes.Take(10))
        {
            _logger.Info($"   {pair.Value}  ->  {pair.Key}");
        }

        _logger.Info(result.AllPosts.Count > 0
            ? " Telegram доступен, разбор работает."
            : " Telegram недоступен. Смотрите объяснение выше.");
        if (result.AllPosts.Count == 0)
        {
            Environment.ExitCode = 2;
        }
        _logger.Info("============================================================");
    }

    /// <summary>
    /// Ошибка, которая проходит сама: страница сменилась во время проверки.
    /// Такое случается на медленной сети и не означает, что с ассетом что-то не так.
    /// </summary>
    private static bool IsTransientPageError(Exception ex) =>
        ex.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Target closed", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Session closed", StringComparison.OrdinalIgnoreCase);

    /// <summary>Запись в памяти отвергнутых промокодов: адрес ассета и код через пробел.</summary>
    private static string PromoCacheKey(string assetUrl, string promoCode) => $"{assetUrl} {promoCode}";

    /// <summary>Сообщение через Telegram-бота, если он настроен.</summary>
    public async Task NotifyAsync(string text)
    {
        if (_notifier is { Enabled: true })
        {
            await _notifier.SendAsync(text);
        }
    }

    /// <summary>
    /// Итог прогона боту. Пишет, только если есть что сказать: что-то добавлено или
    /// оплата нажата, но не подтверждена. Пустые прогоны не спамят.
    /// </summary>
    private async Task NotifyRunSummaryAsync(RunReport report)
    {
        if (_notifier is not { Enabled: true })
        {
            return;
        }

        var added = report.Items.Where(i => i.Status == AssetProcessStatus.Added).ToList();
        var unclear = report.Items.Where(i => i.Status == AssetProcessStatus.UnknownAfterClick).ToList();
        if (added.Count == 0 && unclear.Count == 0)
        {
            return;
        }

        static string Name(string url) => url.TrimEnd('/').Split('/').Last();

        var lines = new List<string> { $"✅ Unity Asset Store — профиль {_profileName}", $"Добавлено: {added.Count}" };
        lines.AddRange(added.Take(30).Select(i =>
            $"• {Name(i.Url)}{(i.PromoCode is null ? string.Empty : $" — по промокоду {i.PromoCode}")}\n  {i.Url}"));

        if (unclear.Count > 0)
        {
            lines.Add($"⚠️ Оплата нажата, но покупка не подтверждена: {unclear.Count}");
            lines.AddRange(unclear.Take(10).Select(i => $"• {i.Url}"));
        }

        var failed = report.Items.Count(i => i.Status == AssetProcessStatus.Failed);
        if (failed > 0)
        {
            lines.Add($"Ошибок: {failed} — подробности в логе.");
        }

        await _notifier.SendAsync(string.Join("\n", lines));
    }

    /// <summary>Сохранить память профиля перед остановкой службы (docker stop, systemctl stop).</summary>
    public void SaveCachesOnExit() => SaveCaches();

    /// <summary>Сохраняет память профиля. Вызывать можно сколько угодно раз.</summary>
    private void SaveCaches()
    {
        _ownedCache?.Save();
        _deprecatedCache?.Save();
        _rejectedPromoCache?.Save();
    }

    /// <summary>Пользователь нажал Ctrl+C — успеваем записать память профиля.</summary>
    private void OnCancelRequested(object? sender, ConsoleCancelEventArgs e)
    {
        try
        {
            SaveCaches();
            Console.WriteLine();
            Console.WriteLine("Прервано. Всё, что программа успела узнать про ассеты, сохранено.");
            Console.WriteLine("Следующий запуск не станет проверять их заново.");
        }
        catch
        {
            // На выходе из программы падать нельзя.
        }
    }

    /// <summary>
    /// Ждёт, пока короткая ссылка вида /packages/package/123 сама перейдёт
    /// на настоящий адрес ассета. До перехода страница ещё пустая,
    /// и проверять на ней нечего.
    /// </summary>
    private static async Task WaitForShortLinkRedirectAsync(IPage page, string assetUrl)
    {
        if (!assetUrl.Contains("/packages/package/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var stopAt = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < stopAt)
        {
            if (!page.Url.Contains("/packages/package/", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(400);
        }
    }

    /// <summary>
    /// Убрал ли издатель ассет из магазина.
    /// Unity показывает на такой странице прямое предупреждение и не рисует кнопку добавления.
    /// </summary>
    private async Task<bool> IsDeprecatedAssetPageAsync(IPage page)
    {
        try
        {
            return await page.EvaluateFunctionAsync<bool>(@"() => {
                const text = (document.body ? document.body.innerText : '').toLowerCase();
                return text.includes('has been deprecated from the unity asset store') ||
                       text.includes('no longer available for purchase') ||
                       text.includes('deprecated from the asset store') ||
                       text.includes('удален из unity asset store') ||
                       text.includes('удалён из unity asset store') ||
                       text.includes('больше не доступен для покупки');
            }");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Есть ли на этом компьютере сохранённая сессия для текущего профиля.</summary>
    private bool HasStoredSession() => File.Exists(_sessionStatePath) || File.Exists(_cookiesPath);

    private async Task<bool> EnsureAuthenticatedAsync(IPage page)
    {
        if (await TryLoadSessionStateAsync(page))
        {
            _logger.Info("Состояние сессии загружено (cookies + localStorage), проверка авторизации...");
            if (await TryCheckAuthFastAsync(page, "restored-state"))
            {
                var stable = await ValidateSessionForAssetStoreAsync(page, "restored-state");
                if (stable)
                {
                    _logger.Info("Сессия активна.");
                    await RememberUnityAccountAsync(page);
                    return true;
                }

                _logger.Warn("Восстановленная сессия нестабильна для Asset Store. Требуется повторная авторизация.");
            }
        }

        if (await TryCheckAuthFastAsync(page, "current-page"))
        {
            var stable = await ValidateSessionForAssetStoreAsync(page, "current-page");
            if (stable)
            {
                _logger.Info("Сессия уже активна на текущей странице.");
                await SaveSessionStateAsync(page);
                return true;
            }
        }

        // Контрольная навигация на витрину магазина нужна, только если сессия вообще
        // сохранялась. На новом компьютере она бессмысленна: сразу идём на страницу входа.
        if (HasStoredSession())
        {
            _logger.Info("Быстрая проверка не подтвердила сессию. Выполняем одну контрольную навигацию на Asset Store...");
            await SafeGoToAsync(page, AssetStoreHomeUrl);
            if (await TryCheckAuthFastAsync(page, "home-check"))
            {
                var stable = await ValidateSessionForAssetStoreAsync(page, "home-check");
                if (stable)
                {
                    _logger.Info("Сессия подтверждена после контрольной навигации.");
                    await SaveSessionStateAsync(page);
                    return true;
                }
            }
        }
        else
        {
            _logger.Info($"Сохранённой сессии для профиля '{_profileName}' нет. Сразу открываем страницу входа.");
        }

        if (_lastFullAuthAttemptUtc.HasValue)
        {
            var elapsed = DateTime.UtcNow - _lastFullAuthAttemptUtc.Value;
            if (elapsed < FullAuthCooldown)
            {
                var waitLeft = (int)Math.Ceiling((FullAuthCooldown - elapsed).TotalSeconds);
                _logger.Warn(
                    $"Полный SSO-вход запрашивается слишком часто. Выжидаем cooldown: {Math.Max(1, waitLeft)}с...");
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, waitLeft)));
            }
        }

        _logger.Warn("Требуется вход в Unity.");
        TrySetupCredentialsInteractively();
        _credentialsAsked = true;

        // Без окна браузера войти руками нельзя. Ждать пять минут впустую незачем.
        if (_options.Headless && !HasCredentials)
        {
            _loginProblem =
                "Сессия Unity истекла, а email и пароль не заданы. На сервере задайте UNITY_EMAIL и UNITY_PASSWORD " +
                "или перенесите папку профиля с компьютера, где вы вошли.";
            _logger.Error(_loginProblem);
            return false;
        }
        _lastFullAuthAttemptUtc = DateTime.UtcNow;
        _logger.Info(HasCredentials
            ? "Программа войдёт сама. Окно браузера трогать не нужно."
            : "Сейчас откроется окно браузера. Войдите в аккаунт Unity — программа дождётся и продолжит сама.");
        var authenticated = await AuthenticateViaAssetStoreAsync(page);
        if (!authenticated)
        {
            _logger.Error("Проверка после попытки входа неуспешна.");
            return false;
        }

        await SaveSessionStateAsync(page);
        TrySavePassword();
        await RememberUnityAccountAsync(page);
        _logger.Info("Авторизация подтверждена, состояние сессии сохранено.");
        return true;
    }

    /// <summary>
    /// Выполняет явное указание --save-password false: удаляет ранее сохранённый пароль.
    /// Вызов идемпотентен — если пароля не было, ничего не происходит.
    /// </summary>
    private void ApplySavePasswordPolicy()
    {
        if (_options.SavePassword != false || !SecretStore.CredentialManagerAvailable)
        {
            return;
        }

        if (SecretStore.TryReadCredentials(_credentialTarget, out _, out _) &&
            SecretStore.TryDeleteCredentials(_credentialTarget))
        {
            _profileStore.Touch(_profileName, passwordSaved: false);
            _logger.Info($"Сохранённый пароль профиля '{_profileName}' удалён из Диспетчера учётных данных Windows.");
        }
    }

    /// <summary>
    /// Предлагает ввести логин и пароль, чтобы программа входила сама.
    /// Пустой email означает обычный вход руками в окне браузера — так можно
    /// войти через Google, Apple и остальные способы, где пароля Unity нет.
    /// </summary>
    private void TrySetupCredentialsInteractively()
    {
        // Спросить можно только один раз за запуск, иначе вопрос повторяется
        // и в консоли, и позже — пользователь видит его дважды.
        if (_credentialsAsked)
        {
            return;
        }

        _credentialsAsked = true;

        if (HasCredentials)
        {
            _logger.Info($"Автовход: используются учётные данные профиля '{_profileName}'.");
            return;
        }

        if (!_options.Interactive)
        {
            _logger.Info("Автовход не настроен. Откроется окно браузера для ручного входа.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("==================================================");
        Console.WriteLine($" Вход в Unity. Профиль: {_profileName}");
        Console.WriteLine("==================================================");
        Console.WriteLine(" 1) Ввести email и пароль — дальше программа будет входить сама");
        Console.WriteLine(" 2) Просто нажать Enter — войдёте руками в окне браузера");
        Console.WriteLine("    (нужно, если вход через Google, Apple или Facebook)");
        Console.WriteLine();
        Console.Write("Email (Enter — вход руками): ");

        var email = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(email))
        {
            Console.WriteLine("Хорошо, откроется окно браузера. Войдите и дождитесь подтверждения.");
            _logger.Info("Выбран ручной вход в браузере.");
            return;
        }

        Console.Write("Пароль: ");
        var password = ReadPasswordMasked();
        Console.WriteLine();

        if (string.IsNullOrWhiteSpace(password))
        {
            Console.WriteLine("Пароль пустой — откроется окно браузера для ручного входа.");
            _logger.Info("Пароль не введён, переходим к ручному входу.");
            return;
        }

        _unityEmail = email;
        _unityPassword = password;
        _logger.Info($"Автовход настроен для профиля '{_profileName}' (email: {email}).");

        if (_options.SavePassword.HasValue)
        {
            return;
        }

        if (!SecretStore.CredentialManagerAvailable)
        {
            Console.WriteLine("Диспетчер учётных данных есть только в Windows. Пароль сохранён не будет.");
            return;
        }

        Console.Write("Сохранить пароль в Диспетчере учётных данных Windows, чтобы больше не вводить? [Д/н]: ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        _savePasswordAnswer = string.IsNullOrWhiteSpace(answer) || answer is "д" or "да" or "y" or "yes";
    }

    /// <summary>Читает пароль, показывая звёздочки вместо символов.</summary>
    private static string ReadPasswordMasked()
    {
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? string.Empty;
        }

        var builder = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                return builder.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                    Console.Write("\b \b");
                }

                continue;
            }

            if (char.IsControl(key.KeyChar))
            {
                continue;
            }

            builder.Append(key.KeyChar);
            Console.Write('*');
        }
    }

    /// <summary>
    /// Сохраняет логин и пароль в Диспетчер учётных данных Windows,
    /// если это разрешено параметром --save-password или ответом пользователя.
    /// </summary>
    private void TrySavePassword()
    {
        var allowed = _options.SavePassword ?? _savePasswordAnswer;
        if (allowed != true || !HasCredentials)
        {
            return;
        }

        if (!SecretStore.CredentialManagerAvailable)
        {
            _logger.Warn("Сохранение пароля доступно только в Windows. Пароль не сохранён.");
            return;
        }

        if (SecretStore.TrySaveCredentials(_credentialTarget, _unityEmail!, _unityPassword!))
        {
            _profileStore.Touch(_profileName, _unityEmail, passwordSaved: true);
            _logger.Info(
                $"Пароль профиля '{_profileName}' сохранён в Диспетчере учётных данных Windows (запись '{_credentialTarget}').");
        }
        else
        {
            _logger.Warn("Не удалось сохранить пароль в Диспетчере учётных данных Windows.");
        }
    }

    private async Task<bool> ValidateSessionForAssetStoreAsync(IPage page, string stage)
    {
        try
        {
            _logger.Debug($"AuthProbe[{stage}]: проверка стабильности сессии на {BaseTopFreeSource}");
            await SafeGoToAsync(page, BaseTopFreeSource);
            await WaitForDocumentReadySoftAsync(page, TimeSpan.FromSeconds(6));

            if (page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase) ||
                IsLikelySignOutFlowUrl(page.Url))
            {
                _logger.Warn($"AuthProbe[{stage}]: редирект в login/logout ({page.Url}).");
                return false;
            }

            if (!await TryCheckAuthFastAsync(page, $"{stage}-probe"))
            {
                _logger.Warn($"AuthProbe[{stage}]: auth markers не подтверждены на боевой странице.");
                return false;
            }

            for (var i = 0; i < 6; i++)
            {
                await Task.Delay(400);
                if (page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase) ||
                    IsLikelySignOutFlowUrl(page.Url))
                {
                    _logger.Warn($"AuthProbe[{stage}]: во время стабилизации пойман logout/login ({page.Url}).");
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"AuthProbe[{stage}] не выполнен: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> TryCheckAuthFastAsync(IPage page, string stage)
    {
        for (var i = 1; i <= 3; i++)
        {
            if (page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug($"AuthFast[{stage}] итерация {i}: на login.unity.com, сессия не подтверждена.");
                return false;
            }

            if (page.Url.Contains("assetstore.unity.com", StringComparison.OrdinalIgnoreCase))
            {
                await WaitForDocumentReadySoftAsync(page, TimeSpan.FromSeconds(4));
                if (await HasAuthMarkersAsync(page))
                {
                    _logger.Debug($"AuthFast[{stage}] итерация {i}: auth markers подтверждены.");
                    return true;
                }
            }

            await Task.Delay(650);
        }

        return false;
    }

    private async Task<bool> AuthenticateViaAssetStoreAsync(IPage page)
    {
        if (HasCredentials)
        {
            _logger.Info("Найдены учетные данные для автовхода. Будет выполнена автоматическая отправка формы.");
        }
        else
        {
            _logger.Info(
                "Учетные данные для автовхода не заданы. Выполните вход в браузере вручную, скрипт продолжит автоматически.");
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            _logger.Info($"Попытка авторизации {attempt}/3...");
            await StartAssetStoreSsoAsync(page);

            // Форму входа по шагам заполняет само ожидание (TryAutoLoginStepAsync).
            if (await WaitForAuthenticatedSessionAsync(page, TimeSpan.FromMilliseconds(_options.AuthTimeoutMs)))
            {
                return true;
            }

            // Unity не приняла пароль или просит то, что программа не умеет. Начинать вход
            // заново — значит снова отправить тот же пароль: так и до блокировки недалеко.
            if (_autoLoginGaveUp && (_options.Headless || !_options.Interactive))
            {
                return false;
            }

            _logger.Warn("Сессия Asset Store не подтверждена в рамках текущей попытки. Повторяем...");
        }

        return false;
    }

    private async Task StartAssetStoreSsoAsync(IPage page)
    {
        // Основной путь: сразу открываем страницу входа Unity.
        // Раньше программа сначала грузила витрину Asset Store и искала кнопку Sign In по интерфейсу —
        // это медленно и ломается каждый раз, когда Unity меняет вёрстку.
        _logger.Info($"AuthStep: open-sign-in | {_signInUrl}");
        await SafeGoToAsync(page, _signInUrl);

        var stopAt = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < stopAt && !IsAuthFlowUrl(page.Url))
        {
            await Task.Delay(500);
        }

        if (IsAuthFlowUrl(page.Url))
        {
            await TrySwitchToSignInPageAsync(page);
            _logger.Info($"SSO запущен напрямую, текущий URL: {page.Url}");
            return;
        }

        // Запасной путь на случай, если прямой адрес перестал работать или нас редиректнуло в другое место.
        _logger.Warn($"Прямой переход на {_signInUrl} не привёл на страницу входа (сейчас {page.Url}). Пробуем через интерфейс Asset Store...");
        _logger.Info("AuthStep: open-home");
        await SafeGoToAsync(page, AssetStoreHomeUrl);

        var clickedSignInFromMenu = await TryTriggerSignInFromHomeUiAsync(page);
        if (!clickedSignInFromMenu)
        {
            _logger.Warn("Не удалось перейти к Sign In через меню профиля. Пробуем альтернативный путь входа...");
        }
        else
        {
            var menuStopAt = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < menuStopAt && !IsAuthFlowUrl(page.Url))
            {
                await Task.Delay(500);
            }
        }

        if (!IsAuthFlowUrl(page.Url))
        {
            var clickedSignInWithUnity = await TryClickSignInWithUnityAsync(page);
            if (clickedSignInWithUnity)
            {
                _logger.Info("AuthStep: click-sign-in-with-unity");
                var ssoStopAt = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < ssoStopAt && !IsAuthFlowUrl(page.Url))
                {
                    await Task.Delay(500);
                }
            }
        }

        _logger.Info($"SSO запущен, текущий URL: {page.Url}");
    }

    private static bool IsAuthFlowUrl(string url)
    {
        return url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("api.unity.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("auth.cloud.unity.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("accounts.google", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("cloud.unity.com/login", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Обрезает длинный адрес. Ссылки OAuth бывают по две тысячи знаков,
    /// и в логе от них не остаётся ничего читаемого.
    /// </summary>
    private static string ShortUrl(string url)
    {
        const int max = 110;
        return url.Length <= max ? url : url[..max] + $"... (всего {url.Length} знаков)";
    }

    /// <summary>Пишет сообщение ожидания только когда оно изменилось, чтобы не забивать лог.</summary>
    private void LogWaitOnce(string message)
    {
        if (_lastWaitMessage == message)
        {
            return;
        }

        _lastWaitMessage = message;
        _logger.Info(message);
    }

    /// <summary>
    /// Пытается узнать, под каким аккаунтом Unity выполнен вход.
    /// Пробует несколько источников: не все из них работают на всех страницах.
    /// Если ничего не вышло — вернёт пусто, и программа просто обойдётся без имени.
    /// </summary>
    private async Task<string?> TryReadSignedInUserAsync(IPage page)
    {
        try
        {
            var raw = await page.EvaluateFunctionAsync<string?>(@"async () => {
                const pickEmail = (obj) => {
                    if (!obj || typeof obj !== 'object') return null;
                    const keys = ['email', 'username', 'userName', 'displayName', 'name'];
                    for (const k of keys) {
                        const v = obj[k];
                        if (typeof v === 'string' && v.trim().length > 0) return v.trim();
                    }
                    for (const k of Object.keys(obj)) {
                        const found = pickEmail(obj[k]);
                        if (found) return found;
                    }
                    return null;
                };

                // 1. Служебный адрес магазина с данными о пользователе.
                for (const url of ['/api/user/info', '/api/user', '/api/account']) {
                    try {
                        const res = await fetch(url, { credentials: 'include' });
                        if (!res.ok) continue;
                        const data = await res.json();
                        const found = pickEmail(data);
                        if (found) return found;
                    } catch (e) { /* пробуем следующий */ }
                }

                // 2. Данные, которые магазин сохранил в браузере.
                try {
                    for (let i = 0; i < localStorage.length; i++) {
                        const value = localStorage.getItem(localStorage.key(i)) || '';
                        const m = value.match(/[\w.+-]+@[\w-]+\.[\w.-]+/);
                        if (m) return m[0];
                    }
                } catch (e) { /* localStorage может быть закрыт */ }

                // 3. Почта, показанная в самом интерфейсе.
                const text = document.body ? document.body.innerText : '';
                const m = text.match(/[\w.+-]+@[\w-]+\.[\w.-]+/);
                return m ? m[0] : null;
            }");

            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Запоминает аккаунт Unity, под которым выполнен вход.
    /// Переименование профиля произойдёт в конце запуска, когда браузер закрыт.
    /// </summary>
    private async Task RememberUnityAccountAsync(IPage page)
    {
        if (!string.IsNullOrWhiteSpace(_unityAccount))
        {
            return;
        }

        _unityAccount = await TryReadSignedInUserAsync(page);

        if (!string.IsNullOrWhiteSpace(_unityAccount))
        {
            _logger.Info($"Вход выполнен под аккаунтом Unity: {_unityAccount}");
            _profileStore.Touch(_profileName, _unityAccount);
        }
        else
        {
            _logger.Debug("Имя аккаунта Unity определить не удалось. Профиль останется с прежним именем.");
        }
    }

    /// <summary>
    /// Приводит имя профиля к виду «пользователь компьютера + аккаунт Unity».
    /// Вызывается в самом конце, когда браузер уже закрыт и папку можно двигать.
    /// </summary>
    private void FinalizeProfileName()
    {
        if (string.IsNullOrWhiteSpace(_unityAccount))
        {
            return;
        }

        var wanted = BuildProfileName(_options.ProfileBaseName, _unityAccount);

        if (string.Equals(wanted, _profileName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_profileStore.TryRename(_profileName, wanted, out var message))
        {
            _logger.Info(message);
            _logger.Info("Так на одном компьютере уживаются несколько аккаунтов Unity.");
        }
        else if (!string.IsNullOrWhiteSpace(message))
        {
            _logger.Info(message);
        }
    }

    /// <summary>
    /// Собирает имя профиля из имени пользователя компьютера и аккаунта Unity.
    /// Так на одном компьютере уживаются несколько аккаунтов: у каждого своя папка.
    /// </summary>
    public static string BuildProfileName(string machineUser, string? unityAccount)
    {
        var baseName = ProfileStore.Sanitize(machineUser);

        if (string.IsNullOrWhiteSpace(unityAccount))
        {
            return baseName;
        }

        // От почты берём часть до собаки: она короткая и узнаваемая.
        var account = unityAccount.Trim();
        var at = account.IndexOf('@');
        if (at > 0)
        {
            account = account[..at];
        }

        account = ProfileStore.Sanitize(account);
        return account.Length == 0 || account == "default" ? baseName : $"{baseName}__{account}";
    }


    /// <summary>
    /// Читает итоговую стоимость заказа со страницы оформления.
    ///
    /// Ищем именно строку итога, а не любое число на странице. Раньше проверка
    /// смотрела, встречается ли где-нибудь "0.00" — под это подходит и "$10.00",
    /// из-за чего платный ассет мог быть принят за бесплатный.
    /// </summary>
    private async Task<CartPriceSnapshot> ReadCartPriceAsync(IPage page)
    {
        try
        {
            var json = await page.EvaluateFunctionAsync<string>(@"() => {
                const norm = (v) => (v || '').replace(/\s+/g, ' ').trim();
                const lower = (v) => norm(v).toLowerCase();

                const visible = (el) => {
                    if (!el) return false;
                    const st = window.getComputedStyle(el);
                    const r = el.getBoundingClientRect();
                    return st.display !== 'none' && st.visibility !== 'hidden' && r.width > 0 && r.height > 0;
                };

                // Превращает '$1,234.56' или '1.234,56 $' в число.
                // Сумма с валютой до или после числа: '$20.00', 'US$ 20', '22.45€', '1 234,56 ₽', 'EUR 5'.
                // Цифры без валюты рядом суммой не считаются: '(1)' в 'Items (1)' — это количество.
                const parseAmount = (text) => {
                    const num = '(\\d{1,3}(?:[ \\u00a0\\u202f.,]\\d{3})+(?:[.,]\\d{1,2})?|\\d+(?:[.,]\\d{1,2})?)';
                    const cur = '(?:US\\$|\\$|€|£|₽|руб\\.?|EUR|USD|GBP|RUB)';
                    const re = new RegExp(cur + '\\s*' + num + '|' + num + '\\s*' + cur, 'i');
                    const m = norm(text).match(re);
                    if (!m) return null;
                    let n = (m[1] || m[2]).replace(/[   ]/g, '');
                    const dec = n.match(/[.,](\d{1,2})$/);
                    const whole = dec ? n.slice(0, -dec[0].length) : n;
                    n = whole.replace(/[.,]/g, '') + (dec ? '.' + dec[1] : '');
                    const v = parseFloat(n);
                    return isNaN(v) ? null : v;
                };

                const bodyText = document.body ? document.body.innerText : '';
                const normBody = lower(bodyText);

                const errorKeywords = [
                    'expired', 'invalid', 'is not valid', 'not valid', 'coupon limit',
                    'no longer', 'has ended', 'недействителен', 'истек', 'истёк',
                    'больше не действует', 'ошибка'
                ];
                let hasPromoError = false;
                let foundError = '';
                for (const kw of errorKeywords) {
                    if (normBody.includes(kw)) { hasPromoError = true; foundError = kw; break; }
                }

                // Сначала точные слова, 'total' последним: он есть и внутри 'subtotal'.
                const totalWords = ['to pay now', 'order total', 'grand total', 'amount due', 'amount to pay', 'to pay',
                    'к оплате', 'итого', 'total'];
                const nodes = Array.from(document.querySelectorAll('div, span, p, td, li, section, dl'))
                    .filter(visible);

                // Слово итога ищем целиком: 'total' внутри 'Subtotal' — это не итог.
                // Иначе при итоге прочерком (налог ещё не посчитан) сумма бралась из Subtotal.
                const lastWordEnd = (lt, w) => {
                    const re = new RegExp('(^|[^a-zа-яё])' + w.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'g');
                    let m, end = -1;
                    while ((m = re.exec(lt)) !== null) end = m.index + m[0].length;
                    return end;
                };

                // Сумма берётся именно после слова итога. В блоке вида
                // 'Покупки (1) $20.00 Промежуточный итог $20.00 Налог $4.40 К оплате $24.40'
                // первая сумма — цена товара, а к оплате — последняя.
                const amountAfterTotal = (t) => {
                    const lt = t.toLowerCase();
                    for (const w of totalWords) {
                        const end = lastWordEnd(lt, w);
                        if (end < 0) continue;
                        const amount = parseAmount(t.slice(end));
                        if (amount !== null) return amount;
                    }
                    return null;
                };

                // Сначала ищем узел, где рядом со словом 'итого' стоит сумма.
                let best = null;
                for (const el of nodes) {
                    const t = norm(el.innerText || '');
                    if (!t || t.length > 200) continue;
                    const lt = t.toLowerCase();
                    if (!totalWords.some(w => lastWordEnd(lt, w) >= 0)) continue;
                    const amount = amountAfterTotal(t);
                    if (amount === null) continue;
                    // Берём самый мелкий подходящий узел: он ближе всего к самой сумме.
                    if (!best || t.length < best.raw.length) best = { raw: t, amount };
                }

                // Явное 'Free' в строке итога тоже считаем нулём.
                if (!best) {
                    for (const el of nodes) {
                        const t = norm(el.innerText || '');
                        if (!t || t.length > 60) continue;
                        const lt = t.toLowerCase();
                        if (!totalWords.some(w => lastWordEnd(lt, w) >= 0)) continue;
                        if (/\bfree\b|бесплатно/.test(lt)) { best = { raw: t, amount: 0 }; break; }
                    }
                }

                return JSON.stringify({
                    found: best !== null,
                    amount: best ? best.amount : 0,
                    rawText: best ? best.raw : '',
                    hasPromoError,
                    foundError
                });
            }");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new CartPriceSnapshot
            {
                Found = root.GetProperty("found").GetBoolean(),
                Amount = root.GetProperty("amount").GetDecimal(),
                RawText = root.GetProperty("rawText").GetString() ?? string.Empty,
                HasPromoError = root.GetProperty("hasPromoError").GetBoolean(),
                FoundError = root.GetProperty("foundError").GetString() ?? string.Empty
            };
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Промокод] Не удалось прочитать стоимость: {ex.Message}");
            return new CartPriceSnapshot { Found = false };
        }
    }

    /// <summary>Сколько кругов переадресации OAuth терпим, прежде чем уйти в магазин самим.</summary>
    private const int MaxOauthSpins = 4;

    private async Task<bool> WaitForAuthenticatedSessionAsync(IPage page, TimeSpan timeout)
    {
        var stopAt = DateTime.UtcNow.Add(timeout);
        var oauthSpins = 0;

        while (DateTime.UtcNow < stopAt)
        {
            if (page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
            {
                await TrySwitchToSignInPageAsync(page);
                if (HasCredentials && !_autoLoginGaveUp &&
                    await TryAutoLoginStepAsync(page) == AutoLoginOutcome.GaveUp)
                {
                    // Без окна и без человека у консоли ждать ручного входа бессмысленно.
                    if (_options.Headless || !_options.Interactive)
                    {
                        return false;
                    }

                    _logger.Warn("Войдите в окне браузера сами — программа подождёт.");
                }

                await Task.Delay(1500);
                continue;
            }

            if (page.Url.Contains("assetstore.unity.com", StringComparison.OrdinalIgnoreCase))
            {
                oauthSpins = 0;

                if (await HasAuthMarkersAsync(page))
                {
                    _logger.Info("AuthStep: auth-confirmed");
                    return true;
                }

                if (!HasCredentials)
                {
                    // В режиме ручного входа не пытаемся агрессивно кликать по меню каждую секунду,
                    // так как это мешает пользователю. Просто ждем, пока он сам войдет.
                    LogWaitOnce("Ожидание авторизации пользователем...");
                }
            }
            else
            {
                // Не форсируем редирект, чтобы не прерывать цепочку OAuth (Google, Apple, Facebook и др.)
                // Возвращаем в Asset Store только если цепочка завершилась в консоли Unity
                if (page.Url.Contains("cloud.unity.com/home", StringComparison.OrdinalIgnoreCase) ||
                    page.Url.Contains("cloud.unity.com/account", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Info($"Вход выполнен, но открыта страница консоли ({page.Url}). Возвращаемся в Asset Store...");
                    await SafeGoToAsync(page, "https://assetstore.unity.com/");
                    oauthSpins = 0;
                }
                else if (page.Url.Contains("api.unity.com/v1/oauth2/authorize", StringComparison.OrdinalIgnoreCase))
                {
                    // Страховка. Если Unity крутит выдачу кода по кругу, вход на самом деле
                    // уже прошёл — просто некуда вернуться. Уходим в магазин сами и проверяем сессию.
                    oauthSpins++;
                    _logger.Debug($"AuthWait: страница выдачи прав OAuth, оборот {oauthSpins}/{MaxOauthSpins}.");

                    if (oauthSpins >= MaxOauthSpins)
                    {
                        _logger.Warn(
                            "Unity зациклил переадресацию после входа. Переходим в Asset Store сами и проверяем сессию.");
                        await SafeGoToAsync(page, "https://assetstore.unity.com/");
                        oauthSpins = 0;
                    }
                }
                else if (page.Url.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase))
                {
                    // Google намеренно не пускает вход в браузере, которым управляет программа.
                    // Это его защита, и обходить её не нужно — есть рабочий путь через пароль Unity.
                    if (!_googleWarningShown)
                    {
                        _googleWarningShown = true;
                        _logger.Warn("============================================================");
                        _logger.Warn(" ОТКРЫТ ВХОД ЧЕРЕЗ GOOGLE");
                        _logger.Warn(" Google не разрешает входить в браузере под управлением программы.");
                        _logger.Warn(" Это его защита от автоматизации, она не обходится.");
                        _logger.Warn("");
                        _logger.Warn(" ЧТО ДЕЛАТЬ: задайте своему Unity ID обычный пароль.");
                        _logger.Warn(" 1. Откройте https://id.unity.com в обычном браузере");
                        _logger.Warn(" 2. Войдите через Google, зайдите в настройки безопасности");
                        _logger.Warn(" 3. Задайте пароль (или воспользуйтесь 'Забыли пароль?')");
                        _logger.Warn(" 4. Вернитесь сюда и входите по email и паролю — это работает");
                        _logger.Warn("");
                        _logger.Warn(" Либо нажмите в окне браузера 'Назад' и войдите по паролю Unity.");
                        _logger.Warn("============================================================");
                    }
                }
                else
                {
                    LogWaitOnce($"AuthWait: ждём завершения входа на стороннем сайте: {ShortUrl(page.Url)}");
                }
            }
            await Task.Delay(1500);
        }

        return false;
    }

    private async Task TrySwitchToSignInPageAsync(IPage page)
    {
        if (!page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (page.Url.Contains("/sign-up", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Debug("Обнаружена страница sign-up, переключаемся на sign-in...");
            await SafeGoToAsync(page, _signInUrl);
        }
    }

    private enum AutoLoginOutcome
    {
        Working,
        GaveUp
    }

    // Состояние автовхода за этот запуск: какой шаг и когда отправили, сколько раз.
    private string? _loginLastStep;
    private DateTime _loginLastSubmitUtc = DateTime.MinValue;
    private HashSet<string> _loginErrorsBeforeSubmit = [];
    private int _loginEmailSubmits;
    private int _loginPasswordSubmits;
    private int _loginCodeSubmits;
    private bool _autoLoginGaveUp;
    private string? _loginProblem;
    private DateTime? _loginPageSeenUtc;
    private readonly HashSet<string> _loginNotes = [];

    /// <summary>
    /// Один шаг автовхода на странице login.unity.com. Вызывается в цикле ожидания входа.
    ///
    /// Каждый шаг (email → пароль → код подтверждения) отправляется один раз, дальше
    /// программа ждёт и читает, что ответила страница. Раньше форма заполнялась заново
    /// на каждом проходе цикла: при неверном пароле программа долбила вход минутами.
    /// Теперь неверный пароль, капча или «слишком много попыток» — это остановка
    /// с понятным объяснением (и сообщение боту на сервере).
    /// </summary>
    private async Task<AutoLoginOutcome> TryAutoLoginStepAsync(IPage page)
    {
        if (_autoLoginGaveUp)
        {
            return AutoLoginOutcome.GaveUp;
        }

        if (!HasCredentials || HostOf(page.Url) != "login.unity.com")
        {
            return AutoLoginOutcome.Working;
        }

        LoginPageState state;
        try
        {
            state = JsonSerializer.Deserialize<LoginPageState>(
                await page.EvaluateFunctionAsync<string>(ReadLoginPageJs), _runtimeJsonOptions) ?? new LoginPageState();
        }
        catch (Exception ex) when (IsTransientPageError(ex))
        {
            return AutoLoginOutcome.Working;
        }

        // Страница входа открылась, но ещё не «ожила»: клик по Continue в первые мгновения
        // теряется (видно по логу 18.09: email пришлось отправлять второй раз через 15 с).
        if (!state.Ready)
        {
            return AutoLoginOutcome.Working;
        }

        _loginPageSeenUtc ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _loginPageSeenUtc < TimeSpan.FromSeconds(1.5))
        {
            return AutoLoginOutcome.Working;
        }

        var sinceSubmit = DateTime.UtcNow - _loginLastSubmitUtc;

        // Что страница ответила на наш последний шаг. Ошибки, которые висели ещё до него, не в счёт.
        if (_loginLastStep is not null && sinceSubmit < TimeSpan.FromSeconds(40))
        {
            var newError = state.Errors.FirstOrDefault(e => !_loginErrorsBeforeSubmit.Contains(e));
            if (newError is not null)
            {
                return await GiveUpAutoLoginAsync(page,
                    $"Unity не пустила: «{newError}». Проверьте email и пароль (войдите ими на id.unity.com в обычном браузере).");
            }
        }

        if (state.Captcha)
        {
            return await GiveUpAutoLoginAsync(page,
                "Unity показывает проверку «я не робот» (капчу). Её программа не проходит. " +
                "Войдите один раз вручную (на ПК — в окне браузера) и перенесите папку профиля.");
        }

        switch (state.Step)
        {
            case "email":
                if (_loginLastStep == "email" && sinceSubmit < TimeSpan.FromSeconds(8))
                {
                    break;
                }

                if (_loginEmailSubmits >= 3)
                {
                    return await GiveUpAutoLoginAsync(page,
                        "Email отправлен трижды, но Unity так и не спросила пароль.");
                }

                if (await TryFillFieldAsync(page, FieldKind.Email, _unityEmail!))
                {
                    _loginEmailSubmits++;
                    RememberLoginSubmit("email", state);
                    var clicked = await TryClickPrimaryButtonAsync(page);
                    _logger.Info($"Автовход, шаг 1: email введён{(clicked ? ", нажато «Continue»" : ", но кнопку продолжения не нашли")}.");
                    await SaveErrorScreenshotAsync(page, "autologin-1-email");
                }

                break;

            case "password":
                if (_loginLastStep == "password" && sinceSubmit < TimeSpan.FromSeconds(25))
                {
                    break;
                }

                if (_loginPasswordSubmits >= 2)
                {
                    return await GiveUpAutoLoginAsync(page,
                        "Пароль отправлен дважды, но Unity снова просит пароль. Проверьте его.");
                }

                if (await TryFillFieldAsync(page, FieldKind.Password, _unityPassword!))
                {
                    _loginPasswordSubmits++;
                    RememberLoginSubmit("password", state);
                    var clicked = await TryClickPrimaryButtonAsync(page);
                    _logger.Info($"Автовход, шаг 2: пароль введён{(clicked ? ", нажато «Sign in»" : ", но кнопку входа не нашли")}. Ждём ответа Unity...");
                    await SaveErrorScreenshotAsync(page, "autologin-2-password");
                }

                break;

            case "code":
                if (_loginLastStep == "code" && sinceSubmit < TimeSpan.FromSeconds(25))
                {
                    break;
                }

                if (_loginCodeSubmits >= 2)
                {
                    return await GiveUpAutoLoginAsync(page, "Код подтверждения введён дважды, но Unity его не приняла.");
                }

                _logger.Warn("Автовход: Unity просит код подтверждения (пришёл на почту или в приложение).");
                await SaveErrorScreenshotAsync(page, "autologin-3-code");
                var code = await AskLoginCodeAsync();
                if (code is null)
                {
                    return await GiveUpAutoLoginAsync(page,
                        "Unity просит код подтверждения, а ввести его некому. " +
                        "Настройте бота (он спросит код) или войдите один раз вручную.");
                }

                if (await TypeLoginCodeAsync(page, code))
                {
                    _loginCodeSubmits++;
                    RememberLoginSubmit("code", state);
                    await TryClickPrimaryButtonAsync(page);
                    _logger.Info("Автовход, шаг 3: код подтверждения введён.");
                }

                break;

            default:
                if (_loginNotes.Add("unknown-step"))
                {
                    _logger.Info($"Автовход: на странице входа нет ни поля email, ни пароля, ни кода. Ждём. Текст: {Shorten(state.Text, 200)}");
                    await SaveErrorScreenshotAsync(page, "autologin-unknown-step");
                }

                break;
        }

        return AutoLoginOutcome.Working;
    }

    private void RememberLoginSubmit(string step, LoginPageState state)
    {
        _loginLastStep = step;
        _loginLastSubmitUtc = DateTime.UtcNow;
        _loginErrorsBeforeSubmit = state.Errors.ToHashSet();
    }

    private async Task<AutoLoginOutcome> GiveUpAutoLoginAsync(IPage page, string reason)
    {
        _autoLoginGaveUp = true;
        _loginProblem = reason;
        _logger.Warn("============================================================");
        _logger.Warn(" АВТОВХОД ОСТАНОВЛЕН");
        _logger.Warn($" {reason}");
        _logger.Warn(" Пароль повторно не отправляем, чтобы Unity не заблокировала вход.");
        _logger.Warn("============================================================");
        await SaveErrorScreenshotAsync(page, "autologin-stopped");
        await SaveHtmlDumpAsync(page, "autologin-stopped");
        return AutoLoginOutcome.GaveUp;
    }

    /// <summary>
    /// Код подтверждения входа: в консоли, если за ней сидит человек, иначе — через бота.
    /// </summary>
    private async Task<string?> AskLoginCodeAsync()
    {
        if (_options.Interactive && !Console.IsInputRedirected)
        {
            Console.WriteLine();
            Console.Write("Unity прислала код подтверждения входа (на почту или в приложение). Введите код: ");
            var typed = Console.ReadLine()?.Trim();
            return string.IsNullOrWhiteSpace(typed) ? null : typed;
        }

        if (_notifier is { Enabled: true })
        {
            await _notifier.SendAsync(
                $"🔐 Unity просит код подтверждения входа (профиль {_profileName}).\n" +
                "Пришлите код ответом на это сообщение в течение 10 минут.");
            _logger.Info("Автовход: спросили код у бота, ждём ответа до 10 минут...");
            var code = await _notifier.WaitForReplyAsync(new Regex(@"\b\d{4,8}\b"), TimeSpan.FromMinutes(10));
            _logger.Info(code is null ? "Автовход: код от бота не пришёл." : "Автовход: код от бота получен.");
            return code;
        }

        return null;
    }

    /// <summary>Вводит код с клавиатуры: так его понимают и одно поле, и шесть полей по цифре.</summary>
    private static async Task<bool> TypeLoginCodeAsync(IPage page, string code)
    {
        var marked = await page.EvaluateFunctionAsync<bool>(@"() => {
            const visible = (el) => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
            const input = Array.from(document.querySelectorAll(
                'input[autocomplete=""one-time-code""], input[name*=""code"" i], input[id*=""code"" i], input[name*=""otp"" i], input[id*=""otp"" i], input[inputmode=""numeric""]'))
                .find(visible);
            if (!input) return false;
            input.setAttribute('data-uad-code', '1');
            return true;
        }");

        if (!marked)
        {
            return false;
        }

        var handle = await page.QuerySelectorAsync("[data-uad-code='1']");
        if (handle is null)
        {
            return false;
        }

        await handle.ClickAsync();
        await page.Keyboard.TypeAsync(code, new PuppeteerSharp.Input.TypeOptions { Delay = 80 });
        return true;
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// Что сейчас на странице входа: какой шаг (email / password / code), есть ли капча,
    /// какие сообщения об ошибке видны.
    /// </summary>
    private const string ReadLoginPageJs = @"() => {
        const norm = (v) => (v || '').replace(/\s+/g, ' ').trim();
        const visible = (el) => {
            if (!el) return false;
            const st = window.getComputedStyle(el);
            const r = el.getBoundingClientRect();
            return st.display !== 'none' && st.visibility !== 'hidden' && r.width > 0 && r.height > 0;
        };
        const first = (sel) => Array.from(document.querySelectorAll(sel)).find(visible) || null;

        const password = first('input#password, input[name=""password""], input[type=""password""]');
        const email = first('input#email, input[name=""email""], input[type=""email""]');
        const codeInput = first('input[autocomplete=""one-time-code""], input[name*=""code"" i], input[id*=""code"" i], input[name*=""otp"" i], input[id*=""otp"" i], input[inputmode=""numeric""]');
        const text = norm(document.body ? document.body.innerText : '');
        const lower = text.toLowerCase();
        const mentionsCode = /code|verif|two-factor|2fa|код|подтвер/.test(lower);

        const captcha = Array.from(document.querySelectorAll('iframe'))
            .some(f => /captcha|arkoselabs|funcaptcha|hcaptcha|turnstile|challenges\.cloudflare/i.test(f.src || '')) ||
            !!first('[class*=""captcha"" i], #captcha, [id*=""captcha"" i]');

        const errors = Array.from(document.querySelectorAll(
            '[role=""alert""], [aria-live=""assertive""], [aria-live=""polite""], .error, [class*=""error"" i], [class*=""invalid"" i], [data-testid*=""error"" i]'))
            .filter(visible)
            .filter(el => !el.closest('#onetrust-consent-sdk, #onetrust-banner-sdk'))
            .map(el => norm(el.innerText))
            .filter(t => t.length >= 3 && t.length <= 300);

        const step = password ? 'password' : (codeInput && mentionsCode && !email) ? 'code' : email ? 'email' : 'unknown';
        return JSON.stringify({ Step: step, Ready: document.readyState === 'complete', Captcha: captcha, Errors: Array.from(new Set(errors)), Text: text.slice(0, 1500) });
    }";

    private enum FieldKind
    {
        Email,
        Password
    }

    private static string SelectorFor(FieldKind kind) => kind == FieldKind.Email
        ? "input#email, input[name=\"email\"], input[type=\"email\"], input[id*=\"email\" i]"
        : "input#password, input[name=\"password\"], input[type=\"password\"], input[id*=\"password\" i]";

    private static async Task<bool> WaitForFieldAsync(IPage page, FieldKind kind, TimeSpan timeout)
    {
        var selector = SelectorFor(kind);
        var stopAt = DateTime.UtcNow.Add(timeout);

        while (DateTime.UtcNow < stopAt)
        {
            var visible = await page.EvaluateFunctionAsync<bool>(@"(selector) => {
                const el = document.querySelector(selector);
                return !!el && el.offsetParent !== null && !el.disabled;
            }", selector);

            if (visible)
            {
                return true;
            }

            await Task.Delay(500);
        }

        return false;
    }

    /// <summary>
    /// Заполняет поле так, чтобы изменение заметил React.
    /// Обычное присваивание value интерфейс Unity игнорирует и стирает.
    /// </summary>
    private static async Task<bool> TryFillFieldAsync(IPage page, FieldKind kind, string value)
    {
        return await page.EvaluateFunctionAsync<bool>(@"(selector, value) => {
            const el = document.querySelector(selector);
            if (!el || el.offsetParent === null || el.disabled) {
                return false;
            }

            const nativeSetter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
            el.focus();
            nativeSetter.call(el, value);
            el.dispatchEvent(new Event('input', { bubbles: true }));
            el.dispatchEvent(new Event('change', { bubbles: true }));
            return el.value === value;
        }", SelectorFor(kind), value);
    }

    /// <summary>Нажимает главную кнопку формы: Continue, Next или Sign in.</summary>
    private static async Task<bool> TryClickPrimaryButtonAsync(IPage page)
    {
        return await page.EvaluateFunctionAsync<bool>(@"() => {
            const isUsable = (el) => el && !el.disabled && el.offsetParent !== null;

            let btn = document.querySelector('button[type=""submit""], input[type=""submit""]');
            if (!isUsable(btn)) {
                const words = /continue|next|sign in|log in|submit|войти|продолжить|далее/i;
                btn = Array.from(document.querySelectorAll('button'))
                    .filter(isUsable)
                    .find(b => words.test((b.innerText || b.textContent || '').trim()));
            }

            if (!isUsable(btn)) {
                return false;
            }

            btn.click();
            return true;
        }");
    }

    private async Task<bool> IsAuthenticatedAsync(IPage page)
    {
        await SafeGoToAsync(page, AssetStoreHomeUrl);

        if (page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return await HasAuthMarkersAsync(page);
    }

    private async Task<bool> HasAuthMarkersAsync(IPage page)
    {
        if (page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var rawMarkers = await EvaluateWithRetryAsync(() => page.EvaluateFunctionAsync<string>(@"() => {
            const text = document.body?.innerText?.toLowerCase() || '';
            const hasMyAssetsLink = !!document.querySelector('a[href*=""/my-assets""], a[href*=""my-assets""]');
            const hasSignInLink = !!document.querySelector('a[href*=""login.unity.com""], a[href*=""/sign-in""]');
            
            // Avoid false positives from 'add to my assets' inside product cards
            const elements = Array.from(document.querySelectorAll('a, button, span, div, p'));
            const hasMyAssetsText = elements.some(el => {
                const t = (el.innerText || '').trim().toLowerCase();
                return t === 'my assets';
            });
            const hasSignInText = elements.some(el => {
                const t = (el.innerText || '').trim().toLowerCase();
                return t === 'sign in' || t === 'log in';
            });
            
            const hasSignInWithUnityText = text.includes('sign in with unity');
            const hasSignInWithUnityButton = elements.some(el => (el.innerText || '').trim().toLowerCase() === 'sign in with unity');

            return JSON.stringify({
                hasMyAssetsLink,
                hasSignInLink,
                hasMyAssetsText,
                hasSignInText,
                hasSignInWithUnityText,
                hasSignInWithUnityButton
            });
        }"), "HasAuthMarkers(rawMarkers)");

            var markers = JsonSerializer.Deserialize<AuthUiMarkers>(rawMarkers ?? "{}", _runtimeJsonOptions) ??
                          new AuthUiMarkers();
            var hasUiAuthMarkers = (markers.HasMyAssetsLink || markers.HasMyAssetsText) &&
                                   !(markers.HasSignInLink && markers.HasSignInText && !markers.HasMyAssetsText);
            var hasUiSignInMarkers = markers.HasSignInLink || markers.HasSignInText || markers.HasSignInWithUnityText ||
                                     markers.HasSignInWithUnityButton;
            var profileState = await GetProfileMenuAuthStateAsync(page);

            var hasApiAuthMarkers = false;
            if (page.Url.Contains("assetstore.unity.com", StringComparison.OrdinalIgnoreCase))
            {
                hasApiAuthMarkers = await EvaluateWithRetryAsync(() => page.EvaluateFunctionAsync<bool>(@"async () => {
                try {
                    // Используем другой API endpoint, который 100% отдает 401 для гостей
                    // или требуем чтобы в ответе были разумные поля (например id или orgs)
                    const res = await fetch('/api/users/organizations', { credentials: 'include' });
                    if (!res.ok) return false;
                    const text = (await res.text() || '').trim();
                    if (!text) return false;

                    const lower = text.toLowerCase();
                    if (lower.includes('unauthorized') || lower.includes('forbidden') || lower.includes('sign in')) {
                        return false;
                    }

                    // Если Unity отдает [], это может быть гость. Нужно чтобы что-то было, либо пробовать другой эндпоинт.
                    // Изменим проверку: если вернулся пустой массив или пустой объект без id, возможно это ложноположительный ответ
                     if (text === '[]' || text === '{}') return false;

                    return text.startsWith('{') || text.startsWith('[');
                } catch {
                    return false;
                }
            }"), "HasAuthMarkers(api)");
            }

            _logger.Debug(
                $"Auth markers: UI={hasUiAuthMarkers}, API={hasApiAuthMarkers}, signInUi={hasUiSignInMarkers}, profileMenuFound={profileState.ProfileMenuFound}, profileMenuSignIn={profileState.HasSignInItem}, profileMenuSignedIn={profileState.HasSignedInItem}, page={page.Url}, myAssetsLink={markers.HasMyAssetsLink}, myAssetsText={markers.HasMyAssetsText}, signInLink={markers.HasSignInLink}, signInText={markers.HasSignInText}, signInWithUnityText={markers.HasSignInWithUnityText}, signInWithUnityButton={markers.HasSignInWithUnityButton}");

            if (profileState.HasSignedInItem)
            {
                return true;
            }

            if (profileState.ProfileMenuFound && profileState.HasSignInItem && !profileState.HasSignedInItem)
            {
                return false;
            }

            if (hasUiSignInMarkers)
            {
                return false;
            }

            return hasApiAuthMarkers || hasUiAuthMarkers;
        }
        catch (Exception ex)
        {
            _logger.Debug($"HasAuthMarkers: ошибка проверки авторизации ({ex.Message}). Считаем сессию невалидной.");
            return false;
        }
    }

    private static async Task<bool> TryOpenUserProfileMenuAsync(IPage page)
    {
        return await page.EvaluateFunctionAsync<bool>(@"() => {
            const button = document.querySelector('[aria-label*=""profile"" i], [aria-label*=""user"" i], [aria-label*=""account"" i], button[class*=""user"" i], button[class*=""profile"" i]');
            if (!button) return false;

            button.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
            return true;
        }");
    }

    private async Task<bool> WaitForProfileMenuReadyAsync(IPage page, TimeSpan timeout)
    {
        var stopAt = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < stopAt)
        {
            var menuReady = await EvaluateWithRetryAsync(() => page.EvaluateFunctionAsync<bool>(@"() => {
                const visible = (el) => {
                    if (!el) return false;
                    const style = window.getComputedStyle(el);
                    const rect = el.getBoundingClientRect();
                    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
                };

                const menuLike = Array.from(document.querySelectorAll('[role=""menu""], [role=""dialog""], [class*=""menu"" i], [class*=""popover"" i], [class*=""dropdown"" i]'));
                const hasSignInInMenu = menuLike.some(container => {
                    if (!visible(container)) return false;
                    const text = (container.innerText || '').toLowerCase();
                    return text.includes('sign in') || text.includes('log in') || text.includes('my assets') || text.includes('sign out');
                });

                if (hasSignInInMenu) return true;

                const fallbackTexts = Array.from(document.querySelectorAll('a, button')).map(x => (x.innerText || '').trim().toLowerCase());
                return fallbackTexts.some(t => t === 'sign in' || t.includes('sign in') || t.includes('log in'));
            }"), "WaitForProfileMenuReady");

            if (menuReady)
            {
                return true;
            }

            await Task.Delay(250);
        }

        return false;
    }

    private static async Task<bool> TryClickSignInFromProfileMenuAsync(IPage page)
    {
        return await page.EvaluateFunctionAsync<bool>(@"() => {
            const visible = (el) => {
                if (!el) return false;
                const style = window.getComputedStyle(el);
                const rect = el.getBoundingClientRect();
                return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
            };

            const menuLike = Array.from(document.querySelectorAll('[role=""menu""], [role=""dialog""], [class*=""menu"" i], [class*=""popover"" i], [class*=""dropdown"" i]'))
                .filter(visible);

            const clickFrom = (root) => {
                const clickableItems = Array.from(root.querySelectorAll('a, button, [role=""menuitem""]'));
                for (const el of clickableItems) {
                    const text = (el.innerText || '').trim().toLowerCase();
                    if (!text) continue;
                    if (!(text === 'sign in' || text.includes('sign in') || text.includes('log in'))) continue;

                    el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                    return true;
                }

                return false;
            };

            for (const container of menuLike) {
                if (clickFrom(container)) return true;
            }

            // fallback: если контейнер не найден, пробуем только по интерактивным элементам страницы
            const fallback = Array.from(document.querySelectorAll('a, button'));
            for (const el of fallback) {
                if (!visible(el)) continue;
                const text = (el.innerText || '').trim().toLowerCase();
                if (!(text === 'sign in' || text.includes('sign in') || text.includes('log in'))) continue;
                el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                return true;
            }

            return false;
        }");
    }

    private async Task<bool> TryTriggerSignInFromHomeUiAsync(IPage page)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var openedProfileMenu = await TryOpenUserProfileMenuAsync(page);
            if (!openedProfileMenu)
            {
                await Task.Delay(300);
                continue;
            }

            _logger.Info("AuthStep: open-profile-menu");
            var menuReady = await WaitForProfileMenuReadyAsync(page, TimeSpan.FromSeconds(3));
            if (!menuReady)
            {
                _logger.Warn("Меню профиля открыто, но пункты не успели загрузиться. Повторяем...");
                await Task.Delay(350);
                continue;
            }

            var clickedSignIn = await TryClickSignInFromProfileMenuAsync(page);
            if (clickedSignIn)
            {
                _logger.Info("AuthStep: click-sign-in");
                return true;
            }

            await Task.Delay(300);
        }

        return false;
    }

    private async Task<ProfileMenuAuthState> GetProfileMenuAuthStateAsync(IPage page)
    {
        try
        {
            var raw = await EvaluateWithRetryAsync(() => page.EvaluateFunctionAsync<string>(@"async () => {
                const wait = (ms) => new Promise(r => setTimeout(r, ms));
                const visible = (el) => {
                    if (!el) return false;
                    const style = window.getComputedStyle(el);
                    const rect = el.getBoundingClientRect();
                    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
                };

                const profileButton = document.querySelector('[aria-label=""Open user profile menu""], button[aria-label*=""profile"" i], button[aria-label*=""user"" i]');
                if (!profileButton) {
                    return JSON.stringify({ profileMenuFound: false, hasSignInItem: false, hasSignedInItem: false });
                }

                profileButton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                await wait(350);

                const menuLike = Array.from(document.querySelectorAll('[role=""menu""], [role=""dialog""], [class*=""menu"" i], [class*=""popover"" i], [class*=""dropdown"" i], [class*=""account"" i]'))
                    .filter(visible)
                    .filter(x => {
                        const text = (x.innerText || '').toLowerCase();
                        return text.includes('sign in') || text.includes('log in') || text.includes('my assets') || text.includes('sign out') || text.includes('log out') || text.includes('account settings');
                    });

                if (menuLike.length === 0) {
                    return JSON.stringify({ profileMenuFound: false, hasSignInItem: false, hasSignedInItem: false });
                }

                const roots = menuLike;

                const texts = roots
                    .flatMap(root => Array.from(root.querySelectorAll('a, button, [role=""menuitem""], [role=""button""]')))
                    .filter(visible)
                    .map(x => (x.innerText || '').trim().toLowerCase())
                    .filter(Boolean)
                    .filter(x => x.length <= 120);

                const hasSignInItem = texts.some(t => t === 'sign in' || t.includes('sign in') || t.includes('log in'));
                const hasSignedInItem = texts.some(t =>
                    t.includes('my assets') ||
                    t.includes('sign out') ||
                    t.includes('log out') ||
                    t.includes('account settings') ||
                    t.includes('organization'));

                return JSON.stringify({ profileMenuFound: true, hasSignInItem, hasSignedInItem });
            }"), "GetProfileMenuAuthState");

            return JsonSerializer.Deserialize<ProfileMenuAuthState>(raw ?? "{}", _runtimeJsonOptions) ??
                   new ProfileMenuAuthState();
        }
        catch
        {
            return new ProfileMenuAuthState();
        }
    }

    private async Task<T> EvaluateWithRetryAsync<T>(Func<Task<T>> action, string operationName, int attempts = 3,
        int delayMs = 250)
    {
        Exception? last = null;
        for (var i = 1; i <= attempts; i++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (IsTransientEvaluateError(ex) && i < attempts)
            {
                last = ex;
                _logger.Debug($"{operationName}: transient evaluate error, retry {i}/{attempts} => {ex.Message}");

                var backoff = delayMs + (int)Math.Pow(i, 2) * 180;
                await Task.Delay(backoff);
            }
            catch (Exception ex)
            {
                last = ex;
                break;
            }
        }

        throw new InvalidOperationException($"{operationName}: evaluate failed after retries.", last);
    }

    private static bool IsTransientEvaluateError(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("Cannot find context with specified id", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("Target closed", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("Cannot find object with id", StringComparison.OrdinalIgnoreCase);
    }

    private async Task WaitForDocumentReadySoftAsync(IPage page, TimeSpan timeout)
    {
        var stopAt = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < stopAt)
        {
            if (page.IsClosed)
            {
                return;
            }

            try
            {
                var ready = await EvaluateWithRetryAsync(
                    () => page.EvaluateFunctionAsync<bool>(
                        "() => ['interactive','complete'].includes(document.readyState)"),
                    "WaitForDocumentReadySoft",
                    attempts: 2,
                    delayMs: 180);

                if (ready)
                {
                    return;
                }
            }
            catch
            {
                // мягкое ожидание, игнорируем единичные ошибки
            }

            await Task.Delay(120);
        }
    }

    private void AttachPageDiagnostics(IPage page)
    {
        page.FrameNavigated += (_, e) => _logger.Debug($"FrameNavigated => {ShortUrl(e.Frame.Url)}");

        page.Request += (_, e) =>
        {
            if (!_options.TraceNetwork)
            {
                return;
            }

            var resourceType = e.Request.ResourceType.ToString().ToLowerInvariant();
            if (resourceType is "document" or "xhr" or "fetch")
            {
                _logger.Debug($"REQUEST [{resourceType}] {e.Request.Method} {e.Request.Url}");
            }
        };

        page.Response += (_, e) =>
        {
            if (!_options.TraceNetwork)
            {
                return;
            }

            var resourceType = e.Response.Request?.ResourceType.ToString().ToLowerInvariant() ?? string.Empty;
            if (resourceType is "document" or "xhr" or "fetch")
            {
                _logger.Debug($"RESPONSE [{resourceType}] {(int)e.Response.Status} {e.Response.Url}");
            }
        };

        page.RequestFailed += (_, e) =>
        {
            if (!_options.TraceNetwork)
            {
                return;
            }

            _logger.Warn($"REQUEST FAILED {e.Request?.Url}");
        };

        page.Console += (_, e) =>
        {
            if (_options.TraceNetwork)
            {
                if (IsKnownNoiseConsoleMessage(e.Message))
                {
                    return;
                }

                _logger.Debug($"BROWSER CONSOLE [{e.Message.Type}] {ShortUrl(e.Message.Text)}");
            }
        };

        // Ошибки скриптов самого магазина: к работе программы отношения не имеют, а каждая —
        // это сотни строк стека. В обычном логе они мешали найти настоящие проблемы.
        page.PageError += (_, e) => _logger.Debug($"PAGE ERROR: {e.Message}");
    }

    private static bool IsKnownNoiseConsoleMessage(ConsoleMessage message)
    {
        var text = message.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("As of Atomic version 3.0.0", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Because analytics are disabled", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Refused to connect to 'https://s.clarity.ms/collect'",
                   StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Amplitude snippet has been loaded", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Amplitude Logger [Error]: Failed to fetch", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Amplitude Logger [Warn]", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Load failed, error in settings", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("No visitor ID available. Load may have failed", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("/api/carts 404", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("the server responded with a status of 451", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Action dispatch error analytics/interface/load/rejected",
                   StringComparison.OrdinalIgnoreCase);
    }

    private async Task SafeGoToAsync(IPage page, string url)
    {
        var attempts = new[] { WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load };
        Exception? lastException = null;

        foreach (var waitUntil in attempts)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                _logger.Debug($"GoTo start: {url} | waitUntil={waitUntil} | timeout={_options.NavigationTimeoutMs}ms");
                await page.GoToAsync(url, new NavigationOptions
                {
                    WaitUntil = [waitUntil],
                    Timeout = _options.NavigationTimeoutMs
                });
                sw.Stop();
                _logger.Debug($"GoTo ok: requested={url}, current={page.Url}, elapsed={sw.ElapsedMilliseconds}ms");
                return;
            }
            catch (Exception ex)
            {
                sw.Stop();
                lastException = ex;
                _logger.Warn($"Навигация не удалась ({waitUntil}) за {sw.ElapsedMilliseconds}ms: {ex.Message}");
            }
        }

        throw new NavigationException($"Не удалось открыть {url} после повторных попыток.", lastException);
    }

    private async Task<bool> TryLoadCookiesAsync(IPage page)
    {
        var raw = SecretStore.ReadProtectedText(_cookiesPath);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            var cookies = JsonSerializer.Deserialize<List<SerializableCookie>>(raw) ?? [];
            if (cookies.Count == 0)
            {
                _logger.Warn("Файл cookies найден, но пустой.");
                return false;
            }

            await page.SetCookieAsync(cookies.Select(c => c.ToCookieParam()).ToArray());
            _logger.Info($"Загружено cookies: {cookies.Count}");
            _logger.Debug(
                $"Домены cookies: {string.Join(", ", cookies.Select(c => c.Domain).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))}");
            return true;
        }
        catch
        {
            _logger.Warn("Не удалось загрузить cookies из файла. Будет выполнен ручной вход.");
            return false;
        }
    }

    private async Task<bool> TryLoadSessionStateAsync(IPage page)
    {
        var storedSession = SecretStore.ReadProtectedText(_sessionStatePath);
        if (!string.IsNullOrWhiteSpace(storedSession))
        {
            try
            {
                var state = JsonSerializer.Deserialize<SessionStateSnapshot>(storedSession, _runtimeJsonOptions) ??
                            new SessionStateSnapshot();
                if (state.Cookies.Count > 0)
                {
                    await page.SetCookieAsync(state.Cookies.Select(c => c.ToCookieParam()).ToArray());
                    _logger.Info($"Загружено cookies из session state: {state.Cookies.Count}");
                }

                if (state.LocalStorageByOrigin.Count > 0)
                {
                    foreach (var origin in LocalStorageOrigins)
                    {
                        if (!state.LocalStorageByOrigin.TryGetValue(origin, out var storage) || storage.Count == 0)
                        {
                            continue;
                        }

                        await RestoreLocalStorageInIsolatedPageAsync(page, origin, storage);
                    }

                    await SafeGoToAsync(page, AssetStoreHomeUrl);
                }

                _logger.Info("Session state успешно восстановлен.");
                return state.Cookies.Count > 0 || state.LocalStorageByOrigin.Count > 0;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Не удалось восстановить session state: {ex.Message}");
            }
        }

        return await TryLoadCookiesAsync(page);
    }

    private async Task SaveSessionStateAsync(IPage page)
    {
        var state = new SessionStateSnapshot
        {
            SavedAtUtc = DateTime.UtcNow
        };

        var cookies = await page.GetCookiesAsync(SessionOrigins);
        state.Cookies = cookies.Select(SerializableCookie.FromCookie).ToList();

        foreach (var origin in LocalStorageOrigins)
        {
            try
            {
                var localStorage = await CaptureLocalStorageInIsolatedPageAsync(page, origin);
                if (localStorage.Count > 0)
                {
                    state.LocalStorageByOrigin[origin] = localStorage;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Не удалось сохранить localStorage для {origin}: {ex.Message}");
            }
        }

        SecretStore.WriteProtectedText(_sessionStatePath, JsonSerializer.Serialize(state, _jsonOptions));
        _profileStore.Touch(_profileName, _unityEmail);

        var protection = SecretStore.EncryptionAvailable
            ? "зашифровано средствами Windows"
            : "файл доступен только текущему пользователю (шифрование ОС недоступно)";
        _logger.Info(
            $"Сохранено состояние сессии профиля '{_profileName}': cookies={state.Cookies.Count}, originsLocalStorage={state.LocalStorageByOrigin.Count} | {protection}");
    }

    private async Task<Dictionary<string, string>> CaptureLocalStorageForOriginAsync(IPage page, string origin)
    {
        var raw = await EvaluateWithRetryAsync(() => page.EvaluateFunctionAsync<string>(@"(expectedOrigin) => {
            const actual = window.location.origin;
            if (actual !== expectedOrigin) {
                return JSON.stringify({});
            }

            const result = {};
            for (let i = 0; i < localStorage.length; i++) {
                const key = localStorage.key(i);
                if (!key) continue;
                result[key] = localStorage.getItem(key) ?? '';
            }
            return JSON.stringify(result);
        }", origin), $"CaptureLocalStorage[{origin}]");

        return JsonSerializer.Deserialize<Dictionary<string, string>>(raw ?? "{}", _runtimeJsonOptions)
               ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, string>> CaptureLocalStorageInIsolatedPageAsync(IPage anchorPage,
        string origin)
    {
        await using var tempPage = await anchorPage.Browser.NewPageAsync();
        tempPage.DefaultNavigationTimeout = _options.NavigationTimeoutMs;
        tempPage.DefaultTimeout = _options.NavigationTimeoutMs;

        await SafeGoToAsync(tempPage, origin);
        await WaitForDocumentReadySoftAsync(tempPage, TimeSpan.FromSeconds(6));
        return await CaptureLocalStorageForOriginAsync(tempPage, origin);
    }

    private async Task RestoreLocalStorageForOriginAsync(IPage page, string origin, Dictionary<string, string> values)
    {
        await EvaluateWithRetryAsync(() => page.EvaluateFunctionAsync<bool>(@"(expectedOrigin, source) => {
            const actual = window.location.origin;
            if (actual !== expectedOrigin) {
                return false;
            }

            for (const key of Object.keys(source || {})) {
                localStorage.setItem(key, source[key] ?? '');
            }
            return true;
        }", origin, values), $"RestoreLocalStorage[{origin}]");
    }

    private async Task RestoreLocalStorageInIsolatedPageAsync(IPage anchorPage, string origin,
        Dictionary<string, string> values)
    {
        await using var tempPage = await anchorPage.Browser.NewPageAsync();
        tempPage.DefaultNavigationTimeout = _options.NavigationTimeoutMs;
        tempPage.DefaultTimeout = _options.NavigationTimeoutMs;

        await SafeGoToAsync(tempPage, origin);
        await WaitForDocumentReadySoftAsync(tempPage, TimeSpan.FromSeconds(6));
        await RestoreLocalStorageForOriginAsync(tempPage, origin, values);
    }

    private async Task SaveCookiesAsync(IPage page)
    {
        var cookies = await page.GetCookiesAsync("https://assetstore.unity.com", "https://login.unity.com");
        var serializable = cookies.Select(SerializableCookie.FromCookie).ToList();
        SecretStore.WriteProtectedText(_cookiesPath, JsonSerializer.Serialize(serializable, _jsonOptions));
        _logger.Info($"Сохранено cookies: {serializable.Count}");
        _logger.Debug(
            $"Домены cookies после входа: {string.Join(", ", serializable.Select(c => c.Domain).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))}");
    }

    private async Task<List<string>> CollectAssetUrlsAsync(IPage page, IEnumerable<string> sourceUrls)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Прямые ссылки на ассеты не требуют разбора страницы. Их бывают сотни,
        // и построчный лог по каждой делает файл нечитаемым. Считаем их и пишем итог.
        var directLinks = 0;
        var failedSources = 0;

        foreach (var source in sourceUrls)
        {
            try
            {
                List<string> sourceUrlsExtracted;

                if (Uri.TryCreate(source, UriKind.Absolute, out var sourceUri) &&
                    sourceUri.Host.Contains("assetstore.unity.com", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryNormalizeDirectAssetUrl(sourceUri, out var directAssetUrl))
                    {
                        directLinks++;
                        _logger.Debug($"Прямая ссылка на ассет: {directAssetUrl}");
                        all.Add(directAssetUrl);
                        continue;
                    }

                    _logger.Info($"Разбор страницы Asset Store: {source}");
                    sourceUrlsExtracted = await CollectAssetUrlsFromAssetStorePageAsync(page, source);
                }
                else
                {
                    _logger.Info($"Чтение источника: {source}");
                    var html = await _httpClient.GetStringAsync(source);
                    sourceUrlsExtracted = ExtractAssetUrlsFromHtml(html, source).ToList();
                }

                foreach (var url in sourceUrlsExtracted)
                {
                    all.Add(url);
                }

                _logger.Info($"  найдено ссылок: {sourceUrlsExtracted.Count}");
            }
            catch (Exception ex)
            {
                failedSources++;
                _logger.Warn($"Ошибка источника {ShortUrl(source)}: {ex.Message}");
            }
        }

        if (directLinks > 0)
        {
            _logger.Info($"Прямых ссылок на ассеты в списках: {directLinks}");
        }

        if (failedSources > 0)
        {
            _logger.Warn($"Источников с ошибками: {failedSources}");
        }

        return all.ToList();
    }

    private List<string> ResolveSources()
    {
        var sources = new List<string>();

        if (_options.Sources.Count > 0)
        {
            sources.AddRange(_options.Sources);
        }
        else if (!_options.UseNoDefaults)
        {
            sources.Add(BaseTopFreeSource);
            sources.AddRange(LoadSourcesFromFile(BaseFreeListFileName, "Файл базового списка бесплатных ассетов не найден"));
        }

        foreach (var extraFile in _options.ExtraSourceFiles)
        {
            sources.AddRange(LoadSourcesFromFile(extraFile, "Дополнительный файл ссылок не найден"));
        }

        if (_options.UseExtendedSources)
        {
            var extendedSourcesList = LoadSourcesFromFile(ExtendedSourcesFileName, "Файл расширенных источников поиска не найден");
            if (extendedSourcesList.Count > 0)
            {
                _logger.Info($"Включены расширенные источники (--extended-sources). Загружено: {extendedSourcesList.Count}");
                sources.AddRange(extendedSourcesList);
            }
        }

        return sources
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x) && !x.Equals("none", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> LoadSourcesFromFile(string fileName, string notFoundMessagePrefix)
    {
        var candidates = BuildFileCandidates(fileName);

        var path = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.Warn($"{notFoundMessagePrefix}: {fileName}");
            return [];
        }

        try
        {
            var urls = File.ReadAllLines(path)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Where(x => Uri.TryCreate(x, UriKind.Absolute, out var uri) &&
                            uri.Host.Contains("assetstore.unity.com", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _logger.Info($"Загружено ссылок из {Path.GetFileName(path)}: {urls.Count}");
            return urls;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Не удалось прочитать файл {path}: {ex.Message}");
            return [];
        }
    }

    private static List<string> BuildFileCandidates(string fileName)
    {
        if (Path.IsPathRooted(fileName))
        {
            return new List<string> { Path.GetFullPath(fileName) };
        }

        return new List<string>
            {
                Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), fileName)),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, fileName)),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", fileName))
            }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryNormalizeDirectAssetUrl(Uri uri, out string normalized)
    {
        normalized = string.Empty;

        if (!uri.Host.Contains("assetstore.unity.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!uri.AbsolutePath.Contains("/packages/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        normalized = $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}".TrimEnd('/');
        return true;
    }

    private async Task<List<string>> CollectAssetUrlsFromAssetStorePageAsync(IPage page, string sourceUrl)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await SafeGoToAsync(page, sourceUrl);
            await WaitForDocumentReadySoftAsync(page, TimeSpan.FromSeconds(8));

            if (IsLikelySignOutFlowUrl(page.Url) ||
                page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warn(
                    $"Источник {sourceUrl}: обнаружен редирект в logout/login ({page.Url}). Выполняем переавторизацию, попытка {attempt}/3...");
                var authOk = await EnsureAuthenticatedAsync(page);
                if (!authOk)
                {
                    _logger.Warn($"Источник {sourceUrl}: переавторизация не удалась.");
                    return [];
                }

                continue;
            }

            await ScrollSourcePageAsync(page, TimeSpan.FromSeconds(20));

            if (IsLikelySignOutFlowUrl(page.Url) ||
                page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warn(
                    $"Источник {sourceUrl}: во время скролла произошел редирект в logout/login ({page.Url}). Повторяем источник...");
                var authOk = await EnsureAuthenticatedAsync(page);
                if (!authOk)
                {
                    _logger.Warn($"Источник {sourceUrl}: переавторизация после logout-flow не удалась.");
                    return [];
                }

                continue;
            }

            var raw = await EvaluateWithRetryAsync(() => page.EvaluateFunctionAsync<string>(@"() => {
            const normalizeUrl = (href) => {
                try {
                    const u = new URL(href, window.location.origin);
                    if (!u.hostname.includes('assetstore.unity.com')) return null;
                    if (!u.pathname.includes('/packages/')) return null;
                    return `${u.protocol}//${u.host}${u.pathname}`.replace(/\/+$/, '');
                } catch {
                    return null;
                }
            };

            const toLower = (x) => (x || '').toLowerCase();
            const hasOwnedSignals = (text) =>
                text.includes('purchased') ||
                text.includes('you own this asset') ||
                text.includes('open in unity');

            const hasAssetSignals = (text) =>
                text.includes('add to my assets') ||
                text.includes('open in unity') ||
                text.includes('purchased') ||
                text.includes('you own this asset') ||
                text.includes('free') ||
                text.includes('$0');

            const links = Array.from(document.querySelectorAll('a[href*=""/packages/""]'));
            const unique = new Map();

            for (const link of links) {
                const url = normalizeUrl(link.getAttribute('href') || link.href || '');
                if (!url) continue;

                const card = link.closest('article, li, [class*=""card"" i], [class*=""product"" i], [data-testid*=""product"" i], [data-testid*=""asset"" i]') || link.parentElement;
                const cardText = toLower(card?.innerText || '');
                const linkText = toLower(link.innerText || '');
                const context = `${cardText}\n${linkText}`;

                if (!hasAssetSignals(context)) continue;

                const isOwned = hasOwnedSignals(context);
                if (!unique.has(url)) {
                    unique.set(url, { url, isOwned });
                } else if (isOwned) {
                    unique.get(url).isOwned = true;
                }
            }

            const items = Array.from(unique.values());
            const ownedSkipped = items.filter(x => x.isOwned).length;
            const urls = items.filter(x => !x.isOwned).map(x => x.url);

            return JSON.stringify({
                totalFound: items.length,
                ownedSkipped,
                urls
            });
        }"), "CollectAssetUrlsFromAssetStorePage");

            var parsed = JsonSerializer.Deserialize<SourceCollectionSnapshot>(raw ?? "{}", _runtimeJsonOptions) ??
                         new SourceCollectionSnapshot();
            _logger.Info(
                $"Источник Asset Store: найдено карточек={parsed.TotalFound}, пропущено как owned={parsed.OwnedSkipped}, к обработке={parsed.Urls.Count}");

            if (parsed.TotalFound == 0 && attempt < 3)
            {
                _logger.Warn(
                    $"Источник {sourceUrl}: карточки не обнаружены (0). Повторяем чтение источника ({attempt}/3)...");
                await Task.Delay(1200);
                continue;
            }

            return parsed.Urls;
        }

        _logger.Warn($"Источник {sourceUrl}: не удалось стабильно собрать карточки после повторов.");
        return [];
    }

    private static bool IsLikelySignOutFlowUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return url.Contains("/oauth2/end-session", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("post_logout_redirect_uri", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ScrollSourcePageAsync(IPage page, TimeSpan timeout)
    {
        var stopAt = DateTime.UtcNow.Add(timeout);
        var stableIterations = 0;
        var lastCount = -1;

        while (DateTime.UtcNow < stopAt && stableIterations < 4)
        {
            if (IsLikelySignOutFlowUrl(page.Url) ||
                page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug(
                    $"ScrollSourcePage: обнаружен logout/login URL ({page.Url}), досрочно останавливаем скролл.");
                return;
            }

            var currentCount = await EvaluateWithRetryAsync(
                () => page.EvaluateFunctionAsync<int>(
                    @"() => document.querySelectorAll('a[href*=""/packages/""]').length"), "ScrollSourcePage(count)");

            if (currentCount <= lastCount)
            {
                stableIterations++;
            }
            else
            {
                stableIterations = 0;
                lastCount = currentCount;
            }

            await EvaluateWithRetryAsync(
                () => page.EvaluateFunctionAsync<int>(
                    @"() => { window.scrollBy(0, window.innerHeight * 1.6); return document.querySelectorAll(""a[href*='/packages/']"").length; }"),
                "ScrollSourcePage(scroll)");
            await Task.Delay(900);
        }
    }

    private static IEnumerable<string> ExtractAssetUrlsFromHtml(string html, string baseUrl)
    {
        var regex = new Regex(@"(?:https?:\/\/assetstore\.unity\.com)?\/packages\/[\w\-\/%\.~]+",
            RegexOptions.IgnoreCase);
        var baseUri = new Uri(baseUrl);

        static bool HasOwnedSignalsNearUrl(string content, int index)
        {
            var start = Math.Max(0, index - 1400);
            var length = Math.Min(content.Length - start, 2800);
            if (length <= 0)
            {
                return false;
            }

            var context = content.Substring(start, length);

            return context.Contains("You own this asset", StringComparison.OrdinalIgnoreCase) ||
                   context.Contains(">PURCHASED<", StringComparison.OrdinalIgnoreCase) ||
                   context.Contains("\"PURCHASED\"", StringComparison.OrdinalIgnoreCase) ||
                   context.Contains("purchased", StringComparison.OrdinalIgnoreCase) &&
                   context.Contains("/packages/", StringComparison.OrdinalIgnoreCase);
        }

        foreach (Match match in regex.Matches(html))
        {
            if (string.IsNullOrWhiteSpace(match.Value))
            {
                continue;
            }

            if (HasOwnedSignalsNearUrl(html, match.Index))
            {
                continue;
            }

            var absolute = match.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? match.Value
                : new Uri(baseUri, match.Value).ToString();

            if (!Uri.TryCreate(absolute, UriKind.Absolute, out var uri))
            {
                continue;
            }

            if (!uri.Host.Contains("assetstore.unity.com", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalized = $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}".TrimEnd('/');
            if (normalized.Contains("/packages/", StringComparison.OrdinalIgnoreCase))
            {
                yield return normalized;
            }
        }
    }

    private async Task<ProcessResult> ProcessAssetAsync(
        IPage page, string assetUrl, string? promoCode = null, bool retriedAfterNavigation = false)
    {
        var result = new ProcessResult
        {
            Url = assetUrl,
            TimestampUtc = DateTime.UtcNow
        };

        try
        {
            for (var processingAttempt = 1; processingAttempt <= 2; processingAttempt++)
            {
                await SafeGoToAsync(page, assetUrl);
                await WaitForShortLinkRedirectAsync(page, assetUrl);

                // Издатель мог убрать ассет из магазина. Такую страницу узнаём сразу:
                // иначе программа ждёт кнопку добавления полторы минуты, а её там нет и не будет.
                if (await IsDeprecatedAssetPageAsync(page))
                {
                    result.Status = AssetProcessStatus.Deprecated;
                    result.Message = "Ассет удалён из магазина издателем. Добавить его нельзя.";
                    _logger.Info($"[Пропуск] Ассет удалён из магазина: {assetUrl}");
                    return result;
                }

                var ready = await WaitForAssetSignalsAsync(page, TimeSpan.FromMilliseconds(_options.AssetUiTimeoutMs));
                if (!ready)
                {
                    // Предупреждение о снятии с продажи могло появиться уже после загрузки.
                    // Проверяем ещё раз, пока не начали ждать кнопку ещё сорок секунд.
                    if (await IsDeprecatedAssetPageAsync(page))
                    {
                        result.Status = AssetProcessStatus.Deprecated;
                        result.Message = "Ассет удалён из магазина издателем. Добавить его нельзя.";
                        _logger.Info($"[Пропуск] Ассет удалён из магазина: {assetUrl}");
                        return result;
                    }

                    _logger.Warn(
                        "Не удалось дождаться появления ключевых элементов ассета (Add/Open/Sign in/Buy). Продолжаем с текущими данными страницы.");
                }

                var status = await DetectStatusAsync(page);
                result.DetectedFree = status.IsFree;
                result.DetectedOwned = status.IsOwned;
                result.CountsTowardsAddLimit = status.IsFree;
                result.PurchasedOnText = status.PurchasedOnText;
                result.DetectionSummary = string.IsNullOrWhiteSpace(status.DetectionSummary)
                    ? "no-signals"
                    : status.DetectionSummary;
                _logger.Debug(
                    $"CTA snapshot: openInUnity={status.HasOpenInUnity}, addToMyAssets={status.HasAddToMyAssets}, requiresLogin={status.RequiresLogin}, free={status.IsFree}, owned={status.IsOwned}");

                if (!status.HasAddToMyAssets && !status.HasOpenInUnity && !status.RequiresLogin)
                {
                    _logger.Debug(
                        "Не найдены ключевые CTA-сигналы (Add/Open/SignIn). Выполняем расширенное ожидание и повторную детекцию...");
                    await WaitForAssetSignalsAsync(page,
                        TimeSpan.FromMilliseconds(Math.Max(_options.AssetUiTimeoutMs, 45000)));

                    status = await DetectStatusAsync(page);
                    result.DetectedFree = status.IsFree;
                    result.DetectedOwned = status.IsOwned;
                    result.CountsTowardsAddLimit = status.IsFree;
                    result.PurchasedOnText = status.PurchasedOnText;
                    result.DetectionSummary = string.IsNullOrWhiteSpace(status.DetectionSummary)
                        ? "no-signals"
                        : status.DetectionSummary;
                    _logger.Debug(
                        $"CTA snapshot (after extra wait): openInUnity={status.HasOpenInUnity}, addToMyAssets={status.HasAddToMyAssets}, requiresLogin={status.RequiresLogin}, free={status.IsFree}, owned={status.IsOwned}");
                }

                _logger.Debug($"Статус ассета (до действия): {status.DetectionSummary}");

                var needsReAuth = page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase) ||
                                  (status.RequiresLogin && !status.IsOwned && !status.HasAddToMyAssets);

                if (needsReAuth)
                {
                    _logger.Warn(
                        $"Обнаружены признаки неавторизованной сессии на странице ассета. Попытка переавторизации {processingAttempt}/2...");
                    var reAuthOk = await EnsureAuthenticatedAsync(page);
                    if (!reAuthOk)
                    {
                        result.Status = AssetProcessStatus.Failed;
                        result.Message = "Требуется авторизация, но подтверждение входа не выполнено.";
                        await SaveErrorScreenshotAsync(page, "reauth-required");
                        return result;
                    }

                    await SafeGoToAsync(page, assetUrl);
                    await WaitForAssetSignalsAsync(page, TimeSpan.FromMilliseconds(_options.AssetUiTimeoutMs));
                    status = await DetectStatusAsync(page);

                    if (!status.HasAddToMyAssets && !status.HasOpenInUnity && !status.RequiresLogin)
                    {
                        _logger.Debug(
                            "После переавторизации CTA-сигналы все еще не готовы. Выполняем расширенное ожидание и повторную детекцию...");
                        await WaitForAssetSignalsAsync(page,
                            TimeSpan.FromMilliseconds(Math.Max(_options.AssetUiTimeoutMs, 45000)));
                        status = await DetectStatusAsync(page);
                    }

                    result.DetectedFree = status.IsFree;
                    result.DetectedOwned = status.IsOwned;
                    result.CountsTowardsAddLimit = status.IsFree;
                    result.PurchasedOnText = status.PurchasedOnText;
                    result.DetectionSummary = string.IsNullOrWhiteSpace(status.DetectionSummary)
                        ? "no-signals"
                        : status.DetectionSummary;
                    _logger.Debug($"Статус ассета (после переавторизации): {status.DetectionSummary}");
                    _logger.Debug(
                        $"CTA snapshot (after re-auth): openInUnity={status.HasOpenInUnity}, addToMyAssets={status.HasAddToMyAssets}, requiresLogin={status.RequiresLogin}, free={status.IsFree}, owned={status.IsOwned}");
                }

                if (status.HasOpenInUnity || status.IsOwned)
                {
                    _logger.Info($"[Пропуск] Ассет уже принадлежит вашему аккаунту: {assetUrl}");
                    result.Status = AssetProcessStatus.AlreadyOwned;
                    return result;
                }

                if (!status.IsFree && !status.HasAddToMyAssets)
                {
                    if (promoCode != null)
                    {
                        _logger.Info($"[Промокод] Ассет платный, но найден промокод '{promoCode}'. Запуск процесса чекаута...");
                        return await TryPromoCheckoutAsync(page, assetUrl, promoCode, result);
                    }

                    _logger.Info($"[Пропуск] Ассет является платным: {assetUrl} (Сигналы: {status.DetectionSummary})");
                    result.Status = AssetProcessStatus.PaidSkipped;
                    return result;
                }

                if (_options.DryRun)
                {
                    _logger.Info($"[Имитация] Ассет бесплатный, был бы добавлен (Dry-run): {assetUrl}");
                    result.Status = AssetProcessStatus.WouldAddInDryRun;
                    return result;
                }

                if (!status.HasAddToMyAssets)
                {
                    // Кнопка иногда дорисовывается позже остальной страницы.
                    // Одна короткая повторная проверка дешевле, чем потерянный ассет.
                    _logger.Debug("Кнопка добавления не найдена. Ждём 3с и проверяем ещё раз...");
                    await Task.Delay(3000);
                    status = await DetectStatusAsync(page);
                    result.DetectedFree = status.IsFree;
                    result.DetectedOwned = status.IsOwned;
                    result.DetectionSummary = status.DetectionSummary;

                    if (status.HasOpenInUnity || status.IsOwned)
                    {
                        _logger.Info($"[Пропуск] Ассет уже принадлежит вашему аккаунту: {assetUrl}");
                        result.Status = AssetProcessStatus.AlreadyOwned;
                        return result;
                    }
                }

                if (!status.HasAddToMyAssets && promoCode != null)
                {
                    // Кнопки бесплатного добавления нет, зато в Telegram к ассету дали промокод.
                    // Признаки цены на странице бывают обманчивы, поэтому пробуем код.
                    _logger.Info($"[Промокод] Бесплатно добавить нельзя, но есть промокод '{promoCode}'. Запуск процесса чекаута...");
                    return await TryPromoCheckoutAsync(page, assetUrl, promoCode, result);
                }

                if (!status.HasAddToMyAssets)
                {
                    result.Status = AssetProcessStatus.Failed;
                    result.Message =
                        "Кнопки добавления нет на странице. Скорее всего раздача закончилась и ассет снова платный.";
                    _logger.Info($"[Пропуск] {assetUrl}");
                    _logger.Info($"          {result.Message}");
                    _logger.Debug($"          Сигналы страницы: {status.DetectionSummary}");
                    await SaveErrorScreenshotAsync(page, "add-button-not-found");
                    return result;
                }

                var clicked = await TryClickAddButtonAsync(page);
                if (!clicked)
                {
                    result.Status = AssetProcessStatus.Failed;
                    result.Message = "Кнопка добавления не найдена.";
                    _logger.Info($"[Ошибка] Не удалось обработать ассет: {assetUrl}. Причина: {result.Message}");
                    await SaveErrorScreenshotAsync(page, "add-button-not-found");
                    return result;
                }

                var accepted = await TryAcceptAddConfirmationAsync(page);
                _logger.Debug($"AcceptFound={accepted}");
                if (accepted)
                {
                    _logger.Info("Подтверждение добавления найдено: нажата кнопка Accept.");
                }

                var postStatus = await VerifyPostAddStatusAsync(
                    page,
                    assetUrl,
                    TimeSpan.FromMilliseconds(Math.Max(12000, Math.Min(_options.AssetUiTimeoutMs, 45000))));

                result.PurchasedOnText = postStatus.PurchasedOnText ?? result.PurchasedOnText;
                result.DetectionSummary = string.IsNullOrWhiteSpace(postStatus.DetectionSummary)
                    ? "no-signals"
                    : postStatus.DetectionSummary;
                _logger.Debug($"Статус ассета (после клика): {postStatus.DetectionSummary}");
                _logger.Debug($"PostAddOpenInUnity={(postStatus.HasOpenInUnity || postStatus.IsOwned)}");

                if (postStatus.RequiresLogin ||
                    page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warn("После попытки добавления потребовалась повторная авторизация.");
                    continue;
                }

                result.Status = (postStatus.HasOpenInUnity || postStatus.IsOwned)
                    ? AssetProcessStatus.Added
                    : AssetProcessStatus.UnknownAfterClick;

                if (result.Status == AssetProcessStatus.Added)
                {
                    _logger.Info($"[УСПЕХ] Ассет успешно добавлен на аккаунт: {assetUrl}");
                }
                else
                {
                    _logger.Info($"[Внимание] Кнопка добавления была нажата, но статус добавления не подтвержден: {assetUrl} (Сигналы: {result.DetectionSummary})");
                }
                return result;
            }

            result.Status = AssetProcessStatus.Failed;
            result.Message = "Не удалось завершить добавление после переавторизации.";
            _logger.Info($"[Ошибка] {result.Message} ({assetUrl})");
            await SaveErrorScreenshotAsync(page, "reauth-loop-failed");
            return result;
        }
        catch (Exception ex) when (IsTransientPageError(ex) && !retriedAfterNavigation)
        {
            // Страница ушла на другой адрес прямо во время проверки — обычное дело
            // на медленной сети. Просто пробуем этот ассет ещё раз.
            _logger.Debug($"Страница сменилась во время проверки. Повторяем: {assetUrl}");
            retriedAfterNavigation = true;
            await Task.Delay(2000);
            return await ProcessAssetAsync(page, assetUrl, promoCode, true);
        }
        catch (Exception ex)
        {
            result.Status = AssetProcessStatus.Failed;
            result.Message = ex.Message;
            _logger.Info($"[Ошибка] Исключение при обработке ассета: {ex.Message} ({assetUrl})");
            await SaveErrorScreenshotAsync(page, "processing-error");
            return result;
        }
    }

    /// <summary>Выкуп по промокоду. В проверочном запуске (dry-run) ничего не покупаем.</summary>
    private async Task<ProcessResult> TryPromoCheckoutAsync(IPage page, string assetUrl, string promoCode, ProcessResult result)
    {
        if (_options.DryRun)
        {
            _logger.Info($"[Имитация] Есть промокод '{promoCode}'. В настоящем запуске программа попробовала бы его: {assetUrl}");
            result.Status = AssetProcessStatus.PaidSkipped;
            result.Message = $"Проверочный запуск: промокод '{promoCode}' не проверялся.";
            return result;
        }

        result.PromoCode = promoCode;
        return await ProcessPromoAssetAsync(page, assetUrl, promoCode, result);
    }

    private async Task<ProcessResult> ProcessPromoAssetAsync(IPage page, string assetUrl, string promoCode, ProcessResult result)
    {
        var sanitizedId = SanitizeFileName(assetUrl.Split('/').Last());
        var totalSw = Stopwatch.StartNew();
        _logger.Info($"[Промокод] ===== НАЧАЛО выкупа по промокоду '{promoCode}' | ассет: {assetUrl} =====");

        // По номеру ассета отличаем его от остального содержимого корзины.
        var packageId = ExtractPackageId(page.Url) ?? ExtractPackageId(assetUrl);
        if (packageId is null)
        {
            result.Status = AssetProcessStatus.Failed;
            result.Message = "Не удалось узнать номер ассета из адреса, а без него его не отличить от других в корзине.";
            _logger.Warn($"[Промокод] {result.Message}");
            return result;
        }

        // После нажатия оплаты ошибка страницы не повод чистить корзину: сначала выясняем, куплен ли ассет.
        var payClicked = false;

        try
        {
            // 1. Кладём ассет в корзину. Только кнопкой «Add to Cart»: «Buy Now» на странице
            // ассета — это Express Purchase, он списывает деньги с привязанной карты
            // сразу, без шага с промокодом.
            var stepSw = Stopwatch.StartNew();
            _logger.Info($"[Промокод][Шаг 1] Кладём ассет #{packageId} в корзину (кнопка 'Add to Cart') | URL: {page.Url}");
            await LogAllButtonsAsync(page, "Шаг 1 - кнопки до клика");
            var addState = await TryClickAddToCartOnlyAsync(page, packageId);
            _logger.Info($"[Промокод][Шаг 1] результат: {addState} | {stepSw.ElapsedMilliseconds}мс");

            if (addState.StartsWith("foreign", StringComparison.Ordinal))
            {
                var foreignIds = addState.Contains(':') ? addState[(addState.IndexOf(':') + 1)..] : "?";
                result.Status = AssetProcessStatus.Failed;
                result.Message = $"Кнопка 'Add to Cart' на странице есть, но она от других ассетов (#{foreignIds}), а не от #{packageId}. Ничего не нажимали.";
                _logger.Warn($"[Ошибка][Шаг 1] {result.Message}");
                await SaveErrorScreenshotAsync(page, $"promo_foreign_cart_{sanitizedId}");
                await SaveHtmlDumpAsync(page, $"promo_dump_step1_foreign_{sanitizedId}");
                return result;
            }

            if (addState == "none")
            {
                result.Status = AssetProcessStatus.Failed;
                result.Message = "На странице ассета нет кнопки 'Add to Cart'.";
                _logger.Warn($"[Ошибка][Шаг 1] {result.Message}");
                await SaveErrorScreenshotAsync(page, $"promo_failed_click_{sanitizedId}");
                await SaveHtmlDumpAsync(page, $"promo_dump_step1_no_btn_{sanitizedId}");
                return result;
            }

            if (addState == "clicked")
            {
                var added = await WaitForAddedToCartAsync(page, TimeSpan.FromSeconds(12));
                _logger.Info(added
                    ? $"[Промокод][Шаг 1] Магазин подтвердил: ассет в корзине | {stepSw.ElapsedMilliseconds}мс"
                    : $"[Промокод][Шаг 1] Подтверждения не видно, проверим по самой корзине | {stepSw.ElapsedMilliseconds}мс");
            }
            else
            {
                _logger.Info("[Промокод][Шаг 1] Ассет уже лежал в корзине, второй раз не добавляем.");
            }

            // 2. Открываем корзину и убираем из неё всё, кроме этого ассета.
            // Иначе в заказ попадут и другие ассеты, а промокод действует только на один.
            stepSw.Restart();
            _logger.Info($"[Промокод][Шаг 2] Открываем корзину {CartUrl} и оставляем в ней только ассет #{packageId}");
            var cart = await RemoveCartItemsAsync(page, keepPackageId: packageId);

            if (cart is null || !cart.Items.Any(i => i.Id == packageId))
            {
                result.Status = AssetProcessStatus.Failed;
                result.Message = cart is null
                    ? "Корзина не открылась."
                    : "Ассет не попал в корзину: кнопка нажата, но в корзине его нет.";
                _logger.Warn($"[Ошибка][Шаг 2] {result.Message} | URL: {page.Url}");
                await LogAllButtonsAsync(page, "Шаг 2 - кнопки корзины");
                await SaveErrorScreenshotAsync(page, $"promo_failed_cart_{sanitizedId}");
                await SaveHtmlDumpAsync(page, $"promo_dump_step2_cart_{sanitizedId}");
                return result;
            }

            if (cart.Items.Count != 1)
            {
                result.Status = AssetProcessStatus.Failed;
                result.Message =
                    $"Из корзины не удалось убрать другие ассеты (осталось {cart.Items.Count}). " +
                    "Оформлять заказ с ними нельзя: промокод относится только к одному ассету.";
                _logger.Warn($"[Ошибка][Шаг 2] {result.Message}");
                await SaveErrorScreenshotAsync(page, $"promo_failed_cart_cleanup_{sanitizedId}");
                await SaveHtmlDumpAsync(page, $"promo_dump_step2_cleanup_{sanitizedId}");
                await ClearCartAsync(page);
                return result;
            }

            _logger.Info($"[Промокод][Шаг 2] В корзине только нужный ассет: {cart.Items[0].Describe()} | {stepSw.ElapsedMilliseconds}мс");

            // 3. Оформление заказа из корзины: магазин сам переводит на pay.unity.com.
            stepSw.Restart();
            _logger.Info("[Промокод][Шаг 3] Нажимаем 'Checkout' в корзине и ждём страницу оплаты pay.unity.com");
            var onPayPage = await ProceedToCheckoutFromCartAsync(page, TimeSpan.FromSeconds(60));
            _logger.Info($"[Промокод][Шаг 3] страница оплаты открылась={onPayPage} | {stepSw.ElapsedMilliseconds}мс | URL: {page.Url}");

            if (!onPayPage)
            {
                result.Status = AssetProcessStatus.Failed;
                result.Message = "После нажатия 'Checkout' страница оплаты pay.unity.com не открылась.";
                _logger.Warn($"[Ошибка][Шаг 3] {result.Message}");
                await LogAllButtonsAsync(page, "Шаг 3 - кнопки");
                await SaveErrorScreenshotAsync(page, $"promo_failed_checkout_{sanitizedId}");
                await SaveHtmlDumpAsync(page, $"promo_dump_step3_checkout_{sanitizedId}");
                await ClearCartAsync(page);
                return result;
            }

            await SaveErrorScreenshotAsync(page, $"promo_pay_page_{sanitizedId}");

            _logger.Info("[Промокод][Шаг 3] Ждём, пока на странице оплаты появятся поле промокода и кнопка оплаты (до 30с)...");
            var elementsReady = await WaitForCartPageElementsAsync(page, TimeSpan.FromSeconds(30));
            _logger.Info($"[Промокод][Шаг 3] elementsReady={elementsReady} | {stepSw.ElapsedMilliseconds}мс");
            if (!elementsReady)
            {
                result.Status = AssetProcessStatus.Failed;
                result.Message =
                    "Страница оформления заказа открылась, но на ней нет ни поля промокода, ни кнопки оплаты.";
                _logger.Warn("============================================================");
                _logger.Warn(" ПРОМОКОД ПРИМЕНИТЬ НЕ УДАЛОСЬ");
                _logger.Warn($" {result.Message}");
                _logger.Warn(" Покупка отменена, деньги не списаны.");
                _logger.Warn("============================================================");

                await LogAllInputFieldsAsync(page, "Шаг 3 - элементы не найдены");
                await LogAllButtonsAsync(page, "Шаг 3 - кнопки");
                await SaveErrorScreenshotAsync(page, $"promo_failed_elements_{sanitizedId}");
                await SaveHtmlDumpAsync(page, $"promo_dump_step3_no_elements_{sanitizedId}");
                await ClearCartAsync(page);
                return result;
            }

            // 4. Вопрос «Tax Business use»: всегда «No». С «Yes» страница требует адрес
            // организации, не считает налог и итог и не принимает промокод.
            stepSw.Restart();
            _logger.Info($"[Промокод][Шаг 4] Вопрос 'Tax Business use': выбираем 'No' | URL: {page.Url}");
            await EnsureTaxBusinessUseNoAsync(page, "Шаг 4");
            await SaveErrorScreenshotAsync(page, $"promo_tax_selected_{sanitizedId}");

            // 4.5. Обработка формы адреса (Billing Address Form), если она открыта
            _logger.Info("[Промокод] Проверка наличия формы ввода адреса (Billing Address)...");
            stepSw.Restart();
            _logger.Info($"[Промокод][Шаг 4.5] Проверка формы Billing Address | URL: {page.Url}");

            var billingAddressHandled = await page.EvaluateFunctionAsync<bool>(@"async () => {
                const normalize = (v) => (v || '').replace(/\s+/g, ' ').trim().toLowerCase();
                const visible = (el) => {
                    if (!el) return false;
                    const style = window.getComputedStyle(el);
                    const rect = el.getBoundingClientRect();
                    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
                };

                const inputs = Array.from(document.querySelectorAll('input'));
                const hasAddressFields = inputs.some(el => {
                    if (!visible(el)) return false;
                    const name = normalize(el.name || '');
                    const id = normalize(el.id || '');
                    return name.includes('address') || name.includes('postal') || name.includes('zip') || name.includes('city') || name.includes('state') ||
                           id.includes('address') || id.includes('postal') || id.includes('zip') || id.includes('city') || id.includes('state');
                });

                if (!hasAddressFields) return false;

                const buttons = Array.from(document.querySelectorAll('button, a, [role=""button""]')).filter(visible);
                const saveBtn = buttons.find(el => {
                    const txt = normalize(el.innerText || '');
                    if (txt.includes('pay') || txt.includes('заплатить') || txt.includes('оплатить') || txt.includes('купить') || txt.includes('place order') || txt.includes('complete purchase')) {
                        return false;
                    }
                    return txt === 'save' || txt === 'continue' || txt === 'next' || txt.includes('save & continue') ||
                           txt === 'далее' || txt === 'сохранить' || txt === 'продолжить' || txt.includes('сохранить и продолжить');
                });

                if (saveBtn) {
                    saveBtn.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                    if (typeof saveBtn.click === 'function') {
                        saveBtn.click();
                    }
                    return true;
                }

                return false;
            }");

            if (billingAddressHandled)
            {
                _logger.Info("[Промокод] Обнаружена форма адреса: нажата кнопка продолжения/сохранения.");
                _logger.Info($"[Промокод][Шаг 4.5] Форма адреса обнаружена: нажата кнопка продолжения | {stepSw.ElapsedMilliseconds}мс");
                await Task.Delay(4000);
                _logger.Debug($"[Промокод][Шаг 4.5] URL после сохранения адреса: {page.Url}");
                await SaveErrorScreenshotAsync(page, $"promo_billing_saved_{sanitizedId}");
            }
            else
            {
                _logger.Debug($"[Промокод][Шаг 4.5] Форма Billing Address не обнаружена (пропущена) | {stepSw.ElapsedMilliseconds}мс | URL: {page.Url}");
            }

            // 5. Вводим промокод и нажимаем Apply
            stepSw.Restart();
            var priceBefore = await ReadCartPriceAsync(page);
            _logger.Info($"[Промокод][Шаг 5] Стоимость до промокода: {priceBefore.Describe()}");
            _logger.Info($"[Промокод][Шаг 5] Ввод промокода '{promoCode}' | URL: {page.Url}");
            await LogAllInputFieldsAsync(page, "Шаг 5 - все input перед вводом кода");
            var promoEntered = await TryFillPromoCodeAsync(page, promoCode);

            _logger.Info($"[Промокод][Шаг 5] promoEntered={promoEntered} | {stepSw.ElapsedMilliseconds}мс");
            if (!promoEntered)
            {
                result.Status = AssetProcessStatus.Failed;
                result.Message = "Не найдено поле ввода промокода.";
                _logger.Warn($"[Ошибка][Шаг 5] {result.Message} | URL: {page.Url}");

                await LogCouponAreaAsync(page);
                await LogAllInputFieldsAsync(page, "Шаг 5 - поле не найдено, все inputs");
                await LogAllButtonsAsync(page, "Шаг 5 - кнопки при ошибке");
                await SaveErrorScreenshotAsync(page, $"promo_failed_input_{sanitizedId}");
                await SaveHtmlDumpAsync(page, $"promo_dump_step5_no_input_{sanitizedId}");
                await ClearCartAsync(page);
                return result;
            }

            // Ответ на налог ещё раз — прямо перед Apply: пока вводился код, страница могла его сбросить.
            await EnsureTaxBusinessUseNoAsync(page, "Шаг 5");

            // Что написано у поля до нажатия Apply — подпись и прочее. Это не ответ на код.
            var couponTextBefore = (await ReadCouponMessagesAsync(page)).Select(m => m.Text).ToHashSet();

            // 5.5 - Apply button
            stepSw.Restart();
            _logger.Info($"[Промокод][Шаг 5.5] Нажатие кнопки Apply/Redeem | URL: {page.Url}");
            var applyState = await ClickPromoApplyAsync(page, promoCode);

            if (applyState != "clicked")
            {
                result.Status = AssetProcessStatus.Failed;
                result.Message = applyState == "disabled"
                    ? "Кнопка Apply для промокода не нажимается."
                    : "Не найдена кнопка Apply для промокода.";
                _logger.Warn($"[Ошибка][Шаг 5.5] {result.Message} | URL: {page.Url}");
                await LogAllButtonsAsync(page, "Шаг 5.5 - кнопки при ошибке Apply");
                await SaveErrorScreenshotAsync(page, $"promo_failed_apply_{sanitizedId}");
                await SaveHtmlDumpAsync(page, $"promo_dump_step5_5_no_apply_{sanitizedId}");
                await ClearCartAsync(page);
                return result;
            }

            _logger.Info($"[Промокод][Шаг 5.5] Кнопка Apply нажата | {stepSw.ElapsedMilliseconds}мс. Ждём ответа магазина (до 20с)...");

            // 6. Промокод сработал только если итоговая цена стала ровно нулём.
            // Ждём, пока страница либо обнулит цену, либо скажет, что код не подходит.
            stepSw.Restart();
            var priceAfter = await WaitForPromoOutcomeAsync(page, priceBefore, TimeSpan.FromSeconds(20), couponTextBefore);
            _logger.Info($"[Промокод] Стоимость после промокода: {priceAfter.Describe()} | {stepSw.ElapsedMilliseconds}мс");

            // «Organization address info is not complete or correct» — это не про код:
            // в вопросе «Tax Business use» стоит «Yes». Ставим «No» и применяем код ещё раз.
            if (priceAfter.HasPromoError && IsTaxOrAddressError(priceAfter.FoundError))
            {
                _logger.Info($"[Промокод][Шаг 6] Страница пишет: '{priceAfter.FoundError}'.");
                _logger.Info("[Промокод][Шаг 6] Это из-за вопроса 'Tax Business use'. Ставим 'No' и применяем код ещё раз.");
                await EnsureTaxBusinessUseNoAsync(page, "Шаг 6");

                // Ошибка от первой попытки может остаться на странице — ответом на вторую она не считается.
                couponTextBefore.UnionWith((await ReadCouponMessagesAsync(page)).Select(m => m.Text));
                var retryPriceBefore = await ReadCartPriceAsync(page);
                _logger.Info($"[Промокод][Шаг 6] Стоимость после выбора 'No': {retryPriceBefore.Describe()}");

                var reapplied = await TryFillPromoCodeAsync(page, promoCode) &&
                                await EnsureTaxBusinessUseNoAsync(page, "Шаг 6") &&
                                await ClickPromoApplyAsync(page, promoCode) == "clicked";
                if (reapplied)
                {
                    stepSw.Restart();
                    priceAfter = await WaitForPromoOutcomeAsync(page, retryPriceBefore, TimeSpan.FromSeconds(20), couponTextBefore);
                    if (!priceAfter.HasPromoError && !(priceAfter.Found && priceAfter.Amount == 0))
                    {
                        // Если старая ошибка так и висит, а итог не обнулился — проблема осталась.
                        var lingering = FindCouponError(await ReadCouponMessagesAsync(page), new HashSet<string>());
                        if (lingering is not null)
                        {
                            priceAfter.HasPromoError = true;
                            priceAfter.FoundError = lingering;
                        }
                    }
                    _logger.Info($"[Промокод] Стоимость после повторного промокода: {priceAfter.Describe()} | {stepSw.ElapsedMilliseconds}мс");
                    if (retryPriceBefore.Found)
                    {
                        priceBefore = retryPriceBefore;
                    }
                }
                else
                {
                    _logger.Warn("[Промокод][Шаг 6] Повторно ввести код не получилось.");
                }
            }

            _logger.Debug($"[Промокод][Шаг 6] URL после Apply: {page.Url}");
            await SaveErrorScreenshotAsync(page, $"promo_coupon_applied_{sanitizedId}");

            if (priceAfter.HasPromoError)
            {
                // В память отвергнутых кодов попадает только явный отказ по коду.
                // Непонятную ошибку в следующий раз стоит попробовать снова.
                var codeRejected = IsPromoCodeRejection(priceAfter.FoundError);
                result.Status = codeRejected ? AssetProcessStatus.PromoNotApplied : AssetProcessStatus.Failed;
                result.Message = codeRejected
                    ? $"Промокод не принят: страница пишет '{priceAfter.FoundError}'. Скорее всего раздача закончилась."
                    : $"Страница оформления пишет '{priceAfter.FoundError}'. Код до конца не проверен — в следующий раз попробуем снова.";
                _logger.Warn("============================================================");
                _logger.Warn(codeRejected ? " ПРОМОКОД НЕ СРАБОТАЛ" : " ОФОРМЛЕНИЕ НЕ ПРОШЛО");
                _logger.Warn($" {result.Message}");
                _logger.Warn(" Ассет пропущен и убран из корзины, деньги не списаны.");
                _logger.Warn("============================================================");
                await SaveErrorScreenshotAsync(page, $"promo_failed_error_{sanitizedId}");
                await SaveHtmlDumpAsync(page, $"promo_dump_step6_error_{sanitizedId}");
                await ClearCartAsync(page);
                return result;
            }

            if (!priceAfter.Found)
            {
                result.Status = AssetProcessStatus.Failed;
                result.Message = "Не удалось прочитать итоговую стоимость на странице оформления.";
                _logger.Warn("============================================================");
                _logger.Warn(" СТОИМОСТЬ НЕ ЧИТАЕТСЯ");
                _logger.Warn(" Не рискуем и отменяем покупку: без уверенности в цене платить нельзя.");
                _logger.Warn("============================================================");
                await SaveErrorScreenshotAsync(page, $"promo_price_unknown_{sanitizedId}");
                await SaveHtmlDumpAsync(page, $"promo_dump_step6_price_unknown_{sanitizedId}");
                await ClearCartAsync(page);
                return result;
            }

            if (priceAfter.Amount > 0)
            {
                // Цена снизилась, но не до нуля — код рабочий, но не на 100%: запоминаем.
                // Цена не сдвинулась и страница молчит — непонятно, дошёл ли код: не запоминаем.
                var partialDiscount = priceBefore.Found && priceAfter.Amount < priceBefore.Amount;
                result.Status = partialDiscount ? AssetProcessStatus.PromoNotApplied : AssetProcessStatus.Failed;
                result.Message = partialDiscount
                    ? $"Промокод дал скидку, но не до нуля: {priceAfter.Describe()}."
                    : $"После ввода кода цена не изменилась ({priceAfter.Describe()}), а страница ничего не написала.";
                _logger.Warn("============================================================");
                _logger.Warn(" ПРОМОКОД НЕ СРАБОТАЛ");
                _logger.Warn($" {result.Message}");
                if (priceBefore.Found)
                {
                    _logger.Warn($" Было до промокода: {priceBefore.Describe()}");
                }

                _logger.Warn(" Ассет пропущен и убран из корзины, деньги не списаны.");
                _logger.Warn("============================================================");
                await SaveErrorScreenshotAsync(page, $"promo_price_not_zero_{sanitizedId}");
                await SaveHtmlDumpAsync(page, $"promo_dump_step6_price_not_zero_{sanitizedId}");
                await ClearCartAsync(page);
                return result;
            }

            _logger.Info($"[Промокод][Шаг 6] Итоговая стоимость обнулилась: {priceAfter.Describe()} | {stepSw.ElapsedMilliseconds}мс");

            // 7. Галки: EULA, отказ от 14 дней на возврат, письма Asset Store.
            stepSw.Restart();
            _logger.Info($"[Промокод][Шаг 7] Ставим галки согласия | URL: {page.Url}");
            var agreementsOk = await EnsureAgreementCheckboxesAsync(page, "Шаг 7");

            // Вопрос о налоге ещё раз: страница могла сбросить ответ, пока применялся код.
            var taxOk = await EnsureTaxBusinessUseNoAsync(page, "Шаг 7");
            await Task.Delay(1000);

            // Последняя проверка перед оплатой: итог ровно ноль, ошибок в блоке купона нет,
            // обязательные галки стоят, в вопросе о налоге — «No».
            var priceBeforePay = await ReadCartPriceAsync(page);
            var couponErrorBeforePay = FindCouponError(await ReadCouponMessagesAsync(page), couponTextBefore);
            string? payBlocker = null;
            if (!priceBeforePay.Found || priceBeforePay.Amount != 0)
            {
                payBlocker = $"Перед оплатой сумма уже не ноль ({priceBeforePay.Describe()}).";
            }
            else if (!string.IsNullOrWhiteSpace(couponErrorBeforePay))
            {
                payBlocker = $"Перед оплатой страница пишет '{couponErrorBeforePay}'.";
            }
            else if (!agreementsOk)
            {
                payBlocker = "Не удалось поставить обязательные галки (EULA / отказ от 14 дней).";
            }
            else if (!taxOk)
            {
                payBlocker = "Не удалось выбрать 'No' в вопросе 'Tax Business use'.";
            }

            if (payBlocker is not null)
            {
                result.Status = AssetProcessStatus.Failed;
                result.Message = payBlocker + " Оплату не нажимаем.";
                _logger.Warn($"[Ошибка][Шаг 8] {result.Message}");
                await SaveErrorScreenshotAsync(page, $"promo_pay_blocked_{sanitizedId}");
                await SaveHtmlDumpAsync(page, $"promo_dump_step8_blocked_{sanitizedId}");
                await ClearCartAsync(page);
                return result;
            }

            // 8. Кликаем кнопку оформления заказа ("Pay Now", "Complete Purchase", "Place Order")
            _logger.Info("[Промокод] Нажатие на кнопку оформления заказа (Pay Now / Place Order)...");
            stepSw.Restart();
            _logger.Info($"[Промокод][Шаг 8] Поиск кнопки Pay Now / Place Order | URL: {page.Url}");
            await LogAllButtonsAsync(page, "Шаг 8 - кнопки перед Pay");
            var finalCheckoutClicked = await page.EvaluateFunctionAsync<bool>(@"() => {
                const normalize = (v) => (v || '').replace(/\s+/g, ' ').trim().toLowerCase();
                const visible = (el) => {
                    if (!el) return false;
                    const style = window.getComputedStyle(el);
                    const rect = el.getBoundingClientRect();
                    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
                };

                const buttons = Array.from(document.querySelectorAll('button, a, [role=""button""]')).filter(visible);
                const payBtn = buttons.find(el => {
                    const txt = normalize(el.innerText || '');
                    return txt.includes('pay now') || txt.includes('place order') || txt.includes('complete purchase') || txt.includes('complete order') || txt === 'pay' || txt.includes('купить') || txt.includes('оформить заказ') || txt.includes('оплатить');
                });

                if (!payBtn) return false;
                
                setTimeout(() => {
                    payBtn.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                    if (typeof payBtn.click === 'function') {
                        payBtn.click();
                    }
                }, 50);
                
                return true;
            }");

            _logger.Info($"[Промокод][Шаг 8] finalCheckoutClicked={finalCheckoutClicked} | {stepSw.ElapsedMilliseconds}мс");
            if (!finalCheckoutClicked)
            {
                result.Status = AssetProcessStatus.Failed;
                result.Message = "Не найдена финальная кнопка оформления заказа (Pay Now / Place Order).";
                _logger.Warn($"[Ошибка][Шаг 8] {result.Message} | URL: {page.Url}");
                await LogAllButtonsAsync(page, "Шаг 8 - кнопки при ошибке Pay");
                await SaveErrorScreenshotAsync(page, $"promo_failed_checkout_btn_{sanitizedId}");
                await SaveHtmlDumpAsync(page, $"promo_dump_step8_no_pay_{sanitizedId}");
                await ClearCartAsync(page);
                return result;
            }

            payClicked = true;
            _logger.Info("[Промокод][Шаг 8] Кнопка Pay нажата. Ждём, пока магазин оформит заказ...");

            // 9. Проверка: после оплаты магазин уводит на свою страницу заказа, и страница
            // меняется прямо во время проверки. Ждём переходов и спрашиваем сам магазин.
            stepSw.Restart();
            var purchased = await VerifyPromoPurchaseAsync(page, assetUrl, sanitizedId);

            if (purchased)
            {
                result.Status = AssetProcessStatus.Added;
                _logger.Info($"[УСПЕХ] Ассет успешно получен по промокоду: {assetUrl}");
                _logger.Info($"[УСПЕХ][Шаг 9] Ассет получен по промокоду! Итого: {totalSw.Elapsed.TotalSeconds:F1}с | URL: {page.Url}");
            }
            else
            {
                result.Status = AssetProcessStatus.UnknownAfterClick;
                result.Message = "Кнопка оплаты нажата, но ассет на аккаунте не появился.";
                _logger.Warn($"[Внимание] Кнопка оформления по промокоду нажата, но ассет на аккаунте не появился: {page.Url}");
                _logger.Warn($"[Внимание][Шаг 9] Успех не подтверждён. Итого: {totalSw.Elapsed.TotalSeconds:F1}с | URL: {page.Url}");
                await SaveHtmlDumpAsync(page, $"promo_dump_step9_unknown_{sanitizedId}");
            }

            _logger.Info($"[Промокод] ===== КОНЕЦ выкупа по промокоду '{promoCode}' | статус: {result.Status} | {totalSw.Elapsed.TotalSeconds:F1}с =====");
            return result;
        }
        catch (Exception ex) when (payClicked)
        {
            // Оплата уже нажата — корзину не трогаем, а выясняем, куплен ли ассет.
            _logger.Warn($"[Промокод][Шаг 9] Сбой после нажатия оплаты ({ex.Message}). Проверяем страницу ассета...");
            var purchased = await VerifyPromoPurchaseAsync(page, assetUrl, sanitizedId);
            result.Status = purchased ? AssetProcessStatus.Added : AssetProcessStatus.UnknownAfterClick;
            result.Message = purchased ? null : $"После оплаты произошёл сбой: {ex.Message}";
            _logger.Info(purchased
                ? $"[УСПЕХ] Ассет успешно получен по промокоду: {assetUrl}"
                : $"[Внимание] Кнопка оплаты нажата, но ассет на аккаунте не появился: {assetUrl}");
            return result;
        }
        catch (Exception ex)
        {
            result.Status = AssetProcessStatus.Failed;
            result.Message = $"Исключение при покупке по промокоду: {ex.Message}";
            _logger.Error($"[КРИТИЧЕСКАЯ ОШИБКА] {result.Message} | URL: {page.Url} | Итого: {totalSw.Elapsed.TotalSeconds:F1}с\n{ex}");

            await SaveErrorScreenshotAsync(page, $"promo_exception_{sanitizedId}");
            await SaveHtmlDumpAsync(page, $"promo_dump_exception_{sanitizedId}");
            await ClearCartAsync(page);
            return result;
        }
    }

    /// <summary>
    /// Проверяет покупку после «Pay now». Магазин уводит на свою страницу заказа
    /// (assetstore.unity.com/orders/&lt;id&gt;/confirm), и адрес меняется несколько раз.
    /// Сначала ждём, пока переходы закончатся (не уходим со страницы подтверждения
    /// раньше времени), потом открываем страницу ассета: «Open in Unity» значит, что он наш.
    /// </summary>
    private async Task<bool> VerifyPromoPurchaseAsync(IPage page, string assetUrl, string sanitizedId)
    {
        var stopAt = DateTime.UtcNow.AddSeconds(30);
        var lastUrl = string.Empty;
        var thanks = false;

        while (DateTime.UtcNow < stopAt && !thanks)
        {
            var url = page.Url;
            if (url != lastUrl)
            {
                _logger.Info($"[Промокод][Шаг 9] Сейчас открыто: {ShortUrl(url)}");
                lastUrl = url;
            }

            try
            {
                thanks = await page.EvaluateFunctionAsync<bool>(@"() => {
                    const text = (document.body ? document.body.innerText : '').replace(/\s+/g, ' ').toLowerCase();
                    return text.includes('thank you for your purchase') || text.includes('thank you for your order') ||
                           text.includes('order completed') || text.includes('order confirmed') ||
                           text.includes('спасибо за покупку') || text.includes('заказ оформлен');
                }");
            }
            catch (Exception ex) when (IsTransientPageError(ex))
            {
                // Страница ещё переходит — это нормально.
            }

            if (!thanks)
            {
                await Task.Delay(1000);
            }
        }

        _logger.Info(thanks
            ? "[Промокод][Шаг 9] Магазин показал страницу благодарности за покупку."
            : "[Промокод][Шаг 9] Страницы благодарности не видно. Проверяем по странице ассета.");
        await SaveErrorScreenshotAsync(page, $"promo_after_pay_{sanitizedId}");

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await SafeGoToAsync(page, assetUrl);
                await WaitForAssetSignalsAsync(page, TimeSpan.FromSeconds(20));
                var status = await DetectStatusAsync(page);
                _logger.Debug($"[Промокод][Шаг 9] Статус ассета после оплаты: {status.DetectionSummary}");

                if (status.IsOwned || status.HasOpenInUnity)
                {
                    _logger.Info("[Промокод][Шаг 9] На странице ассета 'Open in Unity': ассет на аккаунте.");
                    return true;
                }

                break;
            }
            catch (Exception ex) when (IsTransientPageError(ex))
            {
                await Task.Delay(2000);
            }
        }

        // Магазин поблагодарил, но кнопка на странице ассета ещё не обновилась — так бывает первые минуты.
        return thanks;
    }

    /// <summary>
    /// Корзина магазина. Адрес /cart даёт 404 — настоящая корзина лежит в разделе аккаунта.
    /// </summary>
    private const string CartUrl = "https://assetstore.unity.com/account/cart";

    /// <summary>Номер ассета из адреса вида /packages/.../name-135722. null, если номера нет.</summary>
    public static string? ExtractPackageId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var match = Regex.Match(url, @"/packages/[^?#]*?-(\d+)/?(?:[?#]|$)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Нажимает «Add to Cart» на странице ассета. «Buy Now» не трогает: это Express Purchase.
    ///
    /// Кнопка берётся только у нужного ассета. На странице есть блоки «ещё от автора» и
    /// «похожие», у их карточек тоже своя «Add to Cart»: 19.09 на сервере так в корзину
    /// попал чужой ассет #267305 вместо #321010. Принадлежность определяется по ближайшей
    /// карточке вокруг кнопки: если в ней ссылка на другой ассет — кнопка не наша.
    ///
    /// Возвращает "clicked", "in-cart" (ассет уже в корзине, повторно не кладём, чтобы не
    /// удвоить количество), "none" или "foreign:<номера>" (нашлись только чужие кнопки).
    /// </summary>
    private static async Task<string> TryClickAddToCartOnlyAsync(IPage page, string packageId)
    {
        return await page.EvaluateFunctionAsync<string>(@"(wanted) => {
            const normalize = (v) => (v || '').replace(/\s+/g, ' ').trim().toLowerCase();
            const visible = (el) => {
                if (!el) return false;
                const style = window.getComputedStyle(el);
                const rect = el.getBoundingClientRect();
                return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
            };

            const idOf = (href) => {
                const m = (href || '').match(/\/packages\/[^?#]*?-(\d+)\/?(?:[?#]|$)/);
                return m ? m[1] : null;
            };

            // Чей это блок: поднимаемся от кнопки, пока не встретим ссылки на ассеты.
            // Одна ссылка — это карточка одного ассета. Несколько — общий блок страницы,
            // выше подниматься незачем.
            const ownerOf = (el) => {
                let node = el;
                while (node && node !== document.body) {
                    const ids = Array.from(new Set(
                        Array.from(node.querySelectorAll('a[href*=""/packages/""]'))
                            .map(a => idOf(a.getAttribute('href')))
                            .filter(Boolean)));
                    if (ids.length === 1) return ids[0];
                    if (ids.length > 1) return null;
                    node = node.parentElement;
                }
                return null;
            };

            const foreign = new Set();
            const mine = (el) => {
                const owner = ownerOf(el);
                if (owner === null || owner === wanted) return true;
                foreign.add(owner);
                return false;
            };

            const clickables = Array.from(document.querySelectorAll('button, a, [role=""button""]')).filter(visible);
            const textOf = (el) => normalize(el.innerText || el.getAttribute('aria-label') || '');

            const inCart = clickables.some(el => textOf(el).includes('view in cart') && mine(el));
            if (inCart) return 'in-cart';

            const isBuy = (t) => t.includes('buy') || t.includes('express') || t.includes('купить');
            let target = Array.from(document.querySelectorAll('[data-test=""add-to-cart-button""]'))
                .filter(visible)
                .filter(el => !isBuy(textOf(el)))
                .find(mine);
            if (!target) {
                target = clickables.filter(el => {
                    const t = textOf(el);
                    return t === 'add to cart' || t === 'добавить в корзину';
                }).find(mine);
            }
            if (!target) {
                target = clickables.filter(el => {
                    const t = textOf(el);
                    return t.includes('add to cart') && !isBuy(t) && t.length <= 40;
                }).find(mine);
            }

            if (!target) {
                return foreign.size > 0 ? 'foreign:' + Array.from(foreign).join(',') : 'none';
            }
            target.click();
            return 'clicked';
        }", packageId);
    }

    /// <summary>
    /// Ждёт, пока магазин подтвердит добавление в корзину, и по дороге принимает
    /// диалоги лицензии/условий, если они появятся.
    /// </summary>
    private async Task<bool> WaitForAddedToCartAsync(IPage page, TimeSpan timeout)
    {
        var stopAt = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < stopAt)
        {
            try
            {
                if (await TryAcceptAddConfirmationAsync(page))
                {
                    _logger.Info("[Промокод][Шаг 1] Принят диалог с условиями перед добавлением в корзину.");
                }

                var added = await page.EvaluateFunctionAsync<bool>(@"() => {
                    const text = (document.body ? document.body.innerText : '').replace(/\s+/g, ' ').toLowerCase();
                    return text.includes('added to your cart') || text.includes('view in cart') ||
                           text.includes('добавлен в корзину') || text.includes('добавлено в корзину');
                }");

                if (added)
                {
                    return true;
                }
            }
            catch (Exception ex) when (IsTransientPageError(ex))
            {
                // Страница перерисовывается после добавления — просто проверяем ещё раз.
            }

            await Task.Delay(700);
        }

        return false;
    }

    /// <summary>
    /// Читает содержимое корзины на странице /account/cart. Каждой строке ставит метку
    /// data-uad-remove на её кнопку «Remove», чтобы потом нажать именно её.
    /// «Отложенные» ассеты (Saved for Later, у них кнопка «Move to Cart») не считаются.
    /// </summary>
    private async Task<CartSnapshot> ReadCartAsync(IPage page)
    {
        var json = await page.EvaluateFunctionAsync<string>(@"() => {
            const normalize = (v) => (v || '').replace(/\s+/g, ' ').trim().toLowerCase();
            const visible = (el) => {
                if (!el) return false;
                const style = window.getComputedStyle(el);
                const rect = el.getBoundingClientRect();
                return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
            };
            const packageIdOf = (href) => {
                const m = (href || '').match(/\/packages\/[^?#]*?-(\d+)\/?(?:[?#]|$)/);
                return m ? m[1] : null;
            };
            const linkIds = (root) => Array.from(new Set(
                Array.from(root.querySelectorAll('a[href*=""/packages/""]'))
                    .map(a => packageIdOf(a.getAttribute('href')))
                    .filter(Boolean)));

            // Кнопка «Remove» — это иконка с подписью. Берём самый глубокий элемент
            // с этой подписью: клик по нему всплывёт до обработчика кнопки.
            const isRemoveText = (el) => {
                const t = normalize(el.innerText);
                const a = normalize(el.getAttribute('aria-label'));
                return t === 'remove' || t === 'удалить' || a === 'remove' || a === 'удалить';
            };
            const candidates = Array.from(document.querySelectorAll('button, a, [role=""button""], span, div, p'))
                .filter(visible)
                .filter(isRemoveText);
            const innermost = candidates.filter(el => !candidates.some(o => o !== el && el.contains(o)));

            const items = [];
            const seen = new Set();
            for (const btn of innermost) {
                let row = btn.parentElement;
                let ids = [];
                while (row && row !== document.body) {
                    ids = linkIds(row);
                    if (ids.length > 0) break;
                    row = row.parentElement;
                }
                if (!row || row === document.body || ids.length !== 1) continue;

                const rowText = normalize(row.innerText);
                if (rowText.includes('move to cart') || rowText.includes('переместить в корзину')) continue;

                const id = ids[0];
                if (seen.has(id)) continue;
                seen.add(id);

                const name = Array.from(row.querySelectorAll('a[href*=""/packages/""]'))
                    .map(a => (a.innerText || '').replace(/\s+/g, ' ').trim())
                    .find(t => t.length > 0) || '';
                btn.setAttribute('data-uad-remove', id);
                items.push({ id, name: name.slice(0, 80) });
            }

            const body = normalize(document.body ? document.body.innerText : '');
            const empty = ['your shopping cart is empty', 'cart is empty', 'no items in your cart', 'корзина пуста', 'в корзине нет']
                .some(t => body.includes(t));
            return JSON.stringify({ empty, items });
        }");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var snapshot = new CartSnapshot { Empty = root.GetProperty("empty").GetBoolean() };
        foreach (var item in root.GetProperty("items").EnumerateArray())
        {
            snapshot.Items.Add(new CartItemInfo
            {
                Id = item.GetProperty("id").GetString() ?? string.Empty,
                Name = item.GetProperty("name").GetString() ?? string.Empty
            });
        }

        return snapshot;
    }

    /// <summary>
    /// Открывает корзину и ждёт, пока она прогрузится: появятся строки ассетов
    /// или надпись о пустой корзине. null, если корзина так и не показалась.
    /// </summary>
    private async Task<CartSnapshot?> OpenCartAsync(IPage page)
    {
        await SafeGoToAsync(page, CartUrl);

        var stopAt = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < stopAt)
        {
            try
            {
                var cart = await ReadCartAsync(page);
                if (cart.Items.Count > 0 || cart.Empty)
                {
                    // Строки приходят одним ответом сервера, но дорисовываются не мгновенно.
                    await Task.Delay(1000);
                    return await ReadCartAsync(page);
                }
            }
            catch (Exception ex) when (IsTransientPageError(ex))
            {
            }

            await Task.Delay(700);
        }

        _logger.Warn($"[Корзина] Корзина не прогрузилась за 25с | URL: {page.Url}");
        return null;
    }

    /// <summary>
    /// Убирает из корзины все ассеты, кроме keepPackageId (null — убирает все).
    /// Удаляет по одному: пока магазин обрабатывает одно удаление, остальные
    /// кнопки у него заблокированы, и нажатие на них пропадает.
    /// Возвращает то, что осталось в корзине, или null, если корзина не открылась.
    /// </summary>
    private async Task<CartSnapshot?> RemoveCartItemsAsync(IPage page, string? keepPackageId)
    {
        var cart = await OpenCartAsync(page);
        if (cart is null)
        {
            return null;
        }

        _logger.Info($"[Корзина] Сейчас в корзине: {cart.Items.Count} шт." +
                     (cart.Items.Count > 0 ? $" ({string.Join("; ", cart.Items.Select(i => i.Describe()))})" : string.Empty));

        for (var attempt = 0; attempt < 30; attempt++)
        {
            var victim = cart.Items.FirstOrDefault(i => i.Id != keepPackageId);
            if (victim is null)
            {
                return cart;
            }

            var clicked = await page.EvaluateFunctionAsync<bool>(@"(id) => {
                const el = document.querySelector(`[data-uad-remove=""${id}""]`);
                if (!el) return false;
                el.click();
                return true;
            }", victim.Id);

            if (!clicked)
            {
                _logger.Warn($"[Корзина] Не нашли кнопку Remove у {victim.Describe()}");
                return cart;
            }

            // Ждём, пока строка исчезнет.
            var gone = false;
            var stopAt = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < stopAt)
            {
                await Task.Delay(600);
                try
                {
                    cart = await ReadCartAsync(page);
                }
                catch (Exception ex) when (IsTransientPageError(ex))
                {
                    continue;
                }

                if (cart.Items.All(i => i.Id != victim.Id))
                {
                    gone = true;
                    break;
                }
            }

            if (!gone)
            {
                _logger.Warn($"[Корзина] {victim.Describe()} не убирается из корзины.");
                return cart;
            }

            _logger.Info($"[Корзина] Убран из корзины: {victim.Describe()}");
        }

        return cart;
    }

    /// <summary>Убирает из корзины всё, что там лежит. Ошибки не пробрасывает.</summary>
    private async Task ClearCartAsync(IPage page)
    {
        try
        {
            _logger.Info($"[Очистка корзины] Открываем {CartUrl} и убираем из неё ассеты...");
            var cart = await RemoveCartItemsAsync(page, keepPackageId: null);

            if (cart is null)
            {
                _logger.Warn("[Очистка корзины] Корзина не открылась. Проверьте её вручную: " + CartUrl);
            }
            else if (cart.Items.Count > 0)
            {
                _logger.Warn($"[Очистка корзины] В корзине осталось: {cart.Items.Count}. Проверьте её вручную: {CartUrl}");
            }
            else
            {
                _logger.Info("[Очистка корзины] Корзина пуста.");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Очистка корзины] Не удалось очистить корзину: {ex.Message}");
        }
    }

    /// <summary>
    /// Нажимает «Checkout» на странице корзины и ждёт переход на pay.unity.com.
    /// По дороге принимает диалог с условиями магазина, если он появится.
    /// </summary>
    private async Task<bool> ProceedToCheckoutFromCartAsync(IPage page, TimeSpan timeout)
    {
        const string clickCheckoutJs = @"() => {
            const normalize = (v) => (v || '').replace(/\s+/g, ' ').trim().toLowerCase();
            const visible = (el) => {
                if (!el) return false;
                const style = window.getComputedStyle(el);
                const rect = el.getBoundingClientRect();
                return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
            };

            let btn = Array.from(document.querySelectorAll('[data-test=""checkout-button""]')).find(visible);
            if (!btn) {
                btn = Array.from(document.querySelectorAll('button, a, [role=""button""]')).filter(visible).find(el => {
                    const t = normalize(el.innerText);
                    return t === 'checkout' || t === 'proceed to checkout' || t === 'оформить заказ';
                });
            }

            if (!btn) return 'none';
            if (btn.disabled || btn.getAttribute('aria-disabled') === 'true') return 'disabled';
            btn.click();
            return 'clicked';
        }";

        var state = await page.EvaluateFunctionAsync<string>(clickCheckoutJs);
        if (state == "disabled")
        {
            // Кнопка неактивна, если ассет в корзине не отмечен галочкой. Отмечаем.
            _logger.Info("[Промокод][Шаг 3] Кнопка Checkout неактивна. Отмечаем ассет в корзине галочкой...");
            await page.EvaluateFunctionAsync(@"() => {
                for (const cb of document.querySelectorAll('input[type=""checkbox""]')) {
                    if (!cb.checked) cb.click();
                }
            }");
            await Task.Delay(1000);
            state = await page.EvaluateFunctionAsync<string>(clickCheckoutJs);
        }

        _logger.Info($"[Промокод][Шаг 3] кнопка Checkout: {state}");
        if (state != "clicked")
        {
            return false;
        }

        // По дороге на pay.unity.com Unity проводит через вход: api.unity.com/v1/oauth2/authorize
        // и обратно на pay.unity.com/.../auth/login_callback. Адрес pay.unity.com при этом
        // стоит в параметрах ссылки входа, поэтому смотрим на сам сайт, а не на текст адреса.
        var stopAt = DateTime.UtcNow.Add(timeout);
        var lastUrl = string.Empty;
        var loginTried = false;

        while (DateTime.UtcNow < stopAt)
        {
            var url = page.Url;
            if (url != lastUrl)
            {
                _logger.Info($"[Промокод][Шаг 3] Сейчас открыто: {ShortUrl(url)}");
                lastUrl = url;
            }

            if (IsPayCheckoutPage(url))
            {
                return true;
            }

            if (url.Contains("assetstore.unity.com/error", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warn($"[Промокод][Шаг 3] Магазин показал страницу ошибки: {url}");
                return false;
            }

            try
            {
                if (HostOf(url) == "login.unity.com")
                {
                    // Unity просит войти заново перед оплатой.
                    if (HasCredentials)
                    {
                        if (!loginTried)
                        {
                            _logger.Info("[Промокод][Шаг 3] Unity просит войти перед оплатой. Вводим email и пароль профиля...");
                            loginTried = true;
                        }

                        if (await TryAutoLoginStepAsync(page) == AutoLoginOutcome.GaveUp)
                        {
                            return false;
                        }
                    }
                    else if (!loginTried)
                    {
                        loginTried = true;
                        _logger.Warn("[Промокод][Шаг 3] Unity просит войти перед оплатой. Войдите в окне браузера — программа подождёт.");
                        stopAt = DateTime.UtcNow.AddMilliseconds(_options.AuthTimeoutMs);
                    }
                }
                else if (HostOf(url) == "assetstore.unity.com" &&
                         await TryAcceptAddConfirmationAsync(page))
                {
                    _logger.Info("[Промокод][Шаг 3] Принят диалог с условиями магазина.");
                }
            }
            catch (Exception ex) when (IsTransientPageError(ex))
            {
                // Идёт переход на следующую страницу — это и нужно.
            }

            await Task.Delay(700);
        }

        return IsPayCheckoutPage(page.Url);
    }

    /// <summary>Сайт из адреса, например "pay.unity.com". Пусто, если адрес не разобрать.</summary>
    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : string.Empty;

    /// <summary>
    /// Открыта ли сама страница оформления на pay.unity.com. Промежуточный адрес
    /// входа (…/auth/login_callback) ещё не она.
    /// </summary>
    private static bool IsPayCheckoutPage(string url) =>
        HostOf(url) == "pay.unity.com" &&
        !url.Contains("/auth/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Находит поле промокода (при нужде раскрывает его ссылкой «Have a promo code?»)
    /// и вводит код с клавиатуры, как человек. Прямая запись в value React-форма
    /// не замечает и стирает.
    /// </summary>
    private async Task<bool> TryFillPromoCodeAsync(IPage page, string promoCode)
    {
        var found = await page.EvaluateFunctionAsync<bool>(@"async () => {
            const wait = (ms) => new Promise(r => setTimeout(r, ms));
            const normalize = (v) => (v || '').replace(/\s+/g, ' ').trim().toLowerCase();
            const visible = (el) => {
                if (!el) return false;
                const style = window.getComputedStyle(el);
                const rect = el.getBoundingClientRect();
                return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
            };
            const isTextInput = (el) => {
                const type = normalize(el.type);
                return !type || type === 'text' || type === 'search';
            };
            const isPayLike = (t) => t.includes('pay') || t.includes('order') || t.includes('purchase') ||
                t.includes('оплат') || t.includes('заказ') || t.includes('купить');

            // Блок купона на pay.unity.com: .summary-coupon / .order-promotion,
            // в нём dt с заголовком и зелёной надписью, dd.input с полем и кнопкой.
            const couponBoxes = () => Array.from(document.querySelectorAll(
                '.summary-coupon, .order-promotion, [class*=""coupon"" i]'));

            const inputInBoxes = () => {
                for (const box of couponBoxes()) {
                    const input = Array.from(box.querySelectorAll('input')).filter(visible).find(isTextInput);
                    if (input) return input;
                }
                return null;
            };

            const inputByAttributes = () => Array.from(document.querySelectorAll('input')).filter(visible).find(el => {
                if (!isTextInput(el)) return false;

                const placeholder = normalize(el.placeholder);
                const name = normalize(el.name);
                const id = normalize(el.id);
                const aria = normalize(el.getAttribute('aria-label'));
                const all = [placeholder, name, id, aria].join(' ');

                const isOtherField = ['postal', 'zip', 'address', 'phone', 'city', 'state', 'country', 'company',
                    'email', 'card', 'cvc', 'expir', 'vat', 'tax'].some(w => all.includes(w)) ||
                    name.includes('name') || id.includes('name');
                if (isOtherField) return false;

                return ['coupon', 'promo', 'code', 'voucher', 'discount', 'credit',
                    'купон', 'промо', 'код', 'скидк'].some(w => all.includes(w));
            });

            // Поле без говорящих атрибутов узнаём по подписи рядом:
            // 'Enter Coupon/Credit Code to update your price'.
            const couponWords = ['coupon', 'promo', 'voucher', 'discount', 'credit code', 'купон', 'промокод', 'промо-код', 'код скидки'];
            const inputByLabel = () => Array.from(document.querySelectorAll('input')).filter(visible).find(el => {
                if (!isTextInput(el)) return false;
                const texts = [];
                if (el.id) {
                    const l = document.querySelector(`label[for=""${CSS.escape(el.id)}""]`);
                    if (l) texts.push(l.innerText);
                }
                if (el.closest('label')) texts.push(el.closest('label').innerText);
                const by = el.getAttribute('aria-labelledby');
                if (by) by.split(/\s+/).forEach(id => { const l = document.getElementById(id); if (l) texts.push(l.innerText); });
                // Подпись, стоящая прямо перед полем или перед его обёрткой.
                for (let node = el, i = 0; node && i < 3; node = node.parentElement, i++) {
                    const prev = node.previousElementSibling;
                    if (prev && normalize(prev.innerText).length <= 80) texts.push(prev.innerText);
                }
                const all = normalize(texts.join(' '));
                return couponWords.some(w => all.includes(w));
            });

            const findInput = () => inputInBoxes() || inputByAttributes() || inputByLabel();

            let input = findInput();
            if (!input) {
                // Поле спрятано, пока не нажмёшь надпись вроде «Добавить» или «Have a promo code?».
                // Сначала пробуем кликабельное в заголовке блока купона, потом надписи по тексту.
                const inBoxes = couponBoxes().flatMap(box =>
                    Array.from(box.querySelectorAll('dt label, dt a, dt button, dt [role=""button""], label, a, button, [role=""button""]')))
                    .filter(el => !el.closest('.coupon-item, .appended'));

                const byText = Array.from(document.querySelectorAll('button, a, [role=""button""], span, div, p, label, dt'))
                    .filter(visible)
                    .filter(el => {
                        const t = normalize(el.innerText);
                        return t.length > 0 && t.length <= 60 &&
                            (t.includes('promo code') || t.includes('coupon') || t.includes('discount code') ||
                             t.includes('промокод') || t.includes('купон') || t.includes('код скидки') ||
                             t.includes('промо-код'));
                    });
                const byTextInnermost = byText.filter(el => !byText.some(o => o !== el && el.contains(o)));

                const toggles = [...new Set([...inBoxes, ...byTextInnermost])]
                    .filter(visible)
                    .filter(el => !isPayLike(normalize(el.innerText)));

                for (const toggle of toggles.slice(0, 6)) {
                    toggle.click();
                    await wait(1000);
                    input = findInput();
                    if (input) break;
                }
            }

            if (!input) return false;
            document.querySelectorAll('[data-uad-promo]').forEach(el => el.removeAttribute('data-uad-promo'));
            input.setAttribute('data-uad-promo', '1');
            input.scrollIntoView({ block: 'center' });
            return true;
        }");

        if (!found)
        {
            return false;
        }

        var input = await page.QuerySelectorAsync("[data-uad-promo='1']");
        if (input is null)
        {
            return false;
        }

        await input.ClickAsync(new PuppeteerSharp.Input.ClickOptions { Count = 3 });
        await page.Keyboard.PressAsync("Backspace");
        await input.TypeAsync(promoCode, new PuppeteerSharp.Input.TypeOptions { Delay = 40 });

        var typed = await page.EvaluateFunctionAsync<string>(
            "() => (document.querySelector('[data-uad-promo=\"1\"]') || {}).value || ''");
        if (!string.Equals(typed.Trim(), promoCode, StringComparison.OrdinalIgnoreCase))
        {
            _logger.Debug($"[Промокод][Шаг 5] В поле оказалось '{typed}' вместо '{promoCode}'. Вписываем напрямую.");
            await ForcePromoInputValueAsync(page, promoCode);
        }

        return true;
    }

    /// <summary>Вписывает код в поле через родной сеттер value — так его замечает и React.</summary>
    private static async Task ForcePromoInputValueAsync(IPage page, string promoCode)
    {
        await page.EvaluateFunctionAsync(@"(code) => {
            const el = document.querySelector('[data-uad-promo=""1""]');
            if (!el) return;
            const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
            setter.call(el, code);
            el.dispatchEvent(new Event('input', { bubbles: true }));
            el.dispatchEvent(new Event('change', { bubbles: true }));
        }", promoCode);
    }

    /// <summary>
    /// Вопрос «Tax Business use» на pay.unity.com: всегда отвечаем «No».
    /// «Yes» — только для юрлиц и ИП с российским ИНН. С «Yes» страница требует адрес
    /// организации, не считает налог и итог, а на промокод отвечает
    /// «Organization address info is not complete or correct».
    ///
    /// Ищем «No» только рядом с самим вопросом, жмём настоящим кликом мыши
    /// и проверяем, что выбор сохранился. Возвращает true, если «No» выбрано
    /// или вопроса на странице нет.
    /// </summary>
    private async Task<bool> EnsureTaxBusinessUseNoAsync(IPage page, string step)
    {
        const string findJs = @"() => {
            const normalize = (v) => (v || '').replace(/\s+/g, ' ').trim().toLowerCase();
            const visible = (el) => {
                if (!el) return false;
                const style = window.getComputedStyle(el);
                const rect = el.getBoundingClientRect();
                return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
            };
            const isNo = (t) => t === 'no' || t === 'нет';
            const isYes = (t) => t === 'yes' || t === 'да';

            for (const attr of ['data-uad-tax-no', 'data-uad-tax-no-input', 'data-uad-tax-yes-input']) {
                document.querySelectorAll(`[${attr}]`).forEach(el => el.removeAttribute(attr));
            }

            const questionRe = /tax\s*business\s*use|business use|предпринимател|юридическ|коммерческ/;
            const mentions = Array.from(document.querySelectorAll('body *'))
                .filter(el => !['SCRIPT', 'STYLE', 'NOSCRIPT'].includes(el.tagName))
                .filter(el => {
                    const t = normalize(el.innerText);
                    return t.length > 0 && t.length < 600 && questionRe.test(t);
                });
            const questions = mentions.filter(el => !mentions.some(o => o !== el && el.contains(o)));
            if (questions.length === 0) return JSON.stringify({ found: false });

            const labelOf = (input) => {
                let t = '';
                if (input.id) {
                    const l = document.querySelector(`label[for=""${CSS.escape(input.id)}""]`);
                    if (l) t = l.innerText;
                }
                if (!t && input.closest('label')) t = input.closest('label').innerText;
                if (!t && input.nextElementSibling) t = input.nextElementSibling.innerText;
                if (!t) t = input.getAttribute('aria-label') || '';
                return normalize(t);
            };

            for (const q of questions) {
                // Поднимаемся от текста вопроса, пока рядом не найдутся варианты ответа.
                let box = q;
                for (let depth = 0; box && box !== document.body && depth < 6; depth++, box = box.parentElement) {
                    const radios = Array.from(box.querySelectorAll('input[type=""radio""]'));
                    const noInput = radios.find(r => isNo(labelOf(r))) || null;
                    const yesInput = radios.find(r => isYes(labelOf(r))) || null;

                    const noTexts = Array.from(box.querySelectorAll('label, button, [role=""radio""], [role=""button""], [role=""option""], li, span, div, a'))
                        .filter(visible)
                        .filter(el => isNo(normalize(el.innerText)));
                    const noEls = noTexts.filter(el => !noTexts.some(o => o !== el && el.contains(o)));

                    if (!noInput && noEls.length === 0) continue;

                    let target = noEls[0] || null;
                    if (noInput) {
                        const lbl = (noInput.id && document.querySelector(`label[for=""${CSS.escape(noInput.id)}""]`)) || noInput.closest('label');
                        target = (lbl && visible(lbl)) ? lbl : (visible(noInput) ? noInput : (target || noInput));
                        noInput.setAttribute('data-uad-tax-no-input', '1');
                    }
                    if (yesInput) yesInput.setAttribute('data-uad-tax-yes-input', '1');

                    target.setAttribute('data-uad-tax-no', '1');
                    target.scrollIntoView({ block: 'center' });
                    return JSON.stringify({
                        found: true,
                        alreadyChecked: !!(noInput && noInput.checked),
                        question: normalize(q.innerText).slice(0, 90),
                        target: target.tagName.toLowerCase() + ' ' + normalize(target.innerText).slice(0, 20)
                    });
                }
            }

            return JSON.stringify({ found: false, question: normalize(questions[0].innerText).slice(0, 90) });
        }";

        const string verifyJs = @"() => {
            const input = document.querySelector('[data-uad-tax-no-input]');
            const yes = document.querySelector('[data-uad-tax-yes-input]');
            const target = document.querySelector('[data-uad-tax-no]');
            if (input) return input.checked && !(yes && yes.checked) ? 'checked' : 'not-checked';
            if (!target) return 'lost';

            for (let el = target, i = 0; el && i < 3; el = el.parentElement, i++) {
                if (['aria-checked', 'aria-pressed', 'aria-selected'].some(a => el.getAttribute(a) === 'true')) return 'checked';
                const cls = (typeof el.className === 'string' ? el.className : '').toLowerCase();
                if (/(^|[\s_-])(active|selected|checked|is-checked|is-selected)([\s_-]|$)/.test(cls)) return 'checked';
                const inner = el.querySelector && el.querySelector('input[type=""radio""]');
                if (inner) return inner.checked ? 'checked' : 'not-checked';
            }
            return 'unknown';
        }";

        // Вопрос может дорисоваться не сразу после загрузки страницы.
        var stopAt = DateTime.UtcNow.AddSeconds(8);
        TaxQuestionInfo? info = null;
        while (true)
        {
            try
            {
                info = JsonSerializer.Deserialize<TaxQuestionInfo>(
                    await page.EvaluateFunctionAsync<string>(findJs), _runtimeJsonOptions);
            }
            catch (Exception ex) when (IsTransientPageError(ex))
            {
                info = null;
            }

            if (info?.Found == true || DateTime.UtcNow >= stopAt)
            {
                break;
            }

            await Task.Delay(700);
        }

        if (info?.Found != true)
        {
            _logger.Info(string.IsNullOrWhiteSpace(info?.Question)
                ? $"[Промокод][{step}] Вопроса 'Tax Business use' на странице нет."
                : $"[Промокод][{step}] Вопрос есть, но вариант 'No' рядом не найден: '{info.Question}'");
            return string.IsNullOrWhiteSpace(info?.Question);
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (info!.AlreadyChecked)
            {
                _logger.Info($"[Промокод][{step}] Tax Business use: уже выбрано 'No'.");
                return true;
            }

            // Настоящий клик мышью надёжнее всего для самодельных переключателей.
            // Если элемент спрятан и мышью не нажимается — кликаем из скрипта.
            try
            {
                var handle = await page.QuerySelectorAsync("[data-uad-tax-no='1']");
                if (handle is null)
                {
                    throw new InvalidOperationException("вариант 'No' пропал со страницы");
                }

                await handle.ClickAsync();
            }
            catch (Exception ex)
            {
                _logger.Debug($"[Промокод][{step}] Клик мышью по 'No' не прошёл ({ex.Message}), нажимаем из скрипта.");
                await page.EvaluateFunctionAsync(@"() => {
                    const input = document.querySelector('[data-uad-tax-no-input]');
                    const target = document.querySelector('[data-uad-tax-no]');
                    if (target) target.click();
                    if (input && !input.checked) input.click();
                }");
            }

            await Task.Delay(1000);

            string state;
            try
            {
                state = await page.EvaluateFunctionAsync<string>(verifyJs);
            }
            catch (Exception ex) when (IsTransientPageError(ex))
            {
                state = "lost";
            }

            if (state == "checked")
            {
                _logger.Info($"[Промокод][{step}] Tax Business use: выбрано 'No' ({info.Target}), выбор проверен.");
                return true;
            }

            if (state == "unknown")
            {
                _logger.Info($"[Промокод][{step}] Tax Business use: нажато 'No' ({info.Target}). Проверить выбор по разметке не удалось.");
                return true;
            }

            _logger.Info($"[Промокод][{step}] Tax Business use: после нажатия 'No' выбор не виден ({state}), пробуем ещё раз ({attempt}/3)...");

            // Страница могла перерисоваться: ищем вопрос заново.
            try
            {
                info = JsonSerializer.Deserialize<TaxQuestionInfo>(
                    await page.EvaluateFunctionAsync<string>(findJs), _runtimeJsonOptions);
            }
            catch (Exception ex) when (IsTransientPageError(ex))
            {
                info = null;
            }

            if (info?.Found != true)
            {
                break;
            }
        }

        _logger.Warn($"[Промокод][{step}] Не удалось выбрать 'No' в вопросе 'Tax Business use'.");
        return false;
    }

    /// <summary>
    /// Ставит галки на странице оплаты: согласие с EULA, отказ от 14 дней на возврат
    /// (обязательны для оплаты) и подписку на письма Asset Store.
    /// Возвращает false, если обязательную галку поставить не удалось.
    /// </summary>
    private async Task<bool> EnsureAgreementCheckboxesAsync(IPage page, string step)
    {
        const string findJs = @"() => {
            const normalize = (v) => (v || '').replace(/\s+/g, ' ').trim().toLowerCase();
            const kinds = [
                { key: 'EULA', required: true, re: /end user license|eula|license agreement|лицензионн/ },
                { key: 'отказ от 14 дней', required: true, re: /withdrawal|14-day|14 day|14 дн|отказ/ },
                { key: 'письма Asset Store', required: false, re: /receive resources|updates, and information|via email|by email|рассылк|электронной почте|получать/ }
            ];

            document.querySelectorAll('[data-uad-agree]').forEach(el => el.removeAttribute('data-uad-agree'));
            const boxes = Array.from(document.querySelectorAll('input[type=""checkbox""], [role=""checkbox""]'));

            const labelOf = (cb) => {
                if (cb.id) {
                    const l = document.querySelector(`label[for=""${CSS.escape(cb.id)}""]`);
                    if (l && normalize(l.innerText)) return normalize(l.innerText);
                }
                const closest = cb.closest('label');
                if (closest && normalize(closest.innerText)) return normalize(closest.innerText);
                if (cb.nextElementSibling && normalize(cb.nextElementSibling.innerText)) return normalize(cb.nextElementSibling.innerText);
                // Первый родитель с текстом, если в нём только эта галка.
                for (let p = cb.parentElement, i = 0; p && p !== document.body && i < 4; p = p.parentElement, i++) {
                    const t = normalize(p.innerText);
                    if (!t) continue;
                    return p.querySelectorAll('input[type=""checkbox""], [role=""checkbox""]').length === 1 ? t : '';
                }
                return normalize(cb.getAttribute('aria-label'));
            };

            const result = [];
            boxes.forEach((cb, i) => {
                const text = labelOf(cb);
                const kind = kinds.find(k => k.re.test(text));
                if (!kind) return;
                cb.setAttribute('data-uad-agree', String(i));
                const checked = cb.tagName === 'INPUT' ? cb.checked : cb.getAttribute('aria-checked') === 'true';
                result.push({ Idx: String(i), Key: kind.key, Required: kind.required, Checked: checked, Text: text.slice(0, 70) });
            });
            return JSON.stringify(result);
        }";

        const string isCheckedJs = @"(idx) => {
            const cb = document.querySelector(`[data-uad-agree=""${idx}""]`);
            if (!cb) return false;
            return cb.tagName === 'INPUT' ? cb.checked : cb.getAttribute('aria-checked') === 'true';
        }";

        List<AgreementCheckbox> boxes;
        try
        {
            boxes = JsonSerializer.Deserialize<List<AgreementCheckbox>>(
                await page.EvaluateFunctionAsync<string>(findJs), _runtimeJsonOptions) ?? [];
        }
        catch (Exception ex) when (IsTransientPageError(ex))
        {
            boxes = [];
        }

        if (boxes.Count == 0)
        {
            _logger.Warn($"[Промокод][{step}] Галок согласия (EULA, 14 дней) на странице не нашлось.");
            return true;
        }

        var allRequiredOk = true;
        foreach (var box in boxes)
        {
            var isChecked = box.Checked;

            for (var attempt = 1; attempt <= 2 && !isChecked; attempt++)
            {
                try
                {
                    var handle = await page.QuerySelectorAsync($"[data-uad-agree='{box.Idx}']");
                    if (attempt == 1 && handle is not null)
                    {
                        await handle.ClickAsync();
                    }
                    else
                    {
                        throw new InvalidOperationException("клик мышью не подошёл");
                    }
                }
                catch
                {
                    // Спрятанный input: жмём его подпись или сам input из скрипта.
                    await page.EvaluateFunctionAsync(@"(idx) => {
                        const cb = document.querySelector(`[data-uad-agree=""${idx}""]`);
                        if (!cb) return;
                        const lbl = (cb.id && document.querySelector(`label[for=""${CSS.escape(cb.id)}""]`)) || cb.closest('label');
                        if (lbl) lbl.click(); else cb.click();
                    }", box.Idx);
                }

                await Task.Delay(400);
                isChecked = await page.EvaluateFunctionAsync<bool>(isCheckedJs, box.Idx);
            }

            _logger.Info($"[Промокод][{step}] Галка '{box.Key}': {(isChecked ? "стоит" : "НЕ СТОИТ")} ({box.Text}...)");
            if (box.Required && !isChecked)
            {
                allRequiredOk = false;
            }
        }

        return allRequiredOk;
    }

    /// <summary>Ошибка из-за вопроса о налоге и адресе организации, а не из-за самого кода.</summary>
    private static bool IsTaxOrAddressError(string text) =>
        new[] { "address", "organization", "organisation", "tax", "адрес", "организац", "налог" }
            .Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Страница прямо говорит, что код не годится: истёк, неверный, исчерпан.</summary>
    private static bool IsPromoCodeRejection(string text) =>
        new[]
            {
                "expired", "invalid", "not valid", "no longer", "has ended", "not found", "does not exist",
                "limit", "already", "not applicable", "cannot be applied", "not eligible",
                "истек", "истёк", "недействител", "не найден", "не существует", "исчерпан",
                "не применим", "уже использ", "не действует"
            }
            .Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Нажимает Apply; если кнопка неактивна, вписывает код напрямую и пробует ещё раз.</summary>
    private static async Task<string> ClickPromoApplyAsync(IPage page, string promoCode)
    {
        var state = await TryClickPromoApplyAsync(page);
        if (state == "disabled")
        {
            // Кнопка не ожила — значит, форма не заметила введённый текст.
            await ForcePromoInputValueAsync(page, promoCode);
            await Task.Delay(700);
            state = await TryClickPromoApplyAsync(page);
        }

        return state;
    }

    /// <summary>
    /// Нажимает кнопку Apply рядом с полем промокода. Кнопки оплаты не трогает никогда.
    /// Возвращает "clicked", "disabled" или "none".
    /// </summary>
    private static async Task<string> TryClickPromoApplyAsync(IPage page)
    {
        return await page.EvaluateFunctionAsync<string>(@"() => {
            const normalize = (v) => (v || '').replace(/\s+/g, ' ').trim().toLowerCase();
            const visible = (el) => {
                if (!el) return false;
                const style = window.getComputedStyle(el);
                const rect = el.getBoundingClientRect();
                return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
            };
            const textOf = (el) => normalize(el.innerText || el.value || el.getAttribute('aria-label') || '');
            const isPayLike = (t) => t.includes('pay') || t.includes('order') || t.includes('purchase') ||
                t.includes('оплат') || t.includes('заказ') || t.includes('купить');
            const isApplyLike = (t) => !isPayLike(t) &&
                (t.includes('apply') || t === 'redeem' || t === 'submit' || t === 'ok' || t === 'ок' ||
                 t.includes('применить') || t.includes('активировать') || t === 'добавить' || t === 'add');

            const input = document.querySelector('[data-uad-promo=""1""]');
            let btn = null;

            // На pay.unity.com поле и кнопка стоят вдвоём в строке dd.input.
            const row = input ? input.closest('dd.input') : null;
            if (row) {
                btn = Array.from(row.querySelectorAll('button, .btn, [role=""button""], input[type=""submit""]'))
                    .filter(visible)
                    .find(el => !isPayLike(textOf(el))) || null;
            }

            // Сначала ищем рядом с полем: поднимаемся от него на несколько уровней.
            let parent = input && !btn ? input.parentElement : null;
            for (let depth = 0; parent && parent !== document.body && depth < 6 && !btn; depth++) {
                btn = Array.from(parent.querySelectorAll('button, [role=""button""], input[type=""submit""]'))
                    .filter(visible)
                    .find(el => isApplyLike(textOf(el))) || null;
                parent = parent.parentElement;
            }

            if (!btn) {
                btn = Array.from(document.querySelectorAll('button, [role=""button""]'))
                    .filter(visible)
                    .find(el => {
                        const t = textOf(el);
                        return t === 'apply' || t === 'применить' || t === 'redeem';
                    }) || null;
            }

            if (!btn) return 'none';
            if (btn.disabled || btn.getAttribute('aria-disabled') === 'true') return 'disabled';
            btn.click();
            return 'clicked';
        }");
    }

    /// <summary>
    /// Сообщения рядом с полем промокода. Явные — из блока купона pay.unity.com
    /// (красная строка .error и зачёркнутый код .invalid). Остальные — любой текст,
    /// который виден в обёртке поля; ошибка ли это, решает <see cref="FindCouponError"/>.
    /// </summary>
    private static async Task<List<CouponMessage>> ReadCouponMessagesAsync(IPage page)
    {
        try
        {
            var json = await page.EvaluateFunctionAsync<string>(@"() => {
                const normalize = (v) => (v || '').replace(/\s+/g, ' ').trim();
                const visible = (el) => {
                    if (!el) return false;
                    const style = window.getComputedStyle(el);
                    const rect = el.getBoundingClientRect();
                    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
                };
                const out = [];
                const add = (text, isExplicit) => {
                    const t = normalize(text).slice(0, 300);
                    if (t && !out.some(o => o.Text === t)) out.push({ Text: t, Explicit: isExplicit });
                };

                for (const box of document.querySelectorAll('.summary-coupon, .order-promotion')) {
                    box.querySelectorAll('.error').forEach(e => { if (visible(e)) add(e.innerText, true); });
                    const invalid = box.querySelector('.coupon-item span.invalid, .coupon-item .invalid');
                    if (invalid) add('код зачёркнут как недействительный: ' + normalize(invalid.innerText).slice(0, 60), true);
                }

                // Текст вокруг поля: поднимаемся на пару уровней, но не до формы с кнопкой оплаты.
                const input = document.querySelector('[data-uad-promo=""1""]');
                let box = input ? input.parentElement : null;
                for (let i = 0; box && box !== document.body && i < 3; i++, box = box.parentElement) {
                    const hasPay = Array.from(box.querySelectorAll('button, [role=""button""]'))
                        .some(b => /pay|оплат|order|заказ/i.test(b.innerText || ''));
                    if (hasPay) break;
                    Array.from(box.querySelectorAll('*'))
                        .filter(el => el !== input && el.children.length === 0 && visible(el))
                        .forEach(el => { if (normalize(el.innerText).length >= 8) add(el.innerText, false); });
                }

                return JSON.stringify(out);
            }");

            return JsonSerializer.Deserialize<List<CouponMessage>>(json ?? "[]") ?? [];
        }
        catch (Exception ex) when (IsTransientPageError(ex))
        {
            return [];
        }
    }

    /// <summary>
    /// Первая ошибка среди сообщений у поля промокода. Текст, который был там ещё
    /// до нажатия Apply (подпись поля, старая ошибка), не считается.
    /// </summary>
    private static string? FindCouponError(IEnumerable<CouponMessage> messages, ICollection<string> ignore) =>
        messages
            .Where(m => !ignore.Contains(m.Text))
            .FirstOrDefault(m => m.Explicit || IsCouponErrorText(m.Text))
            ?.Text;

    private static bool IsCouponErrorText(string text) =>
        IsPromoCodeRejection(text) || IsTaxOrAddressError(text) ||
        new[] { "error", "wrong", "failed", "incorrect", "not recognized", "unable", "ошибк", "неверн", "не удалось", "не распознан" }
            .Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Что видно в блоке купона и сколько на странице текстовых полей — для лога,
    /// когда поле промокода не нашлось. Отладочные строки в файл не пишутся, а эта — пишется.
    /// </summary>
    private async Task LogCouponAreaAsync(IPage page)
    {
        try
        {
            var info = await page.EvaluateFunctionAsync<string>(@"() => {
                const normalize = (v) => (v || '').replace(/\s+/g, ' ').trim();
                const boxes = Array.from(document.querySelectorAll('.summary-coupon, .order-promotion, [class*=""coupon"" i]'));
                const boxText = boxes.map(b => `<${b.tagName.toLowerCase()} class='${b.className}'> ${normalize(b.innerText).slice(0, 150)}`).join(' || ');
                const inputs = Array.from(document.querySelectorAll('input'))
                    .map(i => `${i.type || 'text'}#${i.id || '-'}[name=${i.name || '-'}][ph=${i.placeholder || '-'}]${i.offsetParent === null ? '(скрыто)' : ''}`)
                    .join(', ');
                const summary = document.querySelector('.order-summary, .summary');
                return `блок купона: ${boxText || 'не найден'} | поля: ${inputs || 'нет'} | сводка: ${summary ? normalize(summary.innerText).slice(0, 300) : 'нет'}`;
            }");
            _logger.Info($"[Промокод][Шаг 5] Что на странице: {info}");
        }
        catch (Exception ex)
        {
            _logger.Info($"[Промокод][Шаг 5] Не удалось описать страницу: {ex.Message}");
        }
    }

    /// <summary>
    /// Ждёт ответа на промокод: цена стала нулём или на странице появилась ошибка.
    /// Ошибка, которая висела на странице ещё до ввода кода, ответом не считается.
    /// </summary>
    private async Task<CartPriceSnapshot> WaitForPromoOutcomeAsync(
        IPage page, CartPriceSnapshot before, TimeSpan timeout, ICollection<string> ignoreMessages)
    {
        var stopAt = DateTime.UtcNow.Add(timeout);
        await Task.Delay(1500);

        while (true)
        {
            var current = await ReadCartPriceAsync(page);

            if (current.HasPromoError && before.HasPromoError && current.FoundError == before.FoundError)
            {
                current.HasPromoError = false;
            }

            // Ответ магазина у поля промокода: «The code is expired», «Organization address…» и т. п.
            var couponError = FindCouponError(await ReadCouponMessagesAsync(page), ignoreMessages);
            if (couponError is not null)
            {
                current.HasPromoError = true;
                current.FoundError = couponError;
            }

            // Ноль засчитываем, только если рядом нет ошибки: без посчитанного налога
            // страница может показать в итоге что угодно.
            if (current.Found && current.Amount == 0 && couponError is null)
            {
                current.HasPromoError = false;
                return current;
            }

            if (current.HasPromoError || DateTime.UtcNow >= stopAt)
            {
                return current;
            }

            await Task.Delay(1000);
        }
    }

    private async Task<bool> WaitForCartPageElementsAsync(IPage page, TimeSpan timeout)
    {
        var stopAt = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < stopAt)
        {
            try
            {
                var exists = await page.EvaluateFunctionAsync<bool>(@"() => {
                    const normalize = (v) => (v || '').replace(/\s+/g, ' ').trim().toLowerCase();
                    const visible = (el) => {
                        if (!el) return false;
                        const style = window.getComputedStyle(el);
                        const rect = el.getBoundingClientRect();
                        return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
                    };
                    
                    const inputs = Array.from(document.querySelectorAll('input'));
                    const hasPromoInput = inputs.some(el => {
                        if (!visible(el)) return false;
                        const placeholder = normalize(el.placeholder || '');
                        const name = normalize(el.name || '');
                        const id = normalize(el.id || '');
                        
                        const isAddressField = 
                            name.includes('postal') || name.includes('zip') || name.includes('address') || name.includes('phone') || name.includes('city') || name.includes('state') || name.includes('country') || name.includes('company') || name.includes('name') || name.includes('email') ||
                            id.includes('postal') || id.includes('zip') || id.includes('address') || id.includes('phone') || id.includes('city') || id.includes('state') || id.includes('country') || id.includes('company') || id.includes('name') || id.includes('email') ||
                            placeholder.includes('zip') || placeholder.includes('postal') || placeholder.includes('address') || placeholder.includes('phone') || placeholder.includes('city') || placeholder.includes('state') || placeholder.includes('country') || placeholder.includes('email');

                        if (isAddressField) return false;

                        return placeholder.includes('coupon') || placeholder.includes('promo') || placeholder.includes('code') || placeholder.includes('credit') ||
                               placeholder.includes('купон') || placeholder.includes('промо') || placeholder.includes('код') || placeholder.includes('скидк') ||
                               name.includes('coupon') || name.includes('promo') || name.includes('code') ||
                               id.includes('coupon') || id.includes('promo') || id.includes('code');
                    });

                    const buttons = Array.from(document.querySelectorAll('button, a, [role=""button""]'));
                    const hasCheckoutBtn = buttons.some(el => {
                        if (!visible(el)) return false;
                        const txt = normalize(el.innerText || '');
                        return txt.includes('checkout') || txt.includes('place order') || txt.includes('proceed') || txt.includes('order') || txt.includes('pay') || txt.includes('complete purchase') ||
                               txt.includes('оформить') || txt.includes('оплатить') || txt.includes('заплатить') ||
                               txt.includes('купить') || txt.includes('заказать');
                    });

                    return hasPromoInput || hasCheckoutBtn;
                }");

                if (exists) return true;
            }
            catch
            {
            }
            await Task.Delay(1000);
        }
        return false;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task<AssetStatusSnapshot> VerifyPostAddStatusAsync(IPage page, string assetUrl, TimeSpan timeout)
    {
        var stopAt = DateTime.UtcNow.Add(timeout);
        AssetStatusSnapshot? lastStatus = null;
        var refreshAttempt = 0;
        var cycle = 0;

        while (DateTime.UtcNow < stopAt)
        {
            cycle++;
            await Task.Delay(900);
            await WaitForAssetSignalsAsync(page, TimeSpan.FromMilliseconds(Math.Min(_options.AssetUiTimeoutMs, 12000)));
            var current = await DetectStatusAsync(page);
            lastStatus = current;

            _logger.Debug(
                $"PostAddCycle[{cycle}]: openInUnity={current.HasOpenInUnity}, addToMyAssets={current.HasAddToMyAssets}, requiresLogin={current.RequiresLogin}, owned={current.IsOwned}");

            if (current.RequiresLogin || page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            if (current.HasOpenInUnity || current.IsOwned)
            {
                return current;
            }

            if (current.HasAddToMyAssets)
            {
                _logger.Debug($"PostAddCycle[{cycle}]: кнопка Add to My Assets всё ещё видна, повторяем клик...");
                var clickedAdd = await TryClickAddButtonAsync(page);
                if (clickedAdd)
                {
                    await Task.Delay(350);
                    var accepted = await TryAcceptAddConfirmationAsync(page);
                    _logger.Debug($"PostAddCycle[{cycle}]: AcceptFound={accepted}");
                    if (accepted)
                    {
                        _logger.Info("Подтверждение добавления найдено во время проверки: нажата кнопка Accept.");
                    }
                }
                else
                {
                    _logger.Debug($"PostAddCycle[{cycle}]: не удалось кликнуть Add на текущем шаге.");
                }

                refreshAttempt++;
                _logger.Debug(
                    $"PostAddCycle[{cycle}]: Open in Unity ещё не появился. Обновляем страницу ассета (refresh={refreshAttempt})...");
                await SafeGoToAsync(page, assetUrl);
                await WaitForAssetSignalsAsync(page,
                    TimeSpan.FromMilliseconds(Math.Min(_options.AssetUiTimeoutMs, 15000)));
            }
            else
            {
                refreshAttempt++;
                _logger.Debug(
                    $"PostAddCycle[{cycle}]: Add/Open не видны, выполняем контрольный refresh (refresh={refreshAttempt})...");
                await SafeGoToAsync(page, assetUrl);
                await WaitForAssetSignalsAsync(page,
                    TimeSpan.FromMilliseconds(Math.Min(_options.AssetUiTimeoutMs, 15000)));
            }
        }

        return lastStatus ?? await DetectStatusAsync(page);
    }

    private async Task<AssetStatusSnapshot> DetectStatusAsync(IPage page)
    {
        var raw = await EvaluateWithRetryAsync(() => page.EvaluateFunctionAsync<string>(@"() => {
            const normalize = (v) => (v || '').replace(/\s+/g, ' ').trim().toLowerCase();
            const visible = (el) => {
                if (!el) return false;
                const style = window.getComputedStyle(el);
                const rect = el.getBoundingClientRect();
                return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
            };

            const bodyText = document.body?.innerText || '';
            const ctaRootSelectors = [
                '[data-testid*=""cta"" i]',
                '[data-testid*=""purchase"" i]',
                '[class*=""cta"" i]',
                '[class*=""purchase"" i]',
                '[class*=""buy"" i]',
                'aside[class*=""sidebar"" i]',
                'div[class*=""sidebar"" i]'
            ].join(', ');

            const ctaRoots = Array.from(document.querySelectorAll(ctaRootSelectors))
                .filter(visible)
                .filter(root => {
                    const txt = normalize(root.innerText || '');
                    return txt.includes('add to my assets') || txt.includes('open in unity') || txt.includes('buy now') || txt.includes('add to cart') || txt.includes('free');
                });

            const extractTexts = (root) => Array.from(root.querySelectorAll('button, a, [role=""button""]'))
                .filter(visible)
                .map(x => normalize(x.innerText))
                .filter(Boolean)
                .filter(t => t.length <= 80);

            let actionTexts = ctaRoots.flatMap(extractTexts);
            if (actionTexts.length === 0) {
                actionTexts = Array.from(document.querySelectorAll('button, a, [role=""button""]'))
                    .filter(visible)
                    .map(x => normalize(x.innerText))
                    .filter(Boolean)
                    .filter(t => t.length <= 80);
            }

            const isLikelyAction = (t) =>
                t.includes('add to my assets') ||
                t.includes('open in unity') ||
                t.includes('buy now') ||
                t.includes('add to cart') ||
                t === 'sign in' ||
                t === 'log in' ||
                t.includes('sign in to') ||
                t.includes('log in to') ||
                t.includes('owned') ||
                t.includes('in my assets') ||
                t.includes('already owned') ||
                t.includes('already in your assets') ||
                t.includes('free');

            actionTexts = actionTexts.filter(isLikelyAction);
            actionTexts = Array.from(new Set(actionTexts));
            const ctaCombined = actionTexts.join(' | ');

            const hasOpenInUnity = actionTexts.some(t => t.includes('open in unity'));
            const hasAddToMyAssets = actionTexts.some(t => t.includes('add to my assets'));
            const hasBuyNow = actionTexts.some(t => t.includes('buy now'));
            const hasAddToCart = actionTexts.some(t => t.includes('add to cart'));
            const hasOwnedSignals = actionTexts.some(t =>
                t.includes('owned') ||
                t.includes('in my assets') ||
                t.includes('already in your assets'));

            const purchaseMatch = bodyText.match(/you purchased this item on\s+([^\n\r]+)/i);
            const purchasedOnText = purchaseMatch?.[1]?.trim() || null;

            const hasSignInSignals = actionTexts.some(t =>
                t === 'sign in' || t === 'log in' || t.includes('sign in to') || t.includes('log in to')) ||
                ctaCombined.includes('sign in with unity');

            const hasFreeSignals = hasAddToMyAssets ||
                actionTexts.some(t => t.includes('free') || t.includes('$0') || t.includes('0.00'));
            const hasBuySignals = hasBuyNow || (hasAddToCart && !hasAddToMyAssets && !hasFreeSignals);
            const hasPaidSignals = hasBuySignals;

            const isOwned = hasOpenInUnity || hasOwnedSignals || !!purchasedOnText;
            const isFree = (hasAddToMyAssets || hasFreeSignals) && !hasPaidSignals;

            const detectionSummary = [
                `free=${isFree}`,
                `owned=${isOwned}`,
                `addBtn=${hasAddToMyAssets}`,
                `openInUnity=${hasOpenInUnity}`,
                `buySignals=${hasBuySignals}`,
                `paidSignals=${hasPaidSignals}`,
                `loginSignals=${hasSignInSignals}`,
                `purchasedOn=${purchasedOnText ? 'yes' : 'no'}`,
                `ctaButtons=[${actionTexts.join(' || ')}]`
            ].join(', ');

            return JSON.stringify({
                isFree,
                isOwned,
                hasAddToMyAssets,
                hasOpenInUnity,
                requiresLogin: hasSignInSignals,
                purchasedOnText,
                detectionSummary
            });
        }"), "DetectStatus");

        return JsonSerializer.Deserialize<AssetStatusSnapshot>(raw ?? "{}", _runtimeJsonOptions) ??
               new AssetStatusSnapshot();
    }

    private async Task<bool> WaitForAssetSignalsAsync(IPage page, TimeSpan timeout)
    {
        var stopAt = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < stopAt)
        {
            if (page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var hasSignals = await EvaluateWithRetryAsync(() => page.EvaluateFunctionAsync<bool>(@"() => {
                const normalize = (v) => (v || '').replace(/\s+/g, ' ').trim().toLowerCase();
                const visible = (el) => {
                    if (!el) return false;
                    const style = window.getComputedStyle(el);
                    const rect = el.getBoundingClientRect();
                    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
                };

                const rootSelectors = [
                    '[data-testid*=""cta"" i]',
                    '[data-testid*=""purchase"" i]',
                    '[class*=""cta"" i]',
                    '[class*=""purchase"" i]',
                    '[class*=""buy"" i]',
                    'aside[class*=""sidebar"" i]',
                    'div[class*=""sidebar"" i]'
                ].join(', ');

                const roots = Array.from(document.querySelectorAll(rootSelectors))
                    .filter(visible)
                    .filter(root => {
                        const txt = normalize(root.innerText || '');
                        return txt.includes('add to my assets') || txt.includes('open in unity') || txt.includes('buy now') || txt.includes('add to cart') || txt.includes('free');
                    });

                let actions = roots.flatMap(root =>
                    Array.from(root.querySelectorAll('button, a, [role=""button""]'))
                        .filter(visible)
                        .map(x => normalize(x.innerText)));
                if (actions.length === 0) {
                    actions = Array.from(document.querySelectorAll('button, a, [role=""button""]'))
                        .filter(visible)
                        .map(x => normalize(x.innerText));
                }

                actions = actions
                    .filter(Boolean)
                    .filter(t => t.length <= 80)
                    .filter(t =>
                        t.includes('add to my assets') ||
                        t.includes('open in unity') ||
                        t.includes('buy now') ||
                        t.includes('add to cart') ||
                        t.includes('sign in') ||
                        t.includes('log in') ||
                        t.includes('owned') ||
                        t.includes('in my assets') ||
                        t.includes('already owned') ||
                        t.includes('already in your assets') ||
                        t.includes('free'));

                const hasAdd = actions.some(t => t.includes('add to my assets'));
                const hasOpen = actions.some(t => t.includes('open in unity'));
                const hasSignIn = actions.some(t => t.includes('sign in') || t.includes('log in'));
                const hasBuy = actions.some(t => t.includes('buy now') || t.includes('add to cart'));

                return hasAdd || hasOpen || hasSignIn || hasBuy;
            }"), "WaitForAssetSignals");

            if (hasSignals)
            {
                return true;
            }

            await Task.Delay(450);
        }

        return false;
    }

    private static async Task<bool> TryClickSignInWithUnityAsync(IPage page)
    {
        return await page.EvaluateFunctionAsync<bool>(@"() => {
            const actions = Array.from(document.querySelectorAll('button, a, span'));
            for (const element of actions) {
                const txt = (element.innerText || '').trim().toLowerCase();
                if (!txt || !txt.includes('sign in with unity')) continue;

                const clickable = element.closest('button, a') || element;
                clickable.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                return true;
            }

            return false;
        }");
    }

    /// <summary>
    /// Нажимает «Add to My Assets» у ассета, открытого на странице.
    ///
    /// Номер нужного ассета берётся из адреса страницы. Кнопки из карточек других
    /// ассетов («ещё от автора», «похожие», «top free assets») не нажимаются: иначе на
    /// аккаунт уедет не то, что просили, а подтверждения добавления мы так и не увидим.
    /// </summary>
    private static async Task<bool> TryClickAddButtonAsync(IPage page)
    {
        return await page.EvaluateFunctionAsync<bool>(@"() => {
            const normalize = (v) => (v || '').replace(/\s+/g, ' ').trim().toLowerCase();
            const visible = (el) => {
                if (!el) return false;
                const style = window.getComputedStyle(el);
                const rect = el.getBoundingClientRect();
                return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
            };

            const idOf = (href) => {
                const m = (href || '').match(/\/packages\/[^?#]*?-(\d+)\/?(?:[?#]|$)/);
                return m ? m[1] : null;
            };

            // Ассет, открытый на странице. Если номер не разобрался, чужие кнопки не отсеиваем.
            const wanted = idOf(location.pathname);

            const ownerOf = (el) => {
                let node = el;
                while (node && node !== document.body) {
                    const ids = Array.from(new Set(
                        Array.from(node.querySelectorAll('a[href*=""/packages/""]'))
                            .map(a => idOf(a.getAttribute('href')))
                            .filter(Boolean)));
                    if (ids.length === 1) return ids[0];
                    if (ids.length > 1) return null;
                    node = node.parentElement;
                }
                return null;
            };

            const mine = (el) => {
                if (!wanted) return true;
                const owner = ownerOf(el);
                return owner === null || owner === wanted;
            };

            const rootSelectors = [
                '[data-testid*=""cta"" i]',
                '[data-testid*=""purchase"" i]',
                '[class*=""cta"" i]',
                '[class*=""purchase"" i]',
                '[class*=""buy"" i]',
                'aside[class*=""sidebar"" i]',
                'div[class*=""sidebar"" i]'
            ].join(', ');

            const roots = Array.from(document.querySelectorAll(rootSelectors))
                .filter(visible)
                .filter(root => {
                    const txt = normalize(root.innerText || '');
                    return txt.includes('add to my assets') || txt.includes('open in unity') || txt.includes('buy now') || txt.includes('add to cart') || txt.includes('free');
                });

            const collectClickables = (root) => Array.from(root.querySelectorAll('button, a, [role=""button""]'))
                .filter(visible)
                .filter(mine)
                .map(el => ({
                    element: el,
                    text: normalize(el.innerText)
                }))
                .filter(x => !!x.text);

            const clickFrom = (items) => {
                const exactAdd = items.find(x => x.text === 'add to my assets' || x.text === 'add to my assets for free');
                const containsAdd = items.find(x => x.text.includes('add to my assets'));
                const fallbackAddToCart = items.find(x => x.text.includes('add to cart') && x.text.includes('free'));
                const target = exactAdd || containsAdd || fallbackAddToCart;

                if (!target) return false;
                target.element.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                return true;
            };

            for (const root of roots) {
                const items = collectClickables(root);
                if (clickFrom(items)) return true;
            }

            const fallback = Array.from(document.querySelectorAll('button, a, [role=""button""]'))
                .filter(visible)
                .filter(mine)
                .map(el => ({ element: el, text: normalize(el.innerText) }))
                .filter(x => !!x.text);

            return clickFrom(fallback);
        }");
    }

    private static async Task<bool> TryAcceptAddConfirmationAsync(IPage page)
    {
        return await page.EvaluateFunctionAsync<bool>(@"async () => {
            const wait = (ms) => new Promise(r => setTimeout(r, ms));
            const normalize = (v) => (v || '').replace(/\s+/g, ' ').trim().toLowerCase();
            const visible = (el) => {
                if (!el) return false;
                const style = window.getComputedStyle(el);
                const rect = el.getBoundingClientRect();
                return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
            };

            const simulateClick = (el) => {
                if (!el) return;
                const events = ['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click'];
                for (const ev of events) {
                    el.dispatchEvent(new MouseEvent(ev, { bubbles: true, cancelable: true, view: window }));
                }
            };

            for (let i = 0; i < 8; i++) {
                const dialogs = Array.from(document.querySelectorAll('[role=""dialog""], [aria-modal=""true""], [class*=""modal"" i], [class*=""dialog"" i], [class*=""popup"" i], [class*=""overlay"" i]'))
                    .filter(visible);

                for (const dialog of dialogs) {
                    const checkboxes = Array.from(dialog.querySelectorAll('input[type=""checkbox""], [role=""checkbox""]'));
                    let checkedAny = false;
                    for (const cb of checkboxes) {
                        const isInputChecked = cb.tagName.toLowerCase() === 'input' && cb.checked;
                        const isAriaChecked = cb.getAttribute('aria-checked') === 'true';
                        
                        if (!isInputChecked && !isAriaChecked) {
                            const label = cb.closest('label');
                            if (label && visible(label)) {
                                simulateClick(label);
                            } else if (visible(cb)) {
                                simulateClick(cb);
                            } else {
                                const nextElem = cb.nextElementSibling;
                                if (nextElem && visible(nextElem)) {
                                    simulateClick(nextElem);
                                } else {
                                    simulateClick(cb);
                                }
                            }
                            
                            if (cb.tagName.toLowerCase() === 'input') {
                                cb.checked = true;
                                cb.dispatchEvent(new Event('change', { bubbles: true }));
                            }
                            checkedAny = true;
                        }
                    }

                    if (checkedAny) {
                        await wait(500); // wait for UI to update enabled state of the Accept button
                    }

                    const dialogText = normalize(dialog.innerText || '');
                    const candidates = Array.from(dialog.querySelectorAll('button, a, [role=""button""]')).filter(visible);
                    const candidateTexts = candidates.map(x => normalize(x.innerText));

                    const hasAccept = candidateTexts.some(t => t === 'accept' || t.startsWith('accept'));
                    const hasCancelLike = candidateTexts.some(t => t.includes('cancel') || t.includes('decline') || t.includes('close') || t.includes('back'));
                    const looksLikeAssetConfirmation = dialogText.includes('license') || dialogText.includes('agreement') || dialogText.includes('terms') || dialogText.includes('eula') || dialogText.includes('unity') || dialogText.includes('asset');

                    if (!hasAccept) continue;
                    if (!hasCancelLike && !looksLikeAssetConfirmation) continue;

                    for (const element of candidates) {
                        const txt = normalize(element.innerText);
                        if (!txt) continue;
                        if (!txt.startsWith('accept')) continue;

                        const isDisabled = element.disabled || 
                                           element.getAttribute('aria-disabled') === 'true' || 
                                           element.classList.contains('disabled') ||
                                           element.classList.contains('btn-disabled');
                        
                        if (isDisabled) {
                            continue; // Cannot click yet, loop and wait
                        }

                        simulateClick(element);
                        return true;
                    }
                }

                await wait(250);
            }

            return false;
        }");
    }

    private async Task SaveErrorScreenshotAsync(IPage page, string prefix)
    {
        try
        {
            var path = Path.Combine(_logsDirectory, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            await page.ScreenshotAsync(path, new ScreenshotOptions { FullPage = false });
        }
        catch
        {
            // игнорируем вторичные ошибки
        }
    }

    /// <summary>
    /// Сохраняет полный HTML страницы в logs/*.html для постдиагностики.
    /// Вызывать при любой ошибке в промокод-флоу.
    /// </summary>
    private async Task SaveHtmlDumpAsync(IPage page, string prefix)
    {
        try
        {
            var path = Path.Combine(_logsDirectory, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.html");
            var html = await page.GetContentAsync();
            await File.WriteAllTextAsync(path, html);
            _logger.Debug($"[HTML-дамп] Сохранён: {Path.GetFileName(path)} ({html.Length:N0} байт) | URL: {page.Url}");
        }
        catch (Exception ex)
        {
            _logger.Debug($"[HTML-дамп] Не удалось сохранить ({prefix}): {ex.Message}");
        }
    }

    /// <summary>
    /// Логирует все input-поля страницы — для диагностики когда нужное поле не найдено.
    /// </summary>
    private async Task LogAllInputFieldsAsync(IPage page, string context)
    {
        try
        {
            var inputsJson = await page.EvaluateFunctionAsync<string>(@"() => {
                const visible = (el) => {
                    if (!el) return false;
                    const s = window.getComputedStyle(el);
                    const r = el.getBoundingClientRect();
                    return s.display !== 'none' && s.visibility !== 'hidden' && r.width > 0 && r.height > 0;
                };
                const inputs = Array.from(document.querySelectorAll('input'));
                return JSON.stringify(inputs.map(el => ({
                    type: el.type || '',
                    name: el.name || '',
                    id: el.id || '',
                    placeholder: el.placeholder || '',
                    value: (el.value || '').substring(0, 40),
                    visible: visible(el)
                })));
            }");
            _logger.Debug($"[{context}] Все <input> на странице ({page.Url}): {inputsJson}");
        }
        catch (Exception ex)
        {
            _logger.Debug($"[{context}] Не удалось получить список <input>: {ex.Message}");
        }
    }

    /// <summary>
    /// Логирует все видимые кнопки страницы — для диагностики когда кнопка не найдена.
    /// </summary>
    private async Task LogAllButtonsAsync(IPage page, string context)
    {
        try
        {
            var btnsJson = await page.EvaluateFunctionAsync<string>(@"() => {
                const visible = (el) => {
                    if (!el) return false;
                    const s = window.getComputedStyle(el);
                    const r = el.getBoundingClientRect();
                    return s.display !== 'none' && s.visibility !== 'hidden' && r.width > 0 && r.height > 0;
                };
                const btns = Array.from(document.querySelectorAll('button, a, [role=""button""]'))
                    .filter(visible)
                    .map(el => ({ tag: el.tagName, text: (el.innerText || '').trim().substring(0, 60) }))
                    .filter(x => x.text);
                return JSON.stringify(btns);
            }");
            _logger.Debug($"[{context}] Видимые кнопки ({page.Url}): {btnsJson}");
        }
        catch (Exception ex)
        {
            _logger.Debug($"[{context}] Не удалось получить список кнопок: {ex.Message}");
        }
    }

    private void PrintSummary(RunReport report)
    {
        var groups = report.Items.GroupBy(x => x.Status).ToDictionary(g => g.Key, g => g.Count());

        _logger.Info("============================================================");
        _logger.Info($" ИТОГИ. Профиль: {_profileName}");
        _logger.Info("============================================================");

        foreach (AssetProcessStatus status in Enum.GetValues<AssetProcessStatus>())
        {
            groups.TryGetValue(status, out var count);
            if (count > 0)
            {
                _logger.Info($" {DescribeStatus(status)}: {count}");
            }
        }

        if (report.Items.Count == 0)
        {
            _logger.Warn(" Ни одного ассета не обработано.");
        }

        _logger.Info($" Всего обработано: {report.Items.Count}");
        _logger.Info("============================================================");
    }

    /// <summary>Переводит технический статус в понятную строку.</summary>
    private static string DescribeStatus(AssetProcessStatus status) => status switch
    {
        AssetProcessStatus.Added => "Добавлено на аккаунт",
        AssetProcessStatus.AlreadyOwned => "Уже было на аккаунте",
        AssetProcessStatus.PaidSkipped => "Платные, пропущены",
        AssetProcessStatus.WouldAddInDryRun => "Добавились бы (проверочный запуск)",
        AssetProcessStatus.UnknownAfterClick => "Непонятный результат после нажатия",
        AssetProcessStatus.PromoNotApplied => "Промокод не сработал (раздача кончилась)",
        AssetProcessStatus.Deprecated => "Удалены из магазина издателем",
        AssetProcessStatus.Failed => "Ошибка",
        _ => status.ToString()
    };
}

internal sealed class AppLogger : IDisposable
{
    private readonly bool _verbose;
    private readonly bool _traceNetwork;
    private readonly StreamWriter? _writer;
    private readonly StreamWriter? _errorWriter;
    private readonly object _sync = new();

    public AppLogger(bool verbose, bool traceNetwork, string? logFilePath, string? errorsFilePath = null)
    {
        _verbose = verbose;
        _traceNetwork = traceNetwork;

        _writer = CreateWriter(logFilePath);

        // Один файл с постоянным именем, куда дописываются только WARN/ERROR.
        // Постоянное имя важно: пользователю не приходится выбирать нужный файл из десятка.
        _errorWriter = CreateWriter(errorsFilePath);
        _errorWriter?.WriteLine();
        _errorWriter?.WriteLine("============================================================");
        _errorWriter?.WriteLine($"ЗАПУСК {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        _errorWriter?.WriteLine("============================================================");

        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            Info($"Логирование в файл включено: {logFilePath}");
        }

        Info($"Verbose={_verbose}; TraceNetwork={_traceNetwork}");
    }

    private static StreamWriter? CreateWriter(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return new StreamWriter(path, append: true) { AutoFlush = true };
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Debug(string message)
    {
        if (_verbose || _traceNetwork)
        {
            Write("DEBUG", message);
        }
    }

    private void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";

        lock (_sync)
        {
            Console.WriteLine(line);
            _writer?.WriteLine(line);

            if (level is "WARN" or "ERROR")
            {
                _errorWriter?.WriteLine(line);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer?.Dispose();
            _errorWriter?.Dispose();
        }
    }
}

internal sealed class CliOptions
{
    public bool LoginOnly { get; init; }
    public bool DryRun { get; init; }
    public bool Headless { get; init; }
    public bool Verbose { get; init; }
    public bool TraceNetwork { get; init; }
    public bool UseExtendedSources { get; init; }
    public bool UseNoDefaults { get; init; }

    /// <summary>Пресет источников: telegram, top-free или all. Пусто — источники заданы флагами.</summary>
    public string? SourcesPreset { get; init; }
    public List<string> ExtraSourceFiles { get; init; } = [];
    /// <summary>
    /// Точка входа Asset Store. Она сама перебрасывает на страницу входа Unity
    /// вместе со служебными параметрами. Прямой адрес login.unity.com/.../sign-in
    /// без этих параметров Unity уводит на страницу регистрации — проверено.
    ///
    /// Параметр redirect_to обязателен. Без него Unity после успешного входа
    /// возвращает обратно на /auth/login, тот снова начинает вход, и так по кругу
    /// без конца. С redirect_to=/ пользователь попадает на витрину магазина.
    /// </summary>
    public const string DefaultSignInUrl = "https://assetstore.unity.com/auth/login?redirect_to=%2F";

    /// <summary>
    /// Единственный файл, который нужно прислать при проблемах.
    /// Имя постоянное, содержимое дописывается — историю запусков видно в одном месте.
    /// </summary>
    public const string ProblemsFileName = "errors.log";

    /// <summary>Как этот файл назывался до версии 1.21.1. Переименовывается при запуске.</summary>
    public const string LegacyProblemsFileName = "ПРИШЛИТЕ-ЭТОТ-ФАЙЛ.log";

    /// <summary>
    /// Переносит файл с ошибками со старого имени на новое, чтобы история запусков
    /// не разъехалась по двум файлам. Делается один раз и только если нового ещё нет.
    /// </summary>
    public static void MigrateProblemsFile(string logsDirectory)
    {
        try
        {
            var legacy = Path.Combine(logsDirectory, LegacyProblemsFileName);
            var current = Path.Combine(logsDirectory, ProblemsFileName);

            if (File.Exists(legacy) && !File.Exists(current))
            {
                File.Move(legacy, current);
            }
        }
        catch
        {
            // Не переименовали — не беда: новые записи пойдут в новый файл.
        }
    }

    public string? LogFilePath { get; init; }
    public string SignInUrl { get; init; } = DefaultSignInUrl;
    public string ProfileName { get; init; } = "default";

    /// <summary>
    /// Основа имени профиля — обычно имя пользователя компьютера.
    /// К ней после входа добавляется имя аккаунта Unity.
    /// </summary>
    public string ProfileBaseName { get; init; } = "default";
    public bool ListProfiles { get; init; }
    public bool CheckLoginPage { get; init; }
    public bool CheckTelegram { get; init; }

    /// <summary>Режим сервера: прогон, пауза WatchInterval, снова прогон — пока не остановят.</summary>
    public bool Watch { get; init; }

    public TimeSpan WatchInterval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Читать в каналах только посты, появившиеся после прошлого прогона.</summary>
    public bool TelegramOnlyNew { get; init; }

    public string? NotifyBotToken { get; init; }
    public string? NotifyChatId { get; init; }

    /// <summary>Только отправить боту проверочное сообщение и выйти.</summary>
    public bool NotifyTest { get; init; }
    public bool SplitScreen { get; init; } = true;
    public bool RecheckOwned { get; init; }
    public string? TelegramProxy { get; init; }
    public string? TelegramProxyList { get; init; }
    public bool TelegramAutoProxy { get; init; }
    public string? ChromeUserDataDir { get; init; }
    public bool UseSystemChromeProfile { get; init; }
    public bool? SavePassword { get; init; }
    public bool Interactive { get; init; }
    public string LogsDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "logs");
    public string DataDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "data");
    public string? UnityEmail { get; init; }
    public string? UnityPassword { get; init; }
    public int DelayMs { get; init; } = 700;
    public int NavigationTimeoutMs { get; init; } = 120000;
    public int AuthTimeoutMs { get; init; } = 300000;
    public int AssetUiTimeoutMs { get; init; } = 30000;
    public int? MaxAddAttempts { get; init; }
    public int? MaxVisitedAssets { get; init; }
    public List<string> Sources { get; init; } = [];
    public bool HasCredentials => !string.IsNullOrWhiteSpace(UnityEmail) && !string.IsNullOrWhiteSpace(UnityPassword);
    
    // Прокси
    public string? ProxyType { get; init; }
    public string? ProxyHost { get; init; }
    public int? ProxyPort { get; init; }
    
    // Telegram
    public List<string> TelegramChannels { get; init; } = [];
    public int TelegramPostLimit { get; init; } = 50;

    /// <summary>Сколько ассетов собирать из Telegram за одну пачку. 0 — читать всё разом по TelegramPostLimit.</summary>
    public int TelegramBatchSize { get; init; } = 30;

    public bool TelegramScreenshotOnNoLinks { get; init; } = true;

    /// <summary>Период вида '45m', '6h', '1d', '1d12h' или '01:30:00'. Число без буквы — минуты.</summary>
    public static bool TryParseInterval(string text, out TimeSpan interval)
    {
        interval = TimeSpan.Zero;
        var t = text.Trim().ToLowerInvariant();

        if (int.TryParse(t, out var minutes) && minutes > 0)
        {
            interval = TimeSpan.FromMinutes(minutes);
            return true;
        }

        var parts = Regex.Matches(t, @"(\d+)\s*(d|h|m|s)");
        if (parts.Count > 0 && string.Concat(parts.Select(p => p.Value)).Replace(" ", "") == t.Replace(" ", ""))
        {
            foreach (Match p in parts)
            {
                var n = int.Parse(p.Groups[1].Value);
                interval += p.Groups[2].Value switch
                {
                    "d" => TimeSpan.FromDays(n),
                    "h" => TimeSpan.FromHours(n),
                    "m" => TimeSpan.FromMinutes(n),
                    _ => TimeSpan.FromSeconds(n)
                };
            }

            return interval > TimeSpan.Zero;
        }

        return TimeSpan.TryParse(t, out interval) && interval > TimeSpan.Zero;
    }

    public static CliOptions Parse(string[] args)
    {
        string? configPath = null;

        var cliLoginOnly = false;
        var cliDryRun = false;
        bool? cliHeadless = null;
        var cliVerbose = false;
        var cliQuiet = false;
        var cliTraceNetwork = false;
        var cliUseExtendedSources = false;
        var cliUseNoDefaults = false;
        string? cliSourcesPreset = null;
        string? cliLogFilePath = null;
        string? cliLogsDirectory = null;
        string? cliSignInUrl = null;
        string? cliProfile = null;
        string? cliSetDefaultProfile = null;
        var cliListProfiles = false;
        var cliCheckLoginPage = false;
        var cliCheckTelegram = false;
        var cliWatch = false;
        var cliNotifyTest = false;
        string? cliWatchInterval = null;
        var cliTelegramOnlyNew = false;
        string? cliNotifyBotToken = null;
        string? cliNotifyChatId = null;
        bool? cliSplitScreen = null;
        var cliRecheckOwned = false;
        string? cliTelegramProxy = null;
        string? cliTelegramProxyList = null;
        bool? cliTelegramAutoProxy = null;
        string? cliChromeUserDataDir = null;
        var cliUseSystemChromeProfile = false;
        bool? cliSavePassword = null;
        bool? cliInteractive = null;
        string? cliDataDirectory = null;
        string? cliUnityEmail = null;
        string? cliUnityPassword = null;
        int? cliDelayMs = null;
        int? cliNavigationTimeoutMs = null;
        int? cliAuthTimeoutMs = null;
        int? cliAssetUiTimeoutMs = null;
        int? cliMaxAddAttempts = null;
        int? cliTelegramBatchSize = null;
        int? cliMaxVisitedAssets = null;
        var cliSources = new List<string>();
        var cliExtraSourceFiles = new List<string>();
        var cliTelegramChannels = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i].Trim();
            switch (arg)
            {
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                case "--login":
                    cliLoginOnly = true;
                    break;
                case "--dry-run":
                    cliDryRun = true;
                    break;
                case "--headless" when i + 1 < args.Length:
                {
                    var raw = args[++i];
                    if (!bool.TryParse(raw, out var parsed))
                    {
                        Console.WriteLine($"Некорректное значение для --headless: '{raw}'. Используется false.");
                        parsed = false;
                    }

                    cliHeadless = parsed;
                    break;
                }
                case "--quiet":
                    cliQuiet = true;
                    break;
                case "--verbose":
                    cliVerbose = true;
                    break;
                case "--trace-network":
                    cliTraceNetwork = true;
                    cliVerbose = true;
                    break;
                case "--extended-sources":
                    cliUseExtendedSources = true;
                    break;
                case "--no-defaults":
                    cliUseNoDefaults = true;
                    break;
                case "--log-file" when i + 1 < args.Length:
                    cliLogFilePath = args[++i];
                    break;
                case "--logs-dir" when i + 1 < args.Length:
                    cliLogsDirectory = args[++i];
                    break;
                case "--sign-in-url" when i + 1 < args.Length:
                    cliSignInUrl = args[++i];
                    break;
                case "--profile" when i + 1 < args.Length:
                    cliProfile = args[++i];
                    break;
                case "--chrome-user-data-dir" when i + 1 < args.Length:
                    cliChromeUserDataDir = args[++i];
                    break;
                case "--use-system-chrome-profile":
                    cliUseSystemChromeProfile = true;
                    break;
                case "--recheck-owned":
                    cliRecheckOwned = true;
                    break;
                case "--split-screen" when i + 1 < args.Length:
                    cliSplitScreen = ParseBool(args[++i], true);
                    break;
                case "--check-telegram":
                    cliCheckTelegram = true;
                    break;
                case "--watch":
                    cliWatch = true;
                    break;
                case "--notify-test":
                    cliNotifyTest = true;
                    break;
                case "--watch-interval" when i + 1 < args.Length:
                    cliWatchInterval = args[++i];
                    break;
                case "--tg-only-new":
                    cliTelegramOnlyNew = true;
                    break;
                case "--notify-bot-token" when i + 1 < args.Length:
                    cliNotifyBotToken = args[++i];
                    break;
                case "--notify-chat-id" when i + 1 < args.Length:
                    cliNotifyChatId = args[++i];
                    break;
                case "--tg-proxy" when i + 1 < args.Length:
                    cliTelegramProxy = args[++i];
                    break;
                case "--tg-auto-proxy":
                    // Работает и как флаг, и с явным значением: --tg-auto-proxy false
                    cliTelegramAutoProxy = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                        ? ParseBool(args[++i], true)
                        : true;
                    break;
                case "--tg-proxy-list" when i + 1 < args.Length:
                    cliTelegramProxyList = args[++i];
                    break;
                case "--check-login-page":
                    cliCheckLoginPage = true;
                    break;
                case "--list-profiles":
                    cliListProfiles = true;
                    break;
                case "--set-default-profile" when i + 1 < args.Length:
                    cliSetDefaultProfile = args[++i];
                    break;
                case "--save-password" when i + 1 < args.Length:
                    cliSavePassword = ParseBool(args[++i], true);
                    break;
                case "--interactive" when i + 1 < args.Length:
                    cliInteractive = ParseBool(args[++i], true);
                    break;
                case "--data-dir" when i + 1 < args.Length:
                    cliDataDirectory = args[++i];
                    break;
                case "--unity-email" when i + 1 < args.Length:
                    cliUnityEmail = args[++i];
                    break;
                case "--unity-password" when i + 1 < args.Length:
                    cliUnityPassword = args[++i];
                    break;
                case "--delay-ms" when i + 1 < args.Length:
                {
                    if (int.TryParse(args[++i], out var delay) && delay > 0)
                    {
                        cliDelayMs = delay;
                    }

                    break;
                }
                case "--nav-timeout-ms" when i + 1 < args.Length:
                {
                    if (int.TryParse(args[++i], out var navTimeout) && navTimeout >= 10000)
                    {
                        cliNavigationTimeoutMs = navTimeout;
                    }

                    break;
                }
                case "--auth-timeout-ms" when i + 1 < args.Length:
                {
                    if (int.TryParse(args[++i], out var authTimeout) && authTimeout >= 30000)
                    {
                        cliAuthTimeoutMs = authTimeout;
                    }

                    break;
                }
                case "--asset-ui-timeout-ms" when i + 1 < args.Length:
                {
                    if (int.TryParse(args[++i], out var assetUiTimeout) && assetUiTimeout >= 5000)
                    {
                        cliAssetUiTimeoutMs = assetUiTimeout;
                    }

                    break;
                }
                case "--tg-batch-size" when i + 1 < args.Length:
                {
                    if (int.TryParse(args[++i], out var parsedBatch) && parsedBatch >= 0)
                    {
                        cliTelegramBatchSize = parsedBatch;
                    }

                    break;
                }
                case "--max-add-attempts" when i + 1 < args.Length:
                {
                    if (int.TryParse(args[++i], out var parsedLimit) && parsedLimit > 0)
                    {
                        cliMaxAddAttempts = parsedLimit;
                    }

                    break;
                }
                case "--max-visited-assets" when i + 1 < args.Length:
                {
                    if (int.TryParse(args[++i], out var parsedVisitedLimit) && parsedVisitedLimit > 0)
                    {
                        cliMaxVisitedAssets = parsedVisitedLimit;
                    }

                    break;
                }
                case "--source" when i + 1 < args.Length:
                    cliSources.Add(args[++i]);
                    break;
                case "--sources" when i + 1 < args.Length:
                    cliSourcesPreset = args[++i];
                    break;
                case "--extra-source-file" when i + 1 < args.Length:
                    cliExtraSourceFiles.Add(args[++i]);
                    break;
                case "--tg-channels" when i + 1 < args.Length:
                {
                    var raw = args[++i];
                    foreach (var ch in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!string.IsNullOrWhiteSpace(ch))
                            cliTelegramChannels.Add(ch);
                    }
                    break;
                }
            }
        }

        var config = AppConfig.Load(configPath, out var usedConfigPath, out var configError);
        if (!string.IsNullOrWhiteSpace(configError))
        {
            Console.WriteLine(configError);
        }
        else if (!string.IsNullOrWhiteSpace(usedConfigPath))
        {
            Console.WriteLine($"Загружен конфиг: {usedConfigPath}");
        }

        var loginOnly = cliLoginOnly || (config?.LoginOnly ?? false);
        var dryRun = cliDryRun || (config?.DryRun ?? false);

        // На сервере экрана нет: в режиме --watch и там, где нет ни X11, ни Wayland
        // (Docker, SSH), браузер невидимый — окно всё равно негде показать.
        var watch = cliWatch || (config?.Server?.Watch ?? false);
        var noScreen = OperatingSystem.IsLinux() &&
                       string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
                       string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        var headless = cliHeadless ?? (watch || noScreen ? true : config?.Headless ?? false);
        // --quiet сильнее всего остального: он нужен, чтобы обычный запуск давал
        // читаемый лог даже там, где в config.json когда-то включили подробности.
        var verbose = !cliQuiet && (cliVerbose || (config?.Verbose ?? false));
        var traceNetwork = !cliQuiet && (cliTraceNetwork || (config?.TraceNetwork ?? false));
        var useExtendedSources = cliUseExtendedSources || (config?.ExtendedSources ?? false);
        var useNoDefaults = cliUseNoDefaults || (config?.NoDefaults ?? false);
        if (traceNetwork)
        {
            verbose = true;
        }

        var logFilePath = string.IsNullOrWhiteSpace(cliLogFilePath)
            ? config?.LogFilePath
            : cliLogFilePath;

        var delayMs = cliDelayMs ?? config?.DelayMs ?? 1200;
        if (delayMs <= 0)
        {
            delayMs = 1200;
        }

        var navigationTimeoutMs = cliNavigationTimeoutMs ?? config?.NavigationTimeoutMs ?? 120000;
        if (navigationTimeoutMs < 10000)
        {
            navigationTimeoutMs = 120000;
        }

        var authTimeoutMs = cliAuthTimeoutMs ?? config?.AuthTimeoutMs ?? 300000;
        if (authTimeoutMs < 30000)
        {
            authTimeoutMs = 300000;
        }

        var assetUiTimeoutMs = cliAssetUiTimeoutMs ?? config?.AssetUiTimeoutMs ?? 30000;
        if (assetUiTimeoutMs < 5000)
        {
            assetUiTimeoutMs = 30000;
        }

        var maxAddAttempts = cliMaxAddAttempts ?? config?.MaxAddAttempts;
        if (maxAddAttempts <= 0)
        {
            maxAddAttempts = null;
        }

        var maxVisitedAssets = cliMaxVisitedAssets ?? config?.MaxVisitedAssets;
        if (maxVisitedAssets <= 0)
        {
            maxVisitedAssets = null;
        }

        var sources = new List<string>();
        if (!useNoDefaults && config?.Sources?.Count > 0)
        {
            sources.AddRange(config.Sources.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        if (cliSources.Count > 0)
        {
            sources = cliSources;
        }

        sources = sources
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var extraSourceFiles = new List<string>();
        if (!useNoDefaults && config?.ExtraSourceFiles?.Count > 0)
        {
            extraSourceFiles.AddRange(config.ExtraSourceFiles.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        if (cliExtraSourceFiles.Count > 0)
        {
            extraSourceFiles.AddRange(cliExtraSourceFiles);
        }

        extraSourceFiles = extraSourceFiles
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Пресет источников: одно слово вместо набора флагов. Так его задаёт сервер
        // в .env (SOURCES=telegram|top-free|all), чтобы не переписывать docker-compose.yml.
        var sourcesPreset = NormalizeSourcesPreset(FirstNonEmpty(cliSourcesPreset, Environment.GetEnvironmentVariable("SOURCES")));

        if (sourcesPreset is not null)
        {
            // Отдельные флаги сильнее пресета: если их задали руками, пресет не мешает.
            if (cliUseNoDefaults || cliSources.Count > 0 || cliUseExtendedSources)
            {
                Console.WriteLine($"Источники заданы флагами, поэтому SOURCES={sourcesPreset} не применяется.");
                sourcesPreset = null;
            }
            else
            {
                switch (sourcesPreset)
                {
                    case "telegram":
                        useNoDefaults = true;
                        break;
                    case "top-free":
                        sources = [UnityAssetAutomationApp.BaseTopFreeSource];
                        break;
                    case "all":
                        useExtendedSources = true;
                        break;
                }
            }
        }

        var unityEmail = config?.UnityEmail;
        var unityPassword = config?.UnityPassword;

        var envUnityEmail = Environment.GetEnvironmentVariable("UNITY_EMAIL");
        var envUnityPassword = Environment.GetEnvironmentVariable("UNITY_PASSWORD");

        if (!string.IsNullOrWhiteSpace(envUnityEmail))
        {
            unityEmail = envUnityEmail;
        }

        if (!string.IsNullOrWhiteSpace(envUnityPassword))
        {
            unityPassword = envUnityPassword;
        }

        if (!string.IsNullOrWhiteSpace(cliUnityEmail))
        {
            unityEmail = cliUnityEmail;
        }

        if (!string.IsNullOrWhiteSpace(cliUnityPassword))
        {
            unityPassword = cliUnityPassword;
        }

        if ((string.IsNullOrWhiteSpace(unityEmail) && !string.IsNullOrWhiteSpace(unityPassword)) ||
            (!string.IsNullOrWhiteSpace(unityEmail) && string.IsNullOrWhiteSpace(unityPassword)))
        {
            Console.WriteLine(
                "Для автовхода необходимо задать и UNITY_EMAIL, и UNITY_PASSWORD (или оба через CLI). Будет использован ручной вход.");
            unityEmail = null;
            unityPassword = null;
        }

        // Прокси из конфига
        string? proxyType = config?.Proxy?.Type;
        string? proxyHost = config?.Proxy?.Host;
        int? proxyPort = config?.Proxy?.Port;

        // Telegram из конфига + CLI (CLI имеет приоритет)
        var telegramChannels = new List<string>();
        var envTelegramChannels = (Environment.GetEnvironmentVariable("TELEGRAM_CHANNELS") ?? string.Empty)
            .Split([',', ' ', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (cliTelegramChannels.Count > 0)
        {
            telegramChannels.AddRange(cliTelegramChannels);
        }
        else if (envTelegramChannels.Count > 0)
        {
            // Так каналы задаёт deploy.sh на сервере: в .env, а не в telegram_sources.txt из git.
            telegramChannels.AddRange(envTelegramChannels);
        }
        else if (config?.Telegram?.Channels?.Count > 0)
        {
            telegramChannels.AddRange(config.Telegram.Channels);
        }
        else
        {
            telegramChannels.AddRange(ReadTelegramSourcesFile("telegram_sources.txt"));
        }

        var signInUrl = FirstNonEmpty(cliSignInUrl, config?.SignInUrl) ?? DefaultSignInUrl;
        var logsDirectory = ResolveDirectory(cliLogsDirectory ?? config?.LogsDirectory, "logs");
        var dataDirectory = ResolveDirectory(cliDataDirectory ?? config?.DataDirectory, "data");

        // Профиль решается до всего остального: от него зависит, где лежит сессия
        // и какие учётные данные брать из Диспетчера учётных данных Windows.
        var profileStore = new ProfileStore(dataDirectory);
        var rawProfileName = cliSetDefaultProfile ?? profileStore.ResolveProfileName(cliProfile, config?.Profile);

        // Имя профиля становится именем папки. Приводим его сразу, иначе в логе
        // видно одно имя, а на диске лежит другое — и непонятно, куда всё делось.
        var profileName = ProfileStore.Sanitize(rawProfileName);

        // Основа — имя пользователя компьютера. Если профиль задали руками,
        // основой становится оно, чтобы имя не превращалось в кашу.
        var machineUser = Environment.UserName;
        var profileBase = !string.IsNullOrWhiteSpace(cliProfile) || !string.IsNullOrWhiteSpace(config?.Profile)
            ? profileName.Split("__")[0]
            : ProfileStore.Sanitize(string.IsNullOrWhiteSpace(machineUser) ? "default" : machineUser);
        if (!string.Equals(profileName, rawProfileName, StringComparison.Ordinal))
        {
            Console.WriteLine($"[Профиль] Имя '{rawProfileName}' содержит символы, которых не может быть в имени папки.");
            Console.WriteLine($"[Профиль] Используется имя: {profileName}");
        }

        if (!string.IsNullOrWhiteSpace(cliSetDefaultProfile))
        {
            profileStore.SetDefault(profileName);
            Console.WriteLine($"Профиль по умолчанию: {profileName}");
        }

        // Если логин и пароль не заданы явно, пробуем взять их из хранилища ОС.
        if (string.IsNullOrWhiteSpace(unityEmail) || string.IsNullOrWhiteSpace(unityPassword))
        {
            var target = SecretStore.BuildCredentialTarget(profileName);
            if (SecretStore.TryReadCredentials(target, out var storedEmail, out var storedPassword))
            {
                unityEmail = storedEmail;
                unityPassword = storedPassword;
                Console.WriteLine($"[Вход] Учётные данные профиля '{profileName}' взяты из Диспетчера учётных данных Windows.");
            }
        }

        // Интерактивный режим доступен, только если программу запустили из живой консоли.
        // На сервере спрашивать некого, даже если консоль вдруг есть.
        var interactive = !watch && (cliInteractive ?? config?.Interactive ?? !Console.IsInputRedirected);

        var watchIntervalText = FirstNonEmpty(cliWatchInterval, config?.Server?.Interval);
        var watchInterval = TimeSpan.FromHours(24);
        if (!string.IsNullOrWhiteSpace(watchIntervalText))
        {
            if (TryParseInterval(watchIntervalText, out var parsedInterval))
            {
                watchInterval = parsedInterval;
            }
            else
            {
                Console.WriteLine($"[Сервер] Не понял период '{watchIntervalText}'. Примеры: 30m, 6h, 1d. Берём 24h.");
            }
        }

        var telegramPostLimit = config?.Telegram?.PostLimit ?? 50;
        var telegramScreenshotOnNoLinks = config?.Telegram?.ScreenshotOnNoLinks ?? false;

        return new CliOptions
        {
            LoginOnly = loginOnly,
            DryRun = dryRun,
            Headless = headless,
            Verbose = verbose,
            TraceNetwork = traceNetwork,
            UseExtendedSources = useExtendedSources,
            UseNoDefaults = useNoDefaults,
            SourcesPreset = sourcesPreset,
            ExtraSourceFiles = extraSourceFiles,
            LogFilePath = logFilePath,
            LogsDirectory = logsDirectory,
            SignInUrl = signInUrl,
            ProfileName = profileName,
            ProfileBaseName = profileBase,
            ListProfiles = cliListProfiles,
            CheckLoginPage = cliCheckLoginPage,
            CheckTelegram = cliCheckTelegram,
            Watch = watch,
            NotifyTest = cliNotifyTest,
            WatchInterval = watchInterval,
            TelegramOnlyNew = watch || cliTelegramOnlyNew || (config?.Telegram?.OnlyNew ?? false),
            NotifyBotToken = FirstNonEmpty(cliNotifyBotToken, Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"), config?.Notify?.TelegramBotToken),
            NotifyChatId = FirstNonEmpty(cliNotifyChatId, Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID"), config?.Notify?.TelegramChatId),
            SplitScreen = !watch && (cliSplitScreen ?? config?.SplitScreen ?? true),
            RecheckOwned = cliRecheckOwned,
            TelegramProxy = FirstNonEmpty(cliTelegramProxy, config?.Telegram?.Proxy),
            TelegramProxyList = FirstNonEmpty(cliTelegramProxyList, config?.Telegram?.ProxyList),
            TelegramAutoProxy = cliTelegramAutoProxy ?? config?.Telegram?.AutoProxy ?? true,
            ChromeUserDataDir = FirstNonEmpty(cliChromeUserDataDir, config?.ChromeUserDataDir),
            UseSystemChromeProfile = cliUseSystemChromeProfile || config?.UseSystemChromeProfile == true,
            SavePassword = cliSavePassword ?? config?.SavePassword,
            Interactive = interactive,
            DataDirectory = dataDirectory,
            UnityEmail = unityEmail,
            UnityPassword = unityPassword,
            DelayMs = delayMs,
            NavigationTimeoutMs = navigationTimeoutMs,
            AuthTimeoutMs = authTimeoutMs,
            AssetUiTimeoutMs = assetUiTimeoutMs,
            MaxAddAttempts = maxAddAttempts,
            MaxVisitedAssets = maxVisitedAssets,
            Sources = sources,
            ProxyType = proxyType,
            ProxyHost = proxyHost,
            ProxyPort = proxyPort,
            TelegramChannels = telegramChannels,
            TelegramPostLimit = telegramPostLimit,
            TelegramBatchSize = cliTelegramBatchSize ?? config?.Telegram?.BatchSize ?? 30,
            TelegramScreenshotOnNoLinks = telegramScreenshotOnNoLinks
        };
    }

    /// <summary>
    /// Разбирает значение вида true/false/да/нет/1/0. Непонятное значение — берём запасное.
    /// </summary>
    private static bool ParseBool(string raw, bool fallback)
    {
        var value = raw.Trim().ToLowerInvariant();

        return value switch
        {
            "true" or "1" or "yes" or "y" or "да" or "д" => true,
            "false" or "0" or "no" or "n" or "нет" or "н" => false,
            _ => LogAndFallback()
        };

        bool LogAndFallback()
        {
            Console.WriteLine($"Непонятное значение '{raw}'. Используется {fallback}.");
            return fallback;
        }
    }

    /// <summary>
    /// Приводит значение SOURCES к одному из трёх: telegram, top-free, all.
    /// Непонятное значение — предупреждение и telegram: на сервере это привычное
    /// поведение, а молча включать долгий обход магазина из-за опечатки нельзя.
    /// </summary>
    private static string? NormalizeSourcesPreset(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim().ToLowerInvariant().Replace('_', '-').Replace(" ", string.Empty);

        switch (value)
        {
            case "telegram":
            case "tg":
            case "telegram-only":
            case "only-telegram":
                return "telegram";
            case "top-free":
            case "topfree":
            case "top":
            case "free":
                return "top-free";
            case "all":
            case "everything":
            case "max":
            case "все":
                return "all";
            default:
                Console.WriteLine($"SOURCES={raw} — такого набора источников нет. Беру telegram. Возможные значения: telegram, top-free, all.");
                return "telegram";
        }
    }

    /// <summary>Что означает пресет источников — человеческими словами, для лога.</summary>
    public static string DescribeSourcesPreset(string preset) => preset switch
    {
        "telegram" => "только Telegram-каналы",
        "top-free" => "Telegram-каналы и страница «топ бесплатных» магазина",
        "all" => "Telegram-каналы, «топ бесплатных», китайский архив и расширенные списки. " +
                 "Первый проход долгий: ссылок больше двухсот",
        _ => preset
    };

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
    }

    /// <summary>
    /// Определяет каталог для логов или данных.
    /// Если путь не задан, используется папка рядом с исполняемым файлом.
    /// </summary>
    private static string ResolveDirectory(string? configured, string defaultFolderName)
    {
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, defaultFolderName)
            : Path.GetFullPath(configured);
    }

    /// <summary>
    /// Читает список Telegram-каналов из текстового файла (по одному имени на строку).
    /// Пустые строки и строки, начинающиеся с # или //, игнорируются.
    /// Файл ищется в рабочем каталоге, рядом с exe и выше по дереву (для запуска из bin/Debug/netX).
    /// </summary>
    private static List<string> ReadTelegramSourcesFile(string fileName)
    {
        var channels = new List<string>();

        var candidates = new List<string>
            {
                Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), fileName)),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, fileName)),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", fileName)),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", fileName))
            }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            Console.WriteLine(
                $"[Telegram] Файл {fileName} не найден. Проверены пути: {string.Join("; ", candidates)}");
            return channels;
        }

        try
        {
            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                // Поддерживаем и голое имя канала, и @name, и ссылку https://t.me/name
                var name = line.TrimStart('@');
                var tmeIndex = name.IndexOf("t.me/", StringComparison.OrdinalIgnoreCase);
                if (tmeIndex >= 0)
                {
                    name = name[(tmeIndex + "t.me/".Length)..];
                }

                name = name.Split('/', '?')[0].Trim();
                if (name.Length > 0 && !channels.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    channels.Add(name);
                }
            }

            Console.WriteLine($"[Telegram] Каналы загружены из {path}: {string.Join(", ", channels)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Telegram] Не удалось прочитать {path}: {ex.Message}");
        }

        return channels;
    }
}

internal sealed class AppConfig
{
    public bool? LoginOnly { get; init; }
    public bool? DryRun { get; init; }
    public bool? Headless { get; init; }
    public bool? Verbose { get; init; }
    public bool? TraceNetwork { get; init; }
    public bool? ExtendedSources { get; init; }
    public bool? NoDefaults { get; init; }
    public List<string> ExtraSourceFiles { get; init; } = [];
    public string? LogFilePath { get; init; }
    public string? SignInUrl { get; init; }
    public string? Profile { get; init; }
    public bool? SavePassword { get; init; }
    public bool? Interactive { get; init; }
    public bool? SplitScreen { get; init; }
    public string? ChromeUserDataDir { get; init; }
    public bool? UseSystemChromeProfile { get; init; }
    public string? LogsDirectory { get; init; }
    public string? DataDirectory { get; init; }
    public string? UnityEmail { get; init; }
    public string? UnityPassword { get; init; }
    public int? DelayMs { get; init; }
    public int? NavigationTimeoutMs { get; init; }
    public int? AuthTimeoutMs { get; init; }
    public int? AssetUiTimeoutMs { get; init; }
    public int? MaxAddAttempts { get; init; }
    public int? MaxVisitedAssets { get; init; }
    public List<string> Sources { get; init; } = [];
    public ProxyConfig? Proxy { get; init; }
    public TelegramConfig? Telegram { get; init; }
    public ServerConfig? Server { get; init; }
    public NotifyConfig? Notify { get; init; }

    public static AppConfig? Load(string? explicitConfigPath, out string? usedConfigPath, out string? error)
    {
        usedConfigPath = null;
        error = null;

        var resolvedPath = !string.IsNullOrWhiteSpace(explicitConfigPath)
            ? Path.GetFullPath(explicitConfigPath)
            : Path.Combine(Directory.GetCurrentDirectory(), "config.json");

        var explicitPathProvided = !string.IsNullOrWhiteSpace(explicitConfigPath);
        if (!File.Exists(resolvedPath))
        {
            if (explicitPathProvided)
            {
                error = $"Конфигурационный файл не найден: {resolvedPath}";
            }

            return null;
        }

        try
        {
            var json = File.ReadAllText(resolvedPath);
            var parsed = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            usedConfigPath = resolvedPath;
            return parsed;
        }
        catch (Exception ex)
        {
            error = $"Не удалось прочитать конфигурационный файл {resolvedPath}: {ex.Message}";
            return null;
        }
    }
}

internal sealed class ProxyConfig
{
    public string Type { get; init; } = "socks5";
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 1080;
}

internal sealed class TelegramConfig
{
    public string? Proxy { get; init; }
    public string? ProxyList { get; init; }
    public bool? AutoProxy { get; init; }
    public List<string> Channels { get; init; } = [];
    public int PostLimit { get; init; } = 50;

    /// <summary>Размер пачки ассетов из Telegram. 0 — читать всё разом по postLimit.</summary>
    public int? BatchSize { get; init; }

    /// <summary>Читать только посты, появившиеся после прошлого прогона.</summary>
    public bool? OnlyNew { get; init; }

    public bool ScreenshotOnNoLinks { get; init; } = true;
}

/// <summary>Режим сервера: периодические прогоны без окна браузера.</summary>
internal sealed class ServerConfig
{
    public bool? Watch { get; init; }

    /// <summary>Период между прогонами: '30m', '6h', '1d'.</summary>
    public string? Interval { get; init; }
}

/// <summary>Сообщения от Telegram-бота.</summary>
internal sealed class NotifyConfig
{
    public string? TelegramBotToken { get; init; }
    public string? TelegramChatId { get; init; }
}

internal sealed class SerializableCookie
{
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string Path { get; init; } = "/";
    public double? Expires { get; init; }
    public bool HttpOnly { get; init; }
    public bool Secure { get; init; }
    public SameSite SameSite { get; init; }

    public static SerializableCookie FromCookie(CookieParam cookie) => new()
    {
        Name = cookie.Name,
        Value = cookie.Value,
        Domain = cookie.Domain,
        Path = cookie.Path,
        Expires = cookie.Expires,
        HttpOnly = cookie.HttpOnly ?? false,
        Secure = cookie.Secure ?? false,
        SameSite = cookie.SameSite ?? SameSite.None
    };

    public CookieParam ToCookieParam() => new()
    {
        Name = Name,
        Value = Value,
        Domain = Domain,
        Path = Path,
        Expires = Expires,
        HttpOnly = HttpOnly,
        Secure = Secure,
        SameSite = SameSite
    };
}

internal sealed class SessionStateSnapshot
{
    public DateTime SavedAtUtc { get; set; }
    public List<SerializableCookie> Cookies { get; set; } = [];

    public Dictionary<string, Dictionary<string, string>> LocalStorageByOrigin { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class RunReport
{
    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }
    public bool DryRun { get; set; }
    public List<string> Sources { get; set; } = [];
    public List<ProcessResult> Items { get; set; } = [];
}

internal sealed class ProcessResult
{
    public string Url { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }
    public AssetProcessStatus Status { get; set; }
    public bool DetectedFree { get; set; }
    public bool DetectedOwned { get; set; }
    public bool CountsTowardsAddLimit { get; set; }
    public string? PurchasedOnText { get; set; }
    public string? DetectionSummary { get; set; }
    public string? Message { get; set; }

    /// <summary>Промокод, по которому выкупался ассет (если выкупался).</summary>
    public string? PromoCode { get; set; }
}

internal sealed class AssetStatusSnapshot
{
    public bool IsFree { get; init; }
    public bool IsOwned { get; init; }
    public bool HasAddToMyAssets { get; init; }
    public bool HasOpenInUnity { get; init; }
    public bool RequiresLogin { get; init; }
    public string? PurchasedOnText { get; init; }
    public string? DetectionSummary { get; init; }
}

internal sealed class AuthUiMarkers
{
    public bool HasMyAssetsLink { get; init; }
    public bool HasSignInLink { get; init; }
    public bool HasMyAssetsText { get; init; }
    public bool HasSignInText { get; init; }
    public bool HasSignInWithUnityText { get; init; }
    public bool HasSignInWithUnityButton { get; init; }
}

internal sealed class ProfileMenuAuthState
{
    public bool ProfileMenuFound { get; init; }
    public bool HasSignInItem { get; init; }
    public bool HasSignedInItem { get; init; }
}

internal sealed class SourceCollectionSnapshot
{
    public int TotalFound { get; init; }
    public int OwnedSkipped { get; init; }
    public List<string> Urls { get; init; } = [];
}

internal enum AssetProcessStatus
{
    Added,
    AlreadyOwned,
    PaidSkipped,
    WouldAddInDryRun,
    UnknownAfterClick,
    PromoNotApplied,
    Deprecated,
    Failed
}

/// <summary>Сообщение у поля промокода. Explicit — из красной строки ошибки блока купона.</summary>
internal sealed class CouponMessage
{
    public string Text { get; set; } = string.Empty;
    public bool Explicit { get; set; }
}

/// <summary>Состояние страницы входа Unity: шаг, капча, сообщения об ошибке.</summary>
internal sealed class LoginPageState
{
    public string Step { get; set; } = "unknown";
    public bool Ready { get; set; }
    public bool Captcha { get; set; }
    public List<string> Errors { get; set; } = [];
    public string Text { get; set; } = string.Empty;
}

/// <summary>Где на странице оплаты вопрос «Tax Business use» и выбран ли уже «No».</summary>
internal sealed class TaxQuestionInfo
{
    public bool Found { get; set; }
    public bool AlreadyChecked { get; set; }
    public string Question { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
}

/// <summary>Галка согласия на странице оплаты.</summary>
internal sealed class AgreementCheckbox
{
    public string Idx { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public bool Required { get; set; }
    public bool Checked { get; set; }
    public string Text { get; set; } = string.Empty;
}

/// <summary>Что лежит в корзине магазина (без отложенных «на потом»).</summary>
internal sealed class CartSnapshot
{
    public bool Empty { get; set; }
    public List<CartItemInfo> Items { get; } = [];
}

internal sealed class CartItemInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public string Describe() => string.IsNullOrWhiteSpace(Name) ? $"#{Id}" : $"'{Name}' (#{Id})";
}

/// <summary>
/// Итоговая стоимость в корзине. Amount = 0 означает именно ноль,
/// а не "на странице где-то встретилось 0.00" — на этом раньше можно было
/// принять платный ассет за бесплатный и нажать оплату.
/// </summary>
internal sealed class CartPriceSnapshot
{
    public bool Found { get; set; }
    public decimal Amount { get; set; }
    public string RawText { get; set; } = string.Empty;
    public bool HasPromoError { get; set; }
    public string FoundError { get; set; } = string.Empty;

    public string Describe() => Found
        ? (Amount == 0 ? $"бесплатно ({RawText})" : $"{RawText}")
        : "не удалось прочитать";
}

