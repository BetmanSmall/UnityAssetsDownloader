#!/usr/bin/env bash
# Меню запуска для Linux. То же, что run.bat на Windows.
# Запуск: ./run.sh   (если не запускается: chmod +x run.sh)

cd "$(dirname "$(readlink -f "$0")")" || exit 1
ROOT="$(pwd)"

PROJECT="UnityAssetsDownloader/UnityAssetsDownloader.csproj"
LOGS="$ROOT/logs"
DATA="$ROOT/data"
COMMON=(--logs-dir "$LOGS" --data-dir "$DATA" --quiet)

pause() {
    read -r -p "Нажмите Enter, чтобы продолжить..." _ || true
}

# dotnet часто ставят скриптом dotnet-install в ~/.dotnet, не добавляя в PATH.
if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$DOTNET_ROOT:$PATH"
fi

if ! command -v dotnet >/dev/null 2>&1; then
    echo "[ОШИБКА] dotnet SDK не найден."
    echo "Установите .NET 8 SDK: https://dotnet.microsoft.com/download"
    echo "Без прав администратора (например, на Steam Deck):"
    echo "  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0"
    echo
    pause
    exit 1
fi

# Программа собрана под .NET 8. Если стоит только более новая версия,
# разрешаем запуск на ней — иначе dotnet откажется стартовать.
if ! dotnet --list-runtimes 2>/dev/null | grep -q "^Microsoft.NETCore.App 8\."; then
    export DOTNET_ROLL_FORWARD=Major
fi

PROFILE_ARG=()
PROFILE_NAME="${USER:-$(id -un)}"
CHROME_ARG=()
CHROME_MODE="своя папка браузера (личный Chrome не трогаем)"
TGPROXY_ARG=()
TGPROXY_MODE="подбирается сам, если понадобится"
LIMIT_ARG=()

echo "Сборка проекта..."
if ! dotnet build "$PROJECT" --nologo -v q; then
    echo
    echo "[ОШИБКА] Проект не собрался. Текст ошибок выше."
    echo
    pause
    exit 1
fi

ask_limit() {
    local limit batch
    read -r -p "Сколько новых ассетов добавить за запуск? [Enter = без лимита]: " limit
    LIMIT_ARG=()
    if [ -n "$limit" ]; then
        LIMIT_ARG=(--max-add-attempts "$limit")
    fi
    echo "Ассеты из Telegram берутся пачками: собрали пачку, проверили в магазине, собираем следующую."
    read -r -p "Сколько ассетов в одной пачке из Telegram? [Enter = 30, 0 = всё разом]: " batch
    LIMIT_ARG+=(--tg-batch-size "${batch:-30}")
}

# Запускает программу. Ctrl+C останавливает только её, а не меню:
# программа успевает сохранить память профиля, и меню открывается снова.
run_app() {
    trap ':' INT
    dotnet run --project "$PROJECT" --no-build -- "$@"
    RUN_CODE=$?
    trap - INT
}

after_run() {
    echo
    if [ "$RUN_CODE" != "0" ]; then
        echo "[ОШИБКА] Программа завершилась с кодом $RUN_CODE."
    else
        echo "Готово."
    fi
    echo
    echo "============================================================"
    echo " ЕСЛИ ЧТО-ТО ПОШЛО НЕ ТАК - ПРИШЛИТЕ ЭТОТ ОДИН ФАЙЛ:"
    echo " $LOGS/ПРИШЛИТЕ-ЭТОТ-ФАЙЛ.log"
    echo "============================================================"
    echo
    echo "Остальное нужно редко:"
    echo "  $LOGS/run-log-*.log             - полный лог запуска"
    echo "  $LOGS/run-report-*.json         - что и с каким статусом обработано"
    echo "  $LOGS/telegram_posts_raw.log    - тексты всех постов Telegram"
    echo "  $LOGS/telegram_promocodes.log   - найденные промокоды"
    echo "  $LOGS/*.png / *.html            - скриншоты страниц при ошибках"
    echo "  Пункт L в меню - упаковать всю папку logs в один архив"
    echo
    echo "Профиль: $PROFILE_NAME. Сменить - пункт P в меню."
    echo
    pause
}

