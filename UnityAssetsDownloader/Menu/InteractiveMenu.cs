using System.IO.Compression;

/// <summary>
/// Меню в самой программе — то же, что в run.bat и run.sh, с теми же буквами.
/// Открывается, когда программу запустили без параметров из живой консоли
/// (двойной щелчок по UnityAssetsDownloader.exe) или с --menu.
///
/// Нужно для компьютерного класса: один .exe без установки .NET и без run.bat рядом.
/// Каждый пункт просто запускает программу с нужными параметрами — в том же процессе,
/// как режим сервера запускает прогоны по расписанию.
/// </summary>
internal sealed class InteractiveMenu
{
    private readonly Func<string[], Task<int>> _runOnce;
    private readonly List<string> _passThrough;
    private readonly string _logsDirectory;
    private readonly string _dataDirectory;

    private string? _profile;
    private bool _systemChrome;
    private string? _telegramProxy;
    private bool _telegramNoAutoProxy;

    /// <param name="runOnce">Один запуск программы с этими параметрами; возвращает код выхода.</param>
    /// <param name="passThrough">Параметры, с которыми запустили само меню (например, --logs-dir) — добавляются к каждому запуску.</param>
    public InteractiveMenu(Func<string[], Task<int>> runOnce, IEnumerable<string> passThrough, string logsDirectory, string dataDirectory)
    {
        _runOnce = runOnce;
        _passThrough = passThrough.ToList();
        _logsDirectory = logsDirectory;
        _dataDirectory = dataDirectory;
    }

    /// <summary>Меню открывается без параметров из живой консоли или с --menu.</summary>
    public static bool ShouldOpen(string[] args) =>
        args.Any(a => a.Equals("--menu", StringComparison.OrdinalIgnoreCase)) ||
        (args.Length == 0 && !Console.IsInputRedirected && !Console.IsOutputRedirected);

    public async Task RunAsync()
    {
        TrySetTitle("UnityAssetsDownloader");

        while (true)
        {
            PrintMenu();
            var choice = Ask("Выберите режим [Enter = 6]: ");
            if (choice is null)
            {
                // Ввод закончился (консоль закрыли) — выходим тихо.
                return;
            }

            choice = choice.Trim().ToUpperInvariant();
            if (choice.Length == 0)
            {
                choice = "6";
            }

            switch (choice)
            {
                case "0":
                    Console.WriteLine("Выход.");
                    return;
                case "1":
                    await RunAsync("основные источники", Limits(), "--headless", "false", "--extra-source-file", "extra_asset_urls.example.txt");
                    break;
                case "2":
                    await RunAsync("только топ бесплатные", Limits(), "--headless", "false", "--source", "https://assetstore.unity.com/top-assets/top-free");
                    break;
                case "3":
                    await RunAsync("только китайский архив", Limits(), "--headless", "false", "--no-defaults",
                        "--extra-source-file", "GreaterChinaUnityAssetArchive/free_list_GreaterChinaUnityAssetArchiveLinks.txt");
                    break;
                case "4":
                    await RunAsync("только extra_asset_urls.example.txt", Limits(), "--headless", "false", "--no-defaults",
                        "--extra-source-file", "extra_asset_urls.example.txt");
                    break;
                case "5":
                    await RunAsync("расширенные списки поиска", Limits(), "--headless", "false", "--source", "https://assetstore.unity.com/", "--extended-sources");
                    break;
                case "6":
                    Console.WriteLine();
                    Console.WriteLine("Откроется окно браузера. Войдите в аккаунт Unity и дождитесь подтверждения.");
                    Console.WriteLine();
                    Console.WriteLine("ВАЖНО: вход через Google в этом окне не сработает — Google не пускает");
                    Console.WriteLine("браузеры под управлением программ. Входите по email и паролю Unity.");
                    Console.WriteLine("Нет пароля? Задайте его на https://id.unity.com («Забыли пароль?»).");
                    await RunAsync("только вход и сохранение сессии", [], "--login", "--headless", "false");
                    break;
                case "7":
                    await RunAsync("проверочный прогон (аккаунт не меняется)", Limits(), "--dry-run", "--headless", "false");
                    break;
                case "8":
                    await RunAsync("Telegram-каналы", Limits(), "--headless", "false", "--no-defaults");
                    break;
                case "9":
                    Console.WriteLine();
                    Console.WriteLine("Диагностика: пишутся максимально подробные логи. Аккаунт НЕ меняется.");
                    await RunAsync("диагностика", [], "--verbose", "--trace-network", "--dry-run", "--headless", "false",
                        "--no-defaults", "--max-visited-assets", "5");
                    break;
                case "T":
                    await TelegramProxyAsync();
                    break;
                case "B":
                    ToggleChrome();
                    break;
                case "C":
                    Console.WriteLine();
                    Console.WriteLine("Программа откроет страницу входа Unity и посмотрит, на месте ли поле email и кнопка.");
                    Console.WriteLine("Ничего не нажимает и никуда не отправляет.");
                    await RunAsync("проверка страницы входа", [], "--check-login-page", "--headless", "false");
                    break;
                case "K":
                    Console.WriteLine();
                    Console.WriteLine("Каталог всех ассетов аккаунта: для ИИ-агента (INDEX.md и разделы) и для вас (catalog.html).");
                    Console.WriteLine("Аккаунт не меняется. Первый раз — пара минут, дальше берутся только новые ассеты.");
                    await RunAsync("каталог ассетов", [], "--build-catalog", "--headless", "false");
                    OfferToOpenCatalog();
                    break;
                case "F":
                    ExplainFab();
                    await RunAsync("Fab: раздача Limited-Time Free и ссылки на Fab из Telegram", [], "--fab");
                    break;
                case "E":
                    ExplainFab();
                    await RunAsync("Fab: вход в Epic Games (аккаунт не меняется)", [], "--fab-login");
                    break;
                case "P":
                    await ChooseProfileAsync();
                    break;
                case "L":
                    CollectLogs();
                    break;
                default:
                    Console.WriteLine();
                    Console.WriteLine($"Нет такого пункта: {choice}");
                    Pause();
                    break;
            }
        }
    }

