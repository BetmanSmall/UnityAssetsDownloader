@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul

cd /d "%~dp0"

set "PROJECT=UnityAssetsDownloader\UnityAssetsDownloader.csproj"
set "LOGS=%~dp0logs"
set "DATA=%~dp0data"
set "COMMON=--logs-dir "%LOGS%" --data-dir "%DATA%" --quiet"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ОШИБКА] dotnet SDK не найден в PATH.
    echo Установите .NET 8 SDK: https://dotnet.microsoft.com/download
    echo.
    pause
    exit /b 1
)

set "PROFILE_ARG="
set "PROFILE_NAME=%USERNAME%"
set "CHROME_ARG="
set "CHROME_MODE=своя папка браузера (личный Chrome не трогаем)"
set "TGPROXY_ARG="
set "TGPROXY_MODE=не задан (Telegram напрямую)"

echo Сборка проекта...
dotnet build "%PROJECT%" --nologo -v q
if errorlevel 1 (
    echo.
    echo [ОШИБКА] Проект не собрался. Текст ошибок выше.
    echo.
    pause
    exit /b 1
)

:menu
echo.
echo ==============================================
echo  UnityAssetsDownloader - выбор режима
echo ==============================================
echo  Профиль аккаунта: %PROFILE_NAME%
echo  Браузер: %CHROME_MODE%
echo  Прокси для Telegram: %TGPROXY_MODE%
echo  Окна: консоль слева, браузер справа
echo  Логи пишутся в: %LOGS%
echo  Учётные данные: %DATA% + Диспетчер учётных данных Windows
echo ==============================================
echo.
echo  1^) Основные источники (топ бесплатные + китайский архив + extra_urls^)
echo  2^) Только топ бесплатные (Asset Store top-free^)
echo  3^) Только free_list_GreaterChinaUnityAssetArchiveLinks.txt
echo  4^) Только extra_asset_urls.example.txt
echo  5^) Только расширенные списки поиска (extended_sources.txt^)
echo  6^) Только логин и сохранение cookies  ^<== начните с этого
echo  7^) Dry-run (проверка без добавления в аккаунт^)
echo  8^) Telegram каналы из telegram_sources.txt
echo  9^) Диагностика: Telegram + максимум логов (--trace-network^)
echo  T^) Проверить Telegram / задать прокси только для Telegram
echo  B^) Переключить браузер: своя папка ^<-^> мой обычный Chrome
echo  C^) Проверить страницу входа Unity (быстро, ничего не меняет^)
echo  P^) Сменить профиль аккаунта (для второго аккаунта на этом компьютере^)
echo  L^) Собрать логи в архив для отправки
echo  0^) Выход
echo.
set "opt="
set /p "opt=Выберите режим [Enter = 6]: "
if not defined opt set "opt=6"

if /i "%opt%"=="1" goto run_all
if /i "%opt%"=="2" goto run_top_free
if /i "%opt%"=="3" goto run_china_list
if /i "%opt%"=="4" goto run_extra_list
if /i "%opt%"=="5" goto run_extended
if /i "%opt%"=="6" goto run_login
if /i "%opt%"=="7" goto run_dry
if /i "%opt%"=="8" goto run_telegram
if /i "%opt%"=="9" goto run_diag
if /i "%opt%"=="T" goto telegram_proxy
if /i "%opt%"=="B" goto toggle_chrome
if /i "%opt%"=="C" goto check_login
if /i "%opt%"=="P" goto choose_profile
if /i "%opt%"=="L" goto collect_logs
if /i "%opt%"=="0" goto end

echo.
echo Некорректный выбор: %opt%
pause
goto menu

:ask_limit
set "limit="
set /p "limit=Сколько новых ассетов добавить за запуск? [Enter = без лимита]: "
set "LIMIT_ARG="
if defined limit set "LIMIT_ARG=--max-add-attempts %limit%"
goto :eof

:run_all
call :ask_limit
echo.
echo Запуск: основные источники...
dotnet run --project "%PROJECT%" --no-build -- %COMMON% %PROFILE_ARG% %CHROME_ARG% %TGPROXY_ARG% --headless false --extra-source-file "extra_asset_urls.example.txt" %LIMIT_ARG%
goto after_run

