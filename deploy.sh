#!/usr/bin/env bash
# Настройка и запуск UnityAssetsDownloader на сервере.
#
#   ./deploy.sh
#
# Спрашивает в консоли всё нужное (вход в Unity, бот, каналы, период), записывает .env,
# затем по шагам проверяет, что всё работает, и запускает службу в Docker.
# Повторный запуск — поменять настройки: Enter оставляет текущее значение.

set -u
cd "$(dirname "$(readlink -f "$0")")" || exit 1

ENV_FILE=".env"

# ---------------------------------------------------------------- оформление

bold() { printf '\n\033[1m%s\033[0m\n' "$*"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$*"; }
warn() { printf '  \033[33m!\033[0m %s\n' "$*"; }
fail() { printf '  \033[31m✗\033[0m %s\n' "$*"; }

# ask VAR "Вопрос" "значение по умолчанию"
ask() {
    local __var=$1 __q=$2 __def=${3-} __ans
    if [ -n "$__def" ]; then
        read -r -p "  $__q [$__def]: " __ans
    else
        read -r -p "  $__q: " __ans
    fi
    printf -v "$__var" '%s' "${__ans:-$__def}"
}

# ask_yes "Вопрос" Y|N — true, если ответили «да»
ask_yes() {
    local ans def=${2:-Y} hint
    [ "$def" = Y ] && hint="Д/н" || hint="д/Н"
    read -r -p "  $1 [$hint]: " ans
    ans=${ans:-$def}
    case "$ans" in y|Y|yes|Yes|д|Д|да|Да|ДА) return 0 ;; *) return 1 ;; esac
}

# ------------------------------------------------------- Telegram Bot API

# Адреса Bot API. DNS с этого сервера может отдавать адрес, до которого нет маршрута,
# хотя другие адреса того же сервиса отвечают. Программа внутри перебирает их так же
# (Plan.md, раздел «Связь с Telegram Bot API»).
TG_ADDRESSES="149.154.167.220 149.154.175.50 149.154.167.50 149.154.171.5 91.108.56.130"
TG_RESOLVE=""        # пусто — идём напрямую; иначе готовый аргумент для curl --resolve
TG_ROUTE_FOUND=0

