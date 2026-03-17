using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using PuppeteerSharp;

var options = CliOptions.Parse(args);
var app = new UnityAssetAutomationApp(options);
await app.RunAsync();

internal sealed class UnityAssetAutomationApp
{
    private const string AssetStoreHomeUrl = "https://assetstore.unity.com/";
    private const string AssetStoreSignInUrl = "https://login.unity.com/en/sign-in";
    private const string BaseTopFreeSource = "https://assetstore.unity.com/top-assets/top-free";
    private const string BaseFreeListFileName = "free_list_GreaterChinaUnityAssetArchiveLinks.txt";

    private readonly CliOptions _options;
    private readonly AppLogger _logger;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly JsonSerializerOptions _runtimeJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
    private readonly string _logsDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
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

    private static readonly string[] ExtendedSources =
    [
        "https://vanquish3r.github.io/greater-china-unity-assets/",
        "https://assetstore.unity.com/search#nf-ec_price_filter=0...0",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=96",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=192",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=288",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=384",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=480",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=576",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=672",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=768",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=864",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=960",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=1056",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=1152",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=1248",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=1344",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=1440",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=1536",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=1632",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=1728",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=1824",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=1920",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=2016",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=2112",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=2208",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=2304",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=2400",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=2496",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=2592",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=2688",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=2784",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=2880",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=2976",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=3072",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=3168",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=3264",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=3360",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=3456",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=3552",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=3648",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=3744",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=3840",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=3936",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=4032",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=4128",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=4224",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=4320",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=4416",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=4512",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=4608",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=4704",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=4800",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=4896",
        "https://assetstore.unity.com/search?hideOwnership=true#nf-ec_price_filter=0...0&firstResult=4992"
    ];

