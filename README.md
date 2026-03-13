# UnityAssetsDownloader

Консольное приложение на **C# (.NET 8)** с использованием **PuppeteerSharp** для автоматизации работы с Unity Asset Store:

- авторизация в Unity (ручная или автоматическая),
- сохранение и повторное использование cookies,
- сбор ассетов из внешних источников,
- проверка статуса ассета (бесплатный/платный, уже на аккаунте или нет),
- добавление бесплатных ассетов в аккаунт.

---

## 1. Возможности

Приложение выполняет следующий процесс:

1. Пытается загрузить сохранённые cookies и проверить активность сессии.
2. Если сессия невалидна — запускает SSO-вход через Asset Store.
3. Поддерживает:
   - **ручной вход** в открытом браузере,
   - **автовход** (если переданы email/password).
4. Собирает ссылки на ассеты из источников (по умолчанию):
   - https://vanquish3r.github.io/greater-china-unity-assets/
   - https://assetstore.unity.com/top-assets/top-free
5. Для каждого ассета:
   - определяет, бесплатный ли он,
   - проверяет, есть ли уже в аккаунте,
   - если бесплатный и ещё не добавлен — пытается нажать Add to My Assets.
6. Формирует лог и JSON-отчёт о результате обработки.

---

## 2. Требования

- Windows / Linux / macOS (в проекте сейчас проверялось на Windows)
- **.NET SDK 8.0+**
- Интернет-доступ к Unity Asset Store / Unity Login
- Unity-аккаунт

> При первом запуске PuppeteerSharp скачивает Chromium автоматически.

---

## 3. Установка и сборка

Из корня репозитория:

```powershell
dotnet restore UnityAssetsDownloader.sln
dotnet build UnityAssetsDownloader.sln
```

Запуск делается через проект:

```powershell
dotnet run --project UnityAssetsDownloader/UnityAssetsDownloader.csproj -- [параметры]
```

---

## 4. Быстрый старт

### 4.1 Только вход и сохранение cookies (ручной)

```powershell
dotnet run --project UnityAssetsDownloader/UnityAssetsDownloader.csproj -- --login --headless false --verbose
```

Что произойдёт:
- откроется окно браузера,
- приложение перейдёт на Unity SSO,
- вы входите вручную,
- скрипт автоматически дождётся подтверждения авторизации,
- cookies сохранятся в `data/unity_cookies.json`.

### 4.2 Полный запуск обработки ассетов

```powershell
dotnet run --project UnityAssetsDownloader/UnityAssetsDownloader.csproj -- --headless false --verbose
```

---

## 5. Параметры CLI

| Параметр | Описание |
|---|---|
| `--login` | Только авторизация и сохранение cookies, без обработки ассетов |
| `--dry-run` | Проверка без нажатия кнопки добавления (без изменений аккаунта) |
| `--headless true/false` | Режим браузера без UI или с UI |
| `--verbose` | Подробные логи |
| `--trace-network` | Сетевые логи (включает verbose) |
| `--log-file <path>` | Путь к файлу лога |
| `--unity-email <email>` | Email для автовхода |
| `--unity-password <password>` | Пароль для автовхода |
| `--delay-ms <int>` | Пауза между обработкой ассетов (мс), по умолчанию 1200 |
| `--nav-timeout-ms <int>` | Таймаут навигации (мс), по умолчанию 120000 |
| `--auth-timeout-ms <int>` | Таймаут ожидания авторизации (мс), по умолчанию 300000 |
| `--max-add-attempts <int>` | Лимит обработки бесплатных ассетов (включая `AlreadyOwned`). После достижения лимита проход останавливается |
| `--source <url>` | Источник ссылок на ассеты (можно указать несколько раз) |

---

## 6. Примеры запуска

### 6.1 Безопасная проверка (dry-run)

```powershell
dotnet run --project UnityAssetsDownloader/UnityAssetsDownloader.csproj -- --dry-run --headless false --verbose
```

### 6.2 Автовход через переменные окружения (рекомендуется вместо пароля в CLI)

```powershell
$env:UNITY_EMAIL="your_email@example.com"
$env:UNITY_PASSWORD="your_password"
dotnet run --project UnityAssetsDownloader/UnityAssetsDownloader.csproj -- --login --headless false --verbose
```

### 6.3 Обработка с собственными источниками

```powershell
dotnet run --project UnityAssetsDownloader/UnityAssetsDownloader.csproj -- --headless false --source "https://vanquish3r.github.io/greater-china-unity-assets/" --source "https://assetstore.unity.com/top-assets/top-free"
```

### 6.4 Увеличенные таймауты

```powershell
dotnet run --project UnityAssetsDownloader/UnityAssetsDownloader.csproj -- --headless false --nav-timeout-ms 180000 --auth-timeout-ms 600000 --verbose
```

### 6.5 Прогон только top-free, в видимом браузере, с лимитом 25

```powershell
dotnet run --project UnityAssetsDownloader/UnityAssetsDownloader.csproj -- --headless false --source "https://assetstore.unity.com/top-assets/top-free" --max-add-attempts 25 --verbose --trace-network
```

---

## 7. Где лежат файлы после запуска

Каталоги создаются рядом с исполняемым приложением:

- `data/unity_cookies.json` — сохранённые cookies,
- `logs/run-log-*.log` — текстовый лог запуска,
- `logs/run-report-*.json` — итоговый отчёт по ассетам,
- `logs/*.png` — скриншоты при ошибках.

Пример статусов в отчёте:

- `Added`
- `AlreadyOwned`
- `PaidSkipped`
- `WouldAddInDryRun`
- `UnknownAfterClick`
- `Failed`

---

## 8. Рекомендованный сценарий работы

1. Выполните вход и сохраните cookies:
   - `--login --headless false`
2. Прогоните `--dry-run`, чтобы проверить распознавание ассетов.
3. Если всё корректно — запускайте без `--dry-run`.
4. Для аккуратного старта используйте `--max-add-attempts 25`.
4. Периодически обновляйте cookies через `--login`.

---

## 9. Типовые проблемы

### Проблема: не подтверждается авторизация

Что попробовать:
- запускать с `--headless false` и `--verbose`,
- увеличить `--auth-timeout-ms`,
- заново пройти `--login`,
- удалить/обновить невалидные cookies (через новый вход).

### Проблема: кнопка добавления не найдена

Возможные причины:
- изменилась верстка страницы ассета,
- региональные/языковые отличия кнопки,
- ассет уже добавлен или недоступен.

Проверьте скриншоты в `logs/` и лог-файл.

### Проблема: много ложных срабатываний free/paid

Рекомендуется сначала запускать с `--dry-run` и смотреть отчёт. При необходимости доработать селекторы/логику в `DetectStatusAsync`.

---

## 10. Важные замечания

- Unity Asset Store может менять HTML/поведение без предупреждения — автоматизацию нужно периодически поддерживать.
- Учитывайте ограничения/правила использования сервиса Unity.
- Не храните реальные пароли в репозитории или в командной строке истории shell.

---

## 11. Команда для запуска по умолчанию

```powershell
dotnet run --project UnityAssetsDownloader/UnityAssetsDownloader.csproj -- --headless false --verbose
```
