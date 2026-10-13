#!/usr/bin/env bash
# Окно Fab на сервере — туннель SSH и браузер одним запуском (Steam Deck, Linux, Mac). Для Windows — fab-window.cmd.
#
#   ./tools/fab-window.sh
#
# Когда бот пишет «Fab ждёт вас», сервер открывает окно браузера для человека (noVNC). Оно слушает только
# localhost сервера, поэтому нужен туннель SSH. Скрипт: спрашивает адрес сервера один раз (запоминает в
# tools/fab-window.local.conf — в git не попадает), читает с сервера порт и пароль окна, проверяет, ждёт ли
# сервер, открывает туннель и браузер с готовой ссылкой. Enter — туннель закрывается.
# На сервере и на этом компьютере ничего не меняет. Лог — tools/logs/fab-window.log (пароль туда не пишется).

set -u
VERSION="1.0.0"
HERE="$(cd "$(dirname "$(readlink -f "$0" 2>/dev/null || echo "$0")")" && pwd)"
SETTINGS="$HERE/fab-window.local.conf"
LOG_DIR="$HERE/logs"
LOG="$LOG_DIR/fab-window.log"

log() {
    mkdir -p "$LOG_DIR" 2>/dev/null || return 0
    # Лог не растёт без конца: больше ~200 КБ — начинаем заново.
    if [ -f "$LOG" ] && [ "$(wc -c <"$LOG")" -gt 204800 ]; then : >"$LOG"; fi
    printf '[%s] %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*" >>"$LOG"
}
say() { echo "$*"; log "$*"; }

SERVER=""
REMOTE_DIR="~/UnityAssetsDownloader"
LOCAL_PORT=""
if [ -f "$SETTINGS" ]; then
    while IFS='=' read -r key value; do
        case "$key" in
            SERVER) SERVER=$value ;;
            REMOTE_DIR) REMOTE_DIR=$value ;;
            LOCAL_PORT) LOCAL_PORT=$value ;;
        esac
    done <"$SETTINGS"
fi

ask() {   # ask "вопрос" "значение по умолчанию" -> ответ в REPLY_VALUE
    local prompt=$1 default=$2 answer
    if [ -n "$default" ]; then read -r -p "$prompt [$default]: " answer; else read -r -p "$prompt: " answer; fi
    REPLY_VALUE=${answer:-$default}
}

edit_settings() {
    echo
    echo "Enter — оставить значение в скобках."
    while :; do
        ask "Сервер для SSH (пользователь@адрес, как в команде ssh)" "$SERVER"; SERVER=$REPLY_VALUE
        [ -n "$SERVER" ] && break
    done
    ask "Папка проекта на сервере" "$REMOTE_DIR"; REMOTE_DIR=$REPLY_VALUE
    ask "Порт на этом компьютере (Enter — как на сервере)" "$LOCAL_PORT"; LOCAL_PORT=$REPLY_VALUE
    printf 'SERVER=%s\nREMOTE_DIR=%s\nLOCAL_PORT=%s\n' "$SERVER" "$REMOTE_DIR" "$LOCAL_PORT" >"$SETTINGS"
    say "Настройки сохранены: сервер $SERVER, папка $REMOTE_DIR, порт здесь ${LOCAL_PORT:-как на сервере}."
}

port_open() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; }

open_url() {
    if command -v xdg-open >/dev/null 2>&1; then xdg-open "$1" >/dev/null 2>&1 &
    elif command -v open >/dev/null 2>&1; then open "$1"
    else echo "Откройте в браузере: $1"; fi
}

# Одним заходом на сервер: порт и пароль окна из .env, и открыто ли окно сейчас (код ответа noVNC).
read_server() {
    WIN_PORT=6085; WIN_PASSWORD=""; WIN_HTTP=""; WIN_ERROR=""
    local out code line
    log "ssh $SERVER: read window settings"
    out=$(ssh -o ConnectTimeout=10 "$SERVER" "cd $REMOTE_DIR || exit 3; grep -E '^(FAB_VNC_PASSWORD|FAB_VNC_PORT)=' .env; p=\$(grep -E '^FAB_VNC_PORT=' .env | tr -dc 0-9); echo HTTP=\$(curl -s -o /dev/null -w %{http_code} --max-time 5 http://127.0.0.1:\${p:-6085}/vnc.html)" 2>&1)
    code=$?
    while IFS= read -r line; do
        case "$line" in
            FAB_VNC_PORT=*) local v; v=$(printf '%s' "${line#*=}" | tr -dc 0-9); [ -n "$v" ] && WIN_PORT=$v ;;
            FAB_VNC_PASSWORD=*) WIN_PASSWORD=${line#*=}; WIN_PASSWORD=${WIN_PASSWORD#[\"\']}; WIN_PASSWORD=${WIN_PASSWORD%[\"\']} ;;
            HTTP=*) WIN_HTTP=${line#HTTP=} ;;
        esac
    done <<<"$out"
    if [ "$code" -eq 255 ]; then WIN_ERROR="нет связи с сервером по SSH: $out"
    elif [ "$code" -eq 3 ]; then WIN_ERROR="на сервере нет папки $REMOTE_DIR — поправьте в настройках (пункт 3)"
    elif [ -z "$WIN_PASSWORD" ]; then WIN_ERROR="на сервере в .env нет FAB_VNC_PASSWORD — запустите там ./deploy.sh"
    fi
    log "server: exit=$code port=$WIN_PORT http=$WIN_HTTP password=$([ -n "$WIN_PASSWORD" ] && echo есть || echo нет)"
    [ -z "$WIN_ERROR" ]
}

