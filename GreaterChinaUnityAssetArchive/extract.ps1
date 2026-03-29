# 1. Укажите путь к вашему файлу (можно перетащить файл в окно консоли)
$inputFile = Read-Host "./html_GreaterChinaUnityAssetArchiveLinks.txt"

# Проверяем, существует ли файл
if (-not (Test-Path $inputFile)) {
    Write-Host "Файл не найден!" -ForegroundColor Red
    exit
}

# 2. Читаем содержимое файла
$content = Get-Content -Path $inputFile -Raw

# 3. Регулярное выражение для поиска ссылок
$regex = 'https://assetstore\.unity\.com/packages/package/\d+'

# 4. Извлекаем все совпадения и оставляем только уникальные
$links = [regex]::Matches($content, $regex).Value | Select-Object -Unique

# 5. Сохраняем результат в файл links.txt рядом с исходным
$outputFile = Join-Path (Split-Path $inputFile) "all_list_GreaterChinaUnityAssetArchiveLinks.txt"
$links | Out-File -FilePath $outputFile -Encoding utf8

Write-Host "Готово! Найдено ссылок: $($links.Count)" -ForegroundColor Green
Write-Host "Результат сохранен в: $outputFile"
