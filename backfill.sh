#!/usr/bin/env bash
# Разовый прогон по ИСТОРИИ Telegram-каналов на сервере: забрать бесплатные ассеты из старых постов.
# Служба проверяет только новые посты; этот скрипт проходит канал с самого начала, пачками по 30 ассетов.
#
#   ./backfill.sh UnityAssetsTools                  — настоящий прогон по всей истории канала
#   ./backfill.sh UnityAssetsTools --dry-run        — репетиция: аккаунт не меняется, смотрит около 40 страниц
#   ./backfill.sh A B C                             — несколько каналов за один прогон
#   ./backfill.sh UnityAssetsTools --limit 50       — остановиться после 50 новых ассетов
#   ./backfill.sh --status                          — идёт ли прогон и как
#   ./backfill.sh --stop                            — остановить прогон (сделанное сохранится, службу вернёт скрипт)
#
# Что делает: останавливает службу (два браузера на одном аккаунте и в памяти 1 ГБ не нужны), запускает прогон
# в фоне (SSH можно закрыть), после прогона ВСЕГДА запускает службу обратно. Деньги не списываются никогда:
# оплата нажимается только при итоге 0 (как и у службы).
#
# Итог — короткая сводка: logs/last-run-summary.txt (и файлом в бот). Её и присылайте, полный лог не нужен.
# Если сервер перезагрузили посреди прогона, службу вернёт ./deploy.sh или docker compose up -d.

set -u
SELF=$(readlink -f "$0")
cd "$(dirname "$SELF")" || exit 1

bold() { printf '\n\033[1m%s\033[0m\n' "$*"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$*"; }
warn() { printf '  \033[33m!\033[0m %s\n' "$*"; }
fail() { printf '  \033[31m✗\033[0m %s\n' "$*"; }

ask_yes() {
    local ans def=${2:-Y} hint
    [ "$def" = Y ] && hint="Д/н" || hint="д/Н"
    read -r -p "  $1 [$hint]: " ans
    ans=${ans:-$def}
    case "$ans" in y|Y|yes|Yes|д|Д|да|Да|ДА) return 0 ;; *) return 1 ;; esac
}