run_all() {
    ask_limit
    echo
    echo "Запуск: основные источники..."
    run_app "${COMMON[@]}" "${PROFILE_ARG[@]}" "${CHROME_ARG[@]}" "${TGPROXY_ARG[@]}" \
        --headless false --extra-source-file "extra_asset_urls.example.txt" "${LIMIT_ARG[@]}"
    after_run
}

run_top_free() {
    ask_limit
    echo
    echo "Запуск: только топ бесплатные..."
    run_app "${COMMON[@]}" "${PROFILE_ARG[@]}" "${CHROME_ARG[@]}" "${TGPROXY_ARG[@]}" \
        --headless false --source "https://assetstore.unity.com/top-assets/top-free" "${LIMIT_ARG[@]}"
    after_run
}

run_china_list() {
    ask_limit
    echo
    echo "Запуск: только из free_list_GreaterChinaUnityAssetArchiveLinks.txt..."
    run_app "${COMMON[@]}" "${PROFILE_ARG[@]}" "${CHROME_ARG[@]}" "${TGPROXY_ARG[@]}" \
        --headless false --no-defaults \
        --extra-source-file "GreaterChinaUnityAssetArchive/free_list_GreaterChinaUnityAssetArchiveLinks.txt" \
        "${LIMIT_ARG[@]}"
    after_run
}

run_extra_list() {
    ask_limit
    echo
    echo "Запуск: только из extra_asset_urls.example.txt..."
    run_app "${COMMON[@]}" "${PROFILE_ARG[@]}" "${CHROME_ARG[@]}" "${TGPROXY_ARG[@]}" \
        --headless false --no-defaults --extra-source-file "extra_asset_urls.example.txt" "${LIMIT_ARG[@]}"
    after_run
}

run_extended() {
    ask_limit
    echo
    echo "Запуск: расширенные списки поиска..."
    run_app "${COMMON[@]}" "${PROFILE_ARG[@]}" "${CHROME_ARG[@]}" "${TGPROXY_ARG[@]}" \
        --headless false --source "https://assetstore.unity.com/" --extended-sources "${LIMIT_ARG[@]}"
    after_run
}

run_login() {
    echo
    echo "Запуск: только логин и сохранение cookies."
    echo "Откроется окно браузера. Войдите в аккаунт Unity и дождитесь подтверждения."
    echo
    echo "ВАЖНО: вход через Google в этом окне не сработает - Google не пускает"
    echo "браузеры под управлением программ. Входите по email и паролю Unity."
    echo "Нет пароля? Задайте его на https://id.unity.com (Забыли пароль)."
    run_app "${COMMON[@]}" "${PROFILE_ARG[@]}" "${CHROME_ARG[@]}" "${TGPROXY_ARG[@]}" \
        --login --headless false
    after_run
}

run_dry() {
    ask_limit
    echo
    echo "Запуск: dry-run (аккаунт не меняется)..."
    run_app "${COMMON[@]}" "${PROFILE_ARG[@]}" "${CHROME_ARG[@]}" "${TGPROXY_ARG[@]}" \
        --dry-run --headless false "${LIMIT_ARG[@]}"
    after_run
}

run_telegram() {
    if [ ! -f "telegram_sources.txt" ]; then
        echo
        echo "[ОШИБКА] Файл telegram_sources.txt не найден рядом с run.sh"
        echo "Создайте его: по одному имени канала на строку."
        echo
        pause
        return
    fi
    echo
    echo "Каналы из telegram_sources.txt:"
    cat "telegram_sources.txt"
    echo
    ask_limit
    echo
    echo "Запуск: Telegram каналы..."
    run_app "${COMMON[@]}" "${PROFILE_ARG[@]}" "${CHROME_ARG[@]}" "${TGPROXY_ARG[@]}" \
        --headless false --no-defaults "${LIMIT_ARG[@]}"
    after_run
}