# Отвечает ли Bot API по этому пути. Пустой аргумент — напрямую.
# Каждый путь пробуем дважды: на сети с потерями одиночная проба объявляет живой адрес
# мёртвым (проверено в проекте LinuxServerWatcher).
tg_probe() {
    local resolve=$1 code try
    for try in 1 2; do
        if [ -n "$resolve" ]; then
            code=$(curl -s -m 8 -o /dev/null -w '%{http_code}' --resolve "$resolve" https://api.telegram.org/ 2>/dev/null || true)
        else
            code=$(curl -s -m 8 -o /dev/null -w '%{http_code}' https://api.telegram.org/ 2>/dev/null || true)
        fi
        # Любой код, кроме 000, значит, что ответ пришёл: адрес живой.
        [ -n "$code" ] && [ "$code" != "000" ] && return 0
    done
    return 1
}

# Ищет путь до Bot API и запоминает его в TG_RESOLVE. Вызывать не внутри $(…):
# переменные, заданные в подстановке команд, наружу не выходят.
tg_route_detect() {
    local ip
    [ "$TG_ROUTE_FOUND" = 1 ] && return 0
    command -v curl >/dev/null 2>&1 || return 1

    if tg_probe ""; then
        TG_RESOLVE=""
        TG_ROUTE_FOUND=1
        return 0
    fi

    for ip in $TG_ADDRESSES; do
        if tg_probe "api.telegram.org:443:$ip"; then
            TG_RESOLVE="api.telegram.org:443:$ip"
            TG_ROUTE_FOUND=1
            warn "Напрямую api.telegram.org не отвечает, а по адресу $ip отвечает. Программа ходит так же."
            return 0
        fi
    done

    return 1
}

# tg_api <метод> — ответ Bot API по найденному пути. Пустой вывод — не дозвонились.
tg_api() {
    local method=$1
    if [ -n "$TG_RESOLVE" ]; then
        curl -s -m 10 --resolve "$TG_RESOLVE" "https://api.telegram.org/bot$TOKEN/$method" || true
    else
        curl -s -m 10 "https://api.telegram.org/bot$TOKEN/$method" || true
    fi
}

# ---------------------------------------------------------------- .env

# Значение в формате .env Docker Compose. В двойных кавычках с экранированием
# \ " и $ Compose передаёт в программу строку буквально — любой пароль,
# даже с кавычками, # и $. Проверено на Compose v5.
env_quote() {
    local v=$1
    v=${v//\\/\\\\}
    v=${v//\"/\\\"}
    v=${v//\$/\\\$}
    printf '"%s"' "$v"
}

# Текущее значение из .env (так, как его прочитает Compose).
env_get() {
    [ -f "$ENV_FILE" ] || return 0
    local line v
    line=$(grep -E "^$1=" "$ENV_FILE" | tail -n 1) || return 0
    v=${line#*=}
    case "$v" in
        \"*\") v=${v:1:${#v}-2}; v=$(printf '%s' "$v" | sed 's/\\\(.\)/\1/g') ;;
        \'*\') v=${v:1:${#v}-2} ;;
    esac
    printf '%s' "$v"
}

mask() { [ -n "$1" ] && printf '%s' "${1:0:3}…(${#1} симв.)" || printf 'не задан'; }

# Для /etc/letsencrypt и certbot нужен root; не под root — через sudo.
SUDO=""
[ "$(id -u)" -ne 0 ] && SUDO="sudo"
# Папка Let's Encrypt (меняется только для проверки скрипта в песочнице).
LE_DIR=${LE_DIR:-/etc/letsencrypt}

# Сертификат Let's Encrypt для окна с телефона. Порт 80 на сервере может занимать другой проект (на нашем — nginx
# smartvan): Let's Encrypt проверяет домен, запрашивая файл по http://<домен>/.well-known/acme-challenge/, поэтому
# файл кладётся в ту папку, которую этот сервер отдаёт по такому адресу. Сначала своя проба, потом certbot.
# Продлевать будет certbot сервера сам (certbot.timer), с той же папкой.
issue_phone_cert() {
    local domain=$1 detected="" webroot token got c
    if ! command -v certbot >/dev/null 2>&1; then
        warn "certbot не установлен — сертификат не выпустить. Установите (apt install certbot) и запустите ./deploy.sh снова."
        return
    fi
    echo "  Let's Encrypt проверит домен, запросив файл по http://$domain/.well-known/acme-challenge/…"
    echo "  Порт 80 здесь отвечает тот, кто его занимает, — файл кладём в его папку проверок."
    for c in $(docker ps --format '{{.Names}}' 2>/dev/null); do
        detected=$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/var/www/certbot"}}{{.Source}}{{end}}{{end}}' "$c" 2>/dev/null)
        [ -n "$detected" ] && break
    done
    [ -z "$detected" ] && [ -d /var/www/certbot ] && detected=/var/www/certbot
    ask webroot "Папка, которую сервер на порту 80 отдаёт по /.well-known/acme-challenge/" "$detected"
    if [ -z "$webroot" ] || ! $SUDO test -d "$webroot"; then
        fail "Папки «$webroot» нет — сертификат не выпущен."
        return
    fi
    token="uad-check-$(LC_ALL=C tr -dc 'a-z0-9' </dev/urandom | head -c 12)"
    $SUDO mkdir -p "$webroot/.well-known/acme-challenge"
    printf '%s' "$token" | $SUDO tee "$webroot/.well-known/acme-challenge/$token" >/dev/null
    got=$(curl -s -m 10 "http://$domain/.well-known/acme-challenge/$token" 2>/dev/null || true)
    $SUDO rm -f "$webroot/.well-known/acme-challenge/$token"
    if [ "$got" != "$token" ]; then
        fail "По http://$domain/.well-known/acme-challenge/ отдаётся не эта папка (или домен смотрит не сюда) — Let's Encrypt домен не проверит."
        return
    fi
    ok "Проба прошла: порт 80 отдаёт файлы из $webroot для $domain."
    ask_yes "Выпустить сертификат Let's Encrypt для $domain (это согласие с условиями Let's Encrypt)?" Y || return
    local extra=()
    $SUDO sh -c "ls $LE_DIR/accounts/*/directory/* >/dev/null 2>&1" || extra=(--register-unsafely-without-email)
    if $SUDO certbot certonly --webroot -w "$webroot" -d "$domain" --non-interactive --agree-tos --keep-until-expiring "${extra[@]}"; then
        ok "Сертификат выпущен."
    else
        fail "certbot не выпустил сертификат — причина выше."
    fi
}

# Имя канала из «@name», «t.me/name», «https://t.me/s/name» и т. п.
normalize_channel() {
    local c=$1
    c=${c#http://}; c=${c#https://}
    c=${c#t.me/}; c=${c#telegram.me/}; c=${c#s/}; c=${c#@}
    c=${c%%/*}; c=${c%%\?*}
    printf '%s' "$c"
}

# ---------------------------------------------------------------- вопросы

bold "UnityAssetsDownloader — настройка сервера"
if [ -f "$ENV_FILE" ]; then
    echo "  Настройки уже есть. Enter оставляет текущее значение."
    FIRST_TIME=0
else
    echo "  Ответьте на несколько вопросов — дальше программа будет работать сама."
    FIRST_TIME=1
fi

bold "1. Вход в Unity"
echo "  Нужен обычный пароль Unity ID. Вход через Google на сервере не работает:"
echo "  если входите через Google, задайте пароль на https://id.unity.com («Забыли пароль?»)."
while :; do
    ask EMAIL "Email Unity" "$(env_get UNITY_EMAIL)"
    [[ "$EMAIL" == *@*.* ]] && break
    fail "Похоже, это не email. Попробуйте ещё раз."
done

OLD_PASSWORD=$(env_get UNITY_PASSWORD)
while :; do
    if [ -n "$OLD_PASSWORD" ]; then
        read -r -s -p "  Пароль Unity (Enter — оставить сохранённый): " PASSWORD; echo
        PASSWORD=${PASSWORD:-$OLD_PASSWORD}
    else
        read -r -s -p "  Пароль Unity (не отображается): " PASSWORD; echo
    fi
    [ -n "$PASSWORD" ] && break
    fail "Пароль пустой."
done

bold "2. Telegram-бот для сообщений"
echo "  Бот пишет вам, что добавлено, и спрашивает код, если Unity его потребует."
echo "  Создать бота: в Telegram откройте @BotFather → /newbot → скопируйте токен."
echo "  Без бота всё тоже работает — просто ничего не будет приходить (Enter, чтобы пропустить)."
OLD_TOKEN=$(env_get TELEGRAM_BOT_TOKEN)
CHAT_ID=$(env_get TELEGRAM_CHAT_ID)
while :; do
    if [ -n "$OLD_TOKEN" ]; then
        read -r -p "  Токен бота (Enter — оставить $(mask "$OLD_TOKEN"), «-» — отключить): " TOKEN
        TOKEN=${TOKEN:-$OLD_TOKEN}
    else
        read -r -p "  Токен бота: " TOKEN
    fi
    TOKEN=${TOKEN// /}
    [ "$TOKEN" = "-" ] && TOKEN=""
    [ -z "$TOKEN" ] && { warn "Бот не настроен."; CHAT_ID=""; break; }
    if [[ ! "$TOKEN" =~ ^[0-9]+:[A-Za-z0-9_-]{20,}$ ]]; then
        fail "Токен выглядит как «123456789:AAE…». Попробуйте ещё раз."
        continue
    fi

    # Проверяем токен у Telegram: напрямую, а если не вышло — по закреплённым адресам.
    # Не открылось совсем — не страшно: программа попробует ещё и через прокси.
    BOT_NAME=""
    if command -v curl >/dev/null 2>&1; then
        tg_route_detect || true
        RESP=$(tg_api getMe)
        if [[ "$RESP" == *'"ok":true'* ]]; then
            BOT_NAME=$(printf '%s' "$RESP" | sed -n 's/.*"username":"\([^"]*\)".*/\1/p')
            ok "Бот найден: @$BOT_NAME"
        elif [[ "$RESP" == *'"ok":false'* ]]; then
            fail "Telegram не принял токен. Проверьте, что скопировали его целиком."
            [ "$TOKEN" = "$OLD_TOKEN" ] && OLD_TOKEN=""
            continue
        else
            warn "До api.telegram.org не дозвонился ни одним путём. Токен сохраню: программа попробует ещё и через прокси."
        fi
    fi
    [ "$TOKEN" != "$OLD_TOKEN" ] && CHAT_ID=""

    # Куда писать: узнаём из сообщения, которое вы напишете боту.
    if [ -z "$CHAT_ID" ] && [ -n "$BOT_NAME" ]; then
        echo "  Напишите боту @$BOT_NAME в Telegram любое сообщение (например, /start)."
        echo "  Жду до 2 минут..."
        for _ in $(seq 1 40); do
            UPD=$(tg_api getUpdates)
            CHAT_ID=$(printf '%s' "$UPD" | grep -o '"chat":{"id":-\?[0-9]*' | tail -n 1 | grep -o -- '-\?[0-9]*$' || true)
            [ -n "$CHAT_ID" ] && break
            sleep 3
        done
        if [ -n "$CHAT_ID" ]; then
            ok "Чат найден ($CHAT_ID). Сообщения будут приходить туда."
        else
            warn "Сообщения не дождался. Не страшно: напишите боту позже, программа запомнит чат сама."
        fi
    fi
    break
done

bold "3. Telegram-каналы с раздачами"
# Общий список — telegram_sources.txt: он в git, один для всех машин (ПК, Deck, сервер) и приходит сюда через
# git pull. В .env (TELEGRAM_CHANNELS) — только каналы, добавленные именно на этом сервере. Программа читает оба.
BASE_CHANNELS=$(grep -vE '^\s*(#|//|$)' telegram_sources.txt 2>/dev/null | tr -d '\r' | xargs | tr ' ' ',')

# has_channel "список,через,запятую" имя — есть ли имя в списке (без учёта регистра)
has_channel() { case ",${1,,}," in *",${2,,},"*) return 0 ;; *) return 1 ;; esac; }

EXTRA_CSV=""
add_extra() {
    if has_channel "$BASE_CHANNELS" "$1"; then
        [ "${2:-}" = quiet ] || warn "«$1» уже в общем списке (telegram_sources.txt)."
        return
    fi
    has_channel "$EXTRA_CSV" "$1" || EXTRA_CSV=${EXTRA_CSV:+$EXTRA_CSV,}$1
}
remove_extra() {
    local out="" e
    for e in ${EXTRA_CSV//,/ }; do
        [ "${e,,}" = "${1,,}" ] || out=${out:+$out,}$e
    done
    EXTRA_CSV=$out
}

# Что уже записано в .env: каналы, которые теперь есть в общем списке, оттуда убираются сами.
for c in $(env_get TELEGRAM_CHANNELS | tr ',;' '  '); do add_extra "$c" quiet; done

echo "  Общий список (telegram_sources.txt, одинаковый на всех машинах): ${BASE_CHANNELS:-пуст}"
[ -n "$EXTRA_CSV" ] && echo "  Добавлено только на этом сервере: ${EXTRA_CSV//,/, }"
echo "  Новые каналы лучше вписывать в telegram_sources.txt на ПК и коммитить: тогда они появятся везде."
echo "  Здесь можно добавить канал только для этого сервера: имя, @имя или ссылка t.me/… (по одному или через запятую)."
echo "  «-имя» убирает канал, добавленный на этом сервере. Пустая строка — закончить (сразу пустая — оставить как есть)."
while :; do
    read -r -p "  канал: " LINE
    [ -z "$LINE" ] && break
    for raw in ${LINE//,/ }; do
        remove=0
        if [ "${raw:0:1}" = "-" ]; then remove=1; raw=${raw:1}; fi
        c=$(normalize_channel "$raw")
        if [[ ! "$c" =~ ^[A-Za-z0-9_]{4,}$ ]]; then
            fail "«$raw» не похоже на канал Telegram, пропускаю."
        elif [ "$remove" = 1 ]; then
            if has_channel "$BASE_CHANNELS" "$c"; then
                warn "«$c» из общего списка: чтобы убрать, удалите строку в telegram_sources.txt и закоммитьте."
            else
                remove_extra "$c"
            fi
        else
            add_extra "$c"
        fi
    done
done
CHANNELS_VALUE=$EXTRA_CSV
ALL_CHANNELS="${BASE_CHANNELS}${EXTRA_CSV:+${BASE_CHANNELS:+,}$EXTRA_CSV}"
if [ -z "$ALL_CHANNELS" ]; then
    warn "Каналы не заданы — программе нечего будет читать. Запустите ./deploy.sh ещё раз, чтобы добавить."
else
    ok "Будут читаться: ${ALL_CHANNELS//,/, }"
fi

bold "4. Что читать, кроме каналов"
echo "  1 — только Telegram-каналы. Быстро, так работало до сих пор."
echo "  2 — ещё страница «топ бесплатных» магазина."
echo "  3 — ещё китайский архив и расширенные списки. Первый проход долгий (больше двухсот"
echo "      ссылок), дальше уже добавленные ассеты пропускаются по памяти профиля."
case "$(env_get SOURCES)" in
    top-free) SOURCES_DEFAULT=2 ;;
    all)      SOURCES_DEFAULT=3 ;;
    *)        SOURCES_DEFAULT=1 ;;
esac
while :; do
    ask SOURCES_CHOICE "Выберите 1, 2 или 3" "$SOURCES_DEFAULT"
    case "$SOURCES_CHOICE" in
        1) SOURCES_VALUE=telegram; break ;;
        2) SOURCES_VALUE=top-free; break ;;
        3) SOURCES_VALUE=all;      break ;;
        *) fail "Введите 1, 2 или 3." ;;
    esac
done
ok "Источники: $SOURCES_VALUE"

# Каналы проверяются каждый прогон, а страница «топ бесплатных» меняется медленно и требует браузера:
# её читаем реже. С SOURCES=telegram вопрос не нужен.
TOP_FREE_EVERY=$(env_get TOP_FREE_EVERY); TOP_FREE_EVERY=${TOP_FREE_EVERY:-3d}
if [ "$SOURCES_VALUE" != telegram ]; then
    echo "  Каналы проверяются в каждом прогоне, а списки магазина («топ бесплатных») — реже: они меняются медленно."
    while :; do
        ask TOP_FREE_EVERY "Как часто читать списки магазина (3d, 1d, 12h; off — в каждом прогоне)" "$TOP_FREE_EVERY"
        [[ "$TOP_FREE_EVERY" =~ ^([0-9]+[dhms])+$ || "$TOP_FREE_EVERY" =~ ^[0-9]+$ || "$TOP_FREE_EVERY" = off ]] && break
        fail "Не понял. Примеры: 3d — раз в 3 дня, 12h — раз в 12 часов, off — каждый прогон."
    done
fi

bold "5. Расписание"
while :; do
    ask INTERVAL "Как часто проверять каналы (30m, 6h, 1d)" "$(env_get WATCH_INTERVAL || true)"
    INTERVAL=${INTERVAL:-24h}
    [[ "$INTERVAL" =~ ^([0-9]+[dhms])+$ || "$INTERVAL" =~ ^[0-9]+$ ]] && break
    fail "Не понял. Примеры: 30m — полчаса, 6h — 6 часов, 1d — раз в сутки."
done
echo
echo "  Ночью служба может сама понемногу проходить ИСТОРИЮ каналов (старые посты, которые обычные"
echo "  прогоны не читают): каждую ночь порция одного канала — до 100 страниц ассетов (~40 минут),"
echo "  с 1:00 до 6:00. История канала пройдена — берётся следующий. Итог ночи — в суточной сводке бота."
echo "  Каналы через запятую по порядку; «-» — выключить. (Порция и часы: NIGHT_BACKFILL_PAGES, NIGHT_BACKFILL_HOURS в .env.)"
if grep -qE '^NIGHT_BACKFILL_CHANNELS=' "$ENV_FILE" 2>/dev/null; then
    NIGHT_DEFAULT=$(env_get NIGHT_BACKFILL_CHANNELS); NIGHT_DEFAULT=${NIGHT_DEFAULT:--}
else
    # Впервые: два канала-ленты, в истории которых больше всего бесплатного (разбор 09–10.10, Plan.md «Очередь каналов»).
    NIGHT_DEFAULT="UnityAssetsTools,UnityAssetsNewFree"
fi
while :; do
    ask NIGHT_CHANNELS "Каналы для ночного добора истории" "$NIGHT_DEFAULT"
    NIGHT_CHANNELS=$(printf '%s' "$NIGHT_CHANNELS" | tr -d ' ')
    case "$NIGHT_CHANNELS" in -|off|нет) NIGHT_CHANNELS="" ;; esac
    [[ "$NIGHT_CHANNELS" =~ ^[A-Za-z0-9_,]*$ ]] && break
    fail "Только имена каналов (латиница, цифры, _) через запятую, или «-»."
done
[ -n "$NIGHT_CHANNELS" ] && ok "Ночной добор истории: $NIGHT_CHANNELS" || ok "Ночной добор истории выключен."
bold "6. Fab (fab.com, аккаунт Epic Games): окно для входа"
echo "  Чтобы войти в Epic на сервере, программа показывает окно браузера в вашем браузере"
echo "  через SSH-туннель (порт слушает только localhost сервера). Пароль окна — чтобы им не"
echo "  мог воспользоваться никто, кто окажется на сервере. VNC читает 8 знаков."
FAB_VNC_PASSWORD=$(env_get FAB_VNC_PASSWORD)
if [ -z "$FAB_VNC_PASSWORD" ]; then
    FAB_VNC_PASSWORD=$(LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 8)
    ok "Пароль окна создан: $FAB_VNC_PASSWORD (сохраню в .env; понадобится при входе в окно)"
else
    ok "Пароль окна уже сохранён в .env."
fi
echo "  Когда вы вошли в Epic (команда в конце вывода), сервер может сам, после каждого прогона,"
echo "  забирать новые бесплатные ассеты Fab из каналов. Человека он зовёт только за входом и"
echo "  галочкой Cloudflare — не чаще раза в сутки. Раздачи −100 % пока забираете вы."
# Программа понимает on/true/1/yes/да в любом регистре — не сбрасываем рабочую настройку в off только из-за написания.
case "$(env_get FAB | tr '[:upper:]' '[:lower:]')" in
    on|true|1|yes|y|да|д|ДА|Д) FAB_DEFAULT=Y ;;
    *) FAB_DEFAULT=N ;;
