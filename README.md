# UnityAssetsDownloader

Консольное приложение на **C# (.NET 8)** с использованием **PuppeteerSharp** для автоматизации работы с Unity Asset Store:

- авторизация в Unity (ручная или автоматическая),
- сохранение и повторное использование состояния сессии (cookies + localStorage),
- сбор ассетов из внешних источников,
- проверка статуса ассета (бесплатный/платный, уже на аккаунте или нет),
- добавление бесплатных ассетов в аккаунт.

---

## 1. Возможности

Приложение выполняет следующий процесс:

1. Пытается загрузить сохранённое состояние сессии (`cookies + localStorage`) и проверить активность сессии.
2. Если сессия невалидна — запускает SSO-вход через Asset Store.
   - Открывает `https://assetstore.unity.com/`,
   - нажимает меню профиля (`aria-label="Open user profile menu"`),
   - дожидается полной загрузки меню профиля,
   - выбирает `Sign In`,
   - и только затем переходит на `https://login.unity.com/en/sign-in` для ручного входа.
3. Поддерживает:
   - **ручной вход** в открытом браузере (включая методы авторизации через Google, Apple, Facebook и др.),
   - **автовход** (если переданы email/password).
4. Собирает ссылки на ассеты из источников.
   По умолчанию используются:
   - https://assetstore.unity.com/top-assets/top-free
   - все ссылки из файла `free_list_GreaterChinaUnityAssetArchiveLinks.txt`
   - ссылки из дополнительных файлов `extraSourceFiles` (из `config.json`) и/или `--extra-source-file`
   
   Расширенный набор источников включается отдельным флагом `--extended-sources`.
5. Для каждого ассета:
   - определяет, бесплатный ли он,
   - проверяет, есть ли уже в аккаунте,
   - если бесплатный и ещё не добавлен — пытается нажать Add to My Assets,
   - если появилось окно подтверждения с `Accept` — нажимает `Accept`,
   - проверяет успешность по признакам `Open in Unity` / `You purchased this item on ...`.
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

### 4.0 Универсальный запуск через `run.bat` (рекомендуется для Windows)

В корне проекта есть `run.bat` с интерактивным меню для легкого запуска. Доступны следующие варианты:

1. **[Все источники]** Топ бесплатные + Китайский архив + extra_urls + расширенные
2. **Только из топ бесплатных** (Asset Store top-free)
3. **Только из списка** `free_list_GreaterChinaUnityAssetArchiveLinks.txt`
4. **Только из списка** `extra_asset_urls.example.txt`
5. **Только логин и сохранение cookies**
6. **Dry-run** (проверка без добавления в аккаунт)

Запуск скрипта в консоли:

```bat
.\run.bat
```