run_diag() {
    echo
    echo "Запуск: диагностика. Пишутся максимально подробные логи."
    echo "Аккаунт НЕ меняется (--dry-run)."
    run_app --logs-dir "$LOGS" --data-dir "$DATA" "${PROFILE_ARG[@]}" "${CHROME_ARG[@]}" \
        --verbose --trace-network --dry-run --headless false --no-defaults --max-visited-assets 5
    after_run
}

telegram_proxy() {
    local tgproxy
    echo
    echo "Telegram у многих провайдеров заблокирован. Помогает прокси."
    echo
    echo "НАСТРАИВАТЬ НИЧЕГО НЕ НУЖНО: если Telegram не открылся, программа сама"
    echo "найдёт рабочий прокси и запомнит его для следующих запусков."
    echo "Этот пункт нужен, только если хотите свой прокси или проверить связь."
    echo
    echo "Прокси используется ТОЛЬКО для Telegram, Unity ходит напрямую."
    echo "Через него проходят лишь открытые страницы каналов - ни входа, ни паролей."
    echo
    echo "Сейчас задан: $TGPROXY_MODE"
    echo
    echo "  Enter = ничего не менять, только проверить связь с Telegram"
    echo "  свой адрес, например socks5://127.0.0.1:1080"
    echo "  N = запретить автоподбор, ходить только напрямую"
    echo
    read -r -p "Ваш выбор: " tgproxy
    if [ "${tgproxy^^}" = "N" ]; then
        TGPROXY_ARG=(--tg-auto-proxy false)
        TGPROXY_MODE="только напрямую, без прокси"
    elif [ -n "$tgproxy" ]; then
        TGPROXY_ARG=(--tg-proxy "$tgproxy")
        TGPROXY_MODE="$tgproxy"
    else
        TGPROXY_ARG=()
        TGPROXY_MODE="подбирается сам, если понадобится"
    fi
    echo
    echo "Проверяем. Вход в Unity для этого не нужен."
    run_app "${COMMON[@]}" "${PROFILE_ARG[@]}" "${TGPROXY_ARG[@]}" --check-telegram --headless true
    after_run
}