    private void PrintMenu()
    {
        Console.WriteLine();
        Console.WriteLine("==============================================");
        Console.WriteLine(" UnityAssetsDownloader — выбор режима");
        Console.WriteLine("==============================================");
        Console.WriteLine($" Профиль аккаунта: {_profile ?? Environment.UserName}");
        Console.WriteLine($" Браузер: {(_systemChrome ? "мой обычный Chrome (закройте все окна Chrome!)" : "своя папка браузера (личный Chrome не трогаем)")}");
        Console.WriteLine($" Прокси для Telegram: {TelegramProxyMode}");
        Console.WriteLine($" Логи: {_logsDirectory}");
        Console.WriteLine($" Данные и вход: {_dataDirectory}");
        Console.WriteLine("==============================================");
        Console.WriteLine();
        Console.WriteLine(" 1) Основные источники (топ бесплатные + китайский архив + extra_urls)");
        Console.WriteLine(" 2) Только топ бесплатные (Asset Store top-free)");
        Console.WriteLine(" 3) Только китайский архив (free_list_GreaterChinaUnityAssetArchiveLinks.txt)");
        Console.WriteLine(" 4) Только extra_asset_urls.example.txt");
        Console.WriteLine(" 5) Только расширенные списки поиска (extended_sources.txt)");
        Console.WriteLine(" 6) Только вход и сохранение сессии  <== начните с этого");
        Console.WriteLine(" 7) Проверочный прогон (аккаунт не меняется)");
        Console.WriteLine(" 8) Telegram-каналы из telegram_sources.txt");
        Console.WriteLine(" 9) Диагностика: максимум логов, аккаунт не меняется");
        Console.WriteLine(" T) Проверить Telegram / задать свой прокси");
        Console.WriteLine(" B) Переключить браузер: своя папка <-> мой обычный Chrome");
        Console.WriteLine(" C) Проверить страницу входа Unity (быстро, ничего не меняет)");
        Console.WriteLine(" K) Каталог ассетов аккаунта: для ИИ-агента и страница для просмотра");
        Console.WriteLine(" F) Fab (fab.com): бесплатные ассеты на аккаунт Epic Games");
        Console.WriteLine(" E) Fab: только войти в Epic Games (F при первом запуске попросит войти и сам)");
        Console.WriteLine(" P) Сменить профиль аккаунта (второй аккаунт на этом компьютере)");
        Console.WriteLine(" L) Собрать логи в архив для отправки");
        Console.WriteLine(" 0) Выход");
        Console.WriteLine();
    }

