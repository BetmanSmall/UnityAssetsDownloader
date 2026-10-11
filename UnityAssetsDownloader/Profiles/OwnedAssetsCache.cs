/// <summary>
/// Помнит ассеты, которые не нужно проверять заново: уже добавленные на аккаунт
/// или удалённые из магазина.
///
/// Без этого каждый запуск заново открывает страницу каждого ассета, чтобы узнать
/// то, что уже известно. На удалённом ассете уходит до полутора минут.
///
/// Список хранится в профиле: у разных аккаунтов он свой.
/// </summary>
internal sealed class OwnedAssetsCache
{
    private readonly string _path;
    private readonly string _title;
    private readonly HashSet<string> _urls = new(StringComparer.OrdinalIgnoreCase);
    private bool _changed;

    public OwnedAssetsCache(string profileDirectory, string fileName = "owned_assets.txt", string? title = null)
    {
        _path = Path.Combine(profileDirectory, fileName);
        _title = title ?? "Ассеты, которые уже есть на аккаунте этого профиля.";
        Load();
    }

    public int Count => _urls.Count;

    public string FilePath => _path;

    /// <summary>Заголовок файла при загрузке (первая строка «# …») — по нему видно, по какому правилу файл записан.</summary>
    public string? LoadedTitle { get; private set; }

    public IEnumerable<string> Items => _urls;

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            foreach (var line in File.ReadAllLines(_path))
            {
                var url = line.Trim();
                if (LoadedTitle is null && url.StartsWith("# ", StringComparison.Ordinal))
                {
                    LoadedTitle = url[2..];
                }

                if (url.Length > 0 && !url.StartsWith('#'))
                {
                    _urls.Add(url);
                }
            }
        }
        catch
        {
            // Битый файл памяти — не повод падать. Просто начнём собирать заново.
        }
    }

    public bool Contains(string url) => _urls.Contains(url);

    /// <summary>Забыть всё (например, началась новая раздача). Сохраняется при следующем Save().</summary>
    public void Clear()
    {
        if (_urls.Count > 0)
        {
            _urls.Clear();
            _changed = true;
        }
    }

    public void Add(string url)
    {
        if (!string.IsNullOrWhiteSpace(url) && _urls.Add(url.Trim()))
        {
            _changed = true;
        }
    }

    public void Remove(string url)
    {
        if (!string.IsNullOrWhiteSpace(url) && _urls.Remove(url.Trim()))
        {
            _changed = true;
        }
    }

    /// <summary>Сохраняет список, если он изменился. Повторный вызов ничего не портит.</summary>
    public void Save()
    {
        if (!_changed)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var lines = new List<string>
            {
                $"# {_title}",
                "# Программа их пропускает, не открывая страницу.",
                "# Можно удалить файл целиком — тогда всё проверится заново."
            };
            lines.AddRange(_urls.OrderBy(u => u, StringComparer.OrdinalIgnoreCase));
            File.WriteAllLines(_path, lines);
            _changed = false;
        }
        catch
        {
            // Не смогли сохранить — в следующий раз просто проверим заново.
        }
    }
}
