using System.Text.Json;
using System.Text.RegularExpressions;
using PuppeteerSharp;

var options = CliOptions.Parse(args);
var app = new UnityAssetAutomationApp(options);
await app.RunAsync();

internal sealed class UnityAssetAutomationApp
{
    private readonly CliOptions _options;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
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
    }

    public async Task RunAsync()
    {
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_logsDirectory);

        Console.WriteLine("Подготовка браузера Chromium...");
        await new BrowserFetcher().DownloadAsync();

        await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = _options.Headless,
            DefaultViewport = null,
            Args = ["--start-maximized"]
        });

        await using var page = await browser.NewPageAsync();

        var authenticated = await EnsureAuthenticatedAsync(page);
        if (!authenticated)
        {
            Console.WriteLine("Не удалось подтвердить авторизацию. Завершение.");
            return;
        }

        if (_options.LoginOnly)
        {
            Console.WriteLine("Режим --login завершен: cookies обновлены.");
            return;
        }

        var sources = _options.Sources.Count > 0 ? _options.Sources : DefaultSources.ToList();
        var assetUrls = await CollectAssetUrlsAsync(sources);

        Console.WriteLine($"Найдено уникальных ассетов: {assetUrls.Count}");
        var report = new RunReport
        {
            StartedAtUtc = DateTime.UtcNow,
            DryRun = _options.DryRun,
            Sources = sources
        };

        var index = 0;
        foreach (var assetUrl in assetUrls)
        {
            index++;
            Console.WriteLine($"[{index}/{assetUrls.Count}] Обработка: {assetUrl}");

            var result = await ProcessAssetAsync(page, assetUrl);
            report.Items.Add(result);

            await Task.Delay(TimeSpan.FromMilliseconds(_options.DelayMs));
        }

        report.FinishedAtUtc = DateTime.UtcNow;
        await File.WriteAllTextAsync(_reportPath, JsonSerializer.Serialize(report, _jsonOptions));

        PrintSummary(report);
        Console.WriteLine($"Отчет сохранен: {_reportPath}");
    }

    private async Task<bool> EnsureAuthenticatedAsync(IPage page)
    {
        if (await TryLoadCookiesAsync(page))
        {
            Console.WriteLine("Cookies загружены, проверка авторизации...");
            if (await IsAuthenticatedAsync(page))
            {
                Console.WriteLine("Сессия активна.");
                return true;
            }
        }

        Console.WriteLine("Требуется вход в Unity.");
        await page.GoToAsync("https://login.unity.com/en/sign-in", WaitUntilNavigation.Networkidle2);
        Console.WriteLine("Выполните вход вручную в окне браузера и нажмите Enter в консоли...");
        Console.ReadLine();

        if (!await IsAuthenticatedAsync(page))
        {
            Console.WriteLine("Проверка после ручного входа неуспешна.");
            return false;
        }

        await SaveCookiesAsync(page);
        Console.WriteLine("Авторизация подтверждена, cookies сохранены.");
        return true;
    }

    private async Task<bool> IsAuthenticatedAsync(IPage page)
    {
        await page.GoToAsync("https://assetstore.unity.com", WaitUntilNavigation.Networkidle2);

        if (page.Url.Contains("login.unity.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hasAccountMarkers = await page.EvaluateFunctionAsync<bool>(@"() => {
            const text = document.body?.innerText?.toLowerCase() || '';
            const myAssetsLink = document.querySelector('a[href*=""/my-assets""], a[href*=""my-assets""]');
            const signInLink = document.querySelector('a[href*=""login.unity.com""], a[href*=""/sign-in""]');
            const hasMyAssetsText = text.includes('my assets');
            const hasSignInText = text.includes('sign in');
            return (!!myAssetsLink || hasMyAssetsText) && !(!!signInLink && hasSignInText && !hasMyAssetsText);
        }");

        return hasAccountMarkers;
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
                return false;
            }

            await page.SetCookieAsync(cookies.Select(c => c.ToCookieParam()).ToArray());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task SaveCookiesAsync(IPage page)
    {
        var cookies = await page.GetCookiesAsync("https://assetstore.unity.com", "https://login.unity.com");
        var serializable = cookies.Select(SerializableCookie.FromCookie).ToList();
        await File.WriteAllTextAsync(_cookiesPath, JsonSerializer.Serialize(serializable, _jsonOptions));
    }

    private async Task<List<string>> CollectAssetUrlsAsync(IEnumerable<string> sourceUrls)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sourceUrls)
        {
            try
            {
                Console.WriteLine($"Чтение источника: {source}");
                var html = await _httpClient.GetStringAsync(source);
                foreach (var url in ExtractAssetUrlsFromHtml(html, source))
                {
                    all.Add(url);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка источника {source}: {ex.Message}");
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
            await page.GoToAsync(assetUrl, WaitUntilNavigation.Networkidle2);

            var status = await DetectStatusAsync(page);
            result.DetectedFree = status.IsFree;
            result.DetectedOwned = status.IsOwned;

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

            var clicked = await TryClickAddButtonAsync(page);
            if (!clicked)
            {
                result.Status = AssetProcessStatus.Failed;
                result.Message = "Кнопка добавления не найдена.";
                await SaveErrorScreenshotAsync(page, "add-button-not-found");
                return result;
            }

            await Task.Delay(2000);
            var postStatus = await DetectStatusAsync(page);
            result.Status = postStatus.IsOwned ? AssetProcessStatus.Added : AssetProcessStatus.UnknownAfterClick;
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

    private async Task<AssetStatusSnapshot> DetectStatusAsync(IPage page)
    {
        var text = await page.EvaluateFunctionAsync<string>("() => document.body?.innerText || ''");
        var normalized = (text ?? string.Empty).ToLowerInvariant();

        var actionTexts = await page.EvaluateFunctionAsync<string[]>(@"() =>
            Array.from(document.querySelectorAll('button, a')).map(x => (x.innerText || '').trim().toLowerCase()).filter(Boolean)");

        var isOwned = actionTexts.Any(t => t.Contains("open in unity") ||
                                           t.Contains("owned") ||
                                           t.Contains("in my assets") ||
                                           t.Contains("download"));

        var hasFreeSignals = actionTexts.Any(t => t.Contains("add to my assets")) ||
                             normalized.Contains("free") ||
                             normalized.Contains("0.00");

        var hasPaidSignals = Regex.IsMatch(normalized, "\\$\\s?\\d") ||
                             Regex.IsMatch(normalized, "€\\s?\\d") ||
                             Regex.IsMatch(normalized, "£\\s?\\d");

        var isFree = hasFreeSignals && !hasPaidSignals;

        return new AssetStatusSnapshot
        {
            IsFree = isFree,
            IsOwned = isOwned
        };
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

internal sealed class CliOptions
{
    public bool LoginOnly { get; init; }
    public bool DryRun { get; init; }
    public bool Headless { get; init; }
    public int DelayMs { get; init; } = 1200;
    public List<string> Sources { get; init; } = [];

    public static CliOptions Parse(string[] args)
    {
        var loginOnly = false;
        var dryRun = false;
        var headless = false;
        var delayMs = 1200;
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
                    headless = bool.TryParse(args[++i], out var parsed) && parsed;
                    break;
                }
                case "--delay-ms" when i + 1 < args.Length:
                {
                    if (int.TryParse(args[++i], out var delay) && delay > 0)
                    {
                        delayMs = delay;
                    }

                    break;
                }
                case "--source" when i + 1 < args.Length:
                    sources.Add(args[++i]);
                    break;
            }
        }

        return new CliOptions
        {
            LoginOnly = loginOnly,
            DryRun = dryRun,
            Headless = headless,
            DelayMs = delayMs,
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
    public string? Message { get; set; }
}

internal sealed class AssetStatusSnapshot
{
    public bool IsFree { get; init; }
    public bool IsOwned { get; init; }
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