esac
if ask_yes "Забирать ассеты Fab на сервере сам (FAB=on)?" "$FAB_DEFAULT"; then FAB_VALUE=on; else FAB_VALUE=off; fi
ok "Fab на сервере: $FAB_VALUE"

bold "6б. Окно Fab с телефона, без туннеля"
echo "  Когда Fab нужна галочка, окно можно открыть прямо с телефона по ссылке из бота — без SSH."
echo "  Ссылка появляется, только когда вы сами нажмёте «📱 С телефона» (или /phone), живёт 15 минут,"
echo "  после первого входа открывается только на вашем устройстве; пароль окна тоже нужен (он в ссылке)."
echo "  1 — выключено: окно только через SSH-туннель (tools/fab-window, Termius)."
echo "  2 — HTTP по адресу сервера: просто, но без шифрования — только для галочки, пароль Epic там не вводить."
echo "  3 — HTTPS через ваш домен: настоящий сертификат Let's Encrypt (запись A домена → этот сервер)."
PHONE_HOST_OLD=$(env_get FAB_PHONE_HOST)
case "$(env_get FAB_PHONE_LINK)" in http) PHONE_DEFAULT=2 ;; https) PHONE_DEFAULT=3 ;; *) PHONE_DEFAULT=1 ;; esac
while :; do
    ask PHONE_CHOICE "Выберите 1, 2 или 3" "$PHONE_DEFAULT"
    case "$PHONE_CHOICE" in 1|2|3) break ;; *) fail "Нужно 1, 2 или 3." ;; esac
