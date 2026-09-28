/// <summary>
/// Сервер читает каналы, но забрать Fab сам не может: Cloudflare на fab.com просит человека,
/// а у сервера нет ни экрана, ни человека. Поэтому бот сразу сообщает о новых ассетах Fab из
/// постов — название и ссылку, — а забирает их пункт F на ПК или Deck. Раздачи бывают временными
/// (KUBIKOS 28.09 успел снова стать платным), так что знать о них лучше сразу, а не из сводки.
///
/// О каждом ассете бот пишет один раз: сообщённое помнится в fab/announced.txt профиля.
/// </summary>
internal sealed class FabAnnouncer
{
    /// <summary>Больше ссылок в одном сообщении — уже простыня; остальное — числом.</summary>
    private const int MaxInMessage = 10;

    private readonly OwnedAssetsCache _announced;
    private readonly List<(string Url, string Title)> _pending = [];

    public FabAnnouncer(string profileDirectory)
    {
        _announced = new OwnedAssetsCache(Path.Combine(profileDirectory, "fab"), "announced.txt",
            "Ассеты Fab из каналов, о которых бот уже сообщил.");
    }

    /// <summary>Запоминает ссылки на Fab из прочитанных постов, о которых бот ещё не писал.</summary>
    public void Add(IEnumerable<TelegramPostInfo> posts)
    {
        foreach (var post in posts)
        {
            foreach (var url in FabStore.ExtractListingUrls(post.Text))
            {
                if (!_announced.Contains(url) && _pending.All(p => p.Url != url))
                {
                    _pending.Add((url, TitleOf(post.Text)));
                }
            }
        }
    }

    /// <summary>Текст сообщения боту. null — новых ассетов Fab нет.</summary>
    public string? BuildMessage()
    {
        if (_pending.Count == 0)
        {
            return null;
        }

        var lines = new List<string> { $"🧩 Fab: в каналах новые ассеты — {_pending.Count}" };
        foreach (var (url, title) in _pending.Take(MaxInMessage))
        {
            lines.Add($"• {title}");
            lines.Add($"  {url}");
        }

        if (_pending.Count > MaxInMessage)
        {
            lines.Add($"… и ещё {_pending.Count - MaxInMessage}");
        }

        lines.Add("Забрать на аккаунт Epic: на ПК или Deck пункт F. Раздачи бывают временными — лучше не откладывать.");
        return string.Join("\n", lines);
    }

    /// <summary>Сообщение ушло — больше об этих ассетах не пишем.</summary>
    public void MarkSent()
    {
        foreach (var (url, _) in _pending)
        {
            _announced.Add(url);
        }

        _announced.Save();
        _pending.Clear();
    }

    /// <summary>Название — первая непустая строка поста (так их пишут в каналах).</summary>
    internal static string TitleOf(string text)
    {
        var first = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "без названия";
        return first.Length <= 90 ? first : first[..90].TrimEnd() + "…";
    }
}
