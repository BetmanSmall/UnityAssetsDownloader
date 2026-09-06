# Telegram Sources File + лимит новых добавлений

## Что хотим

Программа читает `telegram_sources.txt` → парсит посты из каждого канала → добавляет ассеты (бесплатные + платные с промокодом) на аккаунт Unity. Останавливается когда добавлено N **новых** ассетов (уже имеющиеся в аккаунте не считаются). Каждый пост полностью логируется для последующей правки парсера.

---

## Текущее состояние (что уже есть)

| Что | Статус |
|-----|--------|
| `TelegramSourceParser.cs` — парсит посты, извлекает URL + промокод из одного поста | ✅ Работает |
| `AssetPromocodes` dict — map URL→promoCode | ✅ Работает |
| `MaxAddAttempts` (флаг `--max-add-attempts N`) | ✅ Работает, но считает по `status.IsFree` (цена ≤ $0), а не по факту успешного добавления |
| `telegram_sources.txt` файл | ❌ **Не читается**, каналы передаются только через `--tg-channels` или `config.json` |
| Лог текстов постов → `telegram_posts_raw.log` | ✅ Пишется при `--verbose`, но текст каждого поста только в DEBUG |
| `AlreadyOwned` НЕ считается в лимит | ✅ Корректно — `CountsTowardsAddLimit = status.IsFree` |

---

## Что нужно изменить

### Изменение 1: Чтение `telegram_sources.txt`

**Файл:** `Program.cs` (CliOptions.Parse)

Добавить в приоритет источников каналов:
```
CLI --tg-channels  →  config.json telegram.channels  →  telegram_sources.txt
```

Логика: если не задан ни CLI, ни config → ищем `telegram_sources.txt` рядом с exe/cwd. Формат файла: по одному имени канала на строку (как сейчас), строки с `#` — комментарии, пустые — пропускаются.

> [!NOTE]
> Файл уже существует: `/home/deck/Code/CsharpProjects/UnityAssetsDownloader/telegram_sources.txt` с каналами `unity_assets_1` и `unityassets2`.

---

### Изменение 2: Счётчик лимита — считать только фактически добавленные

**Файл:** `Program.cs` (RunAsync, строки ~305–330)

Сейчас `CountsTowardsAddLimit` = `status.IsFree` (т.е. бесплатный ассет). Это неправильно — нужно считать только когда статус результата = `AssetProcessStatus.Added`.

**Новая логика:**
```
newlyAddedCount++  ←  только когда result.Status == Added
```

При достижении `MaxAddAttempts`:
- Дописать отчёт
- Вывести итоги (`PrintSummary`)
- Завершить

---

### Изменение 3: Логирование текста каждого поста (всегда, не только DEBUG)

**Файл:** `TelegramSourceParser.cs` (ParseSingleChannelAsync, строка 128)

Сейчас:
```csharp
_logger.Debug($"[Telegram] {channelName} пост #{postId}: текст ({text.Length} символов)");
```

Нужно: логировать **полный текст** поста на уровне `Info` всегда — чтобы без `--verbose` видеть что нашёл парсер.

Формат в логе:
```
[Telegram] ---- ПОСТ unity_assets_1/#123 ----
[Telegram] <полный текст поста>
[Telegram] Asset URLs найдено: 1 | Промокоды: PROMO123 | Git: 0
```

---

### Изменение 4: Файл `telegram_posts_raw.log` — писать всегда (не только если `AllPosts.Count > 0`)

**Файл:** `Program.cs` (RunAsync, строки ~214–228)

Сейчас файл пишется только если есть посты. Добавить: дополнять файл к уже существующему (append), добавляя timestamp запуска в заголовок — чтобы история накапливалась между запусками.

---

## Proposed Changes

### CliOptions (Program.cs)

#### [MODIFY] CliOptions.Parse — добавить чтение telegram_sources.txt

После блока обработки `cliTelegramChannels` и `config?.Telegram?.Channels`, если список каналов всё ещё пустой — искать `telegram_sources.txt`.

```csharp
// Telegram из конфига + CLI (CLI имеет приоритет)
if (cliTelegramChannels.Count > 0)
    telegramChannels.AddRange(cliTelegramChannels);
else if (config?.Telegram?.Channels?.Count > 0)
    telegramChannels.AddRange(config.Telegram.Channels);
else
{
    // Fallback: telegram_sources.txt рядом с cwd или exe
    var sourcesFile = FindTelegramSourcesFile();
    if (sourcesFile != null)
    {
        var lines = await File.ReadAllLinesAsync(sourcesFile);
        foreach (var line in lines)
        {
            var ch = line.Trim();
            if (!string.IsNullOrWhiteSpace(ch) && !ch.StartsWith('#'))
                telegramChannels.Add(ch);
        }
    }
}
```

> [!NOTE]
> `CliOptions.Parse` сейчас синхронный. Чтение файла можно сделать синхронно через `File.ReadAllLines`.

---

### RunAsync (Program.cs)

#### [MODIFY] Счётчик лимита — newlyAddedCount вместо freeAssetsProcessed

```csharp
var newlyAddedCount = 0;
// ...
if (result.Status == AssetProcessStatus.Added)
{
    newlyAddedCount++;
    _logger.Info($"[Лимит] Добавлено новых ассетов: {newlyAddedCount}/{_options.MaxAddAttempts}");
    if (_options.MaxAddAttempts.HasValue && newlyAddedCount >= _options.MaxAddAttempts.Value)
    {
        _logger.Info($"[Лимит] Достигнут лимит {_options.MaxAddAttempts.Value} новых ассетов. Завершение.");
        break;
    }
}
```

---

### TelegramSourceParser.cs

#### [MODIFY] Логирование полного текста поста всегда

```csharp
_logger.Info($"[Telegram] ---- ПОСТ {channelName}/#{postId} ({text.Length} символов) ----");
_logger.Info($"[Telegram] {text}");
_logger.Info($"[Telegram] ---- Найдено: assets={assetUrls.Count}, promos=[{string.Join(",", promocodes)}], git={gitUrls.Count} ----");
```

---

## Verification Plan

### Automated Tests
```bash
~/.dotnet/dotnet build UnityAssetsDownloader/UnityAssetsDownloader.csproj
```

### Manual Verification
1. Запустить без `--tg-channels` — убедиться что каналы подхватились из `telegram_sources.txt`
2. Запустить с `--max-add-attempts 3` — убедиться что останавливается после 3 новых добавлений
3. Проверить `logs/telegram_posts_raw.log` — должны быть полные тексты всех постов
4. Проверить что `AlreadyOwned` не считается в счётчик

---

## Open Questions

> [!IMPORTANT]
> Нет открытых вопросов. Всё ясно. Ожидаю подтверждение перед реализацией.
