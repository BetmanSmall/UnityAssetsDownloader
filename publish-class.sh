#!/usr/bin/env bash
# Собирает UnityAssetsDownloader в один .exe для компьютерного класса: .NET на
# компьютерах учеников не нужен, run.bat тоже — меню открывается по двойному щелчку.
#
#   ./publish-class.sh               — Windows (win-x64)
#   ./publish-class.sh linux-x64     — Linux, для проверки на Steam Deck
#
# Результат: dist/UnityAssetsDownloader-<платформа>/ и рядом такой же .zip.
#
# Нужен доступ к NuGet (скачать среду .NET 8 для нужной платформы, один раз ~35 МБ).
# Если nuget.org недоступен, скачайте пакет Microsoft.NETCore.App.Runtime.<платформа>
# вручную в папку и укажите её: NUGET_SOURCE=/путь/к/папке ./publish-class.sh
# Папку копируют в общую (можно сетевую) папку класса, ученики запускают .exe оттуда.
# Вход, память ассетов и логи у каждого свои: в %LOCALAPPDATA%\UnityAssetsDownloader.

set -eu
cd "$(dirname "$(readlink -f "$0")")"

RID=${1:-win-x64}
PROJECT=UnityAssetsDownloader/UnityAssetsDownloader.csproj
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT")
NAME="UnityAssetsDownloader-$RID"
OUT="dist/$NAME"
BUILD="dist/.build-$RID"

command -v dotnet >/dev/null 2>&1 || PATH="$HOME/.dotnet:$PATH"

echo "Сборка $VERSION для $RID..."
rm -rf "$OUT" "$BUILD"
# Без обрезки (trim): PuppeteerSharp и разбор JSON работают через reflection.
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none \
    -p:DisableTransitiveFrameworkReferenceDownloads=true \
    -p:NuGetAudit=false \
    ${NUGET_SOURCE:+--source "$NUGET_SOURCE" --source https://api.nuget.org/v3/index.json} \
    -o "$BUILD" --nologo -v q

mkdir -p "$OUT/GreaterChinaUnityAssetArchive"
cp "$BUILD"/UnityAssetsDownloader* "$OUT/"
# Списки ссылок программа ищет рядом с собой.
cp telegram_sources.txt extended_sources.txt extra_asset_urls.example.txt asset_ai_tags.json "$OUT/"
cp GreaterChinaUnityAssetArchive/free_list_GreaterChinaUnityAssetArchiveLinks.txt "$OUT/GreaterChinaUnityAssetArchive/"

# Инструкция — с переносами Windows, чтобы Блокнот показал её нормально.
sed 's/$/\r/' > "$OUT/README.txt" <<EOF
UnityAssetsDownloader $VERSION — бесплатные ассеты Unity на ваш аккаунт

ЗАПУСК
Дважды щёлкните UnityAssetsDownloader.exe. Откроется меню.
Устанавливать ничего не нужно.

ПЕРВЫЙ РАЗ
1. Пункт 6 — вход. Откроется окно браузера, войдите по email и паролю Unity.
   Вход через Google в этом окне НЕ сработает. Нет пароля — задайте его на
   https://id.unity.com («Забыли пароль?»).
2. Пункт 7 — проверочный прогон: аккаунт не меняется, видно, что было бы сделано.
3. Пункт 1 (всё сразу) или 8 (только Telegram-каналы) — настоящий запуск.
   Первый раз на вопрос о лимите ответьте 3.

У КАЖДОГО УЧЕНИКА СВОЁ
Вход, память уже добавленных ассетов и логи хранятся в личной папке
пользователя Windows: %LOCALAPPDATA%\\UnityAssetsDownloader
Поэтому на одном компьютере под разными учётными записями Windows у каждого
свой аккаунт Unity. Второй аккаунт под той же записью — пункт P в меню.

БРАУЗЕР
Используется установленный Google Chrome, если его нет — Microsoft Edge.
Если нет ни того, ни другого, при первом запуске скачается свой (~150 МБ).
Ваш обычный браузер программа не трогает.

КАТАЛОГ АССЕТОВ
Пункт K — список всех ассетов вашего аккаунта: страница catalog.html с картинками,
поиском и фильтрами. Лежит в %LOCALAPPDATA%\\UnityAssetsDownloader\\data\\profiles\\...\\catalog

ЧТО-ТО ПОШЛО НЕ ТАК
Пункт L в меню — логи одним архивом на рабочий стол. Пришлите этот архив.

ДЕНЬГИ
Программа никогда не платит: платный ассет без промокода пропускается,
по промокоду оформляется только при итоговой цене ровно 0.
EOF

rm -f "dist/$NAME.zip"
# Python, а не zip: zip на Steam Deck падал на русских именах файлов.
python3 -c "import shutil; shutil.make_archive('dist/$NAME', 'zip', 'dist', '$NAME')"
rm -rf "$BUILD"

echo
echo "Готово: $OUT"
ls -la "$OUT"
echo "Архив: dist/$NAME.zip ($(du -h "dist/$NAME.zip" | cut -f1))"