done
PHONE_LINK=off
PHONE_HOST=""
PHONE_CERT_LIVE=""
PHONE_CERT_ARCHIVE=""
PHONE_PORT=$(env_get FAB_PHONE_PORT)
PHONE_PORT=${PHONE_PORT:-7443}
if [ "$PHONE_CHOICE" != 1 ]; then
    PUBLIC_IP=$(curl -s -m 6 https://api.ipify.org 2>/dev/null || true)
    while :; do
        ask PHONE_PORT "Порт для ссылки (откроется наружу)" "$PHONE_PORT"
        [[ "$PHONE_PORT" =~ ^[0-9]+$ ]] && [ "$PHONE_PORT" -ge 1024 ] && [ "$PHONE_PORT" -le 65535 ] && break
        fail "Номер порта от 1024 до 65535."
    done
    # Порт не должен быть занят чужим (наш контейнер при повторной настройке держит его сам).
    if ss -ltnH "( sport = :$PHONE_PORT )" 2>/dev/null | grep -q . &&
       ! docker ps --format '{{.Names}} {{.Ports}}' 2>/dev/null | grep -q "^unity-assets-downloader .*:$PHONE_PORT->"; then
        warn "Порт $PHONE_PORT уже занят другой программой — служба не запустится. Выберите другой (./deploy.sh снова)."
    fi
fi
if [ "$PHONE_CHOICE" = 2 ]; then
    DEF_HOST=$PHONE_HOST_OLD
    [[ "$DEF_HOST" =~ ^[0-9.]+$ ]] || DEF_HOST=$PUBLIC_IP
    while :; do
        ask PHONE_HOST "Адрес сервера для ссылки" "$DEF_HOST"
        [[ "$PHONE_HOST" =~ ^[A-Za-z0-9.-]+$ ]] && break
        fail "Нужен адрес (например 203.0.113.5) или имя сервера."
    done
    PHONE_LINK=http
    ok "Окно с телефона: http://$PHONE_HOST:$PHONE_PORT/… — без шифрования, только для галочки."
elif [ "$PHONE_CHOICE" = 3 ]; then
    DEF_HOST=$PHONE_HOST_OLD
    [[ "$DEF_HOST" =~ ^[0-9.]+$ ]] && DEF_HOST=""
    while :; do
        ask PHONE_HOST "Домен для окна (например fab.example.ru)" "$DEF_HOST"
        [[ "$PHONE_HOST" =~ ^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)+$ ]] && break
        fail "Нужен домен, например fab.example.ru."
    done
    RESOLVED=$(getent ahostsv4 "$PHONE_HOST" 2>/dev/null | awk 'NR==1{print $1}')
    if [ -z "$RESOLVED" ]; then
        warn "Домен $PHONE_HOST не находится в DNS — добавьте запись A → ${PUBLIC_IP:-адрес сервера} и запустите ./deploy.sh снова."
    elif [ -n "$PUBLIC_IP" ] && [ "$RESOLVED" != "$PUBLIC_IP" ]; then
        warn "Домен $PHONE_HOST указывает на $RESOLVED, а у сервера адрес $PUBLIC_IP — поправьте запись A."
    else
        ok "Домен $PHONE_HOST указывает на этот сервер ($RESOLVED)."
    fi
    CERT_DIR="$LE_DIR/live/$PHONE_HOST"
    if ! $SUDO test -f "$CERT_DIR/fullchain.pem" && [ -n "$RESOLVED" ]; then
        issue_phone_cert "$PHONE_HOST"
    fi
    if $SUDO test -f "$CERT_DIR/fullchain.pem"; then
        PHONE_LINK=https
        # Подключаем в контейнер только папки этого домена и только когда сертификат уже есть: иначе Docker создал бы
        # пустую папку live/<домен>, и certbot потом отказался бы её заполнять.
        PHONE_CERT_LIVE=$CERT_DIR
        PHONE_CERT_ARCHIVE="$LE_DIR/archive/$PHONE_HOST"
        CERT_END=$($SUDO openssl x509 -enddate -noout -in "$CERT_DIR/fullchain.pem" 2>/dev/null | cut -d= -f2)
        ok "Сертификат для $PHONE_HOST есть${CERT_END:+ (до $CERT_END)}; продлевать будет certbot сервера."
        systemctl is-active certbot.timer >/dev/null 2>&1 ||
            warn "certbot.timer не активен — сертификат сам не продлится (за 2 недели до конца программа напишет в бот)."
        ok "Окно с телефона: https://$PHONE_HOST:$PHONE_PORT/…"
    else
        warn "Сертификата для $PHONE_HOST нет — окно с телефона пока выключено. Будет сертификат — запустите ./deploy.sh снова."
    fi
