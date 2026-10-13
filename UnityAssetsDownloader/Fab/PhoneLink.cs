using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>Режим ссылки на окно Fab с телефона (FAB_PHONE_LINK).</summary>
internal enum PhoneLinkMode
{
    Off,
    Http,
    Https
}

/// <summary>
/// Ссылка на окно Fab с телефона без туннеля SSH (этап C плана «Окно Fab», 13.10). Пользователь: «ссылка без туннеля —
/// только если я нажму боту… что готов тут через телефон сделать это»; «иметь возможность и первый вариант и с доменом».
/// Поэтому два режима: HTTP по адресу сервера (без шифрования — только галочка) и HTTPS через домен пользователя
/// (сертификат Let's Encrypt выпускает deploy.sh, продлевает certbot сервера). Вход открывается только по кнопке
/// «📱 С телефона», живёт Lifetime и закрывается вместе с окном.
/// </summary>
internal sealed class PhoneLinkSettings
{
    /// <summary>Порт внутри контейнера; наружу его выводит docker-compose (FAB_PHONE_PORT, по умолчанию 7443).</summary>
    public const int InternalPort = 7443;

    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);

    public PhoneLinkMode Mode { get; init; }

    /// <summary>Домен (HTTPS) или адрес сервера (HTTP) — как его наберёт телефон.</summary>
    public string? PublicHost { get; init; }

    public int PublicPort { get; init; } = InternalPort;

    /// <summary>Папка сертификата (fullchain.pem, privkey.pem): /etc/letsencrypt/live/&lt;домен&gt;.</summary>
    public string? CertDirectory { get; init; }

    public TimeSpan Lifetime { get; init; } = DefaultLifetime;

    public bool Enabled => Mode != PhoneLinkMode.Off && !string.IsNullOrWhiteSpace(PublicHost);

    public static PhoneLinkSettings FromEnvironment(Action<string> warn) => Parse(
        Environment.GetEnvironmentVariable("FAB_PHONE_LINK"),
        Environment.GetEnvironmentVariable("FAB_PHONE_HOST"),
        Environment.GetEnvironmentVariable("FAB_PHONE_PORT"),
        Environment.GetEnvironmentVariable("FAB_PHONE_CERT_LIVE"),
        warn);

    internal static PhoneLinkSettings Parse(string? mode, string? host, string? port, string? certDirectory, Action<string> warn)
    {
        var m = (mode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" or "off" or "0" or "-" or "no" => PhoneLinkMode.Off,
            "http" => PhoneLinkMode.Http,
            "https" => PhoneLinkMode.Https,
            var other => Unknown(other)
        };

        PhoneLinkMode Unknown(string value)
        {
            warn($"[Окно] Не понял FAB_PHONE_LINK='{value}' (off, http или https) — ссылка с телефона выключена.");
            return PhoneLinkMode.Off;
        }

        var h = (host ?? string.Empty).Trim();
        if (m != PhoneLinkMode.Off && !Regex.IsMatch(h, @"^[A-Za-z0-9.-]+$"))
        {
            warn($"[Окно] FAB_PHONE_HOST='{host}' — нужен домен или адрес сервера (запустите ./deploy.sh). Ссылка с телефона выключена.");
            m = PhoneLinkMode.Off;
        }

        var p = InternalPort;
        if (!string.IsNullOrWhiteSpace(port))
        {
            if (int.TryParse(port.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and < 65536)
            {
                p = parsed;
            }
            else
            {
                warn($"[Окно] Не понял FAB_PHONE_PORT='{port}' — берём {InternalPort}.");
            }
        }

        return new PhoneLinkSettings
        {
            Mode = m,
            PublicHost = h.Length > 0 ? h : null,
            PublicPort = p,
            CertDirectory = m == PhoneLinkMode.Https
                ? string.IsNullOrWhiteSpace(certDirectory) ? $"/etc/letsencrypt/live/{h}" : certDirectory.Trim()
                : null
        };
    }

    /// <summary>
    /// Сертификат домена: сам сертификат с ключом и промежуточные (iPhone без них не верит сертификату). warning — если
    /// до конца меньше двух недель (продление certbot сервера не сработало?).
    /// </summary>
    public (X509Certificate2 Cert, SslStreamCertificateContext Context, string? Warning) LoadCertificate(DateTime nowUtc)
    {
        var dir = CertDirectory ?? throw new InvalidOperationException("для HTTPS не задана папка сертификата");
        var fullchain = Path.Combine(dir, "fullchain.pem");
        var privkey = Path.Combine(dir, "privkey.pem");
        if (!File.Exists(fullchain) || !File.Exists(privkey))
        {
            throw new InvalidOperationException($"нет сертификата для {PublicHost} ({fullchain}) — выпустите его: ./deploy.sh, раздел про телефон");
        }

        var cert = X509Certificate2.CreateFromPemFile(fullchain, privkey);
        var all = new X509Certificate2Collection();
        all.ImportFromPemFile(fullchain);
        var intermediates = new X509Certificate2Collection();
        foreach (var c in all)
        {
            if (c.Thumbprint != cert.Thumbprint)
            {
                intermediates.Add(c);
            }
        }

        var context = SslStreamCertificateContext.Create(cert, intermediates, offline: true);
        var left = cert.NotAfter.ToUniversalTime() - nowUtc;
        var warning = left < TimeSpan.Zero
            ? $"сертификат {PublicHost} истёк {cert.NotAfter:dd.MM.yyyy} — продление certbot на сервере не сработало (./deploy.sh выпустит заново)"
            : left < TimeSpan.FromDays(14)
                ? $"сертификат {PublicHost} кончается {cert.NotAfter:dd.MM.yyyy} — продление certbot на сервере, похоже, не срабатывает"
                : null;
        return (cert, context, warning);
    }
}

