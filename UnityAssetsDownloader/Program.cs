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

    private readonly CliOptions _options;
    private readonly AppLogger _logger;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly JsonSerializerOptions _runtimeJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
    private readonly string _logsDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
    private readonly string _cookiesPath;
    private readonly string _reportPath;
    private readonly HttpClient _httpClient = new();

    private static readonly string[] DefaultSources =
    [
        "https://vanquish3r.github.io/greater-china-unity-assets/",
        "https://assetstore.unity.com/top-assets/top-free"
    ];

    public UnityAssetAutomationApp(CliOptions options)
    {
        _options = options;
        _cookiesPath = Path.Combine(_dataDirectory, "unity_cookies.json");
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

            var sources = _options.Sources.Count > 0 ? _options.Sources : DefaultSources.ToList();
            var assetUrls = await CollectAssetUrlsAsync(sources);

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
        if (await TryLoadCookiesAsync(page))
        {
            _logger.Info("Cookies загружены, проверка авторизации...");
            if (await IsAuthenticatedAsync(page))
            {
                _logger.Info("Сессия активна.");
                return true;
            }
        }

        _logger.Warn("Требуется вход в Unity. Запуск SSO через Asset Store...");
        var authenticated = await AuthenticateViaAssetStoreAsync(page);
        if (!authenticated)
        {
            _logger.Error("Проверка после попытки входа неуспешна.");
            return false;
        }

        await SaveCookiesAsync(page);
        _logger.Info("Авторизация подтверждена, cookies сохранены.");
        return true;
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
        var iteration = 0;

        while (DateTime.UtcNow < stopAt)
        {
            iteration++;

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
            else if (page.Url.Contains("cloud.unity.com", StringComparison.OrdinalIgnoreCase) && iteration % 2 == 0)
            {
                _logger.Debug("Обнаружен cloud.unity.com, возвращаемся в Asset Store для завершения SSO...");
                await StartAssetStoreSsoAsync(page);
            }
            else if (!page.Url.Contains("assetstore.unity.com", StringComparison.OrdinalIgnoreCase) && iteration % 3 == 0)
            {
                await SafeGoToAsync(page, AssetStoreHomeUrl);
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

        if (profileState.HasSignInItem)
        {
            return false;
        }

        if (profileState.HasSignedInItem)
        {
            return true;
        }

        return !hasUiSignInMarkers && hasUiAuthMarkers;
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
                const profileButton = document.querySelector('[aria-label=""Open user profile menu""], button[aria-label*=""profile"" i], button[aria-label*=""user"" i]');
                if (!profileButton) {
                    return JSON.stringify({ profileMenuFound: false, hasSignInItem: false, hasSignedInItem: false });
                }

                profileButton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                await wait(250);

                const texts = Array.from(document.querySelectorAll('a, button, span, div'))
                    .map(x => (x.innerText || '').trim().toLowerCase())
                    .filter(Boolean);

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
                await Task.Delay(delayMs);
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
               msg.Contains("Target closed", StringComparison.OrdinalIgnoreCase);
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
                _logger.Debug($"BROWSER CONSOLE [{e.Message.Type}] {e.Message.Text}");
            }
        };

        page.PageError += (_, e) => _logger.Warn($"PAGE ERROR: {e.Message}");
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

    private async Task SaveCookiesAsync(IPage page)
    {
        var cookies = await page.GetCookiesAsync("https://assetstore.unity.com", "https://login.unity.com");
        var serializable = cookies.Select(SerializableCookie.FromCookie).ToList();
        await File.WriteAllTextAsync(_cookiesPath, JsonSerializer.Serialize(serializable, _jsonOptions));
        _logger.Info($"Сохранено cookies: {serializable.Count}");
        _logger.Debug($"Домены cookies после входа: {string.Join(", ", serializable.Select(c => c.Domain).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))}");
    }

    private async Task<List<string>> CollectAssetUrlsAsync(IEnumerable<string> sourceUrls)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sourceUrls)
        {
            try
            {
                _logger.Info($"Чтение источника: {source}");
                var html = await _httpClient.GetStringAsync(source);
                foreach (var url in ExtractAssetUrlsFromHtml(html, source))
                {
                    all.Add(url);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Ошибка источника {source}: {ex.Message}");
            }
        }

        return all.ToList();
    }

    private static IEnumerable<string> ExtractAssetUrlsFromHtml(string html, string baseUrl)
    {
        var regex = new Regex(@"(?:https?:\/\/assetstore\.unity\.com)?\/packages\/[\w\-\/%\.~]+", RegexOptions.IgnoreCase);
        var baseUri = new Uri(baseUrl);

        foreach (Match match in regex.Matches(html))
        {
            if (string.IsNullOrWhiteSpace(match.Value))
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
                }

                if (!status.IsFree)
                {
                    result.Status = AssetProcessStatus.PaidSkipped;
                    return result;
                }

                if (status.IsOwned)
                {
                    result.Status = AssetProcessStatus.AlreadyOwned;
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

        while (DateTime.UtcNow < stopAt)
        {
            var accepted = await TryAcceptAddConfirmationAsync(page);
            if (accepted)
            {
                _logger.Info("Подтверждение добавления найдено во время проверки: нажата кнопка Accept.");
            }

            await Task.Delay(1200);
            await WaitForAssetSignalsAsync(page, TimeSpan.FromMilliseconds(Math.Min(_options.AssetUiTimeoutMs, 12000)));
            var current = await DetectStatusAsync(page);
            lastStatus = current;

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
                refreshAttempt++;
                _logger.Debug($"После добавления кнопка Add to My Assets все еще видна. Обновляем страницу ассета (попытка {refreshAttempt})...");
                await SafeGoToAsync(page, assetUrl);
                await WaitForAssetSignalsAsync(page, TimeSpan.FromMilliseconds(Math.Min(_options.AssetUiTimeoutMs, 15000)));
            }
            else
            {
                await Task.Delay(800);
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
            const ctaRoots = Array.from(document.querySelectorAll(
                '[data-testid*=""cta"" i], [class*=""cta"" i], [class*=""purchase"" i], [class*=""buy"" i], aside[class*=""sidebar"" i], div[class*=""sidebar"" i]'
            )).filter(visible);

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

                const roots = Array.from(document.querySelectorAll(
                    '[data-testid*=""cta"" i], [class*=""cta"" i], [class*=""purchase"" i], [class*=""buy"" i], aside[class*=""sidebar"" i], div[class*=""sidebar"" i]'
                )).filter(visible);

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
            const targets = [
                'add to my assets',
                'add to my assets for free',
                'add to cart'
            ];

            const elements = Array.from(document.querySelectorAll('button, a, span'));
            for (const element of elements) {
                const txt = (element.innerText || '').trim().toLowerCase();
                if (!txt) continue;
                if (!targets.some(t => txt.includes(t))) continue;

                const clickable = element.closest('button, a') || element;
                clickable.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                return true;
            }
            return false;
        }");
    }

    private static async Task<bool> TryAcceptAddConfirmationAsync(IPage page)
    {
        return await page.EvaluateFunctionAsync<bool>(@"async () => {
            const wait = (ms) => new Promise(r => setTimeout(r, ms));
            for (let i = 0; i < 8; i++) {
                const candidates = Array.from(document.querySelectorAll('button, a, span'));
                for (const element of candidates) {
                    const txt = (element.innerText || '').trim().toLowerCase();
                    if (!txt) continue;
                    if (!(txt === 'accept' || txt.includes('accept'))) continue;

                    const clickable = element.closest('button, a') || element;
                    clickable.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                    return true;
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
        var loginOnly = false;
        var dryRun = false;
        var headless = false;
        var verbose = false;
        var traceNetwork = false;
        string? logFilePath = null;
        string? unityEmail = null;
        string? unityPassword = null;
        var delayMs = 1200;
        var navigationTimeoutMs = 120000;
        var authTimeoutMs = 300000;
        var assetUiTimeoutMs = 30000;
        int? maxAddAttempts = null;
        int? maxVisitedAssets = null;
        var sources = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i].Trim();
            switch (arg)
            {
                case "--login":
                    loginOnly = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--headless" when i + 1 < args.Length:
                {
                    var raw = args[++i];
                    if (!bool.TryParse(raw, out var parsed))
                    {
                        Console.WriteLine($"Некорректное значение для --headless: '{raw}'. Используется false.");
                        parsed = false;
                    }

                    headless = parsed;
                    break;
                }
                case "--verbose":
                    verbose = true;
                    break;
                case "--trace-network":
                    traceNetwork = true;
                    verbose = true;
                    break;
                case "--log-file" when i + 1 < args.Length:
                    logFilePath = args[++i];
                    break;
                case "--unity-email" when i + 1 < args.Length:
                    unityEmail = args[++i];
                    break;
                case "--unity-password" when i + 1 < args.Length:
                    unityPassword = args[++i];
                    break;
                case "--delay-ms" when i + 1 < args.Length:
                {
                    if (int.TryParse(args[++i], out var delay) && delay > 0)
                    {
                        delayMs = delay;
                    }

                    break;
                }
                case "--nav-timeout-ms" when i + 1 < args.Length:
                {
                    if (int.TryParse(args[++i], out var navTimeout) && navTimeout >= 10000)
                    {
                        navigationTimeoutMs = navTimeout;
                    }

                    break;
                }
                case "--auth-timeout-ms" when i + 1 < args.Length:
                {
                    if (int.TryParse(args[++i], out var authTimeout) && authTimeout >= 30000)
                    {
                        authTimeoutMs = authTimeout;
                    }

                    break;
                }
                case "--asset-ui-timeout-ms" when i + 1 < args.Length:
                {
                    if (int.TryParse(args[++i], out var assetUiTimeout) && assetUiTimeout >= 5000)
                    {
                        assetUiTimeoutMs = assetUiTimeout;
                    }

                    break;
                }
                case "--max-add-attempts" when i + 1 < args.Length:
                {
                    if (int.TryParse(args[++i], out var parsedLimit) && parsedLimit > 0)
                    {
                        maxAddAttempts = parsedLimit;
                    }

                    break;
                }
                case "--max-visited-assets" when i + 1 < args.Length:
                {
                    if (int.TryParse(args[++i], out var parsedVisitedLimit) && parsedVisitedLimit > 0)
                    {
                        maxVisitedAssets = parsedVisitedLimit;
                    }

                    break;
                }
                case "--source" when i + 1 < args.Length:
                    sources.Add(args[++i]);
                    break;
            }
        }

        unityEmail ??= Environment.GetEnvironmentVariable("UNITY_EMAIL");
        unityPassword ??= Environment.GetEnvironmentVariable("UNITY_PASSWORD");

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

internal enum AssetProcessStatus
{
    Added,
    AlreadyOwned,
    PaidSkipped,
    WouldAddInDryRun,
    UnknownAfterClick,
    Failed
}
