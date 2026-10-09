using System.Text.Json;

/// <summary>
/// Fab (fab.com, аккаунт Epic Games): бесплатные ассеты — в библиотеку, как с Unity.
///
/// --fab        раздача «Limited-Time Free» + ссылки fab.com из Telegram-каналов
///              (первый раз — вся история каналов, дальше — только новые посты);
/// --fab-url    только эти ассеты;
/// --fab-login  только вход в Epic и проверка, что Fab открывается.
///
/// Работает в видимом окне настоящего браузера (HumanBrowser): Cloudflare на fab.com
/// невидимый браузер не пропускает, а проверку «я человек» за человека программа не проходит.
/// Поэтому Fab — на Deck и ПК, не на сервере.
/// </summary>
internal sealed partial class UnityAssetAutomationApp
{
    /// <summary>Сколько ждём человека на сервере: вход в Epic, галочка, лицензия. Потом окно закрывается — оно занимает память.</summary>
    private static readonly TimeSpan FabServerHumanWait = TimeSpan.FromMinutes(30);

    /// <summary>Сервер: не чаще раза в это время зовём человека, если он в прошлый раз не пришёл.</summary>
    private static readonly TimeSpan FabHumanQuietPeriod = TimeSpan.FromHours(24);

    /// <summary>
    /// Сервер: сколько последних постов канала читаем в первый раз. Старые ссылки Fab к тому времени уже
    /// разобрали на ПК и Deck (пункт F), а читать всю историю — сотни страниц и заходов на Fab.
    /// </summary>
    private const int FabServerFirstPosts = 100;

    /// <summary>Сервер: за сколько до конца раздачи напомнить, что остались неполученные ассеты.</summary>
    private static readonly TimeSpan FabGiveawayReminderLead = TimeSpan.FromDays(3);

    /// <summary>Сервер: как часто заходим на страницу раздачи, если в каналах ничего нового.</summary>
    private static readonly TimeSpan FabLimitedTimeFreeEvery = TimeSpan.FromDays(2);

    /// <summary>Ручной запуск: --fab, --fab-login, --fab-url (Deck, ПК, на сервере — через exec).</summary>
    private Task RunFabAsync() => RunFabAsync(server: _options.FabServerOnce);

    /// <summary>
    /// Сервер (--watch и FAB=on): после прогона Unity. Новые ссылки Fab из каналов — в библиотеку, раздачу
    /// раз в пару дней — сообщить боту. Браузер открывается, только если есть что делать. Человека зовёт
    /// только за входом, галочкой и лицензией — и не чаще раза в сутки. Ничего не бросает наружу.
    /// </summary>
    internal async Task RunFabServerStageAsync()
    {
        if (!_options.Watch || !_options.FabOnServer)
        {
            return;
        }

        try
        {
            await RunFabAsync(server: true);
        }
        catch (Exception ex)
        {
            _logger.Error($"[Fab] Этап Fab на сервере сорвался: {ex.Message}");
            _logger.Debug(ex.ToString());
        }
    }

    private async Task RunFabAsync(bool server)
    {
        var loginOnly = !server && _options.FabLoginOnly;
        _logger.Info("============================================================");
        _logger.Info(server ? " FAB (СЕРВЕР): НОВЫЕ АССЕТЫ ИЗ КАНАЛОВ И РАЗДАЧА"
            : _options.FabReconUrl is not null ? " FAB: РАЗВЕДКА — ВЫ ПОЛУЧАЕТЕ АССЕТ САМИ, ПРОГРАММА ЖДЁТ"
            : _options.FabGiveaway ? " FAB: РАЗДАЧА LIMITED-TIME FREE — АССЕТЫ ПО ОЧЕРЕДИ, КАПЧУ РЕШАЕТЕ ВЫ"
            : loginOnly ? " FAB: ВХОД В АККАУНТ EPIC GAMES" : " FAB.COM: БЕСПЛАТНЫЕ АССЕТЫ НА АККАУНТ EPIC GAMES");
        _logger.Info("============================================================");

        // Сервер без экрана: окно браузера живёт на виртуальном экране, а человеку (вход в Epic,
        // галочка) его показывает удалённый доступ — только на время просьбы.
        RemoteWindow? remote = null;
        if (!_options.HasScreen)
        {
            if (!RemoteWindow.IsInstalled())
            {
                _logger.Error("Fab работает только в видимом окне браузера, а здесь нет экрана (сервер, Docker, SSH),");
                _logger.Error("и виртуального экрана (Xvfb, x11vnc, noVNC) тоже нет — его ставит образ сервера (Dockerfile).");
                _logger.Error("Cloudflare на fab.com не пускает невидимые браузеры, а проверку «я человек» программа");
                _logger.Error("за человека не проходит. Запускайте Fab на Deck или ПК (./run.sh или run.bat → F) или в образе сервера.");
                Environment.ExitCode = 2;
                return;
            }

            try
            {
                remote = await RemoteWindow.StartAsync(_logger, new RemoteWindow.Settings { Password = _options.FabVncPassword });
            }
            catch (Exception ex)
            {
                _logger.Error($"[Fab] Виртуальный экран не запустился: {ex.Message}");
                Environment.ExitCode = 2;
                return;
            }

            _logger.Info("[Fab] Экрана нет — окно браузера на виртуальном экране; человеку его покажет удалённое окно (по SSH-туннелю).");
        }

        try
        {
            await RunFabOnScreenAsync(loginOnly, remote, server);
        }
        finally
        {
            if (remote is not null)
            {
                await remote.DisposeAsync();
            }
        }
    }

