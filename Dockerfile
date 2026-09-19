# UnityAssetsDownloader для сервера: периодически читает Telegram-каналы
# и добавляет новые ассеты. Запуск: docker compose up -d --build (см. docker-compose.yml).

# ---- Сборка ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY UnityAssetsDownloader/UnityAssetsDownloader.csproj UnityAssetsDownloader/
RUN dotnet restore UnityAssetsDownloader/UnityAssetsDownloader.csproj
COPY UnityAssetsDownloader/ UnityAssetsDownloader/
# Номер коммита для строки версии в логе: git внутри сборки нет, поэтому передаётся снаружи.
ARG GIT_COMMIT=""
RUN dotnet publish UnityAssetsDownloader/UnityAssetsDownloader.csproj -c Release -o /out --no-restore \
    -p:SourceRevisionId=${GIT_COMMIT}

# ---- Запуск ----
FROM mcr.microsoft.com/dotnet/runtime:8.0
# Chromium из Debian сам тянет все библиотеки, нужные браузеру без экрана.
# Шрифты — чтобы скриншоты страниц при ошибках были читаемыми. tzdata — время в логах по TZ.
RUN apt-get update \
    && apt-get install -y --no-install-recommends chromium fonts-liberation fonts-noto-core tzdata ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /out/ ./
# Списки источников. telegram_sources.txt обычно подключается снаружи (см. docker-compose.yml),
# чтобы каналы можно было менять без пересборки.
COPY telegram_sources.txt extra_asset_urls.example.txt extended_sources.txt ./
# Китайский архив бесплатных ассетов: нужен при SOURCES=all. Берём только список ссылок,
# html-выгрузки на 3,5 МБ в образе не нужны.
COPY GreaterChinaUnityAssetArchive/free_list_GreaterChinaUnityAssetArchiveLinks.txt GreaterChinaUnityAssetArchive/

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

VOLUME ["/app/data", "/app/logs"]
ENTRYPOINT ["dotnet", "UnityAssetsDownloader.dll", "--logs-dir", "/app/logs", "--data-dir", "/app/data"]
CMD ["--watch", "--watch-interval", "24h", "--sources", "telegram", "--quiet"]