Примечание для PowerShell: запуск из текущей папки выполняйте именно через `./` или `.\`.

### 4.1 Только вход и сохранение cookies (ручной)

```powershell
dotnet run --project UnityAssetsDownloader/UnityAssetsDownloader.csproj -- --login --headless false --verbose
```

Что произойдёт:
- откроется окно браузера,
- приложение перейдёт на Unity SSO,
- вы входите вручную,
- скрипт автоматически дождётся подтверждения авторизации,
- состояние сессии сохранится в `data/unity_session_state.json` (и дублирующий файл cookies — `data/unity_cookies.json`).

### 4.2 Полный запуск обработки ассетов

```powershell
dotnet run --project UnityAssetsDownloader/UnityAssetsDownloader.csproj -- --headless false --verbose
```

---

## 5. Параметры CLI

| Параметр | Описание |
|---|---|
| `--login` | Только авторизация и сохранение cookies, без обработки ассетов |
| `--config <path>` | Путь к JSON-конфигу (если не указан, приложение попробует `config.json` в корне рабочего каталога) |
| `--dry-run` | Проверка без нажатия кнопки добавления (без изменений аккаунта) |
| `--headless true/false` | Режим браузера без UI или с UI |
| `--verbose` | Подробные логи |
| `--trace-network` | Сетевые логи (включает verbose) |
| `--extended-sources` | Добавить расширенный список источников (страницы search + archive URL) к базовым источникам по умолчанию |
| `--log-file <path>` | Путь к файлу лога |
| `--unity-email <email>` | Email для автовхода |
| `--unity-password <password>` | Пароль для автовхода |
| `--delay-ms <int>` | Пауза между обработкой ассетов (мс), по умолчанию 1200 |
| `--nav-timeout-ms <int>` | Таймаут навигации (мс), по умолчанию 120000 |
| `--auth-timeout-ms <int>` | Таймаут ожидания авторизации (мс), по умолчанию 300000 |
| `--asset-ui-timeout-ms <int>` | Сколько ждать загрузки элементов на странице ассета (`Add to My Assets` / `Open in Unity` / `Sign in` / `Buy`), по умолчанию 30000 |
| `--max-add-attempts <int>` | Лимит обработки бесплатных ассетов (включая `AlreadyOwned`). После достижения лимита проход останавливается |
| `--max-visited-assets <int>` | Лимит по количеству посещённых страниц ассетов (защитный стоп при нестабильной детекции) |
| `--source <url>` | Явный источник ссылок на ассеты (можно указать несколько раз). Если указан хотя бы один `--source`, используются только эти источники |
| `--extra-source-file <path>` | Дополнительный файл со списком URL (по одному на строку). Можно указывать несколько раз; добавляется к базовым источникам или к `--source` |

---

## 6. Примеры запуска

### 6.1 Безопасная проверка (dry-run)

```powershell
dotnet run --project UnityAssetsDownloader/UnityAssetsDownloader.csproj -- --dry-run --headless false --verbose
```

### 6.0 Запуск через конфигурационный файл

1) Скопируйте шаблон:

```powershell
copy config.example.json config.json
```

2) Заполните `config.json` (при необходимости).

3) Запустите приложение:

```powershell
dotnet run --project UnityAssetsDownloader/UnityAssetsDownloader.csproj --
```

Либо укажите явный путь:

```powershell
dotnet run --project UnityAssetsDownloader/UnityAssetsDownloader.csproj -- --config "C:\Code\RiderProjects\UnityAssetsDownloader\config.json"
```

Приоритет значений:
1. CLI параметры
2. Переменные окружения (`UNITY_EMAIL`, `UNITY_PASSWORD`)
3. Конфиг (`config.json` / `--config`)
4. Встроенные значения по умолчанию

По источникам по умолчанию:
- базовый режим: `top-free` + `free_list_GreaterChinaUnityAssetArchiveLinks.txt`
- дополнительно можно подключить файлы через `extraSourceFiles` (config) и/или `--extra-source-file`
- расширенный режим: базовый + все расширенные источники (`"extendedSources": true` или `--extended-sources`)

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

### 6.4 Расширенные источники (дополнительно к базовым)

```powershell
dotnet run --project UnityAssetsDownloader/UnityAssetsDownloader.csproj -- --headless false --extended-sources --verbose
```

### 6.5 Увеличенные таймауты

```powershell
dotnet run --project UnityAssetsDownloader/UnityAssetsDownloader.csproj -- --headless false --nav-timeout-ms 180000 --auth-timeout-ms 600000 --verbose
```

### 6.6 Прогон только top-free, в видимом браузере, с лимитом 25

```powershell
dotnet run --project UnityAssetsDownloader/UnityAssetsDownloader.csproj -- --headless false --source "https://assetstore.unity.com/top-assets/top-free" --max-add-attempts 25 --verbose --trace-network
```

### 6.7 Дополнительные файлы ссылок

```powershell
dotnet run --project UnityAssetsDownloader/UnityAssetsDownloader.csproj -- --headless false --extra-source-file "free_list_GreaterChinaUnityAssetArchiveLinks.txt" --extra-source-file "extra_asset_urls.example.txt" --verbose
```

---

## 7. Где лежат файлы после запуска

Каталоги создаются рядом с исполняемым приложением:

- `data/unity_session_state.json` — полное состояние сессии (cookies + localStorage),
- `data/unity_cookies.json` — сохранённые cookies (резервная совместимость),
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

Дополнительно в отчёт пишется поле `PurchasedOnText` (если найден текст о дате покупки/добавления).

Также в отчёт пишется поле `DetectionSummary` — краткая сводка сигналов детектора (`free/owned/addBtn/openInUnity/...`) для диагностики.

---

## 8. Рекомендованный сценарий работы

1. Выполните вход и сохраните cookies:
   - `--login --headless false`
2. Прогоните `--dry-run`, чтобы проверить распознавание ассетов.
3. Если всё корректно — запускайте без `--dry-run`.
4. Для аккуратного старта используйте `--max-add-attempts 25`.
4. Периодически обновляйте состояние сессии через `--login`.

---

## 9. Типовые проблемы

### Проблема: не подтверждается авторизация

Что попробовать:
- запускать с `--headless false` и `--verbose`,
- увеличить `--auth-timeout-ms`,
- заново пройти `--login` (смело используйте вход через Google/Apple/и др. — бот не прервет эти способы входа),
- удалить/обновить невалидное состояние сессии (через новый вход).

### Проблема: кнопка добавления не найдена

Возможные причины:
- изменилась верстка страницы ассета,
- региональные/языковые отличия кнопки,
- ассет уже добавлен или недоступен.

Проверьте скриншоты в `logs/` и лог-файл.

### Проблема: требуется повторная авторизация во время обработки ассетов

Скрипт умеет переавторизовываться в процессе обработки. Если сессия действительно недействительна для операции добавления, он:
- предложит войти (или выполнит автовход, если заданы креды),
- сохранит обновлённое состояние сессии,
- продолжит задачу с текущего ассета.

Если раньше вы видели частые ложные переавторизации из-за `404` на внутренних API, это поведение исправлено: решение больше не опирается на `/api/carts` как единственный критерий авторизации.

Дополнительно: если на странице видна кнопка `Sign in with Unity`, скрипт теперь не считает сессию валидной и инициирует корректный вход.

В логах шаги авторизации помечаются как `AuthStep: ...` (например `open-home`, `open-profile-menu`, `click-sign-in`, `auth-confirmed`) для удобной диагностики.

Также добавлены повторные попытки `Evaluate` при транзитных ошибках навигации (`Execution context was destroyed`), чтобы авторизация не падала в момент редиректов.

### Проблема: много ложных срабатываний free/paid

Рекомендуется сначала запускать с `--dry-run` и смотреть отчёт. При необходимости доработать селекторы/логику в `DetectStatusAsync`.

Если страница ассета грузится медленно, увеличьте `--asset-ui-timeout-ms` (например до `45000`–`60000`), чтобы дождаться появления `Add to My Assets` или `Open in Unity` перед принятием решения.

---

## 10. Важные замечания

- Unity Asset Store может менять HTML/поведение без предупреждения — автоматизацию нужно периодически поддерживать.
- Учитывайте ограничения/правила использования сервиса Unity.
- Не храните реальные пароли в репозитории или в командной строке истории shell.
- `config.json` добавлен в `.gitignore`; используйте `config.example.json` как шаблон.

---

## 11. Команда для запуска по умолчанию

```powershell
dotnet run --project UnityAssetsDownloader/UnityAssetsDownloader.csproj -- --headless false --verbose
```
