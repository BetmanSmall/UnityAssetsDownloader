using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

/// <summary>
/// Магазин Fab (аккаунт Epic Games) в открытом окне обычного браузера.
///
/// Главное правило (проверено вживую 28.09): не делать запросов, которых не делает сама
/// страница. Прямой запрос данных ассета <c>/i/listings/&lt;id&gt;</c> Cloudflare отбивает, и
/// следующая страница уже просит галочку «я человек». Поэтому всё читается из открытой
/// страницы: сайт сам вшивает ответы своего API в HTML (<c>js-json-data-prefetched-data</c>,
/// ключи — адреса вида <c>/i/users/me</c>, <c>/i/listings/&lt;id&gt;</c>), а что ассет уже в
/// библиотеке, страница показывает кнопкой. Единственный запрос программы — добавление
/// (<c>POST /i/listings/&lt;id&gt;/add-to-library</c>), ровно такой, как по кнопке «Add to My Library».
///
/// Вход в Epic, проверка Cloudflare «я человек», капча — только руками человека:
/// программа замечает их, поднимает окно и ждёт.
/// </summary>
internal sealed partial class FabStore
{
    public const string DefaultBaseUrl = "https://www.fab.com";

    private readonly HumanBrowser _browser;
    private readonly AppLogger _logger;
    private readonly string _logsDirectory;
    private readonly string _baseUrl;
    private readonly string _baseHost;
    private readonly bool _interactive;
    private readonly Func<string, Task>? _notify;
    private readonly TimeSpan _navigationTimeout;

