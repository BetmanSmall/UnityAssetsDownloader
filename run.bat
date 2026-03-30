@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul

cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] dotnet SDK was not found in PATH.
    echo Install .NET 8 SDK: https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

set "PROJECT=UnityAssetsDownloader/UnityAssetsDownloader.csproj"

:menu
cls
echo ==============================================
echo UnityAssetsDownloader - выбор режима / Mode selection
echo ==============================================
echo.
echo 1^) [Основные источники] Топ бесплатные + Китайский архив + extra_urls (без расширенных^)
echo 2^) Только из топ бесплатных (Asset Store top-free^)
echo 3^) Только из списка free_list_GreaterChinaUnityAssetArchiveLinks.txt
echo 4^) Только из списка extra_asset_urls.example.txt
echo 5^) Только из расширенных списков поиска (extended_sources.txt^)
echo 6^) Только логин и сохранение cookies / Login only and save cookies
echo 7^) Dry-run ^(проверка без добавления в аккаунт^)
echo 8^) Выход / Exit
echo.
set "opt="
set /p "opt=Выберите режим / Choose mode [Enter = 1]: "
if not defined opt set "opt=1"

if "%opt%"=="1" goto run_all
if "%opt%"=="2" goto run_top_free
if "%opt%"=="3" goto run_china_list
if "%opt%"=="4" goto run_extra_list
if "%opt%"=="5" goto run_extended
if "%opt%"=="6" goto run_login
if "%opt%"=="7" goto run_dry
if "%opt%"=="8" goto end

echo.
echo Некорректный выбор / Invalid choice: %opt%
pause
goto menu

:run_all
echo.
echo Запуск: основные источники... / Running: main sources...
dotnet run --project "%PROJECT%" -- --headless false --extra-source-file "extra_asset_urls.example.txt" --verbose
goto after_run

:run_top_free
echo.
echo Запуск: только топ бесплатные... / Running: only top free...
dotnet run --project "%PROJECT%" -- --headless false --source "https://assetstore.unity.com/top-assets/top-free" --verbose
goto after_run

:run_china_list
echo.
echo Запуск: только из free_list_GreaterChinaUnityAssetArchiveLinks...
dotnet run --project "%PROJECT%" -- --headless false --no-defaults --extra-source-file "GreaterChinaUnityAssetArchive/free_list_GreaterChinaUnityAssetArchiveLinks.txt" --verbose
goto after_run

:run_extra_list
echo.
echo Запуск: только из extra_asset_urls.example.txt...
dotnet run --project "%PROJECT%" -- --headless false --no-defaults --extra-source-file "extra_asset_urls.example.txt" --verbose
goto after_run

:run_extended
echo.
echo Запуск: только расширенные списки поиска... / Running: extended sources...
dotnet run --project "%PROJECT%" -- --headless false --source "https://assetstore.unity.com/" --extended-sources --verbose
goto after_run

:run_login
echo.
echo Запуск: только логин и сохранение cookies... / Running: login only...
dotnet run --project "%PROJECT%" -- --login --headless false --verbose
goto after_run

:run_dry
echo.
echo Запуск: dry-run... / Running: dry-run...
dotnet run --project "%PROJECT%" -- --dry-run --headless false --verbose
goto after_run

:after_run
echo.
pause
goto menu

:end
echo Выход / Exit.
exit /b 0
