using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

/// <summary>
/// Сервер без экрана (Docker): окно браузера для Fab живёт на виртуальном экране (Xvfb), а когда
/// нужен человек, его окно показывается в браузере человека — x11vnc + noVNC (websockify).
/// Человек входит в Epic, ставит галочку Cloudflare и принимает лицензию руками, как у себя на
/// компьютере; закрыл окно — программа продолжает (HumanBrowser.HandOverToHumanAsync).
///
/// Всё запускается по требованию и гасится, как только не нужно: на сервере мало памяти.
/// Виртуальный экран — на весь Fab-прогон, удалённый доступ — только на время просьбы к человеку.
///
/// Доступ: порт слушает только localhost сервера (docker-compose: 127.0.0.1:FAB_VNC_PORT), попасть
/// можно по SSH-туннелю. Без пароля (FAB_VNC_PASSWORD) удалённый доступ не открывается вообще.
/// VNC читает в пароле только первые 8 знаков — длиннее не нужно.
/// </summary>
internal sealed class RemoteWindow : IAsyncDisposable
{
    internal sealed class Settings
    {
        public string Xvfb { get; init; } = "/usr/bin/Xvfb";
        public string X11Vnc { get; init; } = "/usr/bin/x11vnc";
        public string Websockify { get; init; } = "/usr/bin/websockify";

        /// <summary>Страницы noVNC (vnc.html): пакет novnc из Debian.</summary>
        public string NoVncDir { get; init; } = "/usr/share/novnc";

        /// <summary>Порт VNC внутри контейнера, только 127.0.0.1.</summary>
        public int VncPort { get; init; } = 5900;

        /// <summary>Порт noVNC внутри контейнера; наружу его выводит docker-compose (только на localhost сервера).</summary>
        public int WebPort { get; init; } = 6085;

        /// <summary>Пароль окна (FAB_VNC_PASSWORD). Пусто — удалённый доступ не открывается.</summary>
        public string? Password { get; init; }

        /// <summary>Порт на сервере для подсказки человеку. null — FAB_VNC_PORT или WebPort.</summary>
        public string? HostPort { get; init; }

        public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(15);
    }

    private readonly AppLogger _logger;
    private readonly Settings _settings;
    private readonly Process _xvfb;
    private Process? _vnc;
    private Process? _web;
    private Queue<string>? _vncErrors;
    private Queue<string>? _webErrors;
    private string? _passwordFile;

    private RemoteWindow(AppLogger logger, Settings settings, Process xvfb, string display)
    {
        _logger = logger;
        _settings = settings;
        _xvfb = xvfb;
        Display = display;
    }

    /// <summary>Виртуальный экран, например «:99»: его надо передать браузеру в DISPLAY.</summary>
    public string Display { get; }

    /// <summary>Удалённый доступ сейчас открыт.</summary>
    public bool ViewerRunning => _web is { HasExited: false };

    /// <summary>Что человеку сделать, чтобы увидеть окно.</summary>
    public string Hint
    {
        get
        {
            var port = HostPort(_settings);
            return $"Откройте окно: на своём компьютере выполните «ssh -L 127.0.0.1:{port}:127.0.0.1:{port} <ваш сервер>», " +
                   $"затем в браузере откройте http://127.0.0.1:{port}/vnc.html?autoconnect=true&resize=scale " +
                   "(пароль окна — FAB_VNC_PASSWORD в .env на сервере). " +
                   "Закрыть окно Chrome — Ctrl+Shift+W: на виртуальном экране кнопок окна нет.";
        }
    }

    /// <summary>
    /// Убирает то, что осталось от запуска, которого уже нет (команду из `docker compose exec` оборвали, SSH
    /// отвалился): виртуальный экран, x11vnc, websockify и Chromium этой папки профиля. Звать только после того,
    /// как взят замок Fab-запуска: тогда живых «чужих» процессов Fab в контейнере быть не может. Без этого новый
    /// x11vnc не смог бы занять порт, а человеку показали бы старый экран.
    /// </summary>
    public static int KillStaleProcesses(string? browserProfileMarker)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc"))
        {
            return 0;
        }

