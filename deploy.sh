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
OLD_CHANNELS=$(env_get TELEGRAM_CHANNELS)
if [ -z "$OLD_CHANNELS" ] && [ -f telegram_sources.txt ]; then
    OLD_CHANNELS=$(grep -vE '^\s*(#|//|$)' telegram_sources.txt | tr -d '\r' | xargs | tr ' ' ',')
fi
[ -n "$OLD_CHANNELS" ] && echo "  Сейчас: ${OLD_CHANNELS//,/, }"
echo "  Введите каналы: имя, @имя или ссылку t.me/… По одному на строку или через запятую."
echo "  Пустая строка — закончить$([ -n "$OLD_CHANNELS" ] && echo " (сразу пустая — оставить как есть)")."
CHANNELS=()
while :; do
    read -r -p "  канал: " LINE
    [ -z "$LINE" ] && break
    for raw in ${LINE//,/ }; do
        c=$(normalize_channel "$raw")
        if [[ "$c" =~ ^[A-Za-z0-9_]{4,}$ ]]; then
            CHANNELS+=("$c")
        else
            fail "«$raw» не похоже на канал Telegram, пропускаю."
        fi
    done
done
if [ ${#CHANNELS[@]} -eq 0 ]; then
    CHANNELS_VALUE=$OLD_CHANNELS
else
    CHANNELS_VALUE=$(printf '%s\n' "${CHANNELS[@]}" | awk '!seen[$0]++' | paste -sd, -)
fi
if [ -z "$CHANNELS_VALUE" ]; then
    warn "Каналы не заданы — программе нечего будет читать. Запустите ./deploy.sh ещё раз, чтобы добавить."
else
    ok "Каналы: ${CHANNELS_VALUE//,/, }"
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

bold "5. Расписание"
while :; do
    ask INTERVAL "Как часто проверять каналы (30m, 6h, 1d)" "$(env_get WATCH_INTERVAL || true)"
    INTERVAL=${INTERVAL:-24h}
    [[ "$INTERVAL" =~ ^([0-9]+[dhms])+$ || "$INTERVAL" =~ ^[0-9]+$ ]] && break
    fail "Не понял. Примеры: 30m — полчаса, 6h — 6 часов, 1d — раз в сутки."
done
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
    echo "SOURCES=$SOURCES_VALUE"
    echo "PROFILE=$PROFILE"
    echo "TZ=$TZ_VALUE"
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
    step "Шаг 1/5. Сборка образа (первый раз — несколько минут)" "${DC[@]}" build || stop_here
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
    step "Сборка образа" "${DC[@]}" build || stop_here
fi

bold "Служба"
if [ "$SERVICE_RUNNING" = 1 ]; then
    QUESTION="Перезапустить службу с новыми настройками?"
else
    QUESTION="Запустить службу? Дальше она сама проверяет каналы раз в $INTERVAL."
fi
if ask_yes "$QUESTION" Y; then
    "${DC[@]}" up -d --build || { fail "Служба не запустилась."; exit 1; }
    ok "Служба работает."
fi

cat <<EOF

  Что дальше:
    логи прогонов:      ${DC[*]} logs -f        (и файлы в ./logs)
    поменять настройки: ./deploy.sh
    обновить программу: git pull && ./deploy.sh
    остановить:         ${DC[*]} down
EOF