fi
if [ "$PHONE_LINK" = off ]; then
    # Домен или адрес не забываем: включите снова — он будет предложен по умолчанию.
    [ -z "$PHONE_HOST" ] && PHONE_HOST=$PHONE_HOST_OLD
    ok "Окно с телефона без туннеля: выключено."
fi

DEFAULT_TZ=$(env_get TZ)
[ -z "$DEFAULT_TZ" ] && DEFAULT_TZ=$(timedatectl show -p Timezone --value 2>/dev/null || cat /etc/timezone 2>/dev/null || true)
ask TZ_VALUE "Часовой пояс для логов" "${DEFAULT_TZ:-Europe/Moscow}"
DEFAULT_PROFILE=$(env_get PROFILE)
while :; do
    ask PROFILE "Имя профиля (папка в data/profiles)" "${DEFAULT_PROFILE:-server}"
    [[ "$PROFILE" =~ ^[A-Za-z0-9_-]+$ ]] && break
    fail "Только латинские буквы, цифры, _ и -."
done

# ---------------------------------------------------------------- запись

umask 077
{
    echo "# Создано ./deploy.sh $(date '+%Y-%m-%d %H:%M'). Поменять — запустите ./deploy.sh снова."
    echo "UNITY_EMAIL=$(env_quote "$EMAIL")"
    echo "UNITY_PASSWORD=$(env_quote "$PASSWORD")"
    echo "TELEGRAM_BOT_TOKEN=$(env_quote "$TOKEN")"
    echo "TELEGRAM_CHAT_ID=$(env_quote "$CHAT_ID")"
    echo "TELEGRAM_CHANNELS=$(env_quote "$CHANNELS_VALUE")"
    echo "WATCH_INTERVAL=$INTERVAL"
    echo "NIGHT_BACKFILL_CHANNELS=$(env_quote "$NIGHT_CHANNELS")"
    echo "SOURCES=$SOURCES_VALUE"
    echo "TOP_FREE_EVERY=$TOP_FREE_EVERY"
    echo "FAB=$FAB_VALUE"
    echo "FAB_VNC_PASSWORD=$(env_quote "$FAB_VNC_PASSWORD")"
    echo "FAB_PHONE_LINK=$PHONE_LINK"
    echo "FAB_PHONE_HOST=$(env_quote "$PHONE_HOST")"
    echo "FAB_PHONE_PORT=$PHONE_PORT"
    # Наружу порт открывается только при включённой ссылке; иначе он слушает лишь localhost сервера.
    if [ "$PHONE_LINK" = off ]; then echo "FAB_PHONE_BIND=127.0.0.1"; else echo "FAB_PHONE_BIND=0.0.0.0"; fi
    echo "FAB_PHONE_CERT_LIVE=$(env_quote "$PHONE_CERT_LIVE")"
    echo "FAB_PHONE_CERT_ARCHIVE=$(env_quote "$PHONE_CERT_ARCHIVE")"
    echo "PROFILE=$PROFILE"
    echo "TZ=$TZ_VALUE"
    # Настройки, о которых deploy.sh не спрашивает (TELEGRAM_PROXY, BOT_DIGEST и другие,
    # вписанные руками), переносим как есть — раньше они молча пропадали.
    if [ -f "$ENV_FILE" ]; then
        grep -vE '^(#|[[:space:]]*$|(UNITY_EMAIL|UNITY_PASSWORD|TELEGRAM_BOT_TOKEN|TELEGRAM_CHAT_ID|TELEGRAM_CHANNELS|WATCH_INTERVAL|NIGHT_BACKFILL_CHANNELS|SOURCES|TOP_FREE_EVERY|FAB|FAB_VNC_PASSWORD|FAB_PHONE_LINK|FAB_PHONE_HOST|FAB_PHONE_PORT|FAB_PHONE_BIND|FAB_PHONE_CERT_LIVE|FAB_PHONE_CERT_ARCHIVE|PROFILE|TZ)=)' "$ENV_FILE" || true
    fi
} > "$ENV_FILE.tmp" && mv "$ENV_FILE.tmp" "$ENV_FILE"
chmod 600 "$ENV_FILE"
mkdir -p data logs