    private static void ExplainFab()
    {
        Console.WriteLine();
        Console.WriteLine("Fab (fab.com) — магазин Epic Games. Ассеты кладутся на аккаунт Epic, не Unity.");
        Console.WriteLine("Откроется обычное окно Chrome со своей папкой (ваш личный Chrome не трогается).");
        Console.WriteLine("Если Fab попросит подтвердить, что вы человек, или войти в Epic Games —");
        Console.WriteLine("сделайте это в окне: программа сама такие проверки не проходит и подождёт вас.");
        Console.WriteLine();
    }

    private string TelegramProxyMode =>
        _telegramNoAutoProxy ? "только напрямую, без прокси"
        : _telegramProxy ?? "подбирается сам, если понадобится";

    /// <summary>Вопросы про лимит и пачки — как в run.bat перед запуском по источникам.</summary>
    private List<string> Limits()
    {
        var result = new List<string>();
        var limit = Ask("Сколько новых ассетов добавить за запуск? [Enter = без лимита]: ")?.Trim();
        if (int.TryParse(limit, out var max) && max > 0)
        {
            result.AddRange(["--max-add-attempts", max.ToString()]);
        }

        Console.WriteLine("Ассеты из Telegram берутся пачками: собрали пачку, проверили в магазине, собираем следующую.");
        var batch = Ask("Сколько ассетов в одной пачке из Telegram? [Enter = 30, 0 = всё разом]: ")?.Trim();
        result.AddRange(["--tg-batch-size", int.TryParse(batch, out var size) && size >= 0 ? size.ToString() : "30"]);
        return result;
    }

    private async Task RunAsync(string title, List<string> extra, params string[] mode)
    {
        var args = new List<string>(_passThrough);
        // Диагностике нужен подробный лог, а --quiet сильнее --verbose.
        if (!mode.Contains("--verbose"))
        {
            args.Add("--quiet");
        }

        if (_profile is not null)
        {
            args.AddRange(["--profile", _profile]);
        }

        if (_systemChrome)
        {
            args.Add("--use-system-chrome-profile");
        }

        args.AddRange(TelegramProxyArgs());
        args.AddRange(mode);
        args.AddRange(extra);

        Console.WriteLine();
        Console.WriteLine($"Запуск: {title}...");
        var code = await _runOnce(args.ToArray());
        AfterRun(code);
    }

    private IEnumerable<string> TelegramProxyArgs() =>
        _telegramNoAutoProxy ? ["--tg-auto-proxy", "false"]
        : _telegramProxy is not null ? ["--tg-proxy", _telegramProxy]
        : [];

    private async Task TelegramProxyAsync()
    {
        Console.WriteLine();
        Console.WriteLine("Telegram у многих провайдеров заблокирован. Помогает прокси.");
        Console.WriteLine();
        Console.WriteLine("НАСТРАИВАТЬ НИЧЕГО НЕ НУЖНО: если Telegram не открылся, программа сама");
        Console.WriteLine("найдёт рабочий прокси и запомнит его для следующих запусков.");
        Console.WriteLine("Этот пункт нужен, только если хотите свой прокси или проверить связь.");
        Console.WriteLine();
        Console.WriteLine("Прокси используется ТОЛЬКО для Telegram, Unity ходит напрямую.");
        Console.WriteLine();
        Console.WriteLine($"Сейчас: {TelegramProxyMode}");
        Console.WriteLine();
        Console.WriteLine("  Enter = ничего не менять, только проверить связь с Telegram");
        Console.WriteLine("  свой адрес, например socks5://127.0.0.1:1080");
        Console.WriteLine("  N = запретить автоподбор, ходить только напрямую");
        Console.WriteLine();
        var answer = Ask("Ваш выбор: ")?.Trim() ?? string.Empty;

        if (answer.Equals("N", StringComparison.OrdinalIgnoreCase) || answer.Equals("Т", StringComparison.OrdinalIgnoreCase))
        {
            _telegramNoAutoProxy = true;
            _telegramProxy = null;
        }
        else if (answer.Length > 0)
        {
            _telegramNoAutoProxy = false;
            _telegramProxy = answer;
        }

        Console.WriteLine();
        Console.WriteLine("Проверяем. Вход в Unity для этого не нужен.");
        await RunAsync("проверка Telegram", [], "--check-telegram", "--headless", "true");
    }