    public UnityAssetAutomationApp(CliOptions options)
    {
        _options = options;
        _cookiesPath = Path.Combine(_dataDirectory, "unity_cookies.json");
        _sessionStatePath = Path.Combine(_dataDirectory, "unity_session_state.json");
        _reportPath = Path.Combine(_logsDirectory, $"run-report-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var logFilePath = string.IsNullOrWhiteSpace(options.LogFilePath)
            ? Path.Combine(_logsDirectory, $"run-log-{DateTime.Now:yyyyMMdd-HHmmss}.log")
            : Path.GetFullPath(options.LogFilePath);
        _logger = new AppLogger(options.Verbose, options.TraceNetwork, logFilePath);
    }

    public async Task RunAsync()
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            Directory.CreateDirectory(_logsDirectory);

            _logger.Info("Подготовка браузера Chromium...");
            await new BrowserFetcher().DownloadAsync();

            await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = _options.Headless,
                DefaultViewport = null,
                Args = ["--start-maximized"]
            });

            await using var page = await browser.NewPageAsync();
            page.DefaultNavigationTimeout = _options.NavigationTimeoutMs;
            page.DefaultTimeout = _options.NavigationTimeoutMs;
            AttachPageDiagnostics(page);

            var authenticated = await EnsureAuthenticatedAsync(page);
            if (!authenticated)
            {
                _logger.Error("Не удалось подтвердить авторизацию. Завершение.");
                return;
            }

            if (_options.LoginOnly)
            {
                _logger.Info("Режим --login завершен: cookies обновлены.");
                return;
            }

            var sources = ResolveSources();
            var assetUrls = await CollectAssetUrlsAsync(page, sources);

            _logger.Info($"Найдено уникальных ассетов: {assetUrls.Count}");
            var report = new RunReport
            {
                StartedAtUtc = DateTime.UtcNow,
                DryRun = _options.DryRun,
                Sources = sources
            };

            var freeAssetsProcessed = 0;
            if (_options.MaxAddAttempts.HasValue)
            {
                _logger.Info($"Включен лимит по бесплатным ассетам: {_options.MaxAddAttempts.Value}");
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
                    _logger.Warn($"Достигнут лимит посещенных ассетов ({index}/{_options.MaxVisitedAssets.Value}). Обработка остановлена.");
                    break;
                }

                if (_options.MaxAddAttempts.HasValue && freeAssetsProcessed >= _options.MaxAddAttempts.Value)
                {
                    _logger.Warn($"Достигнут лимит бесплатных ассетов ({freeAssetsProcessed}/{_options.MaxAddAttempts.Value}). Обработка остановлена.");
                    break;
                }

                index++;
                _logger.Info($"[{index}/{assetUrls.Count}] Обработка: {assetUrl}");

                var result = await ProcessAssetAsync(page, assetUrl);
                report.Items.Add(result);

                if (result.CountsTowardsAddLimit)
                {
                    freeAssetsProcessed++;
                    if (_options.MaxAddAttempts.HasValue)
                    {
                        _logger.Info($"Лимит-счетчик бесплатных ассетов: {freeAssetsProcessed}/{_options.MaxAddAttempts.Value} (статус: {result.Status})");
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(_options.DelayMs));
            }

            report.FinishedAtUtc = DateTime.UtcNow;
            await File.WriteAllTextAsync(_reportPath, JsonSerializer.Serialize(report, _jsonOptions));

            PrintSummary(report);
            _logger.Info($"Отчет сохранен: {_reportPath}");
        }
        finally
        {
            _logger.Dispose();
        }
    }

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

        if (_lastFullAuthAttemptUtc.HasValue)
        {
            var elapsed = DateTime.UtcNow - _lastFullAuthAttemptUtc.Value;
            if (elapsed < FullAuthCooldown)
            {
                var waitLeft = (int)Math.Ceiling((FullAuthCooldown - elapsed).TotalSeconds);
                _logger.Warn($"Полный SSO-вход запрашивается слишком часто. Выжидаем cooldown: {Math.Max(1, waitLeft)}с...");
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, waitLeft)));
            }
        }

        _logger.Warn("Требуется вход в Unity. Запуск SSO через Asset Store...");
        _lastFullAuthAttemptUtc = DateTime.UtcNow;
        var authenticated = await AuthenticateViaAssetStoreAsync(page);
        if (!authenticated)
        {
            _logger.Error("Проверка после попытки входа неуспешна.");
            return false;
        }

        await SaveSessionStateAsync(page);
        _logger.Info("Авторизация подтверждена, состояние сессии сохранено.");
        return true;
    }

    private async Task<bool> ValidateSessionForAssetStoreAsync(IPage page, string stage)
    {
        try
        {
            _logger.Debug($"AuthProbe[{stage}]: проверка стабильности сессии на {BaseTopFreeSource}");
            await SafeGoToAsync(page, BaseTopFreeSource);
            await WaitForDocumentReadySoftAsync(page, TimeSpan.FromSeconds(6));

            if (page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase) || IsLikelySignOutFlowUrl(page.Url))
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
                if (page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase) || IsLikelySignOutFlowUrl(page.Url))
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
        if (_options.HasCredentials)
        {
            _logger.Info("Найдены учетные данные для автовхода. Будет выполнена автоматическая отправка формы.");
        }
        else
        {
            _logger.Info("Учетные данные для автовхода не заданы. Выполните вход в браузере вручную, скрипт продолжит автоматически.");
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            _logger.Info($"Попытка авторизации {attempt}/3...");
            await StartAssetStoreSsoAsync(page);

            if (_options.HasCredentials)
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
        _logger.Info("AuthStep: open-home");
        await SafeGoToAsync(page, AssetStoreHomeUrl);

        var clickedSignInFromMenu = await TryTriggerSignInFromHomeUiAsync(page);
        if (!clickedSignInFromMenu)
        {
            _logger.Warn("Не удалось перейти к Sign In через меню профиля. Пробуем альтернативный путь входа...");
        }

        if (!page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
        {
            var clickedSignInWithUnity = await TryClickSignInWithUnityAsync(page);
            if (clickedSignInWithUnity)
            {
                _logger.Info("AuthStep: click-sign-in-with-unity");
            }
        }

        if (!page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warn("Не удалось перейти на login.unity.com через UI. Используем прямой переход как fallback.");
            await SafeGoToAsync(page, AssetStoreSignInUrl);
        }

        _logger.Info($"SSO запущен, текущий URL: {page.Url}");
    }

    private async Task<bool> WaitForAuthenticatedSessionAsync(IPage page, TimeSpan timeout)
    {
        var stopAt = DateTime.UtcNow.Add(timeout);
        var redirectedToAssetStore = false;

        while (DateTime.UtcNow < stopAt)
        {
            if (page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
            {
                await TrySwitchToSignInPageAsync(page);
                if (_options.HasCredentials)
                {
                    await TryCompleteUnityLoginFormAsync(page);
                }

                await Task.Delay(1500);
                continue;
            }

            if (page.Url.Contains("assetstore.unity.com", StringComparison.OrdinalIgnoreCase))
            {
                redirectedToAssetStore = false;
                if (await HasAuthMarkersAsync(page))
                {
                    _logger.Info("AuthStep: auth-confirmed");
                    return true;
                }

                if (!_options.HasCredentials)
                {
                    var clicked = await TryTriggerSignInFromHomeUiAsync(page) || await TryClickSignInWithUnityAsync(page);
                    if (clicked)
                    {
                        _logger.Info("AuthStep: wait-user-login");
                    }
                }
            }
            else if (!redirectedToAssetStore)
            {
                _logger.Debug($"AuthWait: сторонний URL '{page.Url}'. Возвращаемся в Asset Store для проверки статуса...");
                await SafeGoToAsync(page, AssetStoreHomeUrl);
                redirectedToAssetStore = true;
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
            await SafeGoToAsync(page, "https://login.unity.com/en/sign-in");
        }
    }

    private async Task<bool> TryCompleteUnityLoginFormAsync(IPage page)
    {
        if (!_options.HasCredentials)
        {
            return false;
        }

        if (!page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return await page.EvaluateFunctionAsync<bool>(@"async (email, password) => {
            const emailInput = document.querySelector('input[type=""email""], input[name*=""email"" i], input[id*=""email"" i]');
            const passInput = document.querySelector('input[type=""password""], input[name*=""password"" i], input[id*=""password"" i]');
            if (!emailInput || !passInput) {
                return false;
            }

            const setValue = (el, value) => {
                el.focus();
                el.value = value;
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
            };

            setValue(emailInput, email);
            setValue(passInput, password);

            const submit = passInput.form?.querySelector('button[type=""submit""], input[type=""submit""]')
                || document.querySelector('button[type=""submit""], button[data-testid*=""sign"" i]');

            if (!submit) {
                return false;
            }

            submit.click();
            return true;
        }", _options.UnityEmail!, _options.UnityPassword!);
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
            const hasMyAssetsText = text.includes('my assets');
            const hasSignInText = text.includes('sign in');
            const hasSignInWithUnityText = text.includes('sign in with unity');
            const hasSignInWithUnityButton = Array.from(document.querySelectorAll('button, a, span'))
                .some(el => (el.innerText || '').trim().toLowerCase().includes('sign in with unity'));

            return JSON.stringify({
                hasMyAssetsLink,
                hasSignInLink,
                hasMyAssetsText,
                hasSignInText,
                hasSignInWithUnityText,
                hasSignInWithUnityButton
            });
        }"), "HasAuthMarkers(rawMarkers)");

            var markers = JsonSerializer.Deserialize<AuthUiMarkers>(rawMarkers ?? "{}", _runtimeJsonOptions) ?? new AuthUiMarkers();
            var hasUiAuthMarkers = (markers.HasMyAssetsLink || markers.HasMyAssetsText) &&
                                   !(markers.HasSignInLink && markers.HasSignInText && !markers.HasMyAssetsText);
            var hasUiSignInMarkers = markers.HasSignInLink || markers.HasSignInText || markers.HasSignInWithUnityText || markers.HasSignInWithUnityButton;
            var profileState = await GetProfileMenuAuthStateAsync(page);

            var hasApiAuthMarkers = false;
            if (page.Url.Contains("assetstore.unity.com", StringComparison.OrdinalIgnoreCase))
            {
                hasApiAuthMarkers = await EvaluateWithRetryAsync(() => page.EvaluateFunctionAsync<bool>(@"async () => {
                try {
                    const res = await fetch('/api/users/organizations', { credentials: 'include' });
                    if (!res.ok) return false;
                    const text = (await res.text() || '').trim();
                    if (!text) return false;

                    const lower = text.toLowerCase();
                    if (lower.includes('unauthorized') || lower.includes('forbidden') || lower.includes('sign in')) {
                        return false;
                    }

                    return text.startsWith('{') || text.startsWith('[');
                } catch {
                    return false;
                }
            }"), "HasAuthMarkers(api)");
            }

            _logger.Debug($"Auth markers: UI={hasUiAuthMarkers}, API={hasApiAuthMarkers}, signInUi={hasUiSignInMarkers}, profileMenuFound={profileState.ProfileMenuFound}, profileMenuSignIn={profileState.HasSignInItem}, profileMenuSignedIn={profileState.HasSignedInItem}, page={page.Url}, myAssetsLink={markers.HasMyAssetsLink}, myAssetsText={markers.HasMyAssetsText}, signInLink={markers.HasSignInLink}, signInText={markers.HasSignInText}, signInWithUnityText={markers.HasSignInWithUnityText}, signInWithUnityButton={markers.HasSignInWithUnityButton}");

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
            const button = document.querySelector('[aria-label=""Open user profile menu""], button[aria-label*=""profile"" i], button[aria-label*=""user"" i]');
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
            var menuReady = await WaitForProfileMenuReadyAsync(page, TimeSpan.FromSeconds(12));
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

            return JsonSerializer.Deserialize<ProfileMenuAuthState>(raw ?? "{}", _runtimeJsonOptions) ?? new ProfileMenuAuthState();
        }
        catch
        {
            return new ProfileMenuAuthState();
        }
    }

    private async Task<T> EvaluateWithRetryAsync<T>(Func<Task<T>> action, string operationName, int attempts = 3, int delayMs = 250)
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
                    () => page.EvaluateFunctionAsync<bool>("() => ['interactive','complete'].includes(document.readyState)"),
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
        page.FrameNavigated += (_, e) => _logger.Debug($"FrameNavigated => {e.Frame.Url}");

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
            if (_options.Verbose)
            {
                if (IsKnownNoiseConsoleMessage(e.Message))
                {
                    return;
                }

                _logger.Debug($"BROWSER CONSOLE [{e.Message.Type}] {e.Message.Text}");
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
               text.Contains("Refused to connect to 'https://s.clarity.ms/collect'", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Amplitude snippet has been loaded", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Amplitude Logger [Error]: Failed to fetch", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Amplitude Logger [Warn]", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Load failed, error in settings", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("No visitor ID available. Load may have failed", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("/api/carts 404", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("the server responded with a status of 451", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Action dispatch error analytics/interface/load/rejected", StringComparison.OrdinalIgnoreCase);
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
        if (!File.Exists(_cookiesPath))
        {
            return false;
        }

        try
        {
            var raw = await File.ReadAllTextAsync(_cookiesPath);
            var cookies = JsonSerializer.Deserialize<List<SerializableCookie>>(raw) ?? [];
            if (cookies.Count == 0)
            {
                _logger.Warn("Файл cookies найден, но пустой.");
                return false;
            }

            await page.SetCookieAsync(cookies.Select(c => c.ToCookieParam()).ToArray());
            _logger.Info($"Загружено cookies: {cookies.Count}");
            _logger.Debug($"Домены cookies: {string.Join(", ", cookies.Select(c => c.Domain).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))}");
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
        if (File.Exists(_sessionStatePath))
        {
            try
            {
                var raw = await File.ReadAllTextAsync(_sessionStatePath);
                var state = JsonSerializer.Deserialize<SessionStateSnapshot>(raw, _runtimeJsonOptions) ?? new SessionStateSnapshot();
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

        await File.WriteAllTextAsync(_sessionStatePath, JsonSerializer.Serialize(state, _jsonOptions));

        await File.WriteAllTextAsync(_cookiesPath, JsonSerializer.Serialize(state.Cookies, _jsonOptions));
        _logger.Info($"Сохранено состояние сессии: cookies={state.Cookies.Count}, originsLocalStorage={state.LocalStorageByOrigin.Count}");
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

    private async Task<Dictionary<string, string>> CaptureLocalStorageInIsolatedPageAsync(IPage anchorPage, string origin)
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

    private async Task RestoreLocalStorageInIsolatedPageAsync(IPage anchorPage, string origin, Dictionary<string, string> values)
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
        await File.WriteAllTextAsync(_cookiesPath, JsonSerializer.Serialize(serializable, _jsonOptions));
        _logger.Info($"Сохранено cookies: {serializable.Count}");
        _logger.Debug($"Домены cookies после входа: {string.Join(", ", serializable.Select(c => c.Domain).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))}");
    }

    private async Task<List<string>> CollectAssetUrlsAsync(IPage page, IEnumerable<string> sourceUrls)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sourceUrls)
        {
            try
            {
                _logger.Info($"Чтение источника: {source}");
                List<string> sourceUrlsExtracted;

                if (Uri.TryCreate(source, UriKind.Absolute, out var sourceUri) &&
                    sourceUri.Host.Contains("assetstore.unity.com", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryNormalizeDirectAssetUrl(sourceUri, out var directAssetUrl))
                    {
                        _logger.Info($"Источник является прямой ссылкой на ассет, добавляем без парсинга карточек: {directAssetUrl}");
                        sourceUrlsExtracted = [directAssetUrl];
                    }
                    else
                    {
                        sourceUrlsExtracted = await CollectAssetUrlsFromAssetStorePageAsync(page, source);
                    }
                }
                else
                {
                    var html = await _httpClient.GetStringAsync(source);
                    sourceUrlsExtracted = ExtractAssetUrlsFromHtml(html, source).ToList();
                }

                foreach (var url in sourceUrlsExtracted)
                {
                    all.Add(url);
                }

                _logger.Info($"Источник обработан: добавлено ссылок {sourceUrlsExtracted.Count} (после фильтра PURCHASED/You own this asset).");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Ошибка источника {source}: {ex.Message}");
            }
        }

        return all.ToList();
    }

    private List<string> ResolveSources()
    {
        var sources = _options.Sources.Count > 0
            ? _options.Sources
            : new List<string> { BaseTopFreeSource };

        if (_options.Sources.Count == 0)
        {
            sources.AddRange(LoadSourcesFromFile(BaseFreeListFileName, "Файл базового списка бесплатных ассетов не найден"));
        }

        foreach (var extraFile in _options.ExtraSourceFiles)
        {
            sources.AddRange(LoadSourcesFromFile(extraFile, "Дополнительный файл ссылок не найден"));
        }

        if (_options.UseExtendedSources)
        {
            _logger.Info("Включены расширенные источники (--extended-sources). Добавляем дополнительные страницы поиска.");
            sources.AddRange(ExtendedSources);
        }

        return sources
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
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
                .Where(x => Uri.TryCreate(x, UriKind.Absolute, out var uri) && uri.Host.Contains("assetstore.unity.com", StringComparison.OrdinalIgnoreCase))
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

            if (IsLikelySignOutFlowUrl(page.Url) || page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warn($"Источник {sourceUrl}: обнаружен редирект в logout/login ({page.Url}). Выполняем переавторизацию, попытка {attempt}/3...");
                var authOk = await EnsureAuthenticatedAsync(page);
                if (!authOk)
                {
                    _logger.Warn($"Источник {sourceUrl}: переавторизация не удалась.");
                    return [];
                }

                continue;
            }

            await ScrollSourcePageAsync(page, TimeSpan.FromSeconds(20));

            if (IsLikelySignOutFlowUrl(page.Url) || page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warn($"Источник {sourceUrl}: во время скролла произошел редирект в logout/login ({page.Url}). Повторяем источник...");
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

            var parsed = JsonSerializer.Deserialize<SourceCollectionSnapshot>(raw ?? "{}", _runtimeJsonOptions) ?? new SourceCollectionSnapshot();
            _logger.Info($"Источник Asset Store: найдено карточек={parsed.TotalFound}, пропущено как owned={parsed.OwnedSkipped}, к обработке={parsed.Urls.Count}");

            if (parsed.TotalFound == 0 && attempt < 3)
            {
                _logger.Warn($"Источник {sourceUrl}: карточки не обнаружены (0). Повторяем чтение источника ({attempt}/3)...");
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
            if (IsLikelySignOutFlowUrl(page.Url) || page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug($"ScrollSourcePage: обнаружен logout/login URL ({page.Url}), досрочно останавливаем скролл.");
                return;
            }

            var currentCount = await EvaluateWithRetryAsync(() => page.EvaluateFunctionAsync<int>(@"() => document.querySelectorAll('a[href*=""/packages/""]').length"), "ScrollSourcePage(count)");

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
                () => page.EvaluateFunctionAsync<int>(@"() => { window.scrollBy(0, window.innerHeight * 1.6); return document.querySelectorAll(""a[href*='/packages/']"").length; }"),
                "ScrollSourcePage(scroll)");
            await Task.Delay(900);
        }
    }

    private static IEnumerable<string> ExtractAssetUrlsFromHtml(string html, string baseUrl)
    {
        var regex = new Regex(@"(?:https?:\/\/assetstore\.unity\.com)?\/packages\/[\w\-\/%\.~]+", RegexOptions.IgnoreCase);
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

    private async Task<ProcessResult> ProcessAssetAsync(IPage page, string assetUrl)
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
                    _logger.Warn("Не удалось дождаться появления ключевых элементов ассета (Add/Open/Sign in/Buy). Продолжаем с текущими данными страницы.");
                }

                var status = await DetectStatusAsync(page);
                result.DetectedFree = status.IsFree;
                result.DetectedOwned = status.IsOwned;
                result.CountsTowardsAddLimit = status.IsFree;
                result.PurchasedOnText = status.PurchasedOnText;
                result.DetectionSummary = string.IsNullOrWhiteSpace(status.DetectionSummary) ? "no-signals" : status.DetectionSummary;
                _logger.Debug($"CTA snapshot: openInUnity={status.HasOpenInUnity}, addToMyAssets={status.HasAddToMyAssets}, requiresLogin={status.RequiresLogin}, free={status.IsFree}, owned={status.IsOwned}");

                if (!status.HasAddToMyAssets && !status.HasOpenInUnity && !status.RequiresLogin)
                {
                    _logger.Debug("Не найдены ключевые CTA-сигналы (Add/Open/SignIn). Выполняем расширенное ожидание и повторную детекцию...");
                    await WaitForAssetSignalsAsync(page, TimeSpan.FromMilliseconds(Math.Max(_options.AssetUiTimeoutMs, 45000)));

                    status = await DetectStatusAsync(page);
                    result.DetectedFree = status.IsFree;
                    result.DetectedOwned = status.IsOwned;
                    result.CountsTowardsAddLimit = status.IsFree;
                    result.PurchasedOnText = status.PurchasedOnText;
                    result.DetectionSummary = string.IsNullOrWhiteSpace(status.DetectionSummary) ? "no-signals" : status.DetectionSummary;
                    _logger.Debug($"CTA snapshot (after extra wait): openInUnity={status.HasOpenInUnity}, addToMyAssets={status.HasAddToMyAssets}, requiresLogin={status.RequiresLogin}, free={status.IsFree}, owned={status.IsOwned}");
                }

                _logger.Debug($"Статус ассета (до действия): {status.DetectionSummary}");

                var needsReAuth = page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase) ||
                                  (status.RequiresLogin && !status.IsOwned && !status.HasAddToMyAssets);

                if (needsReAuth)
                {
                    _logger.Warn($"Обнаружены признаки неавторизованной сессии на странице ассета. Попытка переавторизации {processingAttempt}/2...");
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
                        _logger.Debug("После переавторизации CTA-сигналы все еще не готовы. Выполняем расширенное ожидание и повторную детекцию...");
                        await WaitForAssetSignalsAsync(page, TimeSpan.FromMilliseconds(Math.Max(_options.AssetUiTimeoutMs, 45000)));
                        status = await DetectStatusAsync(page);
                    }

                    result.DetectedFree = status.IsFree;
                    result.DetectedOwned = status.IsOwned;
                    result.CountsTowardsAddLimit = status.IsFree;
                    result.PurchasedOnText = status.PurchasedOnText;
                    result.DetectionSummary = string.IsNullOrWhiteSpace(status.DetectionSummary) ? "no-signals" : status.DetectionSummary;
                    _logger.Debug($"Статус ассета (после переавторизации): {status.DetectionSummary}");
                    _logger.Debug($"CTA snapshot (after re-auth): openInUnity={status.HasOpenInUnity}, addToMyAssets={status.HasAddToMyAssets}, requiresLogin={status.RequiresLogin}, free={status.IsFree}, owned={status.IsOwned}");
                }

                if (status.HasOpenInUnity || status.IsOwned)
                {
                    result.Status = AssetProcessStatus.AlreadyOwned;
                    return result;
                }

                if (!status.IsFree && !status.HasAddToMyAssets)
                {
                    result.Status = AssetProcessStatus.PaidSkipped;
                    return result;
                }

                if (_options.DryRun)
                {
                    result.Status = AssetProcessStatus.WouldAddInDryRun;
                    return result;
                }

                if (!status.HasAddToMyAssets)
                {
                    result.Status = AssetProcessStatus.Failed;
                    result.Message = "Кнопка Add to My Assets не найдена (возможно требуется вход или изменена верстка).";
                    await SaveErrorScreenshotAsync(page, "add-button-not-found");
                    return result;
                }

                var clicked = await TryClickAddButtonAsync(page);
                if (!clicked)
                {
                    result.Status = AssetProcessStatus.Failed;
                    result.Message = "Кнопка добавления не найдена.";
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
                result.DetectionSummary = string.IsNullOrWhiteSpace(postStatus.DetectionSummary) ? "no-signals" : postStatus.DetectionSummary;
                _logger.Debug($"Статус ассета (после клика): {postStatus.DetectionSummary}");
                _logger.Debug($"PostAddOpenInUnity={(postStatus.HasOpenInUnity || postStatus.IsOwned)}");

                if (postStatus.RequiresLogin || page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warn("После попытки добавления потребовалась повторная авторизация.");
                    continue;
                }

                result.Status = (postStatus.HasOpenInUnity || postStatus.IsOwned)
                    ? AssetProcessStatus.Added
                    : AssetProcessStatus.UnknownAfterClick;
                return result;
            }

            result.Status = AssetProcessStatus.Failed;
            result.Message = "Не удалось завершить добавление после переавторизации.";
            await SaveErrorScreenshotAsync(page, "reauth-loop-failed");
            return result;
        }
        catch (Exception ex)
        {
            result.Status = AssetProcessStatus.Failed;
            result.Message = ex.Message;
            await SaveErrorScreenshotAsync(page, "processing-error");
            return result;
        }
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

            _logger.Debug($"PostAddCycle[{cycle}]: openInUnity={current.HasOpenInUnity}, addToMyAssets={current.HasAddToMyAssets}, requiresLogin={current.RequiresLogin}, owned={current.IsOwned}");

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
                _logger.Debug($"PostAddCycle[{cycle}]: Open in Unity ещё не появился. Обновляем страницу ассета (refresh={refreshAttempt})...");
                await SafeGoToAsync(page, assetUrl);
                await WaitForAssetSignalsAsync(page, TimeSpan.FromMilliseconds(Math.Min(_options.AssetUiTimeoutMs, 15000)));
            }
            else
            {
                refreshAttempt++;
                _logger.Debug($"PostAddCycle[{cycle}]: Add/Open не видны, выполняем контрольный refresh (refresh={refreshAttempt})...");
                await SafeGoToAsync(page, assetUrl);
                await WaitForAssetSignalsAsync(page, TimeSpan.FromMilliseconds(Math.Min(_options.AssetUiTimeoutMs, 15000)));
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

        return JsonSerializer.Deserialize<AssetStatusSnapshot>(raw ?? "{}", _runtimeJsonOptions) ?? new AssetStatusSnapshot();
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

            for (let i = 0; i < 8; i++) {
                const dialogs = Array.from(document.querySelectorAll('[role=""dialog""], [aria-modal=""true""], [class*=""modal"" i], [class*=""dialog"" i], [class*=""popup"" i], [class*=""overlay"" i]'))
                    .filter(visible);

                for (const dialog of dialogs) {
                    const dialogText = normalize(dialog.innerText || '');
                    const candidates = Array.from(dialog.querySelectorAll('button, a, [role=""button""]')).filter(visible);
                    const candidateTexts = candidates.map(x => normalize(x.innerText));

                    const hasAccept = candidateTexts.some(t => t === 'accept' || t.startsWith('accept '));
                    const hasCancelLike = candidateTexts.some(t => t.includes('cancel') || t.includes('decline') || t.includes('close') || t.includes('back'));
                    const looksLikeAssetConfirmation = dialogText.includes('license') || dialogText.includes('agreement') || dialogText.includes('terms') || dialogText.includes('eula') || dialogText.includes('unity') || dialogText.includes('asset');

                    if (!hasAccept) continue;
                    if (!hasCancelLike && !looksLikeAssetConfirmation) continue;

                    for (const element of candidates) {
                        const txt = normalize(element.innerText);
                        if (!txt) continue;
                        if (txt !== 'accept' && !txt.startsWith('accept ')) continue;

                        element.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
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
            await page.ScreenshotAsync(path, new ScreenshotOptions { FullPage = true });
        }
        catch
        {
            // игнорируем вторичные ошибки
        }
    }

    private static void PrintSummary(RunReport report)
    {
        var groups = report.Items.GroupBy(x => x.Status).ToDictionary(g => g.Key, g => g.Count());

        Console.WriteLine("\nИтоги:");
        foreach (AssetProcessStatus status in Enum.GetValues<AssetProcessStatus>())
        {
            groups.TryGetValue(status, out var count);
            Console.WriteLine($"- {status}: {count}");
        }
    }
}

internal sealed class AppLogger : IDisposable
{
    private readonly bool _verbose;
    private readonly bool _traceNetwork;
    private readonly StreamWriter? _writer;
    private readonly object _sync = new();

    public AppLogger(bool verbose, bool traceNetwork, string? logFilePath)
    {
        _verbose = verbose;
        _traceNetwork = traceNetwork;

        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            var directory = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _writer = new StreamWriter(logFilePath, append: true) { AutoFlush = true };
            Info($"Логирование в файл включено: {logFilePath}");
        }

        Info($"Verbose={_verbose}; TraceNetwork={_traceNetwork}");
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
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer?.Dispose();
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
    public List<string> ExtraSourceFiles { get; init; } = [];
    public string? LogFilePath { get; init; }
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

    public static CliOptions Parse(string[] args)
    {
        string? configPath = null;

        var cliLoginOnly = false;
        var cliDryRun = false;
        bool? cliHeadless = null;
        var cliVerbose = false;
        var cliTraceNetwork = false;
        var cliUseExtendedSources = false;
        string? cliLogFilePath = null;
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
                case "--log-file" when i + 1 < args.Length:
                    cliLogFilePath = args[++i];
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
        var verbose = cliVerbose || (config?.Verbose ?? false);
        var traceNetwork = cliTraceNetwork || (config?.TraceNetwork ?? false);
        var useExtendedSources = cliUseExtendedSources || (config?.ExtendedSources ?? false);
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
        if (config?.Sources?.Count > 0)
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
        if (config?.ExtraSourceFiles?.Count > 0)
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
            Console.WriteLine("Для автовхода необходимо задать и UNITY_EMAIL, и UNITY_PASSWORD (или оба через CLI). Будет использован ручной вход.");
            unityEmail = null;
            unityPassword = null;
        }

        return new CliOptions
        {
            LoginOnly = loginOnly,
            DryRun = dryRun,
            Headless = headless,
            Verbose = verbose,
            TraceNetwork = traceNetwork,
            UseExtendedSources = useExtendedSources,
            ExtraSourceFiles = extraSourceFiles,
            LogFilePath = logFilePath,
            UnityEmail = unityEmail,
            UnityPassword = unityPassword,
            DelayMs = delayMs,
            NavigationTimeoutMs = navigationTimeoutMs,
            AuthTimeoutMs = authTimeoutMs,
            AssetUiTimeoutMs = assetUiTimeoutMs,
            MaxAddAttempts = maxAddAttempts,
            MaxVisitedAssets = maxVisitedAssets,
            Sources = sources
        };
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
    public List<string> ExtraSourceFiles { get; init; } = [];
    public string? LogFilePath { get; init; }
    public string? UnityEmail { get; init; }
    public string? UnityPassword { get; init; }
    public int? DelayMs { get; init; }
    public int? NavigationTimeoutMs { get; init; }
    public int? AuthTimeoutMs { get; init; }
    public int? AssetUiTimeoutMs { get; init; }
    public int? MaxAddAttempts { get; init; }
    public int? MaxVisitedAssets { get; init; }
    public List<string> Sources { get; init; } = [];

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
    public Dictionary<string, Dictionary<string, string>> LocalStorageByOrigin { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