    private async Task RunFabOnScreenAsync(bool loginOnly, RemoteWindow? remote, bool server)
    {
        if (_options.Headless && remote is null)
        {
            _logger.Info("[Fab] Невидимый режим для Fab не работает (Cloudflare) — окно браузера будет видно.");
        }

        var giveaway = !server && _options.FabGiveaway;
        var reconUid = !server && _options.FabReconUrl is { } reconUrl ? FabStore.ListingUid(reconUrl) : null;
        if (!server && _options.FabReconUrl is not null && reconUid is null)
        {
            _logger.Error("[Fab] --fab-recon: нужна ссылка вида https://www.fab.com/listings/<номер>.");
            Environment.ExitCode = 2;
            return;
        }

        var profileDirectory = _profileStore.GetProfileDirectory(_profileName);
        var fabDirectory = Path.Combine(profileDirectory, "fab");
        Directory.CreateDirectory(fabDirectory);
        // library.txt — только проверенное: после добавления страница показала «View in My Library».
        // owned.txt версий 1.28.0–1.28.2 записывался без такой проверки и больше не читается.
        var owned = new OwnedAssetsCache(fabDirectory, "library.txt", "Ассеты Fab, которые точно в библиотеке аккаунта Epic (проверено по странице).");
        var removed = new OwnedAssetsCache(fabDirectory, "removed.txt", "Ассеты Fab, которых больше нет на сайте.");
        var telegramState = TelegramChannelState.Load(fabDirectory);
        // Сервер: что программа сама не получает (раздача −100 %): бот о таком сообщает один раз.
        var needsManual = new OwnedAssetsCache(fabDirectory, "needs_manual.txt",
            "Ассеты Fab, которые программа сама не получает (раздача −100 % — покупка за 0): бот о них сообщил.");
        var serverState = server ? FabServerState.Load(fabDirectory) : null;
        // Человека недавно звали и он не пришёл: на Fab не заходим (Cloudflare, память) до конца суток
        // или до его ручного входа (--fab-login сбрасывает эту отметку).
        if (serverState is { HumanAskedUtc: { } askedAt } && !serverState.CanAskHuman(DateTime.UtcNow, FabHumanQuietPeriod))
        {
            _logger.Info($"[Fab] Сервер: человека звали {askedAt.ToLocalTime():dd.MM HH:mm} ({serverState.HumanReason}) — ответа не было. " +
                         "На Fab пока не заходим: повторим через сутки после того вызова или после ручного входа (--fab-login).");
            return;
        }

        bool IsKnown(string url) => !_options.RecheckOwned &&
                                    (owned.Contains(url) || removed.Contains(url) || (server && needsManual.Contains(url)));

        // Что проверять. Каналы читаются до браузера: без него, за секунды.
        var queue = new List<(string Url, string From)>();
        var queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fromTelegram = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Enqueue(string url, string from, bool first = false)
        {
            if (!queued.Add(url))
            {
                return;
            }

            if (first)
            {
                queue.Insert(queue.Count(q => q.From.StartsWith("раздача", StringComparison.Ordinal)), (url, from));
            }
            else
            {
                queue.Add((url, from));
            }
        }

        var explicitUrls = _options.FabUrls
            .Select(FabStore.ListingUid).OfType<string>().Select(FabStore.CanonicalUrl).ToList();
        if (_options.FabUrls.Count > explicitUrls.Count)
        {
            _logger.Warn("[Fab] Часть адресов --fab-url — не ссылки на ассеты Fab (нужно https://www.fab.com/listings/<номер>). Они пропущены.");
        }

        List<TelegramChannelCursor>? cursors = null;
        if (!loginOnly && reconUid is null && !giveaway)
        {
            if (explicitUrls.Count > 0)
            {
                _logger.Info($"[Fab] Проверяем только заданные ассеты: {explicitUrls.Count}.");
                foreach (var url in explicitUrls)
                {
                    Enqueue(url, "задан вручную");
                }
            }
            else if (_options.TelegramChannels.Count > 0)
            {
                cursors = _options.TelegramChannels.Select(c =>
                {
                    var seen = telegramState.LastSeen(c);
                    return new TelegramChannelCursor(c) { StopAtId = seen, MaxPosts = server && seen == 0 ? FabServerFirstPosts : int.MaxValue };
                }).ToList();
                foreach (var cursor in cursors)
                {
                    _logger.Info(cursor.StopAtId > 0
                        ? $"[Fab] Telegram: канал {cursor.Name} — посты новее #{cursor.StopAtId}."
                        : server
                            ? $"[Fab] Telegram: канал {cursor.Name} впервые для сервера — берём последние {FabServerFirstPosts} постов (старые ссылки уже разобраны на ПК)."
                            : $"[Fab] Telegram: канал {cursor.Name} впервые для Fab — читаем всю историю (раздачи Fab живут долго).");
                }

                var tg = await ParseTelegramChannelsAsync(null, cursors, int.MaxValue, _ => false, int.MaxValue, int.MaxValue);
                foreach (var post in tg.AllPosts)
                {
                    foreach (var url in FabStore.ExtractListingUrls(post.Text))
                    {
                        if (queued.Contains(url))
                        {
                            continue;
                        }

                        Enqueue(url, $"Telegram {post.PostId}");
                        fromTelegram.Add(url);
                    }
                }

                _logger.Info($"[Fab] Telegram: постов {tg.AllPosts.Count}, ссылок на ассеты Fab: {fromTelegram.Count}.");
            }
        }

        // Сервер заходит на Fab редко и по делу: Cloudflare запоминает частые заходы с одного адреса,
        // а Chrome с экраном занимает на тесном сервере сотни мегабайт.
        var reminderDue = false;
        if (serverState is not null)
        {
            var pending = queue.Count(q => !IsKnown(q.Url));
            var ltfDue = serverState.LimitedTimeFreeDue(DateTime.UtcNow, FabLimitedTimeFreeEvery);
            reminderDue = serverState.GiveawayReminderDue(DateTime.UtcNow, FabGiveawayReminderLead);
            if (pending == 0 && !ltfDue && !reminderDue)
            {
                _logger.Info("[Fab] Сервер: новых ассетов Fab из каналов нет, раздачу смотрели недавно — браузер не открываем.");
                AdvanceFabCursors(cursors, telegramState, unfinished: false, stoppedEarly: false);
                return;
            }

            _logger.Info($"[Fab] Сервер: к проверке из каналов {pending}; раздачу " +
                         (ltfDue ? "пора посмотреть" : reminderDue ? "скоро кончится — проверим, всё ли забрано" : "смотрели недавно") + ".");
        }

        var window = default((int, int, int, int)?);
        if (_options.SplitScreen && ScreenLayout.RightHalf() is { } right)
        {
            window = (right.X, right.Y, right.Width, right.Height);
            ScreenLayout.MoveConsoleToLeftHalf();
        }

        HumanBrowser browser;
        try
        {
            browser = await HumanBrowser.LaunchAsync(new HumanBrowser.LaunchSettings
            {
                UserDataDir = Path.Combine(fabDirectory, "browser"),
                ExplicitBrowser = _options.FabBrowser,
                DownloadDir = CliOptions.IsSingleFileApp ? Path.Combine(_options.DataDirectory, "browser") : null,
                Window = window,
                Display = remote?.Display,
                BeforeHumanWindow = remote is null ? null : remote.StartViewerAsync,
                // Раздача по очереди: окно удалённого доступа не закрываем между ассетами, чтобы не подключаться заново.
                AfterHumanWindow = remote is null || giveaway ? null : remote.StopViewerAsync
            }, _logger);
        }
        catch (Exception ex)
        {
            _logger.Error($"[Fab] {ex.Message}");
            Environment.ExitCode = 2;
            return;
        }

        var report = new RunReport
        {
            StartedAtUtc = DateTime.UtcNow,
            DryRun = _options.DryRun,
            Sources = ["fab"]
        };
        var fabReportPath = Path.Combine(_logsDirectory, $"fab-report-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var stoppedEarly = false;
        var closedByUser = false;
        var challenges = string.Empty;
        var newlyManual = new List<(string Title, string Url)>();
        var reminderMissing = new List<(string Title, string Url)>();
        List<string>? ltfLinks = null;
        string? giveawayUntil = null;

        await using (browser)
        {
            var fab = new FabStore(browser, _logger, _logsDirectory, _options.FabBaseUrl, _options.Interactive || server || reconUid is not null || giveaway,
                _notifier is { Enabled: true } ? NotifyAsync : null, TimeSpan.FromMilliseconds(_options.NavigationTimeoutMs))
            {
                HumanWindowHint = remote?.Hint,
                DeferManualAdds = server,
                LoginWait = server ? FabServerHumanWait : FabStore.DefaultLoginWait,
                ChallengeWait = server ? FabServerHumanWait : FabStore.DefaultChallengeWait,
                ManualAddWait = server ? FabServerHumanWait : FabStore.DefaultManualAddWait,
                CanAskHuman = serverState is null ? null : () => serverState.CanAskHuman(DateTime.UtcNow, FabHumanQuietPeriod),
                OnHumanAsked = serverState is null ? null : reason => serverState.MarkAsked(DateTime.UtcNow, reason)
            };

            try
            {
                _logger.Info($"[Fab] Открываем {fab.LimitedTimeFreeUrl}");
                if (!await fab.OpenAsync(fab.LimitedTimeFreeUrl))
                {
                    _logger.Error("[Fab] Fab не открылся. Скриншот страницы — в папке логов.");
                    await fab.SaveDiagnosticsAsync("fab-open-failed");
                    Environment.ExitCode = 2;
                    return;
                }

                if (!await fab.EnsureSignedInAsync("/limited-time-free"))
                {
                    _logger.Error("============================================================");
                    _logger.Error(" FAB: НЕ ПОЛУЧИЛОСЬ ВОЙТИ В EPIC GAMES");
                    _logger.Error(server
                        ? " Сервер попробует снова в следующий прогон; человека зовём не чаще раза в сутки."
                        : " Запустите ещё раз и войдите в открывшемся окне браузера (пункт E в меню).");
                    _logger.Error("============================================================");
                    Environment.ExitCode = 2;
                    return;
                }

                if (fab.EulaAccepted == false)
                {
                    _logger.Info("[Fab] Лицензия Fab EULA на этом аккаунте ещё не принята. Первый ассет программа попросит");
                    _logger.Info("[Fab] добавить кнопкой в окне и принять лицензию — один раз, дальше всё само.");
                }

                var cookies = await browser.CookieNamesAsync(fab.BaseUrl);
                _logger.Debug("[Fab] Cookies Fab: " + string.Join(", ", cookies.Select(c => c.Session ? $"{c.Name}(на сеанс)" : c.Name)));

                // После входа Epic возвращает прямо на раздачу — второй раз её не открываем.
                async Task<bool> OnLimitedFreeAsync() =>
                    await fab.IsOnAsync(fab.LimitedTimeFreeUrl) || await fab.OpenAsync(fab.LimitedTimeFreeUrl);

                if (loginOnly)
                {
                    // Вход подтверждён вручную: сервер снова может заходить на Fab сам.
                    ClearServerHumanCall(fabDirectory);
                    if (!await OnLimitedFreeAsync())
                    {
                        _logger.Warn("[Fab] Вход есть, но страница раздачи не открылась.");
                    }

                    var now = await fab.ReadLimitedTimeFreeAsync();
                    _logger.Info("============================================================");
                    _logger.Info(" ГОТОВО. ВЫ ВОШЛИ В FAB (АККАУНТ EPIC GAMES).");
                    if (fab.AccountName is not null)
                    {
                        _logger.Info($" Аккаунт: {fab.AccountName}");
                    }

                    _logger.Info($" Браузер: {browser.Description}");
                    _logger.Info(" Вход сохранён в папке браузера Fab. Дальше пункт F — забрать бесплатные ассеты.");
                    if (now.Links.Count > 0)
                    {
                        _logger.Info($" Сейчас раздают бесплатно{(now.Until is null ? string.Empty : $" ({now.Until})")}: {string.Join(", ", now.Titles.Where(t => t.Length > 0))}");
                    }

                    _logger.Info("============================================================");
                    return;
                }

                if (reconUid is not null)
                {
                    var rr = await fab.ReconAsync(reconUid, TimeSpan.FromMinutes(30));
                    _logger.Info("============================================================");
                    _logger.Info($" РАЗВЕДКА: {rr.Message}");
                    if (rr.Outcome == FabStore.ClaimOutcome.Added)
                    {
                        owned.Add(FabStore.CanonicalUrl(reconUid));
                        owned.Save();
                        var hars = FindHarFiles();
                        _logger.Info(hars.Count > 0
                            ? $" Найдены файлы HAR: {string.Join(", ", hars)}"
                            : " Файл HAR не найден в Downloads — сохраните его ещё раз (Export HAR) и запустите разведку на другом ассете.");
                        _logger.Info(" Дальше: docker compose cp unity-assets:<путь к fab.har> . && python3 tools/har-summary.py fab.har");
                    }

                    _logger.Info("============================================================");
                    Environment.ExitCode = rr.Outcome == FabStore.ClaimOutcome.Added ? 0 : 2;
                    return;
                }

                if (giveaway)
                {
                    await RunGiveawayWalkAsync(fab, owned, OnLimitedFreeAsync);
                    return;
                }

                if (explicitUrls.Count == 0)
                {
                    if (!await OnLimitedFreeAsync())
                    {
                        _logger.Warn("[Fab] Страница раздачи «Limited-Time Free» не открылась — берём только ссылки из каналов.");
                    }
                    else
                    {
                        var ltf = await fab.ReadLimitedTimeFreeAsync();
                        serverState?.MarkLimitedTimeFreeChecked(DateTime.UtcNow);
                        serverState?.NoteGiveaway(ltf.Until, FabStore.ParseGiveawayEnd(ltf.Until, DateTime.UtcNow));
                        giveawayUntil = ltf.Until;
                        ltfLinks = ltf.Links.ToList();
                        _logger.Info($"[Fab] Раздача «Limited-Time Free»{(ltf.Until is null ? string.Empty : $" ({ltf.Until})")}: {ltf.Links.Count} ассетов" +
                                     (ltf.Titles.Any(t => t.Length > 0) ? $" — {string.Join(", ", ltf.Titles.Where(t => t.Length > 0))}" : string.Empty));
                        foreach (var url in ltf.Links)
                        {
                            // Раздача временная — её первой, пока не кончилась.
                            Enqueue(url, "раздача Limited-Time Free", first: true);
                        }
                    }
                }

                var known = queue.Count(q => IsKnown(q.Url));
                _logger.Info($"[Fab] К проверке: {queue.Count - known}" +
                             (known > 0 ? $" (ещё {known} уже известны по прошлым запускам — их страницы не открываем)" : string.Empty) + ".");

                var index = 0;
                var added = 0;
                var toCheck = queue.Count - known;
                // Спокойный темп: человек тоже не открывает страницы каждую секунду, а частые
                // заходы Cloudflare замечает и начинает спрашивать галочку.
                var pace = TimeSpan.FromMilliseconds(Math.Max(_options.DelayMs, 4000));

                foreach (var (url, from) in queue)
                {
                    if (IsKnown(url))
                    {
                        var isOwned = owned.Contains(url);
                        var isManual = !isOwned && !removed.Contains(url);
                        report.Items.Add(new ProcessResult
                        {
                            Url = url,
                            TimestampUtc = DateTime.UtcNow,
                            Status = isOwned ? AssetProcessStatus.AlreadyOwned : isManual ? AssetProcessStatus.NeedsHuman : AssetProcessStatus.Deprecated,
                            Message = isOwned
                                ? "Уже в библиотеке Fab (известно с прошлых запусков, страница не открывалась)."
                                : isManual ? "Получает только человек (бот об этом уже сообщал, страница не открывалась)."
                                : "Нет на Fab (известно с прошлых запусков)."
                        });
                        continue;
                    }

                    if ((_options.MaxAddAttempts is { } maxAdd && added >= maxAdd) ||
                        (_options.MaxVisitedAssets is { } maxVisit && index >= maxVisit))
                    {
                        _logger.Info("[Fab] Достигнут лимит этого запуска. Остальное — в следующий раз.");
                        stoppedEarly = true;
                        break;
                    }

                    index++;
                    _logger.Info($"[Fab {index}/{toCheck}] {url} ({from})");
                    var claim = await fab.ClaimAsync(url, _options.DryRun);
                    if (claim.Outcome == FabStore.ClaimOutcome.NeedsLogin && await fab.EnsureSignedInAsync(new Uri(url).AbsolutePath, reload: true))
                    {
                        claim = await fab.ClaimAsync(url, _options.DryRun);
                    }

                    var status = claim.Outcome switch
                    {
                        FabStore.ClaimOutcome.Added => AssetProcessStatus.Added,
                        FabStore.ClaimOutcome.AlreadyOwned => AssetProcessStatus.AlreadyOwned,
                        FabStore.ClaimOutcome.WouldAdd => AssetProcessStatus.WouldAddInDryRun,
                        FabStore.ClaimOutcome.Paid => AssetProcessStatus.PaidSkipped,
                        FabStore.ClaimOutcome.Removed => AssetProcessStatus.Deprecated,
                        FabStore.ClaimOutcome.Unknown => AssetProcessStatus.UnknownAfterClick,
                        FabStore.ClaimOutcome.NeedsHuman => AssetProcessStatus.NeedsHuman,
                        _ => AssetProcessStatus.Failed
                    };

                    var line = $"[Fab] {DescribeStatus(status)}: {claim.Summary}{(claim.Summary.Length > 0 ? " — " : string.Empty)}{claim.Message}";
                    if (status == AssetProcessStatus.Failed)
                    {
                        _logger.Warn(line);
                    }
                    else
                    {
                        _logger.Info(line);
                    }

                    report.Items.Add(new ProcessResult
                    {
                        Url = url,
                        TimestampUtc = DateTime.UtcNow,
                        Status = status,
                        DetectedFree = claim.Outcome is FabStore.ClaimOutcome.Added or FabStore.ClaimOutcome.WouldAdd,
                        DetectedOwned = claim.Outcome is FabStore.ClaimOutcome.Added or FabStore.ClaimOutcome.AlreadyOwned,
                        DetectionSummary = claim.Summary,
                        AddedByHuman = claim.ByHuman,
                        Message = $"{claim.Message} Источник: {from}."
                    });

                    if (status is AssetProcessStatus.Added or AssetProcessStatus.AlreadyOwned)
                    {
                        owned.Add(url);
                    }
                    else if (status == AssetProcessStatus.Deprecated)
                    {
                        removed.Add(url);
                    }
                    else if (status == AssetProcessStatus.NeedsHuman && server)
                    {
                        needsManual.Add(url);
                        newlyManual.Add((claim.Title ?? url, url));
                    }

                    if (status == AssetProcessStatus.Added || (_options.DryRun && status == AssetProcessStatus.WouldAddInDryRun))
                    {
                        added++;
                    }

                    if (index % 5 == 0)
                    {
                        owned.Save();
                        removed.Save();
                    }

                    // Не торопимся: человек тоже не открывает десять страниц в секунду, а Epic
                    // режет слишком частые добавления.
                    await Task.Delay(pace);
                }

                // Раздача скоро кончится: смотрим, остались ли ассеты, которых нет в библиотеке (страницы
                // открываем только сейчас, не при каждом прогоне).
                if (serverState is not null && reminderDue && ltfLinks is { Count: > 0 })
                {
                    foreach (var link in ltfLinks)
                    {
                        if (owned.Contains(link))
                        {
                            continue;
                        }

                        var check = await fab.ClaimAsync(link, dryRun: true);
                        if (check.Outcome == FabStore.ClaimOutcome.AlreadyOwned)
                        {
                            owned.Add(link);
                        }
                        else if (check.Outcome is FabStore.ClaimOutcome.WouldAdd or FabStore.ClaimOutcome.NeedsHuman)
                        {
                            reminderMissing.Add((check.Title ?? link, link));
                        }

                        await Task.Delay(pace);
                    }

                    serverState.MarkGiveawayReminded();
                }
            }
            catch (CdpException ex) when (ex.IsDisconnected)
            {
                closedByUser = true;
                stoppedEarly = true;
                _logger.Warn($"[Fab] Окно браузера закрыто ({ex.Message}) — останавливаемся. Что успели узнать, сохранено.");
            }
            finally
            {
                owned.Save();
                removed.Save();
                needsManual.Save();
                if (serverState is not null)
                {
                    // Всё, что требовало человека, сделано (или не требовалось) — снова можно звать, когда понадобится.
                    if (fab.HumanUnresolved == 0)
                    {
                        serverState.ClearAsked();
                    }

                    serverState.Save();
                }

                if (fab.ChallengesSelfPassed + fab.ChallengesByHuman > 0)
                {
                    challenges = $"Проверок Cloudflare: {fab.ChallengesSelfPassed + fab.ChallengesByHuman} " +
                                 $"(прошли сами: {fab.ChallengesSelfPassed}, галочка человеком: {fab.ChallengesByHuman})";
                }
            }
        }

        // Где остановились в каналах — только если все ассеты из них проверены до конца:
        // иначе в следующий раз их не прочитать заново.
        var telegramUnfinished = report.Items.Any(i => fromTelegram.Contains(i.Url) &&
            i.Status is AssetProcessStatus.Failed or AssetProcessStatus.UnknownAfterClick);
        AdvanceFabCursors(cursors, telegramState, telegramUnfinished, stoppedEarly);

        report.FinishedAtUtc = DateTime.UtcNow;
        try
        {
            await File.WriteAllTextAsync(fabReportPath, JsonSerializer.Serialize(report, _jsonOptions));
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Fab] Отчёт не записался: {ex.Message}");
        }

        PrintFabSummary(report, closedByUser);
        if (challenges.Length > 0)
        {
            _logger.Info(challenges);
        }

        _logger.Info($"Отчёт Fab: {fabReportPath}");

        var addedItems = report.Items.Where(i => i.Status == AssetProcessStatus.Added).ToList();
        if (addedItems.Count > 0)
        {
            var lines = addedItems.Take(15).Select(i => $"• {i.DetectionSummary?.Split(" | ")[0] ?? i.Url}\n  {i.Url}");
            await NotifyAsync($"✅ Fab: добавлено {addedItems.Count} на аккаунт Epic (профиль {_profileName}):\n" +
                              string.Join("\n", lines) + (addedItems.Count > 15 ? $"\n… и ещё {addedItems.Count - 15}" : string.Empty));
        }

        if (newlyManual.Count > 0)
        {
            var lines = newlyManual.Take(15).Select(m => $"• {m.Title}\n  {m.Url}");
            await NotifyAsync($"🧩 Fab: раздача −100 %{(giveawayUntil is null ? string.Empty : $" ({giveawayUntil})")} — эти ассеты получает только человек " +
                              "(покупка за 0, Epic просит капчу), сама я их не беру:\n" +
                              string.Join("\n", lines) + (newlyManual.Count > 15 ? $"\n… и ещё {newlyManual.Count - 15}" : string.Empty) +
                              GiveawayWaysText());
        }

        if (reminderMissing.Count > 0)
        {
            var lines = reminderMissing.Take(15).Select(m => $"• {m.Title}\n  {m.Url}");
            await NotifyAsync($"⏰ Fab: раздача заканчивается{(giveawayUntil is null ? string.Empty : $" ({giveawayUntil})")}. Ещё не у вас:\n" +
                              string.Join("\n", lines) + GiveawayWaysText());
        }
    }