        var killed = 0;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == Environment.ProcessId)
            {
                continue;
            }

            try
            {
                var comm = File.ReadAllText(Path.Combine(dir, "comm")).Trim();
                var cmd = File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\0', ' ');
                var stale = comm is "Xvfb" or "x11vnc" ||
                            cmd.Contains("/usr/bin/websockify", StringComparison.Ordinal) ||
                            (!string.IsNullOrEmpty(browserProfileMarker) && cmd.Contains(browserProfileMarker, StringComparison.Ordinal));
                if (stale && SysKill(pid, 9) == 0)
                {
                    killed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Процесс уже ушёл или чужой — пропускаем.
            }
        }

        return killed;
    }

    /// <summary>Есть ли на машине всё для виртуального экрана и удалённого окна (образ сервера).</summary>
    public static bool IsInstalled(Settings? settings = null)
    {
        settings ??= new Settings();
        return OperatingSystem.IsLinux() &&
               File.Exists(settings.Xvfb) && File.Exists(settings.X11Vnc) && File.Exists(settings.Websockify) &&
               Directory.Exists(settings.NoVncDir);
    }

    /// <summary>Запускает виртуальный экран. Удалённый доступ не открывается — он по требованию (StartViewerAsync).</summary>
    public static async Task<RemoteWindow> StartAsync(AppLogger logger, Settings? settings = null)
    {
        settings ??= new Settings();
        var errors = new Queue<string>();
        // -displayfd 1: сервер сам выбирает свободный экран и сообщает его номер — старые
        // блокировки после аварийной остановки не мешают.
        var xvfb = Spawn(settings.Xvfb, ["-displayfd", "1", "-screen", "0", "1280x900x24", "-nolisten", "tcp"], errors, readStdoutLater: true);
        try
        {
            using var cts = new CancellationTokenSource(settings.StartTimeout);
            string? line;
            try
            {
                line = await xvfb.StandardOutput.ReadLineAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                line = null;
            }

            if (line is null || !int.TryParse(line.Trim(), out var number) || number < 0)
            {
                throw new InvalidOperationException(
                    $"Xvfb не сообщил номер экрана{(xvfb.HasExited ? $" (завершился с кодом {xvfb.ExitCode})" : string.Empty)}. {Tail(errors)}");
            }

            logger.Info($"[Окно] Виртуальный экран :{number} запущен (Xvfb).");
            return new RemoteWindow(logger, settings, xvfb, $":{number}");
        }
        catch
        {
            await StopAsync(xvfb);
            throw;
        }
    }

    /// <summary>
    /// Открывает удалённый доступ к экрану: x11vnc (только 127.0.0.1) и noVNC. Нужен пароль.
    /// Вызывается, когда человека зовут к окну; StopViewerAsync выключает.
    /// </summary>
    public async Task StartViewerAsync()
    {
        if (ViewerRunning)
        {
            return;
        }

        var password = _settings.Password;
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                "пароль окна не задан (FAB_VNC_PASSWORD в .env) — без пароля удалённое окно не открываем");
        }

        if (password.Length > 8)
        {
            _logger.Warn("[Окно] Пароль окна длиннее 8 знаков: VNC читает только первые 8.");
        }

        try
        {
            _passwordFile = Path.Combine(Path.GetTempPath(), $"uad-vnc-{Environment.ProcessId}.pass");
            // Права 600 задаются при создании файла — пароль не лежит открытым ни мгновения.
            var fileOptions = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var file = new FileStream(_passwordFile, fileOptions))
            {
                await file.WriteAsync(System.Text.Encoding.UTF8.GetBytes(password));
            }

            _vncErrors = new Queue<string>();
            _vnc = Spawn(_settings.X11Vnc,
            [
                "-display", Display, "-localhost", "-noipv6", "-rfbport", _settings.VncPort.ToString(),
                "-passwdfile", _passwordFile, "-forever", "-shared", "-quiet"
            ], _vncErrors);
            if (!await WaitForPortAsync(_settings.VncPort, _vnc))
            {
                throw new InvalidOperationException($"x11vnc не открыл порт {_settings.VncPort}. {Tail(_vncErrors)}");
            }

            _webErrors = new Queue<string>();
            _web = Spawn(_settings.Websockify,
                ["--web", _settings.NoVncDir, _settings.WebPort.ToString(), $"127.0.0.1:{_settings.VncPort}"], _webErrors);
            if (!await WaitForPortAsync(_settings.WebPort, _web))
            {
                throw new InvalidOperationException($"noVNC (websockify) не открыл порт {_settings.WebPort}. {Tail(_webErrors)}");
            }
        }
        catch
        {
            await StopViewerAsync();
            throw;
        }

        _logger.Info("[Окно] Удалённый доступ к окну открыт.");
        _logger.Info($"[Окно] {Hint}");
    }

    /// <summary>Закрывает удалённый доступ и стирает файл с паролем. Виртуальный экран остаётся.</summary>
    public async Task StopViewerAsync()
    {
        var wasRunning = _web is not null || _vnc is not null;
        await StopAsync(_web);
        await StopAsync(_vnc);
        _web = null;
        _vnc = null;
        if (_passwordFile is not null)
        {
            try
            {
                File.Delete(_passwordFile);
            }
            catch (IOException)
            {
            }

            _passwordFile = null;
        }

        if (wasRunning)
        {
            _logger.Info("[Окно] Удалённый доступ к окну закрыт.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopViewerAsync();
        await StopAsync(_xvfb);
        _xvfb.Dispose();
    }

    // ------------------------------------------------------------------ процессы

    private static string HostPort(Settings settings) =>
        !string.IsNullOrWhiteSpace(settings.HostPort) ? settings.HostPort
        : Environment.GetEnvironmentVariable("FAB_VNC_PORT") is { Length: > 0 } fromEnv ? fromEnv
        : settings.WebPort.ToString();

    private static Process Spawn(string file, IEnumerable<string> args, Queue<string> errorTail, bool readStdoutLater = false)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("процесс не создан");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"не запустился {Path.GetFileName(file)}: {ex.Message}");
        }

        ChildReaper.Register(process);

        // Служебные сообщения этих программ читаем, чтобы они не упёрлись в полный буфер.
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            lock (errorTail)
            {
                errorTail.Enqueue(e.Data);
                while (errorTail.Count > 8)
                {
                    errorTail.Dequeue();
                }
            }
        };
        process.BeginErrorReadLine();
        if (!readStdoutLater)
        {
            process.OutputDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
        }

        return process;
    }

    private static string Tail(Queue<string> errors)
    {
        lock (errors)
        {
            return errors.Count == 0 ? string.Empty : "Вывод: " + string.Join(" | ", errors);
        }
    }

    private static async Task<bool> WaitForPortAsync(int port, Process process)
    {
        var stopAt = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < stopAt && !process.HasExited)
        {
            try
            {
                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await client.ConnectAsync("127.0.0.1", port, cts.Token);
                return true;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                await Task.Delay(250);
            }
        }

        return false;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    internal static extern int SysKill(int pid, int signal);

    /// <summary>Просит процесс закрыться (SIGTERM), через пару секунд — принудительно.</summary>
    private static async Task StopAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                if (OperatingSystem.IsLinux())
                {
                    SysKill(process.Id, 15);
                }
                else
                {
                    process.Kill(entireProcessTree: true);
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Процесс уже ушёл.
        }
    }
}