    /// <summary>Сколько ждать человека: вход в Epic — дольше, галочку Cloudflare — меньше.</summary>
    public TimeSpan LoginWait { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan ChallengeWait { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan ManualAddWait { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Сколько Cloudflare даём пропустить браузер самому, прежде чем звать человека.</summary>
    public TimeSpan ChallengeSelfPass { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>Сколько ждём, что Epic вспомнит аккаунт и вернёт на Fab без пароля.</summary>
    public TimeSpan EpicRememberWait { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Сколько ждём, пока страница ассета нарисует свою кнопку (она узнаёт о библиотеке сама).</summary>
    public TimeSpan ButtonWait { get; init; } = TimeSpan.FromSeconds(12);

    /// <summary>Имя аккаунта Epic, если Fab его показал.</summary>
    public string? AccountName { get; private set; }

    public FabStore(HumanBrowser browser, AppLogger logger, string logsDirectory, string? baseUrl,
        bool interactive, Func<string, Task>? notify, TimeSpan navigationTimeout)
    {
        _browser = browser;
        _logger = logger;
        _logsDirectory = logsDirectory;
        _baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl).TrimEnd('/');
        _baseHost = new Uri(_baseUrl).Host;
        _interactive = interactive;
        _notify = notify;
        _navigationTimeout = navigationTimeout;
    }

    public string BaseUrl => _baseUrl;
    public string LimitedTimeFreeUrl => $"{_baseUrl}/limited-time-free";

    /// <summary>Открыта ли сейчас эта страница Fab (после входа Epic возвращает прямо на неё).</summary>
    public async Task<bool> IsOnAsync(string url)
    {
        var probe = await ProbeAsync();
        return probe is { Challenge: false } && Uri.TryCreate(probe.Url, UriKind.Absolute, out var now) &&
               now.Host.Equals(_baseHost, StringComparison.OrdinalIgnoreCase) &&
               now.AbsolutePath.TrimEnd('/').Equals(new Uri(url).AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ ссылки

    [GeneratedRegex(@"(?:https?://)?(?:www\.)?fab\.com/(?:[a-z]{2}(?:-[a-z]{2,4})?/)?listings/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})",
        RegexOptions.IgnoreCase)]
    private static partial Regex ListingLinkRegex();

    [GeneratedRegex(@"/listings/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})", RegexOptions.IgnoreCase)]
    private static partial Regex ListingUidRegex();

    /// <summary>Все ссылки на ассеты Fab в тексте, в одном виде: https://www.fab.com/listings/&lt;id&gt;.</summary>
    public static List<string> ExtractListingUrls(string text) => ListingLinkRegex().Matches(text)
        .Select(m => CanonicalUrl(m.Groups[1].Value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Номер ассета Fab из ссылки (в том числе с языком: /ru/listings/…). null — не ссылка на ассет.</summary>
    public static string? ListingUid(string url)
    {
        var m = ListingUidRegex().Match(url);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    public static string CanonicalUrl(string uid) => $"{DefaultBaseUrl}/listings/{uid.ToLowerInvariant()}";

    private string PageUrl(string uid) => $"{_baseUrl}/listings/{uid}";

    // ------------------------------------------------------------------ что на странице

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// Общие куски скрипта: данные, которые сайт вшил в страницу, и кнопки, которые он нарисовал.
    /// Кнопки карточек других ассетов на странице — значки без текста, поэтому по тексту
    /// находится только главная кнопка ассета.
    /// </summary>
    private const string PageHelpers = """
        const pageData = () => {
          try {
            if (window.prefetchedData && typeof window.prefetchedData === 'object') return window.prefetchedData;
            const el = document.getElementById('js-json-data-prefetched-data');
            return el ? JSON.parse(el.textContent) : null;
          } catch (e) { return null; }
        };
        const norm = s => (s || '').replace(/\s+/g, ' ').trim();
        const buttonTexts = () => [...document.querySelectorAll('button, a, [role=button]')]
          .filter(el => el.getClientRects().length > 0)
          .map(el => norm(el.innerText)).filter(t => t && t.length < 40);
        const hasButton = re => buttonTexts().some(t => re.test(t));
        const signInShown = () => !!document.querySelector('form[action*="/social/login"], a[href*="/social/login"]')
          || hasButton(/^(sign in|log in)$/i);
        const ownedShown = () => hasButton(/^(view in my library|saved in my library|view in launcher|open in launcher)$/i);
        """;

    private const string ProbeScript = """
        (() => {
          const t = document.title || '';
          const challenge = /just a moment|one more step|attention required|verify you are human|checking your browser/i.test(t)
            || !!document.querySelector('#challenge-form, #challenge-stage, #challenge-running, #cf-challenge-running, .cf-turnstile, #turnstile-wrapper');
          return { url: location.href, host: location.hostname, title: t, ready: document.readyState, challenge };
        })()
        """;

    internal sealed class PageProbe
    {
        public string Url { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Ready { get; set; } = string.Empty;
        public bool Challenge { get; set; }
    }

    private async Task<PageProbe?> ProbeAsync()
    {
        var value = await _browser.TryEvaluateAsync(ProbeScript);
        return value is { ValueKind: JsonValueKind.Object } v ? v.Deserialize<PageProbe>(Json) : null;
    }

    private bool IsFabHost(PageProbe? p) =>
        p is not null && p.Host.Equals(_baseHost, StringComparison.OrdinalIgnoreCase);

    private async Task<T?> ReadPageAsync<T>(string body) where T : class
    {
        var value = await _browser.TryEvaluateAsync($"(() => {{ {PageHelpers}\n{body} }})()");
        return value is { ValueKind: JsonValueKind.Object } v ? v.Deserialize<T>(Json) : null;
    }

    // ------------------------------------------------------------------ страница и Cloudflare

    /// <summary>
    /// Открывает страницу Fab. Если Cloudflare проверяет браузер — сначала даёт ему пройти
    /// самому, потом зовёт человека. false — страница так и не открылась.
    /// </summary>
    public async Task<bool> OpenAsync(string url)
    {
        try
        {
            await _browser.NavigateAsync(url, _navigationTimeout);
        }
        catch (InvalidOperationException ex)
        {
            _logger.Warn($"[Fab] {ex.Message}");
            return false;
        }

        return await PassChallengeAsync();
    }

    private async Task<bool> PassChallengeAsync()
    {
        var sw = Stopwatch.StartNew();
        var announced = false;
        while (sw.Elapsed < ChallengeSelfPass)
        {
            var probe = await ProbeAsync();
            if (probe is { Challenge: false })
            {
                if (announced)
                {
                    _logger.Info($"[Fab] Cloudflare пропустил сам за {sw.Elapsed.TotalSeconds:0} с.");
                }

                return true;
            }

            if (probe is { Challenge: true } && !announced)
            {
                _logger.Info("[Fab] Cloudflare проверяет браузер. Обычно он пропускает сам за несколько секунд...");
                announced = true;
            }

            await Task.Delay(1000);
        }

        return await WaitForHumanAsync(
            "FAB ПРОСИТ ПОДТВЕРДИТЬ, ЧТО ВЫ ЧЕЛОВЕК",
            [
                "В окне браузера отметьте галочку «Verify you are human» (Cloudflare).",
                "Программа сама её не нажимает — это делает человек, один раз:",
                "после проверки браузер запоминает её, следующие страницы откроются сами."
            ],
            async () => (await ProbeAsync()) is { Challenge: false },
            ChallengeWait,
            "fab-challenge");
    }

    // ------------------------------------------------------------------ вход

    private sealed class SignInView
    {
        public bool HasData { get; set; }
        public bool Me { get; set; }
        public string? Name { get; set; }
        public bool SignIn { get; set; }
    }

    /// <summary>
    /// Вошли ли в Fab — по самой странице: вошедшему сайт вшивает его данные (/i/users/me),
    /// гостю рисует «Sign in». null — страница не Fab или ещё на проверке Cloudflare.
    /// </summary>
    public async Task<bool?> IsSignedInAsync()
    {
        var probe = await ProbeAsync();
        if (!IsFabHost(probe) || probe!.Challenge)
        {
            return null;
        }

        var view = await ReadPageAsync<SignInView>("""
            const d = pageData();
            const u = d && d['/i/users/me'] && typeof d['/i/users/me'] === 'object' ? d['/i/users/me'] : null;
            const name = u ? (u.displayName || u.publicDisplayName || u.username || u.name || u.sellerName || null) : null;
            return { hasData: !!d, me: !!u, name, signIn: signInShown() };
            """);
        if (view is null)
        {
            return null;
        }

        if (view.Me)
        {
            AccountName = view.Name ?? AccountName;
            return true;
        }

        if (view.SignIn)
        {
            return false;
        }

        // Ни данных пользователя, ни кнопки входа: вёрстка поменялась. Кнопки «Sign in» нет —
        // значит, скорее всего, вошли; если нет, добавление ответит 401 и вход повторится.
        _logger.Debug($"[Fab] Вход по странице не понять (данные страницы: {view.HasData}) — считаем, что вошли.");
        return true;
    }

    /// <summary>
    /// Проверяет вход и, если его нет, открывает вход через Epic Games. Epic, который помнит
    /// аккаунт, возвращает на Fab сам; иначе входит человек — почта и пароль, Google, код, капча.
    /// </summary>
    /// <param name="reload">Сначала открыть страницу заново: посреди прогона открытая страница
    /// помнит вход с момента загрузки, а сессия могла кончиться после.</param>
    public async Task<bool> EnsureSignedInAsync(string returnPath, bool reload = false)
    {
        if (reload && !await OpenAsync(_baseUrl + returnPath))
        {
            return false;
        }

        if (await IsSignedInAsync() == true)
        {
            _logger.Info($"[Fab] Вход в аккаунт Epic есть{(AccountName is null ? string.Empty : $": {AccountName}")}.");
            return true;
        }

        _logger.Info("[Fab] Входа в Fab нет — открываем вход через Epic Games.");
        var loginUrl = $"{_baseUrl}/social/login/epic/?next={Uri.EscapeDataString(returnPath)}";
        try
        {
            await _browser.NavigateAsync(loginUrl, _navigationTimeout);
        }
        catch (InvalidOperationException ex)
        {
            _logger.Warn($"[Fab] Страница входа не открылась: {ex.Message}");
        }

        // Epic помнит аккаунт — вернёт на Fab сам за несколько секунд.
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < EpicRememberWait)
        {
            await Task.Delay(1500);
            if (await IsSignedInAsync() == true)
            {
                _logger.Info($"[Fab] Epic вспомнил аккаунт, вход без пароля{(AccountName is null ? string.Empty : $": {AccountName}")}.");
                return true;
            }
        }

        var ok = await WaitForHumanAsync(
            "ВОЙДИТЕ В АККАУНТ EPIC GAMES",
            [
                "В открытом окне браузера войдите в Epic Games — так, как вам удобно:",
                "почта и пароль, Google, Apple, код из письма. Если попросят подтвердить,",
                "что вы человек, — подтвердите. Это обычный браузер, входить можно спокойно.",
                "Вход запоминается в папке браузера Fab: в следующий раз войдёт сам."
            ],
            async () => await IsSignedInAsync() == true,
            LoginWait,
            "fab-login");

        if (ok)
        {
            _logger.Info($"[Fab] Вход выполнен{(AccountName is null ? string.Empty : $": {AccountName}")}. Он сохранён в папке браузера Fab.");
        }

        return ok;
    }

    // ------------------------------------------------------------------ «Limited-Time Free»

    internal sealed class LimitedFreePage
    {
        public List<string> Links { get; set; } = [];
        public List<string> Titles { get; set; } = [];
        public string? Until { get; set; }
    }

    /// <summary>
    /// Ассеты раздачи «Limited-Time Free» с текущей страницы (её надо открыть заранее).
    /// Ждёт, пока карточки нарисуются, до 15 секунд.
    /// </summary>
    public async Task<LimitedFreePage> ReadLimitedTimeFreeAsync()
    {
        var sw = Stopwatch.StartNew();
        LimitedFreePage? page = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            page = await ReadPageAsync<LimitedFreePage>("""
                const seen = new Map();
                for (const a of document.querySelectorAll('a[href*="/listings/"]')) {
                  if (a.closest('header, nav, footer')) continue;
                  const href = a.href.split('?')[0].split('#')[0];
                  const text = (a.innerText || '').split('\n').map(s => s.trim()).filter(Boolean)[0] || '';
                  if (!seen.has(href) || (!seen.get(href) && text)) seen.set(href, text);
                }
                const m = (document.body.innerText || '').match(/Limited-Time Free\s*\(([^)]{3,80})\)/i);
                return { links: [...seen.keys()], titles: [...seen.values()], until: m ? m[1].trim() : null };
                """);
            if (page is { Links.Count: > 0 })
            {
                break;
            }

            await Task.Delay(1000);
        }

        page ??= new LimitedFreePage();
        page.Links = page.Links.Select(ListingUid).OfType<string>().Select(CanonicalUrl).ToList();
        if (page.Links.Count == 0)
        {
            _logger.Warn("[Fab] На странице «Limited-Time Free» не нашлось ни одного ассета. Скриншот — в папке логов.");
            await SaveDiagnosticsAsync("fab-limited-free-empty");
        }

        return page;
    }

    // ------------------------------------------------------------------ ассет

    internal enum ClaimOutcome
    {
        Added,
        AlreadyOwned,
        WouldAdd,
        Paid,
        Removed,
        NeedsLogin,
        Unknown,
        Failed
    }

    internal sealed class ClaimResult
    {
        public ClaimOutcome Outcome { get; set; }
        public string? Title { get; set; }
        public string? Seller { get; set; }
        public List<string> Formats { get; set; } = [];
        public string? License { get; set; }
        public string? Price { get; set; }
        public string Message { get; set; } = string.Empty;

        public string Summary =>
            string.Join(" | ", new[]
            {
                Title, Seller is null ? null : $"автор {Seller}",
                Formats.Count > 0 ? string.Join(", ", Formats) : null,
                Price, License is null ? null : $"лицензия {License}"
            }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    internal sealed class LicenseInfo
    {
        public string? Name { get; set; }
        public string? Slug { get; set; }
        public string? OfferId { get; set; }
        public decimal? Price { get; set; }
        public decimal? Discounted { get; set; }
        public string? Currency { get; set; }

        /// <summary>Цена после скидки — так её считает сама страница.</summary>
        public decimal? Effective => Discounted ?? Price;
    }

    internal sealed class ListingData
    {
        public string? Title { get; set; }
        public string? Seller { get; set; }
        public List<string> Formats { get; set; } = [];
        public List<LicenseInfo> Licenses { get; set; } = [];
    }

    internal sealed class ListingView
    {
        public bool HasData { get; set; }
        public List<string> Keys { get; set; } = [];
        public ListingData? Listing { get; set; }
        public bool? Acquired { get; set; }
        public bool Me { get; set; }
        public bool SignIn { get; set; }
        public bool ButtonAdd { get; set; }
        public bool ButtonOwned { get; set; }
        public bool ButtonPaid { get; set; }
        public string? Heading { get; set; }

        public bool AnyButton => ButtonAdd || ButtonOwned || ButtonPaid;
    }

    /// <summary>Разбор данных ассета — общий для вшитых в страницу и полученных запросом.</summary>
    private const string ListingSummary = """
        const summarize = L => L && typeof L === 'object' && !Array.isArray(L) ? {
          title: L.title || L.name || null,
          seller: (L.user && (L.user.sellerName || L.user.displayName)) || null,
          formats: (L.assetFormats || []).map(f => f.assetFormatType && (f.assetFormatType.name || f.assetFormatType.code)).filter(Boolean),
          licenses: (L.licenses || []).map(l => ({
            name: l.name || null, slug: l.slug || null, offerId: l.offerId || null,
            price: l.priceTier ? l.priceTier.price : null,
            discounted: l.priceTier && l.priceTier.discountedPrice != null ? l.priceTier.discountedPrice : null,
            currency: l.priceTier ? (l.priceTier.currencyCode || null) : null }))
        } : null;
        """;

    private Task<ListingView?> ReadListingViewAsync(string uid) => ReadPageAsync<ListingView>($$"""
        {{ListingSummary}}
        const uid = {{JsonSerializer.Serialize(uid)}};
        const d = pageData();
        const entries = d ? Object.entries(d).map(([k, v]) => [k.replace(/^https?:\/\/[^/]+/, ''), v]) : [];
        const pick = pred => { const e = entries.find(([k, v]) => v && pred(k.split('?')[0], k)); return e ? e[1] : null; };
        const listing = summarize(pick(p => p === '/i/listings/' + uid));
        // Скидка раздачи (100 %) может лежать отдельно — в ответе prices-infos.
        const P = pick(p => p === '/i/listings/prices-infos');
        const offers = Array.isArray(P) ? P : P ? (P.offers || P.results || Object.values(P)) : [];
        if (listing) for (const o of Array.isArray(offers) ? offers : []) {
          const l = o && listing.licenses.find(x => x.offerId === o.offerId);
          if (l && o.discountedPrice != null) l.discounted = o.discountedPrice;
          if (l && l.price == null && o.price != null) l.price = o.price;
        }
        const S = pick((p, k) => p.startsWith('/i/users/me/listings-states') && k.includes(uid));
        const states = S ? (Array.isArray(S) ? S : [S]) : [];
        const mine = states.find(s => s && (s.uid === uid || s.listingUid === uid)) || (states.length === 1 ? states[0] : null);
        const texts = buttonTexts();
        return {
          hasData: !!d, keys: entries.map(([k]) => k).slice(0, 40), listing,
          acquired: mine ? !!(mine.acquired || mine.entitlementId) : null,
          me: !!(d && d['/i/users/me']), signIn: signInShown(),
          buttonAdd: texts.some(t => /^add to my library$/i.test(t)),
          buttonOwned: ownedShown(),
          buttonPaid: texts.some(t => /^(buy now|add to cart)$/i.test(t)),
          heading: norm((document.querySelector('h1') || {}).innerText).slice(0, 120) || null };
        """);

    /// <summary>
    /// Добавляет ассет Fab в библиотеку, если он бесплатный (в том числе по раздаче со скидкой 100 %).
    /// Деньги не тратятся никогда: платное не трогается, а «в библиотеку» Fab кладёт только
    /// бесплатное — покупка идёт другой дорогой, через корзину и оплату, которых программа не касается.
    /// </summary>
    public async Task<ClaimResult> ClaimAsync(string listingUrl, bool dryRun)
    {
        var uid = ListingUid(listingUrl);
        if (uid is null)
        {
            return new ClaimResult { Outcome = ClaimOutcome.Failed, Message = "Это не ссылка на ассет Fab." };
        }

        if (!await OpenAsync(PageUrl(uid)))
        {
            return new ClaimResult { Outcome = ClaimOutcome.Failed, Message = "Страница ассета не открылась." };
        }

        // Страница сама узнаёт, есть ли ассет в библиотеке, и рисует кнопку — ждём её.
        var view = await ReadListingViewAsync(uid);
        var sw = Stopwatch.StartNew();
        while (view is not { AnyButton: true } && sw.Elapsed < ButtonWait)
        {
            await Task.Delay(700);
            view = await ReadListingViewAsync(uid) ?? view;
        }

        if (view is null)
        {
            await SaveDiagnosticsAsync($"fab-unknown-{uid[..8]}");
            return new ClaimResult { Outcome = ClaimOutcome.Failed, Message = "Страница ассета не прочиталась." };
        }

        _logger.Debug($"[Fab] Страница {uid}: данные {(view.HasData ? string.Join(", ", view.Keys) : "нет")}; " +
                      $"кнопки: добавить={view.ButtonAdd}, уже есть={view.ButtonOwned}, платный={view.ButtonPaid}");

        var listing = view.Listing;
        if (listing is null)
        {
            // Данных в странице нет (вёрстка поменялась) — спрашиваем Fab запросом, как спросила бы страница.
            var fetched = await FetchListingAsync(uid);
            if (fetched.Reply is { Status: 404 })
            {
                return new ClaimResult { Outcome = ClaimOutcome.Removed, Title = view.Heading, Message = "Ассета больше нет на Fab (удалён или скрыт)." };
            }

            listing = fetched.Listing;
            if (listing is null && !view.AnyButton)
            {
                await SaveDiagnosticsAsync($"fab-unknown-{uid[..8]}");
                return new ClaimResult
                {
                    Outcome = ClaimOutcome.Failed,
                    Title = view.Heading,
                    Message = $"На странице нет ни данных ассета, ни его кнопки ({fetched.Reply?.Describe() ?? "запрос не ответил"})."
                };
            }
        }

        var result = new ClaimResult
        {
            Title = listing?.Title ?? view.Heading,
            Seller = listing?.Seller,
            Formats = listing?.Formats ?? []
        };

        if (view.Acquired == true || view.ButtonOwned)
        {
            result.Outcome = ClaimOutcome.AlreadyOwned;
            result.Message = "Уже в библиотеке Fab.";
            return result;
        }

        if (!view.Me && view.SignIn)
        {
            result.Outcome = ClaimOutcome.NeedsLogin;
            result.Message = "Fab показывает кнопку «Sign in» — входа нет.";
            return result;
        }

        var licenses = listing?.Licenses ?? [];
        var free = licenses.Where(l => l.OfferId is not null && l.Effective == 0m).ToList();
        if (free.Count == 0 && view.ButtonAdd)
        {
            // Страница предлагает «Add to My Library», а скидки в данных не видно — верим странице.
            free = licenses.Where(l => l.OfferId is not null).ToList();
        }

        if (free.Count == 0 && !view.ButtonAdd)
        {
            var cheapest = licenses.Where(l => l.Effective is not null).MinBy(l => l.Effective);
            result.Outcome = ClaimOutcome.Paid;
            result.Price = cheapest is null ? null : $"{cheapest.Effective} {cheapest.Currency}".Trim();
            result.Message = licenses.Count == 0 && !view.ButtonPaid
                ? "У ассета нет лицензий, которые можно получить (возможно, недоступен в вашей стране)."
                : $"Платный{(result.Price is null ? string.Empty : $": от {result.Price}")}.";
            return result;
        }

        if (free.Count == 0)
        {
            // Кнопка «Add to My Library» есть, а данных о лицензиях нет — нажимать её будет человек.
            result.Price = "бесплатно";
            if (dryRun)
            {
                result.Outcome = ClaimOutcome.WouldAdd;
                result.Message = "Был бы добавлен (проверочный запуск).";
                return result;
            }

            return await AddByHandAsync(uid, result, "на странице нет данных о лицензиях");
        }

        // Бесплатная профессиональная лицензия шире личной: берём её, если отдают даром.
        var ordered = free
            .OrderBy(l => string.Equals(l.Slug, "professional", StringComparison.OrdinalIgnoreCase) ? 0
                : string.Equals(l.Slug, "personal", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ToList();
        var offer = ordered[0];
        result.License = offer.Name ?? offer.Slug;
        result.Price = offer.Price is > 0m && offer.Effective == 0m
            ? $"бесплатно по раздаче (обычно {offer.Price} {offer.Currency})".Replace(" )", ")")
            : "бесплатно";

        if (dryRun)
        {
            result.Outcome = ClaimOutcome.WouldAdd;
            result.Message = "Был бы добавлен (проверочный запуск).";
            return result;
        }

        ApiReply? add = null;
        foreach (var candidate in ordered.Take(2))
        {
            add = await AddToLibraryAsync(uid, candidate.OfferId!);
            if (add is { Challenged: true })
            {
                _logger.Info("[Fab] Cloudflare проверил добавление — открываем страницу заново и повторяем.");
                if (await OpenAsync(PageUrl(uid)))
                {
                    add = await AddToLibraryAsync(uid, candidate.OfferId!);
                }
            }

            if (add is { Ok: true })
            {
                result.License = candidate.Name ?? candidate.Slug;
                result.Outcome = ClaimOutcome.Added;
                result.Message = "Добавлен в библиотеку Fab.";
                return result;
            }

            if (add is { Status: 401 })
            {
                result.Outcome = ClaimOutcome.NeedsLogin;
                result.Message = $"Fab не принял добавление без входа: {add.Describe()}";
                return result;
            }

            if (add is null || add.Status is 0 or 403 || !(add.Status is >= 400 and < 500))
            {
                break;
            }
        }

        // Не вышло запросом (например, Fab хочет, чтобы один раз приняли лицензию Fab EULA) —
        // человек нажимает кнопку на открытой странице сам.
        return await AddByHandAsync(uid, result, add?.Describe() ?? "нет ответа");
    }

    private async Task<ClaimResult> AddByHandAsync(string uid, ClaimResult result, string reason)
    {
        _logger.Info($"[Fab] Запросом добавить не получилось: {reason}");
        await SaveDiagnosticsAsync($"fab-add-failed-{uid[..8]}");

        var byHand = await WaitForHumanAsync(
            "FAB: ДОБАВЬТЕ АССЕТ РУКАМИ",
            [
                $"Ассет «{result.Title ?? uid}» не добавился автоматически ({Shorten(reason, 120)}).",
                "В окне браузера нажмите «Add to My Library». Если Fab попросит принять лицензию",
                "(Fab EULA) — примите её: это нужно один раз на аккаунт, дальше программа справится сама."
            ],
            async () => (await ReadListingViewAsync(uid))?.ButtonOwned == true,
            ManualAddWait,
            "fab-add-by-hand");

        result.Outcome = byHand ? ClaimOutcome.Added : ClaimOutcome.Failed;
        result.Message = byHand ? "Добавлен в библиотеку Fab (кнопкой в окне)." : $"Не добавился: {reason}";
        return result;
    }

    // ------------------------------------------------------------------ запросы как у самой страницы

    /// <summary>
    /// Запрос к Fab изнутри открытой страницы — с теми же заголовками, что у её собственного
    /// клиента (X-Requested-With, X-CsrfToken из cookie fab_csrftoken, Accept-Language).
    /// </summary>
    private const string ApiHelper = """
        const fabApi = async (method, path, form) => {
          const token = () => { const m = document.cookie.match(/(?:^|;\s*)fab_csrftoken=([^;]*)/); return m ? decodeURIComponent(m[1]) : null; };
          if (!token()) { try { await fetch('/i/csrf', { headers: { 'X-Requested-With': 'XMLHttpRequest' } }); } catch (e) {} }
          const headers = { 'X-Requested-With': 'XMLHttpRequest', 'Accept': 'application/json, text/plain, */*', 'Accept-Language': 'en' };
          if (token()) headers['X-CsrfToken'] = token();
          let body;
          if (form) { body = new FormData(); for (const [k, v] of Object.entries(form)) body.append(k, v); }
          try {
            const r = await fetch(path, { method, headers, body, credentials: 'same-origin' });
            const text = await r.text();
            let json = null;
            try { json = text ? JSON.parse(text) : null; } catch (e) {}
            const challenged = r.headers.get('cf-mitigated') === 'challenge' || (json === null && /<html|<!doctype/i.test(text));
            return { status: r.status, json, challenged, text: json === null ? text.slice(0, 300) : null };
          } catch (e) {
            return { status: 0, json: null, challenged: false, text: String(e) };
          }
        };
        """;

    internal sealed class ApiReply
    {
        public int Status { get; set; }
        public JsonElement Json { get; set; }
        public bool Challenged { get; set; }
        public string? Text { get; set; }

        public bool Ok => Status is >= 200 and < 300;
        public bool HasJson => Json.ValueKind is JsonValueKind.Object or JsonValueKind.Array;

        public string Describe() =>
            Challenged ? $"HTTP {Status}, Cloudflare проверяет браузер"
            : $"HTTP {Status}{(HasJson ? $": {Shorten(Json.ToString(), 300)}" : Text is { Length: > 0 } t ? $": {Shorten(t, 200)}" : string.Empty)}";
    }

    private sealed class FetchedListing
    {
        public ApiReply? Reply { get; set; }
        public ListingData? Listing { get; set; }
    }

    private async Task<T?> RunApiAsync<T>(string body) where T : class
    {
        var value = await _browser.EvaluateAsync($"(async () => {{ {ApiHelper}\n{ListingSummary}\n{body} }})()", TimeSpan.FromSeconds(60));
        return value.ValueKind == JsonValueKind.Object ? value.Deserialize<T>(Json) : null;
    }

    private async Task<FetchedListing> FetchListingAsync(string uid)
    {
        _logger.Debug($"[Fab] Данных ассета {uid} в странице нет — спрашиваем /i/listings/{uid}.");
        var fetched = await RunApiAsync<FetchedListing>($$"""
            const r = await fabApi('GET', '/i/listings/' + {{JsonSerializer.Serialize(uid)}});
            const ok = r.status >= 200 && r.status < 300;
            return { reply: { status: r.status, challenged: r.challenged, text: r.text, json: ok ? null : r.json }, listing: ok ? summarize(r.json) : null };
            """);
        return fetched ?? new FetchedListing();
    }

    private async Task<ApiReply?> AddToLibraryAsync(string uid, string offerId)
    {
        var reply = await RunApiAsync<ApiReply>($$"""
            const r = await fabApi('POST', '/i/listings/' + {{JsonSerializer.Serialize(uid)}} + '/add-to-library', { offer_id: {{JsonSerializer.Serialize(offerId)}} });
            return { status: r.status, challenged: r.challenged, text: r.text, json: r.json };
            """);
        _logger.Debug($"[Fab] add-to-library {uid} ({offerId}): {reply?.Describe() ?? "нет ответа"}");
        return reply;
    }

    // ------------------------------------------------------------------ человек

    /// <summary>
    /// Зовёт человека: крупное сообщение в консоли, окно браузера наверх, сообщение боту.
    /// Ждёт, пока done() не скажет «готово», или timeout.
    /// </summary>
    private async Task<bool> WaitForHumanAsync(string title, string[] lines, Func<Task<bool>> done, TimeSpan timeout, string shotPrefix)
    {
        if (!_interactive)
        {
            _logger.Warn($"[Fab] {title.ToLowerInvariant()} — но программа запущена без человека (--interactive false), ждать некого.");
            await SaveDiagnosticsAsync(shotPrefix);
            return false;
        }

        _logger.Info("============================================================");
        _logger.Info($" {title}");
        foreach (var line in lines)
        {
            _logger.Info($" {line}");
        }

        _logger.Info($" Программа ждёт до {timeout.TotalMinutes:0} мин и продолжит сама.");
        _logger.Info("============================================================");

        await _browser.BringToFrontAsync();
        if (_notify is not null)
        {
            await _notify($"⏳ Fab ждёт вас: {title.ToLowerInvariant()}. Окно браузера открыто на компьютере.");
        }

        var sw = Stopwatch.StartNew();
        var nextNote = TimeSpan.FromMinutes(1);
        while (sw.Elapsed < timeout)
        {
            if (!_browser.IsAlive)
            {
                throw new CdpException("окно браузера закрыли", disconnected: true);
            }

            if (await done())
            {
                _logger.Info($"[Fab] Готово, спасибо. Продолжаем ({sw.Elapsed.TotalSeconds:0} с).");
                return true;
            }

            if (sw.Elapsed > nextNote)
            {
                _logger.Info($"[Fab] Ждём... ({sw.Elapsed.TotalMinutes:0} из {timeout.TotalMinutes:0} мин)");
                nextNote += TimeSpan.FromMinutes(1);
            }

            await Task.Delay(2000);
        }

        _logger.Warn($"[Fab] Не дождались: {title.ToLowerInvariant()} ({timeout.TotalMinutes:0} мин).");
        await SaveDiagnosticsAsync(shotPrefix);
        return false;
    }

    /// <summary>Скриншот и HTML страницы в папку логов — чтобы по ним понять, что пошло не так.</summary>
    public async Task SaveDiagnosticsAsync(string prefix)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        await _browser.SaveScreenshotAsync(Path.Combine(_logsDirectory, $"{prefix}-{stamp}.png"));
        await _browser.SaveHtmlAsync(Path.Combine(_logsDirectory, $"{prefix}-{stamp}.html"));
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