open_window() {
    read_server || { say "Не вышло: $WIN_ERROR"; return; }
    if [ "$WIN_HTTP" = "200" ]; then
        say "Сервер ждёт вас: окно открыто."
    else
        say "Сейчас сервер вас не ждёт: окно открывается, когда бот пишет «Fab ждёт вас»."
        ask "Всё равно открыть туннель и браузер? (д/н)" "н"
        case "$REPLY_VALUE" in д|y|да|yes|Д|Y) ;; *) return ;; esac
    fi

    local local_port=${LOCAL_PORT:-$WIN_PORT} tunnel=""
    if port_open "$local_port"; then
        say "Порт $local_port на этом компьютере уже занят — похоже, туннель уже открыт. Открываю браузер."
    else
        say "Открываю туннель: 127.0.0.1:$local_port -> сервер 127.0.0.1:$WIN_PORT..."
        ssh -N -o ExitOnForwardFailure=yes -o ServerAliveInterval=30 -o ConnectTimeout=10 \
            -L "127.0.0.1:$local_port:127.0.0.1:$WIN_PORT" "$SERVER" &
        tunnel=$!
        local i ready=""
        for i in $(seq 1 30); do
            kill -0 "$tunnel" 2>/dev/null || break
            if port_open "$local_port"; then ready=1; break; fi
            sleep 0.5
        done
        if [ -z "$ready" ]; then
            say "Туннель не открылся (нет связи или порт занят на сервере). Подробности — выше."
            kill "$tunnel" 2>/dev/null
            return
        fi
        say "Туннель открыт."
    fi

    # Пароль окна — после «#»: остаётся в браузере, по сети не уходит; в лог не пишется.
    open_url "http://127.0.0.1:$local_port/vnc.html?autoconnect=true&resize=scale#password=$WIN_PASSWORD"
    log "browser opened: http://127.0.0.1:$local_port/vnc.html (password in #)"
    echo
    echo "В окне: поставьте галочку (или войдите, если просит). Потом нажмите «✅ Готово» в сообщении бота"
    echo "или закройте окно Chrome крестиком — сервер продолжит сам."
    if [ -n "$tunnel" ]; then
        read -r -p "Enter — закрыть туннель" _
        kill "$tunnel" 2>/dev/null
        say "Туннель закрыт."
    fi
}

show_status() {
    read_server || { say "Не вышло: $WIN_ERROR"; return; }
    say "Связь есть. Порт окна на сервере: $WIN_PORT, пароль окна: есть."
    if [ "$WIN_HTTP" = "200" ]; then say "Сервер сейчас ждёт вас — пункт 1."
    elif [ -n "$WIN_HTTP" ]; then say "Сервер сейчас не ждёт (код $WIN_HTTP) — окно откроется, когда бот напишет."
    else say "На сервере нет curl — проверить, ждёт ли он, нельзя; окно всё равно можно открыть пунктом 1."; fi
}

log "=== fab-window.sh $VERSION start ==="
command -v ssh >/dev/null 2>&1 || { say "Не найден ssh."; exit 1; }
if [ -z "$SERVER" ]; then say "Первый запуск: нужен адрес сервера."; edit_settings; fi

while :; do
    echo
    echo "=== Окно Fab на сервере ($SERVER) — версия $VERSION ==="
    echo "  1 — Открыть окно (туннель + браузер)"
    echo "  2 — Проверить связь и ждёт ли сервер"
    echo "  3 — Настройки (сервер, папка, порт)"
    echo "  0 — Выход"
    ask "Выбор" "1"
    case "$REPLY_VALUE" in
        1) open_window ;;
        2) show_status ;;
        3) edit_settings ;;
        0) log "=== exit ==="; exit 0 ;;
        *) echo "Нет такого пункта." ;;
    esac
done