:run_top_free
call :ask_limit
echo.
echo Запуск: только топ бесплатные...
dotnet run --project "%PROJECT%" --no-build -- %COMMON% %PROFILE_ARG% %CHROME_ARG% %TGPROXY_ARG% --headless false --source "https://assetstore.unity.com/top-assets/top-free" %LIMIT_ARG%
goto after_run

:run_china_list
call :ask_limit
echo.
echo Запуск: только из free_list_GreaterChinaUnityAssetArchiveLinks.txt...
dotnet run --project "%PROJECT%" --no-build -- %COMMON% %PROFILE_ARG% %CHROME_ARG% %TGPROXY_ARG% --headless false --no-defaults --extra-source-file "GreaterChinaUnityAssetArchive/free_list_GreaterChinaUnityAssetArchiveLinks.txt" %LIMIT_ARG%
goto after_run

:run_extra_list
call :ask_limit
echo.
echo Запуск: только из extra_asset_urls.example.txt...
dotnet run --project "%PROJECT%" --no-build -- %COMMON% %PROFILE_ARG% %CHROME_ARG% %TGPROXY_ARG% --headless false --no-defaults --extra-source-file "extra_asset_urls.example.txt" %LIMIT_ARG%
goto after_run

:run_extended
call :ask_limit
echo.
echo Запуск: расширенные списки поиска...
dotnet run --project "%PROJECT%" --no-build -- %COMMON% %PROFILE_ARG% %CHROME_ARG% %TGPROXY_ARG% --headless false --source "https://assetstore.unity.com/" --extended-sources %LIMIT_ARG%
goto after_run

:run_login
echo.
echo Запуск: только логин и сохранение cookies.
echo Откроется окно браузера. Войдите в аккаунт Unity и дождитесь подтверждения.
echo.
echo ВАЖНО: вход через Google в этом окне не сработает - Google не пускает
echo браузеры под управлением программ. Входите по email и паролю Unity.
echo Нет пароля? Задайте его на https://id.unity.com (Забыли пароль^).
dotnet run --project "%PROJECT%" --no-build -- %COMMON% %PROFILE_ARG% %CHROME_ARG% %TGPROXY_ARG% --login --headless false
goto after_run

:run_dry
call :ask_limit
echo.
echo Запуск: dry-run (аккаунт не меняется^)...
dotnet run --project "%PROJECT%" --no-build -- %COMMON% %PROFILE_ARG% %CHROME_ARG% %TGPROXY_ARG% --dry-run --headless false %LIMIT_ARG%
goto after_run

:run_telegram
if not exist "telegram_sources.txt" (
    echo.
    echo [ОШИБКА] Файл telegram_sources.txt не найден рядом с run.bat
    echo Создайте его: по одному имени канала на строку.
    echo.
    pause
    goto menu
)
echo.
echo Каналы из telegram_sources.txt:
type "telegram_sources.txt"
echo.
call :ask_limit
echo.
echo Запуск: Telegram каналы...
dotnet run --project "%PROJECT%" --no-build -- %COMMON% %PROFILE_ARG% %CHROME_ARG% %TGPROXY_ARG% --headless false --no-defaults %LIMIT_ARG%
goto after_run

:run_diag
echo.
echo Запуск: диагностика. Пишутся максимально подробные логи.
echo Аккаунт НЕ меняется (--dry-run^).
dotnet run --project "%PROJECT%" --no-build -- --logs-dir "%LOGS%" --data-dir "%DATA%" %PROFILE_ARG% %CHROME_ARG% --verbose --trace-network --dry-run --headless false --no-defaults --max-visited-assets 5
goto after_run

:telegram_proxy
echo.
echo Telegram у многих провайдеров заблокирован. Помогает прокси.
echo Он будет использоваться ТОЛЬКО для Telegram, Unity пойдёт напрямую.
echo Через прокси проходят лишь открытые страницы каналов - ни входа, ни паролей.
echo.
echo Сейчас задан: %TGPROXY_MODE%
echo.
echo   A = автоподбор. Программа сама скачает список бесплатных прокси,
echo       найдёт рабочий и запомнит его. Занимает 1-3 минуты в первый раз.
echo   свой адрес, например socks5://127.0.0.1:1080
echo   Enter = убрать прокси, ходить напрямую
echo.
set "tgproxy="
set /p "tgproxy=Ваш выбор: "
if /i "%tgproxy%"=="A" (
    set "TGPROXY_ARG=--tg-auto-proxy"
    set "TGPROXY_MODE=автоподбор из общего списка"
) else if defined tgproxy (
    set "TGPROXY_ARG=--tg-proxy "%tgproxy%""
    set "TGPROXY_MODE=%tgproxy%"
) else (
    set "TGPROXY_ARG="
    set "TGPROXY_MODE=не задан (Telegram напрямую)"
)
echo.
echo Проверяем. Вход в Unity для этого не нужен.
dotnet run --project "%PROJECT%" --no-build -- %COMMON% %PROFILE_ARG% %TGPROXY_ARG% --check-telegram --headless true
goto after_run

