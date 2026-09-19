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

    /// <summary>
    /// Качает страницу канала без браузера. null — значит читаем только браузером.
    /// Ошибки не бросает: вернула null — открываем канал браузером, как раньше.
    /// </summary>
    private readonly Func<string, Task<string?>>? _fetchHtmlAsync;
    // Страница канала отдаёт около 20 постов. При чтении за один раз десяти страниц
    // (~200 постов) хватает: старые раздачи давно закончились. Пачками читается без предела.
    public const int MaxPagesPerChannel = 10;

    /// <summary>
    /// Метка в тексте ошибки: канал не открылся по сети. По ней программа понимает,
    /// что нужно пробовать следующий прокси, а не сдаваться.
    /// </summary>
    public const string NetworkFailureMarker = "канал не открылся по сети";

    // Regex для ссылок на ассеты Unity Asset Store
    private static readonly Regex AssetUrlRegex = new(
        @"(?:https?:\/\/)?(?:www\.)?assetstore\.unity\.com\/packages\/[\w\-\/%\.~]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Regex для git-ссылок (исключаем assetstore — их ловит первый regex)
    private static readonly Regex GitUrlRegex = new(
        @"(?:https?:\/\/)?(?:www\.)?(?:github\.com|gitlab\.com|bitbucket\.org)\/[\w\-\.]+\/[\w\-\.]+(?:\/[\w\-\.\/~]+)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Regex для промокодов: слово «промокод» в любой форме («по промокоду», «промокодом»)
    // или promo code / coupon, за ним сам код латиницей. Кириллица кодом не бывает:
    // иначе в «промокод чтобы получить...» кодом становилось слово «чтобы».
    private static readonly Regex PromocodeRegex = new(
        @"(?<![\p{L}\d])(?:промо-?код[а-я]*|купон[а-я]*|promo\s*-?\s*codes?|promocodes?|coupon(?:\s*codes?)?|promo)(?![\p{L}\d])" +
        @"\s*[:\-–—]?\s*([A-Za-z0-9][A-Za-z0-9_\-]{3,39})(?![\p{L}\d_\-])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public TelegramSourceParser(
        IBrowser browser,
        AppLogger logger,
        string logsDirectory,
        int navigationTimeoutMs,
        int postLimit = 50,
        bool screenshotOnNoLinks = true,
        Func<string, Task<string?>>? fetchHtmlAsync = null)
    {
        _browser = browser;
        _logger = logger;
        _logsDirectory = logsDirectory;
        _navigationTimeoutMs = navigationTimeoutMs;
        _postLimit = postLimit;
        _screenshotOnNoLinks = screenshotOnNoLinks;
        _fetchHtmlAsync = fetchHtmlAsync;
    }

    /// <summary>
    /// Разбирает каналы за один раз: с каждого читает до postLimit последних постов.
    /// </summary>
    public Task<TelegramParseResult> ParseChannelsAsync(List<string> channelNames) =>
        ReadAsync(
            channelNames.Select(c => new TelegramChannelCursor(c)).ToList(),
            wantAssets: int.MaxValue,
            isWanted: _ => true,
            maxPostsPerChannel: _postLimit,
            maxPagesPerChannel: MaxPagesPerChannel);

    /// <summary>
    /// Читает каналы по кругу, по странице с каждого, от новых постов к старым.
    /// Останавливается, когда набралось wantAssets ассетов, подходящих под isWanted,
    /// когда у всех каналов кончились посты или прочитан лимит.
    ///
    /// Курсоры помнят, где остановились, поэтому следующий вызов с теми же
    /// курсорами продолжит с более старых постов. Так ассеты берутся пачками.
    /// Каналы, которые не открылись, получают FailedThisRead и попадают в FailedChannels.
    /// </summary>
    public async Task<TelegramParseResult> ReadAsync(
        IReadOnlyList<TelegramChannelCursor> cursors,
        int wantAssets,
        Func<string, bool> isWanted,
        int maxPostsPerChannel = int.MaxValue,
        int maxPagesPerChannel = int.MaxValue)
    {
        var result = new TelegramParseResult();
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool CanRead(TelegramChannelCursor c) =>
            !c.Exhausted && !c.FailedThisRead &&
            c.PostsRead < Math.Min(maxPostsPerChannel, c.MaxPosts) && c.PagesRead < maxPagesPerChannel;

        // Вкладка открывается только если понадобится: при чтении без браузера
        // она не нужна совсем.
        IPage? page = null;

        async Task<IPage> OpenPageAsync()
        {
            if (page is null)
            {
                page = await _browser.NewPageAsync();
                page.DefaultNavigationTimeout = _navigationTimeoutMs;
                page.DefaultTimeout = _navigationTimeoutMs;
            }

            return page;
        }

        try
        {
            while (wanted.Count < wantAssets && cursors.Any(CanRead))
            {
                foreach (var cursor in cursors.Where(CanRead).ToList())
                {
                    var channelResult = await ReadPageWithRetryAsync(OpenPageAsync, cursor, maxPostsPerChannel);

                    result.GitLinks.AddRange(channelResult.GitLinks);
                    result.Promocodes.AddRange(channelResult.Promocodes);
                    result.PostsWithoutLinks.AddRange(channelResult.PostsWithoutLinks);
                    result.Errors.AddRange(channelResult.Errors);
                    result.AllPosts.AddRange(channelResult.AllPosts);

                    foreach (var url in channelResult.AssetUrls)
                    {
                        if (knownAssets.Add(url))
                        {
                            result.AssetUrls.Add(url);
                        }

                        if (isWanted(url))
                        {
                            wanted.Add(url);
                        }
                    }

                    // Посты идут от новых к старым: код из более нового поста уже записан раньше.
                    foreach (var kvp in channelResult.AssetPromocodes)
                    {
                        result.AssetPromocodes.TryAdd(kvp.Key, kvp.Value);
                    }

                    if (cursor.FailedThisRead)
                    {
                        result.FailedChannels.Add(cursor.Name);
                    }

                    if (wanted.Count >= wantAssets)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            if (page is not null)
            {
                await page.CloseAsync();
                await page.DisposeAsync();
            }
        }

        result.GitLinks = result.GitLinks.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var cursor in cursors.Where(c => c.PostsRead > 0))
        {
            var state = !cursor.Exhausted ? string.Empty
                : cursor.StopAtId > 0 ? $", дочитали до прошлого раза (#{cursor.StopAtId})"
                : cursor.PostsRead >= cursor.MaxPosts ? $", взяли последние {cursor.MaxPosts}"
                : ", посты канала кончились";
            _logger.Info($"[Telegram] Канал {cursor.Name}: всего прочитано постов {cursor.PostsRead}{state}.");
        }

        return result;
    }

    /// <summary>
    /// Читает следующую страницу канала. Бесплатные прокси часто срываются на запросе,
    /// поэтому при сетевой ошибке пробует ещё раз; не вышло — помечает канал FailedThisRead.
    /// </summary>
    private async Task<TelegramChannelResult> ReadPageWithRetryAsync(
        Func<Task<IPage>> openPageAsync, TelegramChannelCursor cursor, int maxPostsPerChannel)
    {
        for (var attempt = 1; ; attempt++)
        {
            var channelResult = new TelegramChannelResult { ChannelName = cursor.Name };
            try
            {
                await ReadNextPageAsync(openPageAsync, cursor, channelResult, maxPostsPerChannel);
                if (attempt > 1)
                {
                    _logger.Info($"[Telegram] Со второй попытки канал {cursor.Name} открылся.");
                }

                return channelResult;
            }
            catch (Exception ex)
            {
                _logger.Warn($"[Telegram] Ошибка при парсинге канала {cursor.Name}: {ex.Message}");
                if (attempt == 1 && ex.Message.Contains("ERR_", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warn($"[Telegram] Канал {cursor.Name} не открылся. Пробуем ещё раз...");
                    await Task.Delay(2000);
                    continue;
                }

                cursor.FailedThisRead = true;
                channelResult.Errors.Add($"Ошибка: {ex.Message}");
                return channelResult;
            }
        }
    }

    /// <summary>
    /// Читает одну страницу канала: первую — t.me/s/&lt;канал&gt;, дальше — ?before=&lt;самый старый
    /// прочитанный пост&gt;. Листать страницу вниз бесполезно: t.me/s показывает около 20
    /// последних постов, а более старые лежат именно на страницах ?before.
    /// </summary>
    private async Task ReadNextPageAsync(
        Func<Task<IPage>> openPageAsync, TelegramChannelCursor cursor,
        TelegramChannelResult channelResult, int maxPostsPerChannel)
    {
        var channelUrl = $"{TelegramWebBaseUrl}{cursor.Name}";
        var firstPage = cursor.OldestId == 0;
        if (!firstPage && cursor.OldestId <= 1)
        {
            cursor.Exhausted = true;
            return;
        }

        var url = firstPage ? channelUrl : $"{channelUrl}?before={cursor.OldestId}";
        if (firstPage)
        {
            _logger.Info($"[Telegram] Открытие канала: {channelUrl}");
        }

        // Сначала — простой запрос страницы: t.me/s/<канал> приходит уже готовой,
        // выполнять на ней нечего. Это в разы быстрее браузера и не требует второго Chrome.
        List<(string Text, string PostId)> pagePosts = [];
        IPage? page = null;

        if (_fetchHtmlAsync is not null)
        {
            var html = await _fetchHtmlAsync(url);
            if (html is null)
            {
                throw new InvalidOperationException($"{NetworkFailureMarker}: страница {url} не скачалась");
            }

            pagePosts = TelegramHtmlParser.ExtractPosts(html);

            // Постов может не быть по двум причинам: они кончились или вместо страницы
            // пришла заглушка провайдера. Настоящую страницу узнаём по разметке Telegram.
            if (pagePosts.Count == 0 && !html.Contains("tgme_", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{NetworkFailureMarker}: вместо {url} пришла не та страница");
            }
        }
        else
        {
            page = await openPageAsync();
            await page.GoToAsync(url, new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
                Timeout = _navigationTimeoutMs
            });
            await Task.Delay(firstPage ? 2000 : 1500);

            pagePosts = await ExtractPostsRawAsync(page);
        }
        var postLimit = Math.Min(maxPostsPerChannel, cursor.MaxPosts);

        // Режим «только новые»: посты с номером не больше StopAtId прочитаны в прошлый раз.
        bool IsOld(string postId)
        {
            var n = ParsePostNumber(postId);
            return cursor.StopAtId > 0 && (n == 0 || n <= cursor.StopAtId);
        }

        var reachedKnown = cursor.StopAtId > 0 && pagePosts.Any(p => IsOld(p.PostId));

        // Сначала новые посты: промокоды быстро истекают, свежие важнее.
        var fresh = pagePosts
            .Where(p => !cursor.SeenPostIds.Contains(p.PostId) && !IsOld(p.PostId))
            .OrderByDescending(p => ParsePostNumber(p.PostId))
            .Take(Math.Max(0, postLimit - cursor.PostsRead))
            .ToList();

        cursor.PagesRead++;
        _logger.Info($"[Telegram] Канал {cursor.Name}: страница {cursor.PagesRead}, новых постов: {fresh.Count}" +
                     (reachedKnown ? $" (дошли до поста #{cursor.StopAtId}, прочитанного в прошлый раз)" : string.Empty));

        if (fresh.Count == 0)
        {
            cursor.Exhausted = true;
            if (reachedKnown && cursor.PostsRead == 0)
            {
                _logger.Info($"[Telegram] Канал {cursor.Name}: новых постов с прошлого раза нет.");
            }
            else if (cursor.PostsRead == 0)
            {
                _logger.Warn($"[Telegram] Канал {cursor.Name}: не найдено постов. Возможно канал недоступен или заблокирован.");
                channelResult.Errors.Add($"Канал {cursor.Name}: посты не найдены");
            }

            return;
        }

        // Разбираем сразу, пока страница открыта: скриншот поста без ссылок
        // можно снять только с той страницы, где он виден.
        foreach (var (text, postId) in fresh)
        {
            cursor.SeenPostIds.Add(postId);
            await AnalyzePostAsync(page, cursor.Name, text, postId, channelResult);
        }

        cursor.PostsRead += fresh.Count;
        cursor.NewestId = Math.Max(cursor.NewestId,
            fresh.Select(p => ParsePostNumber(p.PostId)).DefaultIfEmpty(0).Max());

        var oldest = fresh.Select(p => ParsePostNumber(p.PostId)).Where(n => n > 0).DefaultIfEmpty(0).Min();
        if (oldest <= 1 || reachedKnown || cursor.PostsRead >= postLimit)
        {
            cursor.Exhausted = true;
        }
        else
        {
            cursor.OldestId = oldest;
        }
    }

    /// <summary>Ищет в посте ссылки на ассеты, git-ссылки и промокоды.</summary>
    private async Task AnalyzePostAsync(
        IPage? page, string channelName, string text, string postId, TelegramChannelResult channelResult)
    {
        channelResult.AllPosts.Add(new TelegramPostInfo
        {
            ChannelName = channelName,
            PostId = postId,
            Text = text
        });
        _logger.Debug($"[Telegram] ---- ПОСТ {channelName}/#{postId} ({text.Length} символов) ----");
        _logger.Debug($"[Telegram] {text}");

        var assetUrls = AssetUrlRegex.Matches(text)
            .Select(m => NormalizeAssetUrl(m.Value))
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var gitUrls = GitUrlRegex.Matches(text)
            .Select(m => m.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? m.Value
                : "https://" + m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var promocodes = ExtractPromocodes(text);

        channelResult.AssetUrls.AddRange(assetUrls);
        channelResult.GitLinks.AddRange(gitUrls);
        channelResult.Promocodes.AddRange(promocodes);

        if (assetUrls.Count > 0 && promocodes.Count > 0)
        {
            // Посты идут от новых к старым, поэтому первый найденный код — самый свежий.
            // Старый пост с тем же ассетом не должен подменить его истёкшим кодом.
            var firstPromo = promocodes[0];
            foreach (var url in assetUrls)
            {
                channelResult.AssetPromocodes.TryAdd(url, firstPromo);
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
        if (assetUrls.Count == 0 && gitUrls.Count == 0 && promocodes.Count == 0)
        {
            // Скриншот возможен только там, где пост открыт браузером. При чтении
            // без браузера текст поста всё равно попадает в telegram_posts_raw.log.
            if (_screenshotOnNoLinks && page is not null)
            {
                await TakePostScreenshotAsync(page, channelName, postId, text);
            }

            channelResult.PostsWithoutLinks.Add(new PostWithoutLink
            {
                ChannelName = channelName,
                PostId = postId,
                TextPreview = text.Length > 200 ? text[..200] + "..." : text
            });
        }
    }

    /// <summary>Промокоды из текста поста.</summary>
    internal static List<string> ExtractPromocodes(string text) => PromocodeRegex.Matches(text)
        .Select(m => m.Groups[1].Value.Trim())
        .Where(IsLikelyPromocode)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// Код раздачи — это «GIANTGREY2026», а не «below» из «use promo code below».
    /// Настоящие коды пишут заглавными или с цифрами.
    /// </summary>
    private static bool IsLikelyPromocode(string code) =>
        !code.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
        (code.Any(char.IsDigit) || code == code.ToUpperInvariant());

    /// <summary>Номер поста из "канал/1348". 0, если номера нет.</summary>
    private static int ParsePostNumber(string postId)
    {
        var tail = postId.Split('/').LastOrDefault() ?? string.Empty;
        return int.TryParse(tail, out var number) ? number : 0;
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

/// <summary>
/// Где остановилось чтение канала. Нужен, чтобы брать ассеты пачками:
/// следующая пачка начинается с постов старше уже прочитанных.
/// </summary>
internal sealed class TelegramChannelCursor(string name)
{
    public string Name { get; } = name;

    /// <summary>Номер самого старого прочитанного поста. 0 — канал ещё не открывали.</summary>
    public int OldestId { get; set; }

    /// <summary>
    /// Больше читать не нужно: посты кончились, дошли до StopAtId или прочитано MaxPosts.
    /// </summary>
    public bool Exhausted { get; set; }

    public int PostsRead { get; set; }
    public int PagesRead { get; set; }

    /// <summary>Режим «только новые»: посты с этим номером и старше читались в прошлый раз.</summary>
    public int StopAtId { get; set; }

    /// <summary>Сколько постов канала читать максимум (первый прогон в режиме «только новые»).</summary>
    public int MaxPosts { get; set; } = int.MaxValue;

    /// <summary>Номер самого свежего прочитанного поста — запоминается для следующего прогона.</summary>
    public int NewestId { get; set; }

    /// <summary>Канал не открылся в текущем чтении. Сбрасывается перед повтором через другой прокси.</summary>
    public bool FailedThisRead { get; set; }

    public HashSet<string> SeenPostIds { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class TelegramParseResult
{
    /// <summary>Каналы, которые не открылись в этом чтении.</summary>
    public List<string> FailedChannels { get; set; } = [];

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