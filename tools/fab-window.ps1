# Окно Fab на сервере — туннель SSH и браузер одним запуском (Windows).
#
#   Двойной щелчок по tools\fab-window.cmd
#   (или: powershell -NoProfile -ExecutionPolicy Bypass -File tools\fab-window.ps1)
#
# Когда бот пишет «Fab ждёт вас», сервер открывает окно браузера для человека (noVNC). Оно слушает только
# localhost сервера, поэтому нужен туннель SSH. Скрипт: спрашивает адрес сервера один раз (запоминает в
# tools\fab-window.local.json — в git не попадает), читает с сервера порт и пароль окна, проверяет, ждёт ли
# сервер, открывает туннель и браузер с готовой ссылкой. Enter — туннель закрывается.
# На сервере и на этом компьютере ничего не меняет. Лог — tools\logs\fab-window.log (пароль туда не пишется).

$ErrorActionPreference = 'Continue'
$Version = '1.0.1'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$SettingsPath = Join-Path $Here 'fab-window.local.json'
$LogDir = Join-Path $Here 'logs'
$LogPath = Join-Path $LogDir 'fab-window.log'

function Write-Log([string]$text) {
    try {
        if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir | Out-Null }
        # Лог не растёт без конца: больше ~200 КБ — начинаем заново.
        if ((Test-Path $LogPath) -and (Get-Item $LogPath).Length -gt 200KB) { Remove-Item $LogPath }
        Add-Content -Path $LogPath -Value ('[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $text) -Encoding UTF8
    } catch { }
}

function Say([string]$text) { Write-Host $text; Write-Log $text }

function Read-Settings {
    $s = [pscustomobject]@{ Server = ''; RemoteDir = '~/UnityAssetsDownloader'; LocalPort = '' }
    if (Test-Path $SettingsPath) {
        try {
            $saved = Get-Content $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($name in 'Server', 'RemoteDir', 'LocalPort') {
                if ($saved.$name) { $s.$name = [string]$saved.$name }
            }
        } catch { Say "Файл настроек не прочитался ($SettingsPath) — спрошу заново." }
    }
    return $s
}

function Ask([string]$question, [string]$default) {
    $prompt = if ($default) { "$question [$default]" } else { $question }
    $answer = Read-Host $prompt
    if ([string]::IsNullOrWhiteSpace($answer)) { return $default }
    return $answer.Trim()
}

function Edit-Settings($s) {
    Write-Host ''
    Write-Host 'Enter — оставить значение в скобках.'
    do {
        $s.Server = Ask 'Сервер для SSH (пользователь@адрес, как в команде ssh)' $s.Server
    } while (-not $s.Server)
    $s.RemoteDir = Ask 'Папка проекта на сервере' $s.RemoteDir
    $s.LocalPort = Ask 'Порт на этом компьютере (Enter — как на сервере)' $s.LocalPort
    $s | ConvertTo-Json | Set-Content -Path $SettingsPath -Encoding UTF8
    Say "Настройки сохранены: сервер $($s.Server), папка $($s.RemoteDir), порт здесь $(if ($s.LocalPort) { $s.LocalPort } else { 'как на сервере' })."
}

function Test-LocalPort([int]$port) {
    try {
        $client = New-Object Net.Sockets.TcpClient
        $client.Connect('127.0.0.1', $port)
        $client.Close()
        return $true
    } catch { return $false }
}

# Одним заходом на сервер: порт и пароль окна из .env, и открыто ли окно сейчас (код ответа noVNC).
function Get-ServerWindow($s) {
    $remote = 'cd ' + $s.RemoteDir + ' || exit 3; grep -E ''^(FAB_VNC_PASSWORD|FAB_VNC_PORT)='' .env; ' +
              'p=$(grep -E ''^FAB_VNC_PORT='' .env | tr -dc 0-9); ' +
              'echo HTTP=$(curl -s -o /dev/null -w %{http_code} --max-time 5 http://127.0.0.1:${p:-6085}/vnc.html)'
    Write-Log "ssh $($s.Server): read window settings"
    $out = & ssh -o ConnectTimeout=10 $s.Server $remote 2>&1
    $code = $LASTEXITCODE
    $info = [pscustomobject]@{ Ok = $false; Port = '6085'; Password = ''; Http = ''; Error = '' }
    foreach ($line in @($out)) {
        $text = [string]$line
        if ($text -match '^FAB_VNC_PORT=(.*)$') { $v = ($Matches[1] -replace '[^0-9]', ''); if ($v) { $info.Port = $v } }
        elseif ($text -match '^FAB_VNC_PASSWORD=(.*)$') { $info.Password = ($Matches[1].Trim() -replace '^["'']|["'']$', '') }
        elseif ($text -match '^HTTP=(.*)$') { $info.Http = $Matches[1].Trim() }
    }

    if ($code -eq 255) { $info.Error = 'нет связи с сервером по SSH: ' + (($out | Out-String).Trim()) }
    elseif ($code -eq 3) { $info.Error = "на сервере нет папки $($s.RemoteDir) — поправьте в настройках (пункт 3)" }
    elseif (-not $info.Password) { $info.Error = 'на сервере в .env нет FAB_VNC_PASSWORD — запустите там ./deploy.sh' }
    else { $info.Ok = $true }
    Write-Log "server: exit=$code port=$($info.Port) http=$($info.Http) password=$(if ($info.Password) { 'есть' } else { 'нет' })"
    return $info
}

