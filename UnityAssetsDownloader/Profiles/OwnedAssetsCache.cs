/// <summary>
/// Помнит, какие ассеты уже есть на аккаунте.
///
/// Без этого каждый запуск заново открывает страницу каждого ассета,
/// чтобы узнать то, что уже известно. На двадцати ассетах это больше минуты.
///
/// Список хранится в профиле: у разных аккаунтов он свой.
/// </summary>
internal sealed class OwnedAssetsCache
{
    private readonly string _path;
    private readonly HashSet<string> _urls = new(StringComparer.OrdinalIgnoreCase);
    private bool _changed;

    public OwnedAssetsCache(string profileDirectory)
    {
        _path = Path.Combine(profileDirectory, "owned_assets.txt");
        Load();
    }

    public int Count => _urls.Count;

    public string FilePath => _path;

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

    public void Add(string url)
    {
        if (!string.IsNullOrWhiteSpace(url) && _urls.Add(url.Trim()))
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
                "# Ассеты, которые уже есть на аккаунте этого профиля.",
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