bold "Настройки сохранены в $ENV_FILE (читать его можете только вы)."

# ---------------------------------------------------------------- Docker

if [ "${DEPLOY_CONFIG_ONLY:-0}" = 1 ]; then
    exit 0
fi

bold "Docker"
DOCKER=(docker)
if ! command -v docker >/dev/null 2>&1; then
    fail "Docker не установлен."
    if ask_yes "Установить Docker сейчас (официальный скрипт get.docker.com, нужен sudo)?" Y; then
        curl -fsSL https://get.docker.com | sudo sh || { fail "Установить не получилось."; exit 1; }
    else
        echo "  Установите Docker и запустите ./deploy.sh снова: https://docs.docker.com/engine/install/"
        exit 1
    fi
fi
if ! docker info >/dev/null 2>&1; then
    if sudo docker info >/dev/null 2>&1; then
        DOCKER=(sudo docker)
        warn "Docker работает только через sudo — буду вызывать его так."
    else
        fail "Docker не запущен. Попробуйте: sudo systemctl start docker"
        exit 1
    fi
fi
if "${DOCKER[@]}" compose version >/dev/null 2>&1; then
    DC=("${DOCKER[@]}" compose)
elif command -v docker-compose >/dev/null 2>&1; then
    DC=(docker-compose)
    [ "${DOCKER[0]}" = sudo ] && DC=(sudo docker-compose)