    /// <summary>Как забрать раздачу: два способа — на ПК или Deck и в окне сервера. Текст для бота.</summary>
    private string GiveawayWaysText() =>
        "\nДва способа:\n" +
        "1) ПК или Deck: пункт F → Fab. С домашнего адреса капча Epic обычно проходит надёжнее (с сервера Epic отклонил 2 решения из 3).\n" +
        "2) Окно сервера: docker compose exec -it unity-assets dotnet UnityAssetsDownloader.dll --logs-dir /app/logs --data-dir /app/data " +
        $"--profile {_options.ProfileName} --fab-giveaway\n" +
        "   Дальше SSH-туннель и окно, как при входе: программа проведёт по всем ещё не полученным ассетам раздачи подряд.";

    /// <summary>
    /// --fab-giveaway: раздача по очереди. Программа открывает каждый ещё не полученный ассет в окне человека;
    /// кнопку, оформление за 0 и капчу Epic делает человек, а подтверждает программа — по странице.
    /// </summary>
    private async Task RunGiveawayWalkAsync(FabStore fab, OwnedAssetsCache owned, Func<Task<bool>> onLimitedFree)
    {
        if (!await onLimitedFree())
        {
            _logger.Error("[Fab] Страница раздачи «Limited-Time Free» не открылась.");
            Environment.ExitCode = 2;
            return;
        }

        var ltf = await fab.ReadLimitedTimeFreeAsync();
        _logger.Info($"[Fab] Раздача «Limited-Time Free»{(ltf.Until is null ? string.Empty : $" ({ltf.Until})")}: {ltf.Links.Count} ассетов.");
        var todo = ltf.Links.Where(u => _options.RecheckOwned || !owned.Contains(u)).ToList();
        if (todo.Count == 0)
        {
            _logger.Info("[Fab] Нечего получать: раздачи сейчас нет или все её ассеты уже у вас.");
            return;
        }

        _logger.Info($"[Fab] Провожу по ассетам раздачи по очереди ({todo.Count}). В каждом: кнопка, проверка «к оплате 0», капча Epic, закрыть окно (Ctrl+Shift+W).");
        var results = await fab.TakeGiveawayAsync(todo, TimeSpan.FromMinutes(10));
        var got = 0;
        var failed = 0;
        foreach (var (url, r) in results)
        {
            var title = r.Title ?? url;
            if (r.Outcome == FabStore.ClaimOutcome.Added)
            {
                got++;
                owned.Add(url);
                _logger.Info($"[Fab] Получено: {title}");
            }
            else if (r.Outcome == FabStore.ClaimOutcome.AlreadyOwned)
            {
                owned.Add(url);
                _logger.Info($"[Fab] Уже было: {title}");
            }
            else
            {
                failed++;
                _logger.Warn($"[Fab] Не получено: {title} — {r.Message}");
            }
        }

        owned.Save();
        var skipped = todo.Count - results.Count;
        _logger.Info("============================================================");
        _logger.Info($" РАЗДАЧА: получено {got}, не получено {failed + skipped} из {todo.Count}.");
        _logger.Info("============================================================");
        Environment.ExitCode = failed + skipped == 0 ? 0 : 2;
        if (got > 0)
        {
            await NotifyAsync($"✅ Fab: по раздаче получено {got} из {todo.Count} (профиль {_profileName}).");
        }
    }