:toggle_chrome
echo.
if defined CHROME_ARG (
    set "CHROME_ARG="
    set "CHROME_MODE=своя папка браузера (личный Chrome не трогаем)"
    echo Переключено: программа откроет свой браузер.
    echo Вход в Unity запоминается между запусками, ваш Chrome не затрагивается.
) else (
    set "CHROME_ARG=--use-system-chrome-profile"
    set "CHROME_MODE=мой обычный Chrome (закройте все окна Chrome!)"
    echo Переключено: программа откроет ВАШ обычный Chrome.
    echo.
    echo ВАЖНО: перед запуском закройте ВСЕ окна Chrome, включая значок у часов.
    echo Иначе Chrome не отдаст свою папку и программа не запустится.
)
echo.
pause
goto menu

:check_login
echo.
echo Проверка страницы входа Unity. Программа откроет её и посмотрит,
echo на месте ли поле email и кнопка. Ничего не нажимает и никуда не отправляет.
dotnet run --project "%PROJECT%" --no-build -- %COMMON% %PROFILE_ARG% %CHROME_ARG% %TGPROXY_ARG% --check-login-page --headless false
goto after_run

:choose_profile
echo.
echo Профили на этом компьютере:
dotnet run --project "%PROJECT%" --no-build -- --data-dir "%DATA%" --list-profiles
echo.
echo Профиль - это отдельный аккаунт Unity со своей сессией.
echo По умолчанию используется имя пользователя Windows: %USERNAME%
echo.
set "newprofile="
set /p "newprofile=Имя профиля [Enter = %USERNAME%]: "
if not defined newprofile set "newprofile=%USERNAME%"
set "PROFILE_NAME=%newprofile%"
set "PROFILE_ARG=--profile "%newprofile%""
echo.
echo Выбран профиль: %PROFILE_NAME%
echo.
pause
goto menu

:collect_logs
echo.
if not exist "%LOGS%" (
    echo Папка логов пуста: %LOGS%
    echo.
    pause
    goto menu
)
for /f "usebackq tokens=*" %%t in (`powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"`) do set "STAMP=%%t"
set "ZIP=%~dp0logs-%STAMP%.zip"
powershell -NoProfile -Command "Compress-Archive -Path '%LOGS%\*' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 (
    echo [ОШИБКА] Не удалось создать архив.
) else (
    echo Архив с логами готов: %ZIP%
    echo Пришлите этот файл для разбора ошибок.
)
echo.
pause
goto menu

:after_run
set "CODE=%ERRORLEVEL%"
echo.
if not "%CODE%"=="0" (
    echo [ОШИБКА] Программа завершилась с кодом %CODE%.
) else (
    echo Готово.
)
echo.
echo ============================================================
echo  ЕСЛИ ЧТО-ТО ПОШЛО НЕ ТАК - ПРИШЛИТЕ ЭТОТ ОДИН ФАЙЛ:
echo  %LOGS%\ПРИШЛИТЕ-ЭТОТ-ФАЙЛ.log
echo ============================================================
echo.
echo Остальное нужно редко:
echo   %LOGS%\run-log-*.log             - полный лог запуска
echo   %LOGS%\run-report-*.json         - что и с каким статусом обработано
echo   %LOGS%\telegram_posts_raw.log    - тексты всех постов Telegram
echo   %LOGS%\telegram_promocodes.log   - найденные промокоды
echo   %LOGS%\*.png / *.html            - скриншоты страниц при ошибках
echo   Пункт L в меню - упаковать всю папку logs в один архив
echo.
echo Профиль: %PROFILE_NAME%. Сменить - пункт P в меню.
echo Консоль больше не очищается - текст выше можно выделить и скопировать.
echo.
pause
goto menu

:end
echo Выход.
exit /b 0
