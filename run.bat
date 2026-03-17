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
echo UnityAssetsDownloader - универсальный запуск / universal launcher
echo ==============================================
echo.
echo 1^) [По умолчанию / Default] Простой запуск ^(видимый браузер^) / Simple run ^(visible browser^)
echo 2^) Только логин и сохранение cookies / Login only and save cookies
echo 3^) Dry-run ^(проверка без добавления в аккаунт^) / Dry-run ^(check without adding to account^)
echo 4^) Расширенные источники / Extended sources
echo 5^) Дополнительный файл ссылок ^(extra_asset_urls.example.txt^) / Extra URL file
echo 6^) Выход / Exit
echo.
set "opt="
set /p "opt=Выберите режим / Choose mode [Enter = 1]: "
if not defined opt set "opt=1"

if "%opt%"=="1" goto run_default
if "%opt%"=="2" goto run_login
if "%opt%"=="3" goto run_dry
if "%opt%"=="4" goto run_extended
if "%opt%"=="5" goto run_extra_file
if "%opt%"=="6" goto end

echo.
echo Некорректный выбор / Invalid choice: %opt%
pause
goto menu

:run_default
echo.
echo Запуск: простой режим по умолчанию... / Running: default simple mode...
dotnet run --project "%PROJECT%" -- --headless false --verbose
goto after_run

:run_login
echo.
echo Запуск: только логин и сохранение cookies... / Running: login only and save cookies...
dotnet run --project "%PROJECT%" -- --login --headless false --verbose
goto after_run

:run_dry
echo.
echo Запуск: dry-run... / Running: dry-run...
dotnet run --project "%PROJECT%" -- --dry-run --headless false --verbose
goto after_run

:run_extended
echo.
echo Запуск: расширенные источники... / Running: extended sources...
dotnet run --project "%PROJECT%" -- --headless false --extended-sources --verbose
goto after_run

:run_extra_file
echo.
echo Запуск: дополнительный файл ссылок... / Running: extra URL file...
dotnet run --project "%PROJECT%" -- --headless false --extra-source-file "extra_asset_urls.example.txt" --verbose
goto after_run

:after_run
echo.
pause
goto menu

:end
echo Выход / Exit.
exit /b 0
