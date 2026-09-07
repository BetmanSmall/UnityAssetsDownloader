using System.Diagnostics;
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

try
{
    var app = new UnityAssetAutomationApp(options);
    await app.RunAsync();
}
catch (Exception ex)
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
        Directory.CreateDirectory(options.LogsDirectory);
        var problemsPath = Path.Combine(options.LogsDirectory, CliOptions.ProblemsFileName);
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
    private const string BaseTopFreeSource = "https://assetstore.unity.com/top-assets/top-free";

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
        var errorsFilePath = Path.Combine(_logsDirectory, CliOptions.ProblemsFileName);
        _logger = new AppLogger(options.Verbose, options.TraceNetwork, logFilePath, errorsFilePath);
        _logger.Info($"ВЕРСИЯ ПРОГРАММЫ: {BuildVersionLine()}");
        _logger.Info($"Каталог логов: {_logsDirectory}");
        _logger.Info($"Каталог данных (cookies): {_dataDirectory}");
        _logger.Info($"Профиль аккаунта: {_profileName} | папка: {profileDirectory}");
        _logger.Info($"ЕСЛИ ЧТО-ТО ПОШЛО НЕ ТАК — ПРИШЛИТЕ ЭТОТ ФАЙЛ: {errorsFilePath}");
    }

    public async Task RunAsync()
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            Directory.CreateDirectory(_logsDirectory);

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
                "--start-maximized",
                "--disable-blink-features=AutomationControlled",
                "--disable-infobars"
            };

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

            IBrowser browser;
            try
            {
                browser = await Puppeteer.LaunchAsync(launchOptions);
            }
            catch (Exception ex) when (_options.UseSystemChromeProfile)
            {
                _logger.Error(
                    "Не удалось открыть ваш обычный профиль Chrome. Скорее всего, Chrome сейчас запущен: " +
                    "он не отдаёт свою папку второму окну.");
                _logger.Error("Закройте ВСЕ окна Chrome (проверьте значок у часов) и запустите заново.");
                _logger.Error($"Текст ошибки: {ex.Message}");
                throw;
            }

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
                _logger.Error(" Что делать написано выше. Обычно помогает вход по email и паролю.");
                _logger.Error("============================================================");
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

            // Парсинг Telegram каналов (если указаны)
            if (_options.TelegramChannels.Count > 0)
            {
                var tgResult = await ParseTelegramChannelsAsync(browser);

                var tgPostsLogPath = Path.Combine(_logsDirectory, "telegram_posts_raw.log");
                var tgPostLines = new List<string>
                {
                    string.Empty,
                    "############################################################",
                    $"# ЗАПУСК: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"# КАНАЛЫ: {string.Join(", ", _options.TelegramChannels)}",
                    $"# ПОСТОВ СОБРАНО: {tgResult.AllPosts.Count}",
                    "############################################################"
                };
                foreach (var post in tgResult.AllPosts)
                {
                    tgPostLines.Add("============================================================");
                    tgPostLines.Add($"CHANNEL: {post.ChannelName} | POST ID: {post.PostId}");
                    tgPostLines.Add("============================================================");
                    tgPostLines.Add(post.Text);
                    tgPostLines.Add(string.Empty);
                }

                await File.AppendAllLinesAsync(tgPostsLogPath, tgPostLines);
                _logger.Info($"Telegram: тексты постов ({tgResult.AllPosts.Count}) дописаны в: {tgPostsLogPath}");

                if (tgResult.AssetUrls.Count > 0)
                {
                    _logger.Info($"Telegram: найдено ссылок на ассеты: {tgResult.AssetUrls.Count}");
                    foreach (var url in tgResult.AssetUrls)
                    {
                        assetUrls.Add(url);
                        _logger.Debug($"Telegram asset: {url}");
                    }
                }

                if (tgResult.AssetPromocodes.Count > 0)
                {
                    foreach (var kvp in tgResult.AssetPromocodes)
                    {
                        assetPromocodes[kvp.Key] = kvp.Value;
                    }
                }

                if (tgResult.GitLinks.Count > 0)
                {
                    var gitLogPath = Path.Combine(_logsDirectory, "telegram_git_links.log");
                    await File.WriteAllLinesAsync(gitLogPath, tgResult.GitLinks);
                    _logger.Info($"Telegram git-ссылки сохранены в: {gitLogPath} (всего: {tgResult.GitLinks.Count})");
                }

                if (tgResult.Promocodes.Count > 0)
                {
                    var promoLogPath = Path.Combine(_logsDirectory, "telegram_promocodes.log");
                    await File.WriteAllLinesAsync(promoLogPath, tgResult.Promocodes);
                    _logger.Info($"Telegram промокоды сохранены в: {promoLogPath} (всего: {tgResult.Promocodes.Count})");
                }

                if (tgResult.PostsWithoutLinks.Count > 0)
                {
                    _logger.Warn($"Telegram: постов без ссылок: {tgResult.PostsWithoutLinks.Count}. Скриншоты сохранены в logs/telegram/");
                }

                if (tgResult.Errors.Count > 0)
                {
                    foreach (var err in tgResult.Errors)
                    {
                        _logger.Warn($"Telegram ошибка: {err}");
                    }
                }
            }

            _logger.Info($"Найдено уникальных ассетов: {assetUrls.Count}");
            var report = new RunReport
            {
                StartedAtUtc = DateTime.UtcNow,
                DryRun = _options.DryRun,
                Sources = sources
            };

            var newlyAddedCount = 0;
            if (_options.MaxAddAttempts.HasValue)
            {
                _logger.Info($"Включен лимит по новым добавленным ассетам: {_options.MaxAddAttempts.Value}");
            }

            if (_options.MaxVisitedAssets.HasValue)
            {
                _logger.Info($"Включен лимит по посещенным ассетам: {_options.MaxVisitedAssets.Value}");
            }

            var index = 0;
            foreach (var assetUrl in assetUrls)
            {
                if (_options.MaxVisitedAssets.HasValue && index >= _options.MaxVisitedAssets.Value)
                {
                    _logger.Warn(
                        $"Достигнут лимит посещенных ассетов ({index}/{_options.MaxVisitedAssets.Value}). Обработка остановлена.");
                    break;
                }

                if (_options.MaxAddAttempts.HasValue && newlyAddedCount >= _options.MaxAddAttempts.Value)
                {
                    _logger.Warn(
                        $"[Лимит] Достигнут лимит новых ассетов ({newlyAddedCount}/{_options.MaxAddAttempts.Value}). Обработка остановлена.");
                    break;
                }

                index++;
                _logger.Info($"[{index}/{assetUrls.Count}] {assetUrl}");

                assetPromocodes.TryGetValue(assetUrl, out var promoCode);
                var result = await ProcessAssetAsync(page, assetUrl, promoCode);
                report.Items.Add(result);

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
                        break;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(_options.DelayMs));
            }

            report.FinishedAtUtc = DateTime.UtcNow;
            await File.WriteAllTextAsync(_reportPath, JsonSerializer.Serialize(report, _jsonOptions));

            PrintSummary(report);
            _logger.Info($"Отчет сохранен: {_reportPath}");
            }
        }
        finally
        {
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

    /// <summary>
    /// Разбирает Telegram-каналы. Если задан отдельный прокси для Telegram,
    /// поднимает под это второй браузер, чтобы Unity продолжал ходить напрямую.
    /// </summary>
    private async Task<TelegramParseResult> ParseTelegramChannelsAsync(IBrowser mainBrowser)
    {
        _logger.Info($"Запуск парсинга Telegram каналов: {string.Join(", ", _options.TelegramChannels)}");

        IBrowser tgBrowser = mainBrowser;
        IBrowser? ownBrowser = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(_options.TelegramProxy))
            {
                _logger.Info($"Telegram идёт через отдельный прокси: {_options.TelegramProxy}");
                _logger.Info("Unity при этом работает напрямую, без прокси.");
                ownBrowser = await LaunchTelegramBrowserAsync(_options.TelegramProxy);
                tgBrowser = ownBrowser;
            }

            var parser = new TelegramSourceParser(
                tgBrowser,
                _logger,
                _logsDirectory,
                _options.NavigationTimeoutMs,
                _options.TelegramPostLimit,
                _options.TelegramScreenshotOnNoLinks);

            var result = await parser.ParseChannelsAsync(_options.TelegramChannels);
            ExplainTelegramFailure(result);
            return result;
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
            Args = [..args]
        };

        if (_chromePath != null)
        {
            options.ExecutablePath = _chromePath;
        }

        return await Puppeteer.LaunchAsync(options);
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
            "ERR_TIMED_OUT", "ERR_ADDRESS_UNREACHABLE");

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
        _logger.Info($" Прокси: {(string.IsNullOrWhiteSpace(_options.TelegramProxy) ? "не задан, идём напрямую" : _options.TelegramProxy)}");
        _logger.Info("============================================================");

        if (_options.TelegramChannels.Count == 0)
        {
            _logger.Warn("Каналы не заданы. Впишите их в telegram_sources.txt, по одному на строку.");
            return;
        }

        var result = await ParseTelegramChannelsAsync(browser);

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
        _logger.Info("============================================================");
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

            if (HasCredentials)
            {
                await TrySwitchToSignInPageAsync(page);
                var submitted = await TryCompleteUnityLoginFormAsync(page);
                if (submitted)
                {
                    _logger.Info("Форма входа отправлена автоматически.");
                }
            }

            if (await WaitForAuthenticatedSessionAsync(page, TimeSpan.FromMilliseconds(_options.AuthTimeoutMs)))
            {
                return true;
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

    /// <summary>Пытается прочитать, под каким аккаунтом выполнен вход.</summary>
    private async Task<string?> TryReadSignedInUserAsync(IPage page)
    {
        try
        {
            var raw = await page.EvaluateFunctionAsync<string?>(@"async () => {
                try {
                    const res = await fetch('https://assetstore.unity.com/api/user/info', { credentials: 'include' });
                    if (!res.ok) return null;
                    const data = await res.json();
                    return data?.user?.email || data?.email || data?.user?.username || null;
                } catch (e) {
                    return null;
                }
            }");

            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }
        catch
        {
            return null;
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
                if (HasCredentials)
                {
                    await TryCompleteUnityLoginFormAsync(page);
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

    /// <summary>
    /// Заполняет форму входа Unity. Форма двухшаговая: сначала спрашивают email,
    /// и только потом, на следующем экране, пароль.
    /// </summary>
    private async Task<bool> TryCompleteUnityLoginFormAsync(IPage page)
    {
        if (!HasCredentials)
        {
            return false;
        }

        if (!page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Debug($"Автовход: страница не похожа на форму Unity ({page.Url}). Пропускаем.");
            return false;
        }

        // Шаг 1. Email.
        if (await TryFillFieldAsync(page, FieldKind.Email, _unityEmail!))
        {
            _logger.Info("Автовход, шаг 1: email введён.");
            if (await TryClickPrimaryButtonAsync(page))
            {
                _logger.Info("Автовход, шаг 1: кнопка продолжения нажата.");
            }
        }
        else
        {
            _logger.Debug("Автовход, шаг 1: поле email не найдено — возможно, мы уже на шаге пароля.");
        }

        // Шаг 2. Пароль. Поле появляется не сразу, поэтому ждём.
        var passwordAppeared = await WaitForFieldAsync(page, FieldKind.Password, TimeSpan.FromSeconds(25));
        if (!passwordAppeared)
        {
            _logger.Warn("Автовход: поле пароля не появилось. Войдите в браузере вручную.");
            await SaveErrorScreenshotAsync(page, "autologin-no-password-field");
            return false;
        }

        if (!await TryFillFieldAsync(page, FieldKind.Password, _unityPassword!))
        {
            _logger.Warn("Автовход: не удалось заполнить поле пароля.");
            return false;
        }

        _logger.Info("Автовход, шаг 2: пароль введён.");

        if (!await TryClickPrimaryButtonAsync(page))
        {
            _logger.Warn("Автовход: кнопка входа не найдена. Нажмите её в браузере сами.");
            await SaveErrorScreenshotAsync(page, "autologin-no-submit-button");
            return false;
        }

        _logger.Info("Автовход, шаг 2: кнопка входа нажата. Ждём подтверждения сессии.");
        return true;
    }

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

        page.PageError += (_, e) => _logger.Warn($"PAGE ERROR: {e.Message}");
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

    private async Task<ProcessResult> ProcessAssetAsync(IPage page, string assetUrl, string? promoCode = null)
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

                var ready = await WaitForAssetSignalsAsync(page, TimeSpan.FromMilliseconds(_options.AssetUiTimeoutMs));
                if (!ready)
                {
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
                        return await ProcessPromoAssetAsync(page, assetUrl, promoCode, result);
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
                    result.Status = AssetProcessStatus.Failed;
                    result.Message =
                        "Кнопка Add to My Assets не найдена (возможно требуется вход или изменена верстка).";
                    _logger.Info($"[Ошибка] Не удалось обработать ассет: {assetUrl}. Причина: {result.Message}");
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
        catch (Exception ex)
        {
            result.Status = AssetProcessStatus.Failed;
            result.Message = ex.Message;
            _logger.Info($"[Ошибка] Исключение при обработке ассета: {ex.Message} ({assetUrl})");
            await SaveErrorScreenshotAsync(page, "processing-error");
            return result;
        }
    }

    private async Task<ProcessResult> ProcessPromoAssetAsync(IPage page, string assetUrl, string promoCode, ProcessResult result)
    {
        var sanitizedId = SanitizeFileName(assetUrl.Split('/').Last());
        var totalSw = Stopwatch.StartNew();
        _logger.Info($"[Промокод] ===== НАЧАЛО выкупа по промокоду '{promoCode}' | ассет: {assetUrl} =====");

        try
        {
            // 1. Нажимаем кнопку "Add to Cart" или "Buy Now"
            var stepSw = Stopwatch.StartNew();
            _logger.Info($"[Промокод][Шаг 1] Попытка нажать 'Add to Cart' / 'Buy Now' | URL: {page.Url}");
            await LogAllButtonsAsync(page, "Шаг 1 - кнопки до клика");
            var clicked = await TryClickAddToCartOrBuyNowButtonAsync(page);
            _logger.Info($"[Промокод][Шаг 1] clicked={clicked} | {stepSw.ElapsedMilliseconds}мс");
            if (!clicked)
            {
                result.Status = AssetProcessStatus.Failed;
                result.Message = "Не удалось нажать кнопку Add to Cart или Buy Now на странице ассета.";
                _logger.Warn($"[Ошибка][Шаг 1] {result.Message}");
                await SaveErrorScreenshotAsync(page, $"promo_failed_click_{sanitizedId}");
                await SaveHtmlDumpAsync(page, $"promo_dump_step1_no_btn_{sanitizedId}");
                return result;
            }


            await Task.Delay(3000);
            _logger.Debug($"[Промокод][Шаг 1] URL после клика: {page.Url}");
            await SaveErrorScreenshotAsync(page, $"promo_added_to_cart_{sanitizedId}");

            // 2. Ожидаем автоматического перехода в корзину или чекаут
            stepSw.Restart();
            _logger.Info($"[Промокод][Шаг 2] Ожидание редиректа в корзину/чекаут...");
            var redirected = false;
            for (var i = 0; i < 15; i++)
            {
                _logger.Debug($"[Промокод][Шаг 2] Проверка URL [{i+1}/15]: {page.Url}");
                if (page.Url.Contains("pay.unity.com", StringComparison.OrdinalIgnoreCase) ||
                    page.Url.Contains("/cart", StringComparison.OrdinalIgnoreCase) ||
                    page.Url.Contains("/checkout", StringComparison.OrdinalIgnoreCase))
                {
                    redirected = true;
                    _logger.Info($"[Промокод][Шаг 2] Автоматический переход зафиксирован: {page.Url} | {stepSw.ElapsedMilliseconds}мс");
                    break;
                }
                await Task.Delay(1000);
            }


            if (!redirected)
            {
                _logger.Info($"[Промокод][Шаг 2] Авторедирект не произошёл за 15с. Принудительный переход на /cart | URL сейчас: {page.Url}");
                await SafeGoToAsync(page, "https://assetstore.unity.com/cart");
                await Task.Delay(3000);
                _logger.Debug($"[Промокод][Шаг 2] URL после принудительного перехода: {page.Url}");
            }


            await SaveErrorScreenshotAsync(page, $"promo_pay_page_{sanitizedId}");

            // 3. Ожидание полей ввода
            stepSw.Restart();
            _logger.Info($"[Промокод][Шаг 3] Ожидание элементов страницы корзины (poле промо / кнопка оплаты) | URL: {page.Url}");
            var elementsReady = await WaitForCartPageElementsAsync(page, TimeSpan.FromSeconds(30));
            _logger.Info($"[Промокод][Шаг 3] elementsReady={elementsReady} | {stepSw.ElapsedMilliseconds}мс");
            if (!elementsReady)
            {
                result.Status = AssetProcessStatus.Failed;
                result.Message = "Не удалось загрузить элементы оформления заказа (поле ввода или кнопки оплаты).";
                _logger.Warn($"[Ошибка][Шаг 3] {result.Message}");

                await LogAllInputFieldsAsync(page, "Шаг 3 - элементы не найдены");
                await LogAllButtonsAsync(page, "Шаг 3 - кнопки");
                await SaveErrorScreenshotAsync(page, $"promo_failed_elements_{sanitizedId}");
                await SaveHtmlDumpAsync(page, $"promo_dump_step3_no_elements_{sanitizedId}");
                await ClearCartAsync(page);
                return result;
            }

            // 4. Обработка шага налогообложения "Tax Business use"
            stepSw.Restart();
            _logger.Info($"[Промокод][Шаг 4] Проверка налогового вопроса 'Tax Business use' | URL: {page.Url}");

            var taxHandled = await page.EvaluateFunctionAsync<bool>(@"() => {
                const visible = (el) => {
                    if (!el) return false;
                    const style = window.getComputedStyle(el);
                    const rect = el.getBoundingClientRect();
                    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
                };

                const labels = Array.from(document.querySelectorAll('label, span, div'));
                const taxLabel = labels.find(el => {
                    const txt = (el.innerText || '').toLowerCase();
                    return txt.includes('tax business use') || txt.includes('business use') || txt.includes('коммерческ') || txt.includes('для бизнеса') || txt.includes('предпринимател') || txt.includes('инн') || txt.includes('tax number');
                });

                if (!taxLabel) return false;

                const inputs = Array.from(document.querySelectorAll('input[type=""radio""]'));
                let noRadio = null;
                for (const input of inputs) {
                    if (!visible(input)) continue;
                    
                    let labelText = '';
                    if (input.id) {
                        const lbl = document.querySelector(`label[for=""${input.id}""]`);
                        if (lbl) labelText = lbl.innerText;
                    }
                    if (!labelText) {
                        let parent = input.parentElement;
                        while (parent && parent !== document.body) {
                            if (parent.tagName === 'LABEL') {
                                labelText = parent.innerText;
                                break;
                            }
                            parent = parent.parentElement;
                        }
                    }

                    const labelTextNorm = labelText.toLowerCase().replace(/\s+/g, '');
                    if (labelTextNorm.includes('no') || labelTextNorm.includes('нет') || input.value.toLowerCase() === 'no') {
                        noRadio = input;
                        break;
                    }
                }

                if (!noRadio) {
                    const clickables = Array.from(document.querySelectorAll('button, span, div, label')).filter(visible);
                    noRadio = clickables.find(el => {
                        const t = (el.innerText || '').trim().toLowerCase();
                        return t === 'no' || t === 'нет';
                    });
                }

                if (noRadio) {
                    noRadio.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                    if (typeof noRadio.click === 'function') {
                        noRadio.click();
                    }
                    noRadio.dispatchEvent(new Event('change', { bubbles: true }));
                    return true;
                }

                return false;
            }");

            if (taxHandled)
            {
                _logger.Info("[Промокод] Вопрос налогообложения обнаружен: выбран вариант 'No'.");
                _logger.Info($"[Промокод][Шаг 4] Налоговый вопрос обнаружен: выбран 'No' | {stepSw.ElapsedMilliseconds}мс");
                await Task.Delay(2000);
                await SaveErrorScreenshotAsync(page, $"promo_tax_selected_{sanitizedId}");
            }
            else
            {
                _logger.Debug("[Промокод] Вопрос налогообложения 'Tax Business use' не найден (возможно, не pay.unity.com или шаг пропущен).");
                _logger.Debug($"[Промокод][Шаг 4] Налоговый вопрос 'Tax Business use' не найден (не pay.unity.com или пропущен) | {stepSw.ElapsedMilliseconds}мс | URL: {page.Url}");
            }

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
            _logger.Info($"[Промокод] Ввод промокода '{promoCode}'...");
            stepSw.Restart();
            _logger.Info($"[Промокод][Шаг 5] Поиск поля ввода промокода и ввод '{promoCode}' | URL: {page.Url}");
            await LogAllInputFieldsAsync(page, "Шаг 5 - все input перед вводом кода");
            var promoEntered = await page.EvaluateFunctionAsync<bool>(@"async (code) => {
                const normalize = (v) => (v || '').replace(/\s+/g, ' ').trim().toLowerCase();
                const visible = (el) => {
                    if (!el) return false;
                    const style = window.getComputedStyle(el);
                    const rect = el.getBoundingClientRect();
                    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
                };

                const inputs = Array.from(document.querySelectorAll('input'));
                const promoInput = inputs.find(el => {
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

                if (!promoInput) return false;

                promoInput.value = code;
                promoInput.dispatchEvent(new Event('input', { bubbles: true }));
                promoInput.dispatchEvent(new Event('change', { bubbles: true }));
                return true;
            }", promoCode);

            _logger.Info($"[Промокод][Шаг 5] promoEntered={promoEntered} | {stepSw.ElapsedMilliseconds}мс");
            if (!promoEntered)
            {
                result.Status = AssetProcessStatus.Failed;
                result.Message = "Не найдено поле ввода промокода.";
                _logger.Warn($"[Ошибка][Шаг 5] {result.Message} | URL: {page.Url}");

                await LogAllInputFieldsAsync(page, "Шаг 5 - поле не найдено, все inputs");
                await LogAllButtonsAsync(page, "Шаг 5 - кнопки при ошибке");
                await SaveErrorScreenshotAsync(page, $"promo_failed_input_{sanitizedId}");
                await SaveHtmlDumpAsync(page, $"promo_dump_step5_no_input_{sanitizedId}");
                await ClearCartAsync(page);
                return result;
            }


            var applyClicked = await page.EvaluateFunctionAsync<bool>(@"() => {
                const normalize = (v) => (v || '').replace(/\s+/g, ' ').trim().toLowerCase();
                const visible = (el) => {
                    if (!el) return false;
                    const style = window.getComputedStyle(el);
                    const rect = el.getBoundingClientRect();
                    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
                };

                const buttons = Array.from(document.querySelectorAll('button, a, [role=""button""]')).filter(visible);
                let applyBtn = buttons.find(el => {
                    const txt = normalize(el.innerText || '');
                    return txt === 'apply' || txt === 'redeem' || txt === 'submit' || txt.includes('apply') || txt.includes('применить') || txt.includes('ввести');
                });

                if (!applyBtn) {
                    const inputs = Array.from(document.querySelectorAll('input'));
                    const promoInput = inputs.find(el => {
                        const placeholder = normalize(el.placeholder || '');
                        const name = normalize(el.name || '');
                        const id = normalize(el.id || '');
                        
                        const isAddressField = 
                            name.includes('postal') || name.includes('zip') || name.includes('address') || name.includes('phone') || name.includes('city') || name.includes('state') || name.includes('country') || name.includes('company') || name.includes('name') || name.includes('email') ||
                            id.includes('postal') || id.includes('zip') || id.includes('address') || id.includes('phone') || id.includes('city') || id.includes('state') || id.includes('country') || id.includes('company') || id.includes('name') || id.includes('email') ||
                            placeholder.includes('zip') || placeholder.includes('postal') || placeholder.includes('address') || placeholder.includes('phone') || placeholder.includes('city') || placeholder.includes('state') || placeholder.includes('country') || placeholder.includes('email');

                        if (isAddressField) return false;

                        return placeholder.includes('coupon') || placeholder.includes('promo') || placeholder.includes('code') || placeholder.includes('купон') || placeholder.includes('промо') || placeholder.includes('код') ||
                               name.includes('coupon') || name.includes('promo') || name.includes('code') ||
                               id.includes('coupon') || id.includes('promo') || id.includes('code');
                    });
                    if (promoInput) {
                        let parent = promoInput.parentElement;
                        while (parent && parent !== document.body) {
                            const btnInParent = Array.from(parent.querySelectorAll('button, a, [role=""button""]')).filter(visible)[0];
                            if (btnInParent) {
                                applyBtn = btnInParent;
                                break;
                            }
                            parent = parent.parentElement;
                        }
                    }
                }

                if (!applyBtn) return false;
                applyBtn.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                return true;
            }");

            // 5.5 - Apply button
            stepSw.Restart();
            _logger.Info($"[Промокод][Шаг 5.5] Нажатие кнопки Apply/Redeem | URL: {page.Url}");
            if (!applyClicked)
            {
                result.Status = AssetProcessStatus.Failed;
                result.Message = "Не найдена кнопка Apply для промокода.";
                _logger.Warn($"[Ошибка][Шаг 5.5] {result.Message} | URL: {page.Url}");
                await LogAllButtonsAsync(page, "Шаг 5.5 - кнопки при ошибке Apply");
                await SaveErrorScreenshotAsync(page, $"promo_failed_apply_{sanitizedId}");
                await SaveHtmlDumpAsync(page, $"promo_dump_step5_5_no_apply_{sanitizedId}");
                await ClearCartAsync(page);
                return result;
            }

            _logger.Info($"[Промокод][Шаг 5.5] Кнопка Apply нажата | {stepSw.ElapsedMilliseconds}мс. Ожидание обновления стоимости (4с)...");
            await Task.Delay(4000);
            _logger.Debug($"[Промокод][Шаг 5.5] URL после Apply: {page.Url}");

            await SaveErrorScreenshotAsync(page, $"promo_coupon_applied_{sanitizedId}");

            // 6. Проверка ошибок применения промокода и обнуления цены
            stepSw.Restart();
            _logger.Info($"[Промокод][Шаг 6] Проверка статуса корзины (ошибки кода / цена = $0) | URL: {page.Url}");
            var cartState = await page.EvaluateFunctionAsync<string>(@"() => {
                const normalize = (v) => (v || '').replace(/\s+/g, ' ').trim().toLowerCase();
                const visible = (el) => {
                    if (!el) return false;
                    const style = window.getComputedStyle(el);
                    const rect = el.getBoundingClientRect();
                    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
                };

                const bodyText = (document.body?.innerText || '');
                const normBody = normalize(bodyText);

                const errorKeywords = ['expired', 'invalid', 'is not valid', 'недействителен', 'истек', 'ошибка', 'coupon limit'];
                let hasPromoError = false;
                let foundError = '';
                for (const kw of errorKeywords) {
                    if (normBody.includes(kw)) {
                        hasPromoError = true;
                        foundError = kw;
                        break;
                    }
                }

                const candidates = Array.from(document.querySelectorAll('span, div, p, strong, td')).filter(visible);
                
                const hasZero = candidates.some(x => {
                    const t = normalize(x.innerText || '');
                    return t === '$0.00' || t === '$0,00' || t === '$0' || t === 'free' || t === 'бесплатно' || 
                           t.includes('0.00') || t.includes('0,00') || t.includes('free') || t.includes('бесплатно');
                });

                return JSON.stringify({
                    hasPromoError,
                    foundError,
                    hasZeroPrice: hasZero,
                    bodyTextPreview: bodyText.substring(0, 500)
                    bodyTextPreview: bodyText.substring(0, 3000)
                });
            }");

            var state = JsonSerializer.Deserialize<CartStateSnapshot>(cartState, _runtimeJsonOptions) ?? new CartStateSnapshot();
            _logger.Debug($"[Промокод] Статус корзины: hasPromoError={state.HasPromoError} (код: {state.FoundError}), hasZeroPrice={state.HasZeroPrice}");
            _logger.Info($"[Промокод][Шаг 6] CartState: hasPromoError={state.HasPromoError} (keyword: '{state.FoundError}'), hasZeroPrice={state.HasZeroPrice} | {stepSw.ElapsedMilliseconds}мс");
            _logger.Debug($"[Промокод][Шаг 6] BodyText (первые 3000 символов):\n{state.BodyTextPreview}");

            if (state.HasPromoError || !state.HasZeroPrice)
            {
                result.Status = AssetProcessStatus.Failed;
                result.Message = state.HasPromoError
                    ? $"Промокод не был применен: обнаружена ошибка '{state.FoundError}'."
                    : "Промокод введен, но итоговая стоимость не стала бесплатной ($0.00).";
                _logger.Warn($"[Ошибка][Шаг 6] {result.Message} Абортируем оформление. URL: {page.Url}");
                await SaveErrorScreenshotAsync(page, $"promo_failed_error_{sanitizedId}");
                await SaveHtmlDumpAsync(page, $"promo_dump_step6_promo_failed_{sanitizedId}");
                await ClearCartAsync(page);
                return result;
            }

            // 7. Прохождение EULA (согласие с EULA чекбоксом)
            stepSw.Restart();
            _logger.Info($"[Промокод][Шаг 7] Поиск и принятие EULA-чекбокса | URL: {page.Url}");


            var eulaHandled = await page.EvaluateFunctionAsync<bool>(@"() => {
                const visible = (el) => {
                    if (!el) return false;
                    const style = window.getComputedStyle(el);
                    const rect = el.getBoundingClientRect();
                    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
                };

                const checkboxes = Array.from(document.querySelectorAll('input[type=""checkbox""]')).filter(visible);
                let eulaCheckbox = null;
                for (const checkbox of checkboxes) {
                    let labelText = '';
                    if (checkbox.id) {
                        const lbl = document.querySelector(`label[for=""${checkbox.id}""]`);
                        if (lbl) labelText = lbl.innerText;
                    }
                    if (!labelText) {
                        let parent = checkbox.parentElement;
                        while (parent && parent !== document.body) {
                            if (parent.tagName === 'LABEL' || parent.innerText.trim().length > 0) {
                                labelText = parent.innerText;
                                break;
                            }
                            parent = parent.parentElement;
                        }
                    }

                    const lt = labelText.toLowerCase();
                    if (lt.includes('understand and agree') || lt.includes('eula') || lt.includes('license agreement') || lt.includes('withdrawal') || lt.includes('согласен') || lt.includes('условия')) {
                        eulaCheckbox = checkbox;
                        break;
                    }
                }

                if (!eulaCheckbox && checkboxes.length > 0) {
                    eulaCheckbox = checkboxes[0];
                }

                if (eulaCheckbox) {
                    if (!eulaCheckbox.checked) {
                        eulaCheckbox.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                        if (typeof eulaCheckbox.click === 'function') {
                            eulaCheckbox.click();
                        }
                        eulaCheckbox.dispatchEvent(new Event('change', { bubbles: true }));
                    }
                    return true;
                }

                return false;
            }");

            if (eulaHandled)
            {
                _logger.Info("[Промокод] Согласие с EULA успешно отмечено.");
                _logger.Info($"[Промокод][Шаг 7] EULA-чекбокс отмечен | {stepSw.ElapsedMilliseconds}мс");
                await Task.Delay(1000);
            }
            else
            {
                _logger.Warn("[Промокод] Предупреждение: чекбокс соглашения с EULA не обнаружен.");
                _logger.Warn($"[Промокод][Шаг 7] EULA-чекбокс не обнаружен (возможно, не требуется) | URL: {page.Url}");
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

            _logger.Info($"[Промокод][Шаг 8] Кнопка Pay нажата. Ожидание завершения транзакции (10с)...");
            await Task.Delay(10000);
            _logger.Debug($"[Промокод][Шаг 8] URL после транзакции: {page.Url}");

            await SaveErrorScreenshotAsync(page, $"promo_checkout_success_{sanitizedId}");

            // 9. Проверка успешного завершения покупки
            stepSw.Restart();
            _logger.Info($"[Промокод][Шаг 9] Проверка успешного завершения | URL: {page.Url}");
            var successState = await page.EvaluateFunctionAsync<bool>(@"() => {
                const normalize = (v) => (v || '').replace(/\s+/g, ' ').trim().toLowerCase();
                const text = normalize(document.body?.innerText || '');
                return text.includes('thank you') || text.includes('success') || text.includes('order completed') || text.includes('успешно') || text.includes('спасибо за покупку') || window.location.href.includes('success');
            }");

            if (successState || page.Url.Contains("success", StringComparison.OrdinalIgnoreCase))
            {
                result.Status = AssetProcessStatus.Added;
                _logger.Info($"[УСПЕХ] Ассет успешно получен по промокоду: {assetUrl}");
                _logger.Info($"[УСПЕХ][Шаг 9] Ассет получен по промокоду! Итого: {totalSw.Elapsed.TotalSeconds:F1}с | URL: {page.Url}");
            }
            else
            {
                result.Status = AssetProcessStatus.UnknownAfterClick;
                _logger.Warn($"[Внимание] Кнопка оформления по промокоду нажата, но переход на страницу успешного завершения не зафиксирован: {page.Url}");
                _logger.Warn($"[Внимание][Шаг 9] Кнопка Pay нажата, но страница успеха не зафиксирована. Итого: {totalSw.Elapsed.TotalSeconds:F1}с | URL: {page.Url}");
                await SaveHtmlDumpAsync(page, $"promo_dump_step9_unknown_{sanitizedId}");
            }

            _logger.Info($"[Промокод] ===== КОНЕЦ выкупа по промокоду '{promoCode}' | статус: {result.Status} | {totalSw.Elapsed.TotalSeconds:F1}с =====");
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


    private async Task ClearCartAsync(IPage page)
    {
        try
        {
            _logger.Info("[Очистка корзины] Переход на https://assetstore.unity.com/cart для очистки...");
            await SafeGoToAsync(page, "https://assetstore.unity.com/cart");
            await Task.Delay(3000);

            var cleared = await page.EvaluateFunctionAsync<bool>(@"() => {
                const normalize = (v) => (v || '').replace(/\s+/g, ' ').trim().toLowerCase();
                const visible = (el) => {
                    if (!el) return false;
                    const style = window.getComputedStyle(el);
                    const rect = el.getBoundingClientRect();
                    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
                };

                const buttons = Array.from(document.querySelectorAll('button, a, [role=""button""]')).filter(visible);
                const removeButtons = buttons.filter(el => {
                    const txt = normalize(el.innerText || '');
                    const label = normalize(el.getAttribute('aria-label') || '');
                    return txt === 'remove' || txt === 'delete' || txt === 'удалить' || label.includes('remove') || label.includes('delete');
                });

                if (removeButtons.length === 0) return false;

                for (const btn of removeButtons) {
                    btn.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                    if (typeof btn.click === 'function') {
                        btn.click();
                    }
                }
                return true;
            }");

            if (cleared)
            {
                _logger.Info("[Очистка корзины] Элементы удалены из корзины.");
                await Task.Delay(2000);
            }
            else
            {
                _logger.Debug("[Очистка корзины] Корзина пуста или кнопки удаления не найдены.");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Очистка корзины] Не удалось очистить корзину: {ex.Message}");
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
                               txt.includes('оформить') || txt.includes('оплатить') || txt.includes('купить') || txt.includes('заказать');
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

    private static async Task<bool> TryClickAddToCartOrBuyNowButtonAsync(IPage page)
    {
        return await page.EvaluateFunctionAsync<bool>(@"() => {
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
                    return txt.includes('add to cart') || txt.includes('buy now') || txt.includes('buy');
                });

            const collectClickables = (root) => Array.from(root.querySelectorAll('button, a, [role=""button""]'))
                .filter(visible)
                .map(el => ({
                    element: el,
                    text: normalize(el.innerText)
                }))
                .filter(x => !!x.text);

            const clickFrom = (items) => {
                const addCart = items.find(x => x.text.includes('add to cart'));
                const buyNow = items.find(x => x.text.includes('buy now') || x.text === 'buy');
                const target = addCart || buyNow;

                if (!target) return false;
                
                setTimeout(() => {
                    if (typeof target.element.click === 'function') {
                        target.element.click();
                    } else {
                        target.element.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                    }
                }, 50);
                
                return true;
            };

            for (const root of roots) {
                const items = collectClickables(root);
                if (clickFrom(items)) return true;
            }

            const fallback = Array.from(document.querySelectorAll('button, a, [role=""button""]'))
                .filter(visible)
                .map(el => ({ element: el, text: normalize(el.innerText) }))
                .filter(x => !!x.text);

            return clickFrom(fallback);
        }");
    }

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
    public const string ProblemsFileName = "ПРИШЛИТЕ-ЭТОТ-ФАЙЛ.log";

    public string? LogFilePath { get; init; }
    public string SignInUrl { get; init; } = DefaultSignInUrl;
    public string ProfileName { get; init; } = "default";
    public bool ListProfiles { get; init; }
    public bool CheckLoginPage { get; init; }
    public bool CheckTelegram { get; init; }
    public string? TelegramProxy { get; init; }
    public string? ChromeUserDataDir { get; init; }
    public bool UseSystemChromeProfile { get; init; }
    public bool? SavePassword { get; init; }
    public bool Interactive { get; init; }
    public string LogsDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "logs");
    public string DataDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "data");
    public string? UnityEmail { get; init; }
    public string? UnityPassword { get; init; }
    public int DelayMs { get; init; } = 1200;
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
    public int TelegramPostLimit { get; init; } = 20;
    public bool TelegramScreenshotOnNoLinks { get; init; } = true;

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
        string? cliLogFilePath = null;
        string? cliLogsDirectory = null;
        string? cliSignInUrl = null;
        string? cliProfile = null;
        string? cliSetDefaultProfile = null;
        var cliListProfiles = false;
        var cliCheckLoginPage = false;
        var cliCheckTelegram = false;
        string? cliTelegramProxy = null;
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
                case "--check-telegram":
                    cliCheckTelegram = true;
                    break;
                case "--tg-proxy" when i + 1 < args.Length:
                    cliTelegramProxy = args[++i];
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

        var headless = cliHeadless ?? config?.Headless ?? false;
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
        if (cliTelegramChannels.Count > 0)
        {
            telegramChannels.AddRange(cliTelegramChannels);
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
        var interactive = cliInteractive ?? config?.Interactive ?? !Console.IsInputRedirected;

        var telegramPostLimit = config?.Telegram?.PostLimit ?? 20;
        var telegramScreenshotOnNoLinks = config?.Telegram?.ScreenshotOnNoLinks ?? true;

        return new CliOptions
        {
            LoginOnly = loginOnly,
            DryRun = dryRun,
            Headless = headless,
            Verbose = verbose,
            TraceNetwork = traceNetwork,
            UseExtendedSources = useExtendedSources,
            UseNoDefaults = useNoDefaults,
            ExtraSourceFiles = extraSourceFiles,
            LogFilePath = logFilePath,
            LogsDirectory = logsDirectory,
            SignInUrl = signInUrl,
            ProfileName = profileName,
            ListProfiles = cliListProfiles,
            CheckLoginPage = cliCheckLoginPage,
            CheckTelegram = cliCheckTelegram,
            TelegramProxy = FirstNonEmpty(cliTelegramProxy, config?.Telegram?.Proxy),
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
    public List<string> Channels { get; init; } = [];
    public int PostLimit { get; init; } = 20;
    public bool ScreenshotOnNoLinks { get; init; } = true;
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
    Failed
}

internal sealed class CartStateSnapshot
{
    public bool HasPromoError { get; set; }
    public string FoundError { get; set; } = string.Empty;
    public bool HasZeroPrice { get; set; }
    public string BodyTextPreview { get; set; } = string.Empty;
}