else
    fail "Нет Docker Compose. Установите пакет docker-compose-plugin и запустите ./deploy.sh снова."
    exit 1
fi
# Номер коммита — в строку «Версия» лога и сводки прогона (иначе не понять, какой код работает на сервере).
BUILD_ARGS=(--build-arg "GIT_COMMIT=$(git rev-parse --short HEAD 2>/dev/null)")
ok "Docker: $("${DC[@]}" version --short 2>/dev/null || echo есть)"

APP=("${DC[@]}" run --rm unity-assets --profile "$PROFILE" --quiet)
SERVICE_RUNNING=0
[ -n "$("${DC[@]}" ps -q unity-assets 2>/dev/null)" ] && SERVICE_RUNNING=1

# step "Название" команда... — показывает вывод и останавливает скрипт при неудаче
step() {
    local title=$1; shift
    bold "$title"
    if "$@"; then
        ok "Готово."
        return 0
    fi
    fail "Шаг не прошёл. Причина — в выводе выше и в папке ./logs."
    return 1
}

stop_here() {
    echo
    echo "  Исправьте причину и запустите ./deploy.sh снова — ответы сохранены, Enter их оставит."
    exit 1
}

if [ "$FIRST_TIME" = 1 ]; then CHECK_DEFAULT=Y; else CHECK_DEFAULT=N; fi
if ask_yes "Собрать и проверить всё по шагам?" "$CHECK_DEFAULT"; then
    step "Шаг 1/5. Сборка образа (первый раз — несколько минут)" "${DC[@]}" build "${BUILD_ARGS[@]}" || stop_here
    step "Шаг 2/5. Браузер в контейнере открывает страницу входа Unity" "${APP[@]}" --check-login-page || stop_here

    # Каналы раньше бота: если Telegram заблокирован, здесь программа найдёт и запомнит
    # прокси, и бот пойдёт через него же.
    if ! step "Шаг 3/5. Доступ к Telegram-каналам" "${APP[@]}" --check-telegram; then
        ask_yes "Продолжить без Telegram (ассеты из каналов не будут находиться)?" N || stop_here
    fi

    if [ -n "$TOKEN" ]; then
        if ! step "Шаг 4/5. Сообщение от бота" "${APP[@]}" --notify-test; then
            ask_yes "Продолжить без сообщений от бота?" Y || stop_here
        fi
    else
        bold "Шаг 4/5. Бот не настроен — пропускаю."
    fi

    echo
    echo "  Если Unity попросит код подтверждения — введите его прямо здесь."
    step "Шаг 5/5. Вход в Unity" "${APP[@]}" --login || stop_here

    bold "Первый прогон"
    echo "  Программа прочитает последние посты каналов, добавит бесплатные ассеты"
    echo "  и выкупит по промокоду те, что станут бесплатными (только при итоге 0)."
    if ask_yes "Сначала проверочный прогон, без изменений аккаунта?" N; then
        step "Проверочный прогон" "${APP[@]}" --sources "$SOURCES_VALUE" --tg-only-new --dry-run || stop_here
    fi
    if ask_yes "Сделать первый настоящий прогон сейчас?" Y; then
        step "Первый прогон" "${APP[@]}" --sources "$SOURCES_VALUE" --tg-only-new || stop_here
    fi
