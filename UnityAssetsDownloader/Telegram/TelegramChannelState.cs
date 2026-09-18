using System.Text.Json;

/// <summary>
/// Где остановилось чтение каждого канала: номер самого свежего поста, ассеты из которого
/// уже проверены. Следующий прогон читает только то, что появилось после него.
/// Хранится в профиле: у разных аккаунтов своя история.
/// </summary>
internal sealed class TelegramChannelState
{
    private readonly string _path;
    private readonly Dictionary<string, int> _lastSeen;

    private TelegramChannelState(string path, Dictionary<string, int> lastSeen)
    {
        _path = path;
        _lastSeen = lastSeen;
    }

    public string FilePath => _path;

    public static TelegramChannelState Load(string profileDirectory)
    {
        var path = Path.Combine(profileDirectory, "telegram_state.json");
        var data = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path));
                foreach (var kvp in loaded ?? [])
                {
                    data[kvp.Key] = kvp.Value;
                }
            }
        }
        catch
        {
            // Испорченный файл — начнём заново, как в первый раз.
        }

        return new TelegramChannelState(path, data);
    }

    /// <summary>Номер последнего прочитанного поста. 0 — канал ещё не читали.</summary>
    public int LastSeen(string channel) => _lastSeen.TryGetValue(channel, out var id) ? id : 0;

    /// <summary>Сдвигает отметку вперёд. Назад не двигает никогда.</summary>
    public bool Advance(string channel, int postId)
    {
        if (postId <= LastSeen(channel))
        {
            return false;
        }

        _lastSeen[channel] = postId;
        return true;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_lastSeen, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Не сохранили — следующий прогон перечитает эти посты, ассеты отсеет память профиля.
        }
    }
}
