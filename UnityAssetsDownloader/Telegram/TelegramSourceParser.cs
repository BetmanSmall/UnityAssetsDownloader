using System.Text.RegularExpressions;
using PuppeteerSharp;

internal sealed class TelegramSourceParser
{
    private const string TelegramWebBaseUrl = "https://t.me/s/";
    private readonly IBrowser _browser;
    private readonly AppLogger _logger;
    private readonly string _logsDirectory;
    private readonly int _navigationTimeoutMs;
    private readonly int _postLimit;
    private readonly bool _screenshotOnNoLinks;
    private static readonly TimeSpan ScrollTimeout = TimeSpan.FromSeconds(25);

    // Regex для ссылок на ассеты Unity Asset Store
    private static readonly Regex AssetUrlRegex = new(
        @"(?:https?:\/\/)?(?:www\.)?assetstore\.unity\.com\/packages\/[\w\-\/%\.~]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Regex для git-ссылок (исключаем assetstore — их ловит первый regex)
    private static readonly Regex GitUrlRegex = new(
        @"(?:https?:\/\/)?(?:www\.)?(?:github\.com|gitlab\.com|bitbucket\.org)\/[\w\-\.]+\/[\w\-\.]+(?:\/[\w\-\.\/~]+)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Regex для промокодов
    private static readonly Regex PromocodeRegex = new(
        @"(?:промокод|промо\-код|promocode|promo\s*code|promo|coupon)\s*:?\s*([\w\-]{4,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public TelegramSourceParser(
        IBrowser browser,
        AppLogger logger,
        string logsDirectory,
        int navigationTimeoutMs,
        int postLimit = 20,
        bool screenshotOnNoLinks = true)
    {
        _browser = browser;
        _logger = logger;
        _logsDirectory = logsDirectory;
        _navigationTimeoutMs = navigationTimeoutMs;
        _postLimit = postLimit;
        _screenshotOnNoLinks = screenshotOnNoLinks;
    }

    /// <summary>
    /// Парсит Telegram каналы и возвращает найденные ссылки на ассеты Unity.
    /// </summary>
    public async Task<TelegramParseResult> ParseChannelsAsync(List<string> channelNames)
    {
        var result = new TelegramParseResult();

        foreach (var channel in channelNames)
        {
            var channelResult = await ParseSingleChannelAsync(channel);

            // Бесплатные прокси часто срываются на первом же запросе.
            // Одна повторная попытка почти всегда спасает, поэтому делаем её.
            if (channelResult.AssetUrls.Count == 0 && channelResult.Errors.Count > 0 &&
                channelResult.Errors.Any(e => e.Contains("ERR_", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.Warn($"[Telegram] Канал {channel} не открылся. Пробуем ещё раз...");
                await Task.Delay(2000);
                var retry = await ParseSingleChannelAsync(channel);

                if (retry.Errors.Count == 0 || retry.AssetUrls.Count > 0)
                {
                    _logger.Info($"[Telegram] Со второй попытки канал {channel} открылся.");
                    channelResult = retry;
                }
            }

            result.AssetUrls.AddRange(channelResult.AssetUrls);
            result.GitLinks.AddRange(channelResult.GitLinks);
            result.Promocodes.AddRange(channelResult.Promocodes);
            result.PostsWithoutLinks.AddRange(channelResult.PostsWithoutLinks);
            result.Errors.AddRange(channelResult.Errors);
            result.AllPosts.AddRange(channelResult.AllPosts);

            foreach (var kvp in channelResult.AssetPromocodes)
            {
                result.AssetPromocodes[kvp.Key] = kvp.Value;
            }
        }

        // Дедупликация
        result.AssetUrls = result.AssetUrls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        result.GitLinks = result.GitLinks.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return result;
    }

    private async Task<TelegramChannelResult> ParseSingleChannelAsync(string channelName)
    {
        var channelResult = new TelegramChannelResult { ChannelName = channelName };
        IPage? page = null;

        try
        {
            page = await _browser.NewPageAsync();
            page.DefaultNavigationTimeout = _navigationTimeoutMs;
            page.DefaultTimeout = _navigationTimeoutMs;

            var channelUrl = $"{TelegramWebBaseUrl}{channelName}";
            _logger.Info($"[Telegram] Открытие канала: {channelUrl}");

            await page.GoToAsync(channelUrl, new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
                Timeout = _navigationTimeoutMs
            });

            await Task.Delay(2000); // Ждём первичную загрузку постов

            // Скроллим чтобы загрузить N постов
            var loadedPosts = await ScrollForPostsAsync(page, _postLimit);
            _logger.Info($"[Telegram] Канал {channelName}: загружено постов (видимых элементов): {loadedPosts}");

            // Собираем все посты
            var postsRaw = await ExtractPostsRawAsync(page);
            _logger.Info($"[Telegram] Канал {channelName}: извлечено текстовых блоков постов: {postsRaw.Count}");

            if (postsRaw.Count == 0)
            {
                _logger.Warn($"[Telegram] Канал {channelName}: не найдено постов. Возможно канал недоступен или заблокирован.");
                channelResult.Errors.Add($"Канал {channelName}: посты не найдены");
                return channelResult;
            }

            var processedCount = 0;
            foreach (var post in postsRaw)
            {
                if (processedCount >= _postLimit)
                    break;

                processedCount++;

                var (text, postId) = post;
                channelResult.AllPosts.Add(new TelegramPostInfo
                {
                    ChannelName = channelName,
                    PostId = postId,
                    Text = text
                });
                _logger.Debug($"[Telegram] ---- ПОСТ {channelName}/#{postId} ({text.Length} символов) ----");
                _logger.Debug($"[Telegram] {text}");

                // Ищем ссылки на ассеты
                var assetMatches = AssetUrlRegex.Matches(text);
                var gitMatches = GitUrlRegex.Matches(text);
                var promoMatches = PromocodeRegex.Matches(text);

                var assetUrls = assetMatches
                    .Select(m => NormalizeAssetUrl(m.Value))
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var gitUrls = gitMatches
                    .Select(m => m.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? m.Value
                        : "https://" + m.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var promocodes = promoMatches
                    .Select(m => m.Groups[1].Value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                channelResult.AssetUrls.AddRange(assetUrls);
                channelResult.GitLinks.AddRange(gitUrls);
                channelResult.Promocodes.AddRange(promocodes);

                if (assetUrls.Count > 0 && promocodes.Count > 0)
                {
                    var firstPromo = promocodes[0];
                    foreach (var url in assetUrls)
                    {
                        channelResult.AssetPromocodes[url] = firstPromo;
                    }
                }

                if (assetUrls.Count > 0 || promocodes.Count > 0)
                {
                    var promoPart = promocodes.Count > 0
                        ? $", промокод: {string.Join(", ", promocodes)}"
                        : string.Empty;
                    _logger.Info($"[Telegram] пост {postId}: ассетов {assetUrls.Count}{promoPart}");

                    foreach (var url in assetUrls)
                    {
                        _logger.Info($"[Telegram]   {url}");
                    }
                }
                else
                {
                    _logger.Debug(
                        $"[Telegram] пост {postId}: ссылок на Asset Store нет (текст в telegram_posts_raw.log)");
                }

                if (gitUrls.Count > 0)
                {
                    _logger.Info($"[Telegram] {channelName} пост #{postId}: найдено git-ссылок: {gitUrls.Count}");
                    foreach (var url in gitUrls)
                    {
                        _logger.Info($"[Telegram]   Git URL (пропущено): {url}");
                    }
                }

                if (promocodes.Count > 0)
                {
                    _logger.Info($"[Telegram] {channelName} пост #{postId}: найдено промокодов: {string.Join(", ", promocodes)}");
                }

                // Если не найдено ни одной ссылки — скриншот
                if (_screenshotOnNoLinks && assetUrls.Count == 0 && gitUrls.Count == 0 && promocodes.Count == 0)
                {
                    await TakePostScreenshotAsync(page, channelName, postId, text);
                    channelResult.PostsWithoutLinks.Add(new PostWithoutLink
                    {
                        ChannelName = channelName,
                        PostId = postId,
                        TextPreview = text.Length > 200 ? text[..200] + "..." : text
                    });
                }
            }

            _logger.Info($"[Telegram] Канал {channelName}: итого ассетов={channelResult.AssetUrls.Count}, git-ссылок={channelResult.GitLinks.Count}, промокодов={channelResult.Promocodes.Count}, постов без ссылок={channelResult.PostsWithoutLinks.Count}");
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Telegram] Ошибка при парсинге канала {channelName}: {ex.Message}");
            channelResult.Errors.Add($"Ошибка: {ex.Message}");
        }
        finally
        {
            if (page != null)
            {
                await page.CloseAsync();
                await page.DisposeAsync();
            }
        }

        return channelResult;
    }

    private async Task<int> ScrollForPostsAsync(IPage page, int targetCount)
    {
        var stableIterations = 0;
        var lastCount = -1;

        var stopAt = DateTime.UtcNow.Add(ScrollTimeout);
        while (DateTime.UtcNow < stopAt && stableIterations < 5)
        {
            var currentCount = await page.EvaluateFunctionAsync<int>(@"() => {
                // Считаем посты как элементы tgme_widget_message_wrap или похожие
                const posts = document.querySelectorAll('.tgme_widget_message_wrap, .tgme_widget_message, [data-post], article');
                return posts.length;
            }");

            if (currentCount <= lastCount)
            {
                stableIterations++;
            }
            else
            {
                stableIterations = 0;
                lastCount = currentCount;
            }

            if (currentCount >= targetCount)
                break;

            // Скроллим вниз
            await page.EvaluateFunctionAsync<string>(@"() => {
                const allMessages = document.querySelectorAll('.tgme_widget_message_wrap, .tgme_widget_message');
                if (allMessages.length > 0) {
                    const last = allMessages[allMessages.length - 1];
                    last.scrollIntoView({ behavior: 'smooth', block: 'start' });
                } else {
                    window.scrollBy(0, window.innerHeight * 2);
                }
                return '';
            }");

            await Task.Delay(1200);
        }

        return lastCount > 0 ? lastCount : 0;
    }

    private async Task<List<(string Text, string PostId)>> ExtractPostsRawAsync(IPage page)
    {
        var raw = await page.EvaluateFunctionAsync<string>(@"() => {
            // Ищем контейнеры постов
            const wrappers = document.querySelectorAll('.tgme_widget_message_wrap');
            const results = [];

            wrappers.forEach(wrap => {
                const message = wrap.querySelector('.tgme_widget_message');
                if (!message) return;

                // Получаем ID поста из атрибута data-post или из ссылки
                const postAttr = message.getAttribute('data-post') || '';
                let postId = postAttr;
                if (!postId) {
                    const postLink = message.querySelector('a.tgme_widget_message_date, a[href*=""t.me/""]');
                    if (postLink) {
                        const href = postLink.getAttribute('href') || '';
                        const parts = href.split('/');
                        postId = parts[parts.length - 1] || 'unknown';
                    } else {
                        postId = 'unknown';
                    }
                }

                // Извлекаем текст поста
                const textEl = message.querySelector('.tgme_widget_message_text');
                let text = textEl ? textEl.innerText || '' : '';

                // Находим все ссылки <a> в сообщении и дописываем их href в текст поста,
                // чтобы C# регулярные выражения могли извлечь скрытые за текстом ссылки (например, 'тут', 'вот здесь')
                const links = message.querySelectorAll('a');
                links.forEach(a => {
                    const href = a.getAttribute('href') || '';
                    if (href && !text.includes(href)) {
                        text += '\n' + href;
                    }
                });

                results.push({ text, postId });
            });

            // Если tgme_widget_message_wrap не найдены, пробуем альтернативные селекторы
            if (results.length === 0) {
                const articles = document.querySelectorAll('article, [class*=""message""], .tgme_widget_message');
                articles.forEach(article => {
                    let text = article.innerText || '';
                    const postId = article.getAttribute('data-post') || 'unknown';

                    const links = article.querySelectorAll('a');
                    links.forEach(a => {
                        const href = a.getAttribute('href') || '';
                        if (href && !text.includes(href)) {
                            text += '\n' + href;
                        }
                    });

                    results.push({ text, postId });
                });
            }

            return JSON.stringify(results);
        }");

        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<TelegramPostRaw>>(raw ?? "[]",
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return parsed?.Select(p => (p.Text ?? string.Empty, p.PostId ?? "unknown")).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task TakePostScreenshotAsync(IPage page, string channelName, string postId, string text)
    {
        try
        {
            var telegramDir = Path.Combine(_logsDirectory, "telegram");
            Directory.CreateDirectory(telegramDir);

            var safeChannel = SanitizeFileName(channelName);
            var safePostId = SanitizeFileName(postId);
            var fileName = $"{safeChannel}_{DateTime.Now:yyyyMMdd-HHmmss}_post{safePostId}.png";
            var filePath = Path.Combine(telegramDir, fileName);

            // Скроллим к посту перед скриншотом
            await page.EvaluateFunctionAsync<string>(@"(targetPostId) => {
                const messages = document.querySelectorAll('[data-post], .tgme_widget_message');
                for (const msg of messages) {
                    const id = msg.getAttribute('data-post') || '';
                    if (id.includes(targetPostId) || (msg.innerText || '').includes(targetPostId)) {
                        msg.scrollIntoView({ behavior: 'instant', block: 'center' });
                        return;
                    }
                }
                return '';
            }", postId);

            await Task.Delay(500);
            await page.ScreenshotAsync(filePath, new ScreenshotOptions { FullPage = false });

            _logger.Debug($"[Telegram] Скриншот поста без ссылок сохранён: {fileName}");
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Telegram] Не удалось сделать скриншот поста {postId}: {ex.Message}");
        }
    }

    private static string NormalizeAssetUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return string.Empty;

        if (!uri.Host.Contains("assetstore.unity.com", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        if (!uri.AbsolutePath.Contains("/packages/", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}".TrimEnd('/');
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
    }
}

internal sealed class TelegramParseResult
{
    public List<string> AssetUrls { get; set; } = [];
    public List<string> GitLinks { get; set; } = [];
    public List<string> Promocodes { get; set; } = [];
    public List<PostWithoutLink> PostsWithoutLinks { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public List<TelegramPostInfo> AllPosts { get; set; } = [];
    public Dictionary<string, string> AssetPromocodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class TelegramChannelResult
{
    public string ChannelName { get; set; } = string.Empty;
    public List<string> AssetUrls { get; set; } = [];
    public List<string> GitLinks { get; set; } = [];
    public List<string> Promocodes { get; set; } = [];
    public List<PostWithoutLink> PostsWithoutLinks { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public List<TelegramPostInfo> AllPosts { get; set; } = [];
    public Dictionary<string, string> AssetPromocodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class PostWithoutLink
{
    public string ChannelName { get; set; } = string.Empty;
    public string PostId { get; set; } = string.Empty;
    public string TextPreview { get; set; } = string.Empty;
}

internal sealed class TelegramPostRaw
{
    public string Text { get; set; } = string.Empty;
    public string PostId { get; set; } = string.Empty;
}

internal sealed class TelegramPostInfo
{
    public string ChannelName { get; set; } = string.Empty;
    public string PostId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}