/// <summary>
/// Если процесс программы убит сигналом (Ctrl+C в `docker compose exec -it`, обрыв SSH — SIGHUP, SIGTERM), блоки
/// `finally` не выполняются, и в контейнере остаются Xvfb, x11vnc, websockify и Chromium — сотни мегабайт на
/// тесном сервере до следующего прогона. Здесь запоминаем дочерние процессы Fab и по сигналу сразу их убиваем;
/// действие по умолчанию (выход) после этого выполняется как обычно. Только Linux и macOS.
/// </summary>
internal static class ChildReaper
{
    private static readonly List<Process> Children = [];
    private static readonly object Sync = new();
    private static readonly List<PosixSignalRegistration> Registrations = [];

    public static void Register(Process process)
    {
        lock (Sync)
        {
            Children.RemoveAll(Gone);
            Children.Add(process);
            if (Registrations.Count > 0 || OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                foreach (var signal in new[] { PosixSignal.SIGHUP, PosixSignal.SIGINT, PosixSignal.SIGTERM })
                {
                    Registrations.Add(PosixSignalRegistration.Create(signal, _ => KillAll()));
                }
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or ArgumentException)
            {
                // Нет такой возможности — остаётся очистка при следующем запуске (KillStaleProcesses).
            }
        }
    }

    /// <summary>Процесс уже ушёл или его объект освобождён (тогда HasExited бросает исключение).</summary>
    private static bool Gone(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    public static void KillAll()
    {
        lock (Sync)
        {
            foreach (var process in Children)
            {
                try
                {
                    if (!Gone(process))
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }
}