/// <summary>
/// Вход в окно с телефона: страница noVNC и само окно (WebSocket → x11vnc на 127.0.0.1) по одноразовой ссылке
/// /w/&lt;ключ&gt;/vnc.html. Слушает только пока открыт: ключ длинный и случайный, живёт Lifetime; окно (WebSocket) пускает
/// только с того устройства, с которого его открыли первым (страницы noVNC — с любого: в них ничего своего нет, а
/// предпросмотр ссылки Telegram иначе занял бы окно раньше человека). Пароль окна (VNC) по-прежнему нужен — он в ссылке
/// после «#» и на сервер не уходит.
/// </summary>
internal sealed class PhoneGateway : IAsyncDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MaxConnections = 32;

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".mp3"] = "audio/mpeg",
        [".oga"] = "audio/ogg",
        [".wav"] = "audio/wav"
    };

    private readonly TcpListener _listener;
    private readonly SslStreamCertificateContext? _tls;
    private readonly string _webRoot;
    private readonly int _vncPort;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private readonly List<TcpClient> _clients = [];
    private Task? _acceptLoop;

    private PhoneGateway(TcpListener listener, SslStreamCertificateContext? tls, string webRoot, int vncPort, string token,
        DateTime expiresUtc, Action<string> log)
    {
        _listener = listener;
        _tls = tls;
        _webRoot = Path.GetFullPath(webRoot);
        _vncPort = vncPort;
        Token = token;
        ExpiresUtc = expiresUtc;
        _log = log;
    }

    public string Token { get; }
    public DateTime ExpiresUtc { get; }

    /// <summary>Адрес устройства, которому отдано окно (первое, открывшее WebSocket), или null.</summary>
    public string? BoundClient { get; private set; }

    public bool IsOpen => !_stop.IsCancellationRequested && DateTime.UtcNow < ExpiresUtc;

    /// <summary>Ссылка без пароля окна (пароль добавляет тот, кто его знает: «#password=…»).</summary>
    public string Link(string scheme, string host, int port) =>
        $"{scheme}://{host}:{port}/w/{Token}/vnc.html?autoconnect=true&resize=scale&reconnect=true&reconnect_delay=3000&path=w/{Token}/websockify";

    /// <summary>Начинает слушать listenPort (все адреса контейнера). tls null — без шифрования (режим HTTP).</summary>
    public static PhoneGateway Start(int listenPort, SslStreamCertificateContext? tls, string webRoot, int vncPort, TimeSpan lifetime,
        Action<string> log, IPAddress? bind = null)
    {
        var listener = new TcpListener(bind ?? IPAddress.Any, listenPort);
        listener.Start();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var gateway = new PhoneGateway(listener, tls, webRoot, vncPort, token, DateTime.UtcNow + lifetime, log);
        gateway._acceptLoop = Task.Run(gateway.AcceptLoopAsync);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(lifetime, gateway._stop.Token);
                log("[Окно] Ссылка для телефона устарела — вход закрыт.");
                await gateway.DisposeAsync();
            }
            catch (OperationCanceledException)
            {
                // закрыли раньше
            }
        });
        return gateway;
    }

    /// <summary>Порт, который реально слушается (для стенда: 0 — свободный).</summary>
    public int ListeningPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch
            {
                break;
            }

            lock (_sync)
            {
                if (_clients.Count >= MaxConnections)
                {
                    client.Dispose();
                    continue;
                }

                _clients.Add(client);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleConnectionAsync(client);
                }
                catch
                {
                    // Оборванное соединение телефона — не повод тревожить лог.
                }
                finally
                {
                    lock (_sync)
                    {
                        _clients.Remove(client);
                    }

                    client.Dispose();
                }
            });
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        client.NoDelay = true;
        var remote = client.Client.RemoteEndPoint is IPEndPoint ep ? ep.Address.MapToIPv4().ToString() : "?";
        Stream stream = client.GetStream();
        if (_tls is not null)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateContext = _tls,
                ApplicationProtocols = [SslApplicationProtocol.Http11]
            }, handshake.Token);
            stream = ssl;
        }

        await using (stream)
        {
            // Несколько запросов в одном соединении (keep-alive): страница noVNC — это десятки мелких файлов.
            while (!_stop.IsCancellationRequested)
            {
                var request = await ReadRequestAsync(stream, TimeSpan.FromSeconds(20));
                if (request is null)
                {
                    return;
                }

                if (!await RespondAsync(stream, request, remote))
                {
                    return;
                }
            }
        }
    }

    private sealed record Request(string Method, string Path, Dictionary<string, string> Headers);

    /// <summary>Заголовки запроса (до пустой строки, не больше 16 КБ). null — соединение закрыли или молчат.</summary>
    private async Task<Request?> ReadRequestAsync(Stream stream, TimeSpan idle)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(idle);
        var buffer = new List<byte>(1024);
        var one = new byte[1];
        try
        {
            while (buffer.Count < 16 * 1024)
            {
                if (await stream.ReadAsync(one, timeout.Token) == 0)
                {
                    return null;
                }

                buffer.Add(one[0]);
                var n = buffer.Count;
                if (n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n')
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        var lines = Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 3)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        return new Request(first[0], first[1], headers);
    }

    /// <summary>Отвечает на запрос. false — соединение больше не нужно (окно закрылось, ошибка, Connection: close).</summary>
    private async Task<bool> RespondAsync(Stream stream, Request request, string remote)
    {
        var prefix = $"/w/{Token}/";
        var target = request.Path.Split('?', 2)[0];
        if (!target.StartsWith(prefix, StringComparison.Ordinal) && target != $"/w/{Token}")
        {
            return await SendAsync(stream, 404, "text/plain; charset=utf-8", "Not found"u8.ToArray(), request);
        }

        if (!IsOpen)
        {
            return await SendAsync(stream, 410, "text/html; charset=utf-8",
                Encoding.UTF8.GetBytes(Page("Ссылка устарела. Нажмите «📱 С телефона» в боте ещё раз.")), request);
        }

        if (request.Method is not ("GET" or "HEAD"))
        {
            return await SendAsync(stream, 405, "text/plain; charset=utf-8", "Method not allowed"u8.ToArray(), request);
        }

        var rest = target.Length > prefix.Length ? Uri.UnescapeDataString(target[prefix.Length..]) : string.Empty;
        if (rest == "websockify")
        {
            return await BridgeAsync(stream, request, remote);
        }

        if (rest.Length == 0)
        {
            rest = "vnc.html";
        }

        // Только файлы noVNC: никаких «..», абсолютных путей и выхода из папки.
        var full = Path.GetFullPath(Path.Combine(_webRoot, rest));
        if (rest.Contains("..", StringComparison.Ordinal) || rest.StartsWith('/') ||
            !full.StartsWith(_webRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
        {
            return await SendAsync(stream, 404, "text/plain; charset=utf-8", "Not found"u8.ToArray(), request);
        }

        var type = ContentTypes.GetValueOrDefault(Path.GetExtension(full), "application/octet-stream");
        return await SendAsync(stream, 200, type, await File.ReadAllBytesAsync(full, _stop.Token), request);
    }

    private static string Page(string text) =>
        "<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">" +
        $"<title>Окно Fab</title></head><body style=\"font:18px sans-serif;padding:24px\">{WebUtility.HtmlEncode(text)}</body></html>";

    private async Task<bool> SendAsync(Stream stream, int status, string type, byte[] body, Request request)
    {
        var close = request.Headers.TryGetValue("Connection", out var c) && c.Contains("close", StringComparison.OrdinalIgnoreCase);
        var head = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {Reason(status)}\r\n")
            .Append(CultureInfo.InvariantCulture, $"Content-Type: {type}\r\n")
            .Append(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n")
            .Append("Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nX-Robots-Tag: noindex\r\n")
            .Append(close ? "Connection: close\r\n" : "Connection: keep-alive\r\n")
            .Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), _stop.Token);
        if (request.Method != "HEAD")
        {
            await stream.WriteAsync(body, _stop.Token);
        }

        await stream.FlushAsync(_stop.Token);
        return !close;
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        101 => "Switching Protocols",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        410 => "Gone",
        _ => "Error"
    };

    /// <summary>Окно: WebSocket телефона ↔ x11vnc. Пускает только устройство, открывшее окно первым.</summary>
    private async Task<bool> BridgeAsync(Stream stream, Request request, string remote)
    {
        if (!request.Headers.TryGetValue("Upgrade", out var upgrade) || !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase) ||
            !request.Headers.TryGetValue("Sec-WebSocket-Key", out var key))
        {
            return await SendAsync(stream, 400, "text/plain; charset=utf-8", "WebSocket expected"u8.ToArray(), request);
        }

        lock (_sync)
        {
            BoundClient ??= remote;
        }

        if (BoundClient != remote)
        {
            _log($"[Окно] Окно с телефона уже открыто на другом устройстве ({BoundClient}); подключение с {remote} не пускаем.");
            return await SendAsync(stream, 403, "text/plain; charset=utf-8", "This link is already open on another device."u8.ToArray(), request);
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        var protocols = request.Headers.GetValueOrDefault("Sec-WebSocket-Protocol", string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var protocol = protocols.Contains("binary") ? "binary" : null;
        var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                       $"Sec-WebSocket-Accept: {accept}\r\n" + (protocol is null ? string.Empty : $"Sec-WebSocket-Protocol: {protocol}\r\n") + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
        await stream.FlushAsync(_stop.Token);

        using var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
        {
            IsServer = true,
            SubProtocol = protocol,
            KeepAliveInterval = TimeSpan.FromSeconds(20)
        });
        using var vnc = new TcpClient { NoDelay = true };
        await vnc.ConnectAsync(IPAddress.Loopback, _vncPort, _stop.Token);
        var vncStream = vnc.GetStream();
        using var pair = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _log($"[Окно] Телефон подключился к окну ({remote}).");

        var toVnc = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            while (!pair.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buffer, pair.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                await vncStream.WriteAsync(buffer.AsMemory(0, r.Count), pair.Token);
            }
        });
        var toPhone = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            while (!pair.IsCancellationRequested)
            {
                var n = await vncStream.ReadAsync(buffer, pair.Token);
                if (n == 0)
                {
                    break;
                }

                await ws.SendAsync(buffer.AsMemory(0, n), WebSocketMessageType.Binary, true, pair.Token);
            }
        });

        await Task.WhenAny(toVnc, toPhone);
        await pair.CancelAsync();
        _ = toVnc.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        _ = toPhone.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        _log($"[Окно] Телефон отключился от окна ({remote}).");
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        await _stop.CancelAsync();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // уже остановлен
        }

        lock (_sync)
        {
            foreach (var c in _clients)
            {
                c.Dispose();
            }

            _clients.Clear();
        }

        if (_acceptLoop is not null)
        {
            await Task.WhenAny(_acceptLoop, Task.Delay(TimeSpan.FromSeconds(2)));
        }
    }
}
