#!/usr/bin/env bash
# Собрать всё для разбора того, что служба делала на сервере, в один архив.
#
#   ./collect-logs.sh          — за последние 14 дней
#   ./collect-logs.sh 30       — за последние 30 дней
#
# Ничего не меняет и службу не перезапускает. Пароли, токен бота и сессию Unity
# в архив не кладёт: из .env — только несекретные настройки, из data/ — только
# память профиля (что уже добавлено, где остановилось чтение каналов).
#
# Запускать ДО ./deploy.sh: пересоздание контейнера стирает его вывод
# (docker compose logs), а там видно то, чего нет в файлах, — например, падения.

set -u
cd "$(dirname "$(readlink -f "$0")")" || exit 1

DAYS=${1:-14}
case "$DAYS" in ''|*[!0-9]*) echo "Число дней — целое число, например: ./collect-logs.sh 30"; exit 1 ;; esac

STAMP=$(date +%Y%m%d-%H%M)
NAME="uad-diag-$STAMP"
WORK=$(mktemp -d)
OUT="$WORK/$NAME"
mkdir -p "$OUT"
trap 'rm -rf "$WORK"' EXIT

SINCE=$(date -d "-$DAYS days" +%Y-%m-%d 2>/dev/null || date +%Y-%m-%d)
echo "Собираю за $DAYS дн. (с $SINCE)…"

# Docker: как в deploy.sh — без sudo, если можно.
DOCKER=(docker)
if ! docker info >/dev/null 2>&1 && sudo -n docker info >/dev/null 2>&1; then
    DOCKER=(sudo docker)
fi
if "${DOCKER[@]}" compose version >/dev/null 2>&1; then
    DC=("${DOCKER[@]}" compose)
elif command -v docker-compose >/dev/null 2>&1; then
    DC=(docker-compose)
else
    DC=()
fi

# ---------------------------------------------------------------- состояние
{
    echo "== Когда собрано";      date
    echo; echo "== Код (git)";    git log -1 --format='%h %ci %s' 2>&1; git status --short 2>&1 | head -20
    echo; echo "== Сервер";       uptime; echo; df -h . 2>&1; echo; free -h 2>&1

    echo; echo "== Настройки из .env (секреты — только есть/нет)"
    if [ -f .env ]; then
        while IFS= read -r line; do
            case "$line" in ''|\#*) continue ;; esac
            key=${line%%=*}; val=${line#*=}
            case "$key" in
                *PASSWORD*|*TOKEN*|*EMAIL*|*PROXY*)
                    [ -n "${val//\"/}" ] && echo "$key=<задано>" || echo "$key=<пусто>" ;;
                *) echo "$line" ;;
            esac
        done < .env
    else
        echo ".env нет"
    fi

    if [ ${#DC[@]} -gt 0 ]; then
        echo; echo "== docker compose ps"; "${DC[@]}" ps -a 2>&1
        echo; echo "== Контейнер: запуск, перезапуски, код выхода, нехватка памяти"
        "${DOCKER[@]}" inspect unity-assets-downloader --format \
            'Status={{.State.Status}} StartedAt={{.State.StartedAt}} FinishedAt={{.State.FinishedAt}} ExitCode={{.State.ExitCode}} OOMKilled={{.State.OOMKilled}} RestartCount={{.RestartCount}} Image={{.Image}}' 2>&1
        echo; echo "== Образ"
        "${DOCKER[@]}" image inspect unity-assets-downloader --format 'Created={{.Created}}' 2>&1
        echo; echo "== Процессы в контейнере"
        "${DC[@]}" top 2>&1 | head -40
    else
        echo; echo "Docker Compose не найден"
    fi
} > "$OUT/state.txt" 2>&1

# Вывод службы: здесь и расписание прогонов, и падения, которых нет в файлах логов.
if [ ${#DC[@]} -gt 0 ]; then
    "${DC[@]}" logs --no-color --timestamps --since "$SINCE" > "$OUT/compose.log" 2>&1
fi
if command -v journalctl >/dev/null 2>&1; then
    journalctl -u docker --since "$SINCE" --no-pager 2>/dev/null | tail -300 > "$OUT/journal-docker.log"
fi

# ---------------------------------------------------------------- файлы логов
mkdir -p "$OUT/logs"
if [ -d logs ]; then
    cp -p logs/errors.log logs/telegram_promocodes.log "$OUT/logs/" 2>/dev/null
    # Тексты постов: весь файл большой, хватит хвоста.
    [ -f logs/telegram_posts_raw.log ] && tail -c 3000000 logs/telegram_posts_raw.log > "$OUT/logs/telegram_posts_raw.tail.log"
    # Логи прогонов и скриншоты ошибок за период.
    find logs -maxdepth 1 -type f -newermt "$SINCE" \
        \( -name 'run-log-*' -o -name 'run-report-*' -o -name '*.png' -o -name '*.html' \) \
        -exec cp -p {} "$OUT/logs/" \;
    ls -la logs > "$OUT/logs-listing.txt" 2>&1
fi

# ---------------------------------------------------------------- память профиля
if [ -d data ]; then
    mkdir -p "$OUT/data"
    cp -p data/profiles.json data/telegram_bot_route.txt "$OUT/data/" 2>/dev/null
    for dir in data/profiles/*/; do
        [ -d "$dir" ] || continue
        p=$(basename "$dir")
        mkdir -p "$OUT/data/profiles/$p"
        for f in telegram_state.json owned_assets.txt deprecated_assets.txt rejected_promocodes.txt telegram_proxy.txt; do
            [ -f "$dir$f" ] && cp -p "$dir$f" "$OUT/data/profiles/$p/"
        done
        ls -la "$dir" > "$OUT/data/profiles/$p/listing.txt" 2>&1
    done
fi

ARCHIVE="$HOME/$NAME.tar.gz"
tar -czf "$ARCHIVE" -C "$WORK" "$NAME" || { echo "Не удалось упаковать архив"; exit 1; }

echo
echo "Готово: $ARCHIVE ($(du -h "$ARCHIVE" | cut -f1))"
echo "Скачать к себе:  scp $(hostname):$ARCHIVE ."