    private void ToggleChrome()
    {
        _systemChrome = !_systemChrome;
        Console.WriteLine();
        if (_systemChrome)
        {
            Console.WriteLine("Переключено: программа откроет ВАШ обычный Chrome.");
            Console.WriteLine();
            Console.WriteLine("ВАЖНО: перед запуском закройте ВСЕ окна Chrome, включая значок у часов.");
            Console.WriteLine("Иначе Chrome не отдаст свою папку и программа не запустится.");
        }
        else
        {
            Console.WriteLine("Переключено: программа откроет свой браузер.");
            Console.WriteLine("Вход в Unity запоминается между запусками, ваш Chrome не затрагивается.");
        }

        Pause();
    }

    private async Task ChooseProfileAsync()
    {
        Console.WriteLine();
        Console.WriteLine("Профили на этом компьютере:");
        await _runOnce([.._passThrough, "--list-profiles"]);
        Console.WriteLine();
        Console.WriteLine("Профиль — это отдельный аккаунт Unity со своей сессией.");
        Console.WriteLine($"По умолчанию используется имя пользователя Windows: {Environment.UserName}");
        Console.WriteLine();
        var name = Ask($"Имя профиля [Enter = {Environment.UserName}]: ")?.Trim();
        _profile = string.IsNullOrEmpty(name) || name == Environment.UserName ? null : name;
        Console.WriteLine();
        Console.WriteLine($"Выбран профиль: {_profile ?? Environment.UserName}");
        Pause();
    }

    /// <summary>После пункта K: открыть страницу каталога в браузере по умолчанию.</summary>
    private void OfferToOpenCatalog()
    {
        var profiles = Path.Combine(_dataDirectory, "profiles");
        var page = Directory.Exists(profiles)
            ? Directory.GetFiles(profiles, "catalog.html", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        if (page is null)
        {
            return;
        }

        var answer = Ask($"Открыть страницу каталога ({page}) в браузере? [Enter = да, Н = нет]: ")?.Trim().ToUpperInvariant();
        if (answer is "Н" or "N" or "НЕТ" or "NO")
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не открылась сама ({ex.Message}). Откройте файл вручную: {page}");
        }
    }

    /// <summary>Вся папка логов одним архивом — на рабочий стол, чтобы ученику было легко найти.</summary>
    private void CollectLogs()
    {
        Console.WriteLine();
        if (!Directory.Exists(_logsDirectory) || !Directory.EnumerateFileSystemEntries(_logsDirectory).Any())
        {
            Console.WriteLine($"Папка логов пуста: {_logsDirectory}");
            Pause();
            return;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var folder = Directory.Exists(desktop) ? desktop : Path.GetDirectoryName(_logsDirectory)!;
        var zip = Path.Combine(folder, $"UnityAssetsDownloader-logs-{Environment.UserName}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        try
        {
            ZipFile.CreateFromDirectory(_logsDirectory, zip, CompressionLevel.Optimal, includeBaseDirectory: false);
            Console.WriteLine($"Архив с логами готов: {zip}");
            Console.WriteLine("Пришлите этот файл для разбора ошибок.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ОШИБКА] Не удалось создать архив: {ex.Message}");
        }

        Pause();
    }

    private void AfterRun(int code)
    {
        Console.WriteLine();
        Console.WriteLine(code == 0 ? "Готово." : $"[ОШИБКА] Программа завершилась с кодом {code}.");
        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine(" ЕСЛИ ЧТО-ТО ПОШЛО НЕ ТАК — ПРИШЛИТЕ ЭТОТ ОДИН ФАЙЛ:");
        Console.WriteLine($" {Path.Combine(_logsDirectory, CliOptions.ProblemsFileName)}");
        Console.WriteLine(" или всю папку логов архивом — пункт L в меню.");
        Console.WriteLine("============================================================");
        Console.WriteLine();
        Console.WriteLine($"Профиль: {_profile ?? Environment.UserName}. Сменить — пункт P в меню.");
        Pause();
    }

    private static string? Ask(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine();
    }

    private static void Pause()
    {
        Console.WriteLine();
        Ask("Нажмите Enter, чтобы вернуться в меню...");
    }

    private static void TrySetTitle(string title)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Console.Title = title;
            }
        }
        catch
        {
            // Заголовок окна — не главное.
        }
    }
}