    /// <summary>Файлы HAR, которые человек мог сохранить в Downloads окна Chrome (разведка).</summary>
    private static List<string> FindHarFiles()
    {
        var found = new List<string>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var dir in new[] { Path.Combine(home, "Downloads"), "/root/Downloads", "/tmp" })
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    found.AddRange(Directory.EnumerateFiles(dir, "*.har").Where(f => !found.Contains(f)));
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return found;
    }

    /// <summary>Человек вошёл вручную (--fab-login): отметка «звали, не пришёл» больше не нужна.</summary>
    private static void ClearServerHumanCall(string fabDirectory)
    {
        var state = FabServerState.Load(fabDirectory);
        state.ClearAsked();
        state.Save();
    }

    /// <summary>
    /// Запоминает, до какого поста в каналах дочитали Fab. Только если каналы прочитаны до конца и все
    /// найденные ссылки проверены (unfinished = false): иначе часть постов потерялась бы.
    /// </summary>
    private void AdvanceFabCursors(List<TelegramChannelCursor>? cursors, TelegramChannelState telegramState, bool unfinished, bool stoppedEarly)
    {
        if (cursors is null || stoppedEarly || _options.DryRun)
        {
            return;
        }

        if (unfinished)
        {
            _logger.Info("[Fab] Не все ассеты из каналов получилось проверить — в следующий раз прочитаем эти посты снова.");
            return;
        }

        var advanced = cursors
            .Where(c => c.Exhausted && c.NewestId > 0 && telegramState.Advance(c.Name, c.NewestId))
            .Select(c => $"{c.Name} #{c.NewestId}")
            .ToList();
        if (advanced.Count > 0)
        {
            telegramState.Save();
            _logger.Info($"[Fab] Telegram: запомнили, где остановились: {string.Join(", ", advanced)}. Дальше — только новые посты.");
        }
    }

    private void PrintFabSummary(RunReport report, bool closedByUser)
    {
        _logger.Info("============================================================");
        _logger.Info($" ИТОГИ FAB. Профиль: {_profileName}{(report.DryRun ? " (проверочный запуск, аккаунт не менялся)" : string.Empty)}");
        _logger.Info("============================================================");

        foreach (var group in report.Items.GroupBy(i => i.Status).OrderBy(g => g.Key))
        {
            var byHuman = group.Count(i => i.AddedByHuman);
            _logger.Info($" {DescribeStatus(group.Key)}: {group.Count()}" +
                         (group.Key == AssetProcessStatus.Added && byHuman > 0 ? $" (программой: {group.Count() - byHuman}, вами в окне: {byHuman})" : string.Empty));
            if (group.Key is AssetProcessStatus.Added or AssetProcessStatus.WouldAddInDryRun or AssetProcessStatus.Failed or AssetProcessStatus.UnknownAfterClick)
            {
                foreach (var item in group)
                {
                    _logger.Info($"   - {item.DetectionSummary ?? item.Url}{(item.AddedByHuman ? " — вами в окне" : string.Empty)}");
                }
            }
        }

        if (report.Items.Count == 0)
        {
            _logger.Info(closedByUser ? " Окно закрыли раньше, чем дошло до ассетов." : " Новых ассетов Fab не было.");
        }

        _logger.Info("============================================================");
    }
}
