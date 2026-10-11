using PuppeteerSharp;

/// <summary>
/// Облегчённые страницы магазина для сервера: без картинок, видео, шрифтов и счётчиков аналитики, и меньше
/// отдельных процессов Chrome.
///
/// Сервер — 1 ядро и ~1 ГБ на несколько чужих проектов. 10.10 в прогоне службы свободной памяти оставалось 70 МБ,
/// а вкладка в репетиции умерла (вероятно, от нехватки памяти). Страница ассета — ~1,4 МБ, и почти всё это картинки,
/// ролики YouTube в отдельных процессах и аналитика; программе из этого нужны только кнопки и данные страницы.
///
/// Для магазина ничего не меняется: клики те же, запросы магазина (страница, GraphQL, вход) идут как обычно, не
/// грузятся только картинки с CDN и сторонние счётчики — так же выглядит браузер с блокировщиком рекламы. Блокирует
/// сам браузер (Network.setBlockedURLs): запросы не проходят через программу, кэш браузера работает как обычно.
///
/// Включено по умолчанию в контейнере (сервер), выключено на ПК и Deck (там человек может смотреть в окно браузера).
/// LITE_PAGES=0 / 1 в окружении (на сервере — в .env) — выключить или включить явно.
/// </summary>
internal static class LitePages
{
    /// <summary>Чего не грузим. Шаблоны браузера: «*» — любые символы.</summary>
    internal static readonly string[] BlockedUrlPatterns =
    [
        // Картинки: обложки, скриншоты, аватары (CDN магазина и чужие).
        "*.jpg*", "*.jpeg*", "*.png*", "*.gif*", "*.webp*", "*.avif*", "*.bmp*",
        // Видео и встроенные ролики. Саму страничку ролика в рамке браузер всё равно откроет (рамки этот список не
        // останавливает), но всё тяжёлое в ней — плеер, превью, поток — с этих адресов и не грузится.
        "*.mp4*", "*.webm*", "*.m3u8*", "*youtube.com/*", "*youtube-nocookie.com*", "*ytimg.com*", "*googlevideo.com*",
        "*vimeo.com*", "*vimeocdn.com*",
        // Шрифты.
        "*.woff*", "*.ttf*", "*.otf*", "*.eot*",
        // Счётчики аналитики и рекламы.
        "*google-analytics.com*", "*googletagmanager.com*", "*doubleclick.net*", "*googleadservices.com*",
        "*clarity.ms*", "*amplitude.com*", "*hotjar.com*", "*facebook.net*", "*connect.facebook.com*",
        "*bat.bing.com*", "*snap.licdn.com*", "*demdex.net*", "*omtrdc.net*", "*quantserve.com*", "*tiktok.com*"
    ];

    /// <summary>
    /// Флаги Chrome: ролики и фреймы чужих сайтов — в том же процессе, что и страница, а процессов отрисовки не больше
    /// двух. Ходим только на сайты магазина и входа, так что изоляция сайтов друг от друга здесь не нужна.
    /// </summary>
    internal static readonly string[] ChromeArgs =
    [
        "--disable-site-isolation-trials",
        "--disable-features=site-per-process,IsolateOrigins",
        "--renderer-process-limit=2"
    ];

    /// <summary>Включены ли облегчённые страницы: LITE_PAGES, иначе — да в контейнере, нет на ПК и Deck.</summary>
    public static bool IsEnabled(string? env = null, bool? inContainer = null)
    {
        env ??= Environment.GetEnvironmentVariable("LITE_PAGES");
        switch (env?.Trim().ToLowerInvariant())
        {
            case "0" or "off" or "no" or "false" or "нет":
                return false;
            case "1" or "on" or "yes" or "true" or "да":
                return true;
            default:
                return inContainer ?? File.Exists("/.dockerenv");
        }
    }

    /// <summary>Включает блокировку на вкладке. null — включилось, иначе текст ошибки (страницы тогда грузятся целиком).</summary>
    public static async Task<string?> ApplyAsync(IPage page)
    {
        try
        {
            await page.Client.SendAsync("Network.setBlockedURLs", new { urls = BlockedUrlPatterns });
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Считает запросы, которые браузер не стал грузить по списку. Событие PuppeteerSharp об ошибке запроса для них не
    /// приходит (браузер отказывает раньше, чем запрос «начался»), поэтому слушаем само сообщение браузера.
    /// </summary>
    public static void CountBlocked(IPage page, Action onBlocked) =>
        page.Client.MessageReceived += (_, e) =>
        {
            if (e.MessageID == "Network.loadingFailed" && IsBlockedFailure(e.MessageData.ToString()))
            {
                onBlocked();
            }
        };

    /// <summary>Сообщение Network.loadingFailed — это наша блокировка (а не сбой сети).</summary>
    internal static bool IsBlockedFailure(string? json) =>
        json is not null && (json.Contains("\"blockedReason\":\"inspector\"", StringComparison.Ordinal) ||
                             json.Contains("ERR_BLOCKED_BY_CLIENT", StringComparison.Ordinal));
}
