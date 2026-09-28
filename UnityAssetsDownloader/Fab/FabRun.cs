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
    private async Task RunFabAsync()
    {
        var loginOnly = _options.FabLoginOnly;
        _logger.Info("============================================================");
        _logger.Info(loginOnly ? " FAB: ВХОД В АККАУНТ EPIC GAMES" : " FAB.COM: БЕСПЛАТНЫЕ АССЕТЫ НА АККАУНТ EPIC GAMES");
        _logger.Info("============================================================");

        if (!_options.HasScreen)
        {
            _logger.Error("Fab работает только в видимом окне браузера, а здесь нет экрана (сервер, Docker, SSH).");
            _logger.Error("Cloudflare на fab.com не пускает невидимые браузеры, а проверку «я человек» программа");
            _logger.Error("за человека не проходит. Запускайте Fab на Deck или ПК: ./run.sh или run.bat → F.");
            Environment.ExitCode = 2;
            return;
        }

        if (_options.Headless)
        {
            _logger.Info("[Fab] Невидимый режим для Fab не работает (Cloudflare) — окно браузера будет видно.");
        }

        var profileDirectory = _profileStore.GetProfileDirectory(_profileName);
        var fabDirectory = Path.Combine(profileDirectory, "fab");
        Directory.CreateDirectory(fabDirectory);
        // library.txt — только проверенное: после добавления страница показала «View in My Library».
        // owned.txt версий 1.28.0–1.28.2 записывался без такой проверки и больше не читается.
        var owned = new OwnedAssetsCache(fabDirectory, "library.txt", "Ассеты Fab, которые точно в библиотеке аккаунта Epic (проверено по странице).");
        var removed = new OwnedAssetsCache(fabDirectory, "removed.txt", "Ассеты Fab, которых больше нет на сайте.");
        var telegramState = TelegramChannelState.Load(fabDirectory);

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
        if (!loginOnly)
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
                cursors = _options.TelegramChannels.Select(c => new TelegramChannelCursor(c) { StopAtId = telegramState.LastSeen(c) }).ToList();
                foreach (var cursor in cursors)
                {
                    _logger.Info(cursor.StopAtId > 0
                        ? $"[Fab] Telegram: канал {cursor.Name} — посты новее #{cursor.StopAtId}."
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
                Window = window
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

        await using (browser)
        {
            var fab = new FabStore(browser, _logger, _logsDirectory, _options.FabBaseUrl, _options.Interactive,
                _notifier is { Enabled: true } ? NotifyAsync : null, TimeSpan.FromMilliseconds(_options.NavigationTimeoutMs));

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
                    _logger.Error(" Запустите ещё раз и войдите в открывшемся окне браузера (пункт E в меню).");
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

                if (explicitUrls.Count == 0)
                {
                    if (!await OnLimitedFreeAsync())
                    {
                        _logger.Warn("[Fab] Страница раздачи «Limited-Time Free» не открылась — берём только ссылки из каналов.");
                    }
                    else
                    {
                        var ltf = await fab.ReadLimitedTimeFreeAsync();
                        _logger.Info($"[Fab] Раздача «Limited-Time Free»{(ltf.Until is null ? string.Empty : $" ({ltf.Until})")}: {ltf.Links.Count} ассетов" +
                                     (ltf.Titles.Any(t => t.Length > 0) ? $" — {string.Join(", ", ltf.Titles.Where(t => t.Length > 0))}" : string.Empty));
                        foreach (var url in ltf.Links)
                        {
                            // Раздача временная — её первой, пока не кончилась.
                            Enqueue(url, "раздача Limited-Time Free", first: true);
                        }
                    }
                }

                var known = queue.Count(q => !_options.RecheckOwned && (owned.Contains(q.Url) || removed.Contains(q.Url)));
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
                    if (!_options.RecheckOwned && (owned.Contains(url) || removed.Contains(url)))
                    {
                        var isOwned = owned.Contains(url);
                        report.Items.Add(new ProcessResult
                        {
                            Url = url,
                            TimestampUtc = DateTime.UtcNow,
                            Status = isOwned ? AssetProcessStatus.AlreadyOwned : AssetProcessStatus.Deprecated,
                            Message = isOwned
                                ? "Уже в библиотеке Fab (известно с прошлых запусков, страница не открывалась)."
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
        if (cursors is not null && !stoppedEarly && !_options.DryRun)
        {
            if (telegramUnfinished)
            {
                _logger.Info("[Fab] Не все ассеты из каналов получилось проверить — в следующий раз прочитаем эти посты снова.");
            }
            else
            {
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
        }

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