function Open-Window($s) {
    $w = Get-ServerWindow $s
    if (-not $w.Ok) { Say "Не вышло: $($w.Error)"; return }

    if ($w.Http -eq '200') {
        Say 'Сервер ждёт вас: окно открыто.'
    } else {
        Say 'Сейчас сервер вас не ждёт: окно открывается, когда бот пишет «Fab ждёт вас».'
        $go = Ask 'Всё равно открыть туннель и браузер? (д/н)' 'н'
        if ($go -notmatch '^(д|y|да|yes)$') { return }
    }

    $local = if ($s.LocalPort) { [int]$s.LocalPort } else { [int]$w.Port }
    $tunnel = $null
    if (Test-LocalPort $local) {
        Say "Порт $local на этом компьютере уже занят — похоже, туннель уже открыт. Открываю браузер."
    } else {
        Say "Открываю туннель: 127.0.0.1:$local -> сервер 127.0.0.1:$($w.Port)..."
        $sshArgs = @('-N', '-n', '-o', 'ExitOnForwardFailure=yes', '-o', 'ServerAliveInterval=30', '-o', 'ConnectTimeout=10',
                     '-L', "127.0.0.1:${local}:127.0.0.1:$($w.Port)", $s.Server)
        # В этом же окне: если закрыть окно консоли, туннель закроется вместе с ним.
        $tunnel = Start-Process -FilePath 'ssh' -ArgumentList $sshArgs -NoNewWindow -PassThru
        $ready = $false
        for ($i = 0; $i -lt 30 -and -not $tunnel.HasExited; $i++) {
            if (Test-LocalPort $local) { $ready = $true; break }
            Start-Sleep -Milliseconds 500
        }
        if (-not $ready) {
            Say 'Туннель не открылся (нет связи или порт занят на сервере). Подробности — выше.'
            if (-not $tunnel.HasExited) { Stop-Process -Id $tunnel.Id -Force }
            return
        }
        Say 'Туннель открыт.'
    }

    # Пароль окна — после «#»: остаётся в браузере, по сети не уходит; в лог не пишется.
    $url = "http://127.0.0.1:$local/vnc.html?autoconnect=true&resize=scale&reconnect=true&reconnect_delay=3000#password=$([Uri]::EscapeDataString($w.Password))"
    Start-Process $url
    Write-Log "browser opened: http://127.0.0.1:$local/vnc.html (password in #)"
    Write-Host ''
    Write-Host 'В окне: поставьте галочку (или войдите, если просит). Потом нажмите «✅ Готово» в сообщении бота'
    Write-Host 'или закройте окно Chrome крестиком — сервер продолжит сам.'
    if ($tunnel) {
        Read-Host 'Enter — закрыть туннель' | Out-Null
        if (-not $tunnel.HasExited) { Stop-Process -Id $tunnel.Id -Force }
        Say 'Туннель закрыт.'
    }
}

function Show-ServerStatus($s) {
    $w = Get-ServerWindow $s
    if (-not $w.Ok) { Say "Не вышло: $($w.Error)"; return }
    Say "Связь есть. Порт окна на сервере: $($w.Port), пароль окна: есть."
    Say $(if ($w.Http -eq '200') { 'Сервер сейчас ждёт вас — пункт 1.' } elseif ($w.Http) { "Сервер сейчас не ждёт (код $($w.Http)) — окно откроется, когда бот напишет." } else { 'На сервере нет curl — проверить, ждёт ли он, нельзя; окно всё равно можно открыть пунктом 1.' })
}

Write-Log "=== fab-window.ps1 $Version start ==="
if (-not (Get-Command ssh -ErrorAction SilentlyContinue)) {
    Say 'Не найден ssh. В Windows 10/11 он есть: Параметры → Приложения → Дополнительные компоненты → «Клиент OpenSSH».'
    Read-Host 'Enter — выход' | Out-Null
    exit 1
}

$settings = Read-Settings
if (-not $settings.Server) {
    Say 'Первый запуск: нужен адрес сервера.'
    Edit-Settings $settings
}

while ($true) {
    Write-Host ''
    Write-Host "=== Окно Fab на сервере ($($settings.Server)) — версия $Version ==="
    Write-Host '  1 — Открыть окно (туннель + браузер)'
    Write-Host '  2 — Проверить связь и ждёт ли сервер'
    Write-Host '  3 — Настройки (сервер, папка, порт)'
    Write-Host '  0 — Выход'
    $choice = Ask 'Выбор' '1'
    switch ($choice) {
        '1' { Open-Window $settings }
        '2' { Show-ServerStatus $settings }
        '3' { Edit-Settings $settings }
        '0' { Write-Log '=== exit ==='; exit 0 }
        default { Write-Host 'Нет такого пункта.' }
    }
}