toggle_chrome() {
    echo
    if [ ${#CHROME_ARG[@]} -gt 0 ]; then
        CHROME_ARG=()
        CHROME_MODE="своя папка браузера (личный Chrome не трогаем)"
        echo "Переключено: программа откроет свой браузер."
        echo "Вход в Unity запоминается между запусками, ваш Chrome не затрагивается."
    else
        CHROME_ARG=(--use-system-chrome-profile)
        CHROME_MODE="мой обычный Chrome (закройте все окна Chrome!)"
        echo "Переключено: программа откроет ВАШ обычный Chrome (~/.config/google-chrome)."
        echo
        echo "ВАЖНО: перед запуском закройте ВСЕ окна Chrome."
        echo "Иначе Chrome не отдаст свою папку и программа не запустится."
    fi
    echo
    pause
}

check_login() {
    echo
    echo "Проверка страницы входа Unity. Программа откроет её и посмотрит,"
    echo "на месте ли поле email и кнопка. Ничего не нажимает и никуда не отправляет."
    run_app "${COMMON[@]}" "${PROFILE_ARG[@]}" "${CHROME_ARG[@]}" "${TGPROXY_ARG[@]}" \
        --check-login-page --headless false
    after_run
}

choose_profile() {
    local user newprofile
    user="${USER:-$(id -un)}"
    echo
    echo "Профили на этом компьютере:"
    dotnet run --project "$PROJECT" --no-build -- --data-dir "$DATA" --list-profiles
    echo
    echo "Профиль - это отдельный аккаунт Unity со своей сессией."
    echo "По умолчанию используется имя пользователя Linux: $user"
    echo
    read -r -p "Имя профиля [Enter = $user]: " newprofile
    newprofile="${newprofile:-$user}"
    PROFILE_NAME="$newprofile"
    PROFILE_ARG=(--profile "$newprofile")
    echo
    echo "Выбран профиль: $PROFILE_NAME"
    echo
    pause
}

collect_logs() {
    local stamp archive
    echo
    if [ ! -d "$LOGS" ] || [ -z "$(ls -A "$LOGS" 2>/dev/null)" ]; then
        echo "Папка логов пуста: $LOGS"
        echo
        pause
        return
    fi
    stamp="$(date +%Y%m%d-%H%M%S)"
    if command -v zip >/dev/null 2>&1; then
        archive="$ROOT/logs-$stamp.zip"
        (cd "$LOGS" && zip -qr "$archive" .)
    else
        archive="$ROOT/logs-$stamp.tar.gz"
        tar -czf "$archive" -C "$LOGS" .
    fi
    if [ $? -ne 0 ]; then
        echo "[ОШИБКА] Не удалось создать архив."
    else
        echo "Архив с логами готов: $archive"
        echo "Пришлите этот файл для разбора ошибок."
    fi
    echo
    pause
}

while true; do
    echo
    echo "=============================================="
    echo " UnityAssetsDownloader - выбор режима"
    echo "=============================================="
    echo " Профиль аккаунта: $PROFILE_NAME"
    echo " Браузер: $CHROME_MODE"
    echo " Прокси для Telegram: $TGPROXY_MODE"
    echo " Окна: браузер на весь экран (деление экрана с консолью - только в Windows)"
    echo " Логи пишутся в: $LOGS"
    echo " Сессия входа: $DATA (файлы доступны только вам)"
    echo " Пароль Unity: config.json или UNITY_EMAIL / UNITY_PASSWORD"
    echo "=============================================="
    echo
    echo " 1) Основные источники (топ бесплатные + китайский архив + extra_urls)"
    echo " 2) Только топ бесплатные (Asset Store top-free)"
    echo " 3) Только free_list_GreaterChinaUnityAssetArchiveLinks.txt"
    echo " 4) Только extra_asset_urls.example.txt"
    echo " 5) Только расширенные списки поиска (extended_sources.txt)"
    echo " 6) Только логин и сохранение cookies  <== начните с этого"
    echo " 7) Dry-run (проверка без добавления в аккаунт)"
    echo " 8) Telegram каналы из telegram_sources.txt"
    echo " 9) Диагностика: Telegram + максимум логов (--trace-network)"
    echo " T) Проверить Telegram / задать свой прокси"
    echo " B) Переключить браузер: своя папка <-> мой обычный Chrome"
    echo " C) Проверить страницу входа Unity (быстро, ничего не меняет)"
    echo " P) Сменить профиль аккаунта (для второго аккаунта на этом компьютере)"
    echo " L) Собрать логи в архив для отправки"
    echo " 0) Выход"
    echo
    if ! read -r -p "Выберите режим [Enter = 6]: " opt; then
        echo
        break
    fi
    opt="${opt:-6}"

    case "${opt^^}" in
        1) run_all ;;
        2) run_top_free ;;
        3) run_china_list ;;
        4) run_extra_list ;;
        5) run_extended ;;
        6) run_login ;;
        7) run_dry ;;
        8) run_telegram ;;
        9) run_diag ;;
        T) telegram_proxy ;;
        B) toggle_chrome ;;
        C) check_login ;;
        P) choose_profile ;;
        L) collect_logs ;;
        0) break ;;
        *)
            echo
            echo "Некорректный выбор: $opt"
            pause
            ;;
    esac
done

echo "Выход."
exit 0