# Имя канала из «@name», «t.me/name», «https://t.me/s/name» и т. п.
normalize_channel() {
    local c=$1
    c=${c#http://}; c=${c#https://}
    c=${c#t.me/}; c=${c#telegram.me/}; c=${c#s/}; c=${c#@}
    c=${c%%/*}; c=${c%%\?*}
    printf '%s' "$c"
}

env_get() {
    [ -f .env ] || return 0
    local line v
    line=$(grep -E "^$1=" .env | tail -n 1) || return 0
    v=${line#*=}
    case "$v" in
        \"*\") v=${v:1:${#v}-2}; v=$(printf '%s' "$v" | sed 's/\\\(.\)/\1/g') ;;
        \'*\') v=${v:1:${#v}-2} ;;
    esac
    printf '%s' "$v"
}

# ---------------------------------------------------------------- разбор аргументов

MODE=run          # run | status | stop | inner
CHANNELS=()
DRY=0
LIMIT=""
ASSUME_YES=0
STAMP=""

while [ $# -gt 0 ]; do
    case "$1" in
        --status)  MODE=status ;;
        --stop)    MODE=stop ;;
        --inner)   MODE=inner; shift; STAMP=${1:-} ;;   # служебный: так скрипт запускает сам себя в фоне
        --dry-run) DRY=1 ;;
        --limit)   shift; LIMIT=${1:-} ;;
        -y|--yes)  ASSUME_YES=1 ;;
        -h|--help) sed -n '2,19p' "$0"; exit 0 ;;
        -*)        fail "Не знаю флаг «$1». ./backfill.sh --help"; exit 1 ;;
        *)
            c=$(normalize_channel "$1")
            if [[ "$c" =~ ^[A-Za-z0-9_]{4,}$ ]]; then CHANNELS+=("$c"); else fail "«$1» не похоже на канал Telegram."; exit 1; fi
            ;;
    esac
    shift
done

case "$LIMIT" in ''|*[!0-9]*) [ -n "$LIMIT" ] && { fail "--limit — целое число, например --limit 50"; exit 1; } ;; esac

# ---------------------------------------------------------------- Docker

DOCKER=(docker)
if ! command -v docker >/dev/null 2>&1; then fail "Docker не установлен. Сначала ./deploy.sh"; exit 1; fi
if ! docker info >/dev/null 2>&1; then
    if sudo -n docker info >/dev/null 2>&1; then DOCKER=(sudo docker); else fail "Docker недоступен без sudo. Запустите: sudo ./backfill.sh …"; exit 1; fi
fi
if "${DOCKER[@]}" compose version >/dev/null 2>&1; then DC=("${DOCKER[@]}" compose)
elif command -v docker-compose >/dev/null 2>&1; then DC=(docker-compose); [ "${DOCKER[0]}" = sudo ] && DC=(sudo docker-compose)
else fail "Нет Docker Compose. Сначала ./deploy.sh"; exit 1; fi

RUN_NAME=unity-assets-backfill
PROFILE=$(env_get PROFILE); PROFILE=${PROFILE:-server}

# Имена контейнеров постоянные (container_name в docker-compose.yml).
is_running()  { [ "$("${DOCKER[@]}" inspect -f '{{.State.Running}}' "$RUN_NAME" 2>/dev/null)" = true ]; }
service_up()  { [ "$("${DOCKER[@]}" inspect -f '{{.State.Running}}' unity-assets-downloader 2>/dev/null)" = true ]; }

# ---------------------------------------------------------------- --status

if [ "$MODE" = status ]; then
    bold "Прогон по истории каналов"
    if is_running; then
        ok "Идёт (контейнер $RUN_NAME, запущен $("${DOCKER[@]}" inspect -f '{{.State.StartedAt}}' "$RUN_NAME" | cut -c1-19 | tr T ' '))."
    else
        echo "  Не идёт."
    fi
    if service_up; then echo "  Служба: работает."; else warn "Служба остановлена (во время прогона так и должно быть)."; fi

    LOG=$(ls -t logs/run-log-*.log 2>/dev/null | head -n 1)
    if [ -n "$LOG" ]; then
        echo
        echo "  Последний лог: $LOG ($(date -r "$LOG" '+%F %T'))"
        echo "  Добавлено новых в этом логе: $(grep -c '\[УСПЕХ\]' "$LOG")"
        echo "  Последние шаги:"
        grep -E '\[пачка [0-9]+:|Пачка №|ИТОГИ|Сводка прогона|\[Лимит\]' "$LOG" | tail -n 5 | cut -c1-200 | sed 's/^/    /'
    fi
    if [ -f logs/last-run-summary.txt ]; then
        echo
        echo "  Сводка последнего завершённого прогона ($(date -r logs/last-run-summary.txt '+%F %T')):"
        echo "    cat logs/last-run-summary.txt"
    fi
    exit 0
fi

# ---------------------------------------------------------------- --stop

if [ "$MODE" = stop ]; then
    if is_running; then
        "${DOCKER[@]}" stop -t 90 "$RUN_NAME" >/dev/null && ok "Прогон остановлен: сделанное сохранено. Службу вернёт сам скрипт через минуту."
    else
        echo "  Прогон не идёт."
    fi
    exit 0
fi

# ---------------------------------------------------------------- прогон в фоне (внутренняя часть)

if [ "$MODE" = inner ]; then
    LOGFILE="logs/backfill-$STAMP.log"
    RAW="logs/backfill-$STAMP.console"
    say() { printf '[%s] %s\n' "$(date '+%F %T')" "$*" >> "$LOGFILE"; }

    WAS_RUNNING=0
    service_up && WAS_RUNNING=1

    restore() {
        # Служба возвращается всегда: и после успеха, и после ошибки, и после --stop.
        if [ "$WAS_RUNNING" = 1 ] && ! service_up; then
            say "Возвращаю службу…"
            "${DC[@]}" start unity-assets >> "$LOGFILE" 2>&1 || "${DC[@]}" up -d >> "$LOGFILE" 2>&1
            service_up && say "Служба работает." || say "СЛУЖБА НЕ ЗАПУСТИЛАСЬ — выполните ./deploy.sh"
        fi
    }
    trap restore EXIT
    trap 'exit 143' TERM HUP INT   # сигнал → обычный выход, чтобы сработал restore

    ARGS=(--profile "$PROFILE" --sources telegram --tg-channels "$(IFS=,; echo "${CHANNELS[*]}")" --quiet)
    [ "$DRY" = 1 ] && ARGS+=(--dry-run --max-visited-assets 40)
    [ -n "$LIMIT" ] && ARGS+=(--max-add-attempts "$LIMIT")

    say "Начало. Каналы: ${CHANNELS[*]}. Режим: $([ "$DRY" = 1 ] && echo репетиция || echo настоящий)$([ -n "$LIMIT" ] && echo ", лимит $LIMIT новых")."
    if [ "$WAS_RUNNING" = 1 ]; then
        say "Останавливаю службу…"
        "${DC[@]}" stop -t 120 unity-assets >> "$LOGFILE" 2>&1
    fi
    "${DOCKER[@]}" rm -f "$RUN_NAME" >/dev/null 2>&1
    BEFORE=$(ls logs/run-summary-*.txt 2>/dev/null | sort | tail -n 1)

    # FAB=off: этап Fab (окно для человека) в этом прогоне не нужен.
    "${DC[@]}" run -T --rm --no-deps --name "$RUN_NAME" -e FAB=off unity-assets "${ARGS[@]}" > "$RAW" 2>&1
    RC=$?
    say "Программа завершилась с кодом $RC."
    # Вывод программы повторяет её лог в ./logs; держим только хвост — на случай падения до создания лога.
    tail -n 200 "$RAW" > "logs/backfill-$STAMP.tail.txt" 2>/dev/null
    rm -f "$RAW"

    SUMMARY=$(ls logs/run-summary-*.txt 2>/dev/null | sort | tail -n 1)
    if [ -n "$SUMMARY" ] && [ "$SUMMARY" != "$BEFORE" ]; then
        say "Сводка прогона: $SUMMARY"
        { echo; cat "$SUMMARY"; } >> "$LOGFILE"
    else
        say "Сводки нет (прогон остановили или он упал до её записи). Хвост вывода: logs/backfill-$STAMP.tail.txt, ошибки: logs/errors.log"
    fi
    exit "$RC"
fi

# ---------------------------------------------------------------- запуск

if [ ${#CHANNELS[@]} -eq 0 ]; then
    fail "Укажите канал: ./backfill.sh UnityAssetsTools   (подсказка: ./backfill.sh --help)"
    exit 1
fi
[ -f .env ] || { fail "Нет .env — сначала ./deploy.sh"; exit 1; }
if is_running; then fail "Прогон уже идёт: ./backfill.sh --status"; exit 1; fi

bold "Прогон по истории каналов"
echo "  Каналы:   ${CHANNELS[*]}"
echo "  Профиль:  $PROFILE"
if [ "$DRY" = 1 ]; then
    echo "  Режим:    репетиция (аккаунт не меняется, около 40 страниц) — проверить, что всё читается"
else
    echo "  Режим:    настоящий: бесплатные ассеты добавляются на аккаунт$([ -n "$LIMIT" ] && echo ", не больше $LIMIT новых")"
fi
if service_up; then
    echo "  Служба:   работает — будет остановлена на время прогона и запущена снова"
else
    echo "  Служба:   сейчас остановлена — останется как есть"
fi
echo "  Время:    от получаса (небольшой канал) до нескольких часов (сотни ассетов, по ~10 с на каждый)"
[ "$ASSUME_YES" = 1 ] || ask_yes "Начать?" Y || { echo "  Отменено."; exit 0; }

mkdir -p logs data
echo "  Проверяю образ…"
"${DC[@]}" build >/dev/null 2>&1 || { fail "Образ не собрался: ${DC[*]} build"; exit 1; }

STAMP=$(date +%Y%m%d-%H%M%S)
ARGS_PASS=(--inner "$STAMP")
[ "$DRY" = 1 ] && ARGS_PASS+=(--dry-run)
[ -n "$LIMIT" ] && ARGS_PASS+=(--limit "$LIMIT")
setsid nohup "$SELF" "${ARGS_PASS[@]}" "${CHANNELS[@]}" > /dev/null 2>&1 &

ok "Запущено в фоне (SSH можно закрыть)."
cat <<EOF

  Как идёт:       ./backfill.sh --status
  Остановить:     ./backfill.sh --stop
  Ход скрипта:    cat logs/backfill-$STAMP.log
  Живой лог:      tail -f \$(ls -t logs/run-log-*.log | head -n 1)
  Итог:           придёт файлом в бот и ляжет сюда: logs/last-run-summary.txt — его и присылайте
EOF