elif [ "$SERVICE_RUNNING" = 0 ]; then
    step "Сборка образа" "${DC[@]}" build "${BUILD_ARGS[@]}" || stop_here
fi

bold "Служба"
# Порт окна для Fab публикуется всегда (только на 127.0.0.1): если он занят чужим, служба целиком не запустится.
FAB_PORT=$(env_get FAB_VNC_PORT); FAB_PORT=${FAB_PORT:-6085}
if [ "$SERVICE_RUNNING" = 0 ] && command -v ss >/dev/null 2>&1 && ss -ltn 2>/dev/null | grep -qE "127\.0\.0\.1:${FAB_PORT}[[:space:]]"; then
    warn "Порт $FAB_PORT на 127.0.0.1 уже занят — служба не запустится. Задайте другой: строка FAB_VNC_PORT=… в .env, потом ./deploy.sh снова."
fi
if [ "$SERVICE_RUNNING" = 1 ]; then
    QUESTION="Перезапустить службу с новыми настройками?"
else
    QUESTION="Запустить службу? Дальше она сама проверяет каналы раз в $INTERVAL."
fi
if ask_yes "$QUESTION" Y; then
    # Сборка — отдельно и с номером коммита (иначе в строке «Версия» службы его нет), потом запуск.
    { "${DC[@]}" build "${BUILD_ARGS[@]}" && "${DC[@]}" up -d; } || {
        fail "Служба не запустилась. Если в выводе выше «port is already allocated» — порт $FAB_PORT занят: задайте другой (FAB_VNC_PORT=… в .env) и запустите ./deploy.sh снова."
        exit 1
    }
    ok "Служба работает."
fi

cat <<EOF

  Что дальше:
    логи прогонов:      ${DC[*]} logs -f        (и файлы в ./logs)
    поменять настройки: ./deploy.sh
    обновить программу: git pull && ./deploy.sh
    остановить:         ${DC[*]} down

  Окно Fab (галочка, вход в Epic): проще всего — /fab в боте или кнопка «Открыть окно» в его сообщении;
  с телефона без туннеля — /phone или «📱 С телефона» (если включено в разделе 6б).
  Или вручную — один раз, между прогонами (в логе «Следующий прогон»):
    ${DC[*]} exec -it unity-assets dotnet UnityAssetsDownloader.dll --logs-dir /app/logs --data-dir /app/data --profile $PROFILE --fab-login
    затем на своём компьютере: tools/fab-window (Windows: tools\\fab-window.cmd) — туннель и браузер одним кликом,
    или вручную: ssh -L 127.0.0.1:${FAB_PORT}:127.0.0.1:${FAB_PORT} <ваш сервер> и ссылка из сообщения бота
    (или http://127.0.0.1:${FAB_PORT}/vnc.html?autoconnect=true&resize=scale, пароль окна — FAB_VNC_PASSWORD в .env)

  Раздачу Fab «Limited-Time Free» (−100 %) сервер сам не берёт: на оформлении Epic просит капчу. Два способа:
    на ПК или Deck:     ./run.sh → F (с домашнего адреса капча проходит надёжнее)
    в окне сервера:     ${DC[*]} exec -it unity-assets dotnet UnityAssetsDownloader.dll --logs-dir /app/logs --data-dir /app/data --profile $PROFILE --fab-giveaway
EOF
