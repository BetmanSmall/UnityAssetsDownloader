using System.Net;
using System.Text.RegularExpressions;

/// <summary>
/// Достаёт посты из готовой страницы t.me/s/&lt;канал&gt; без браузера.
///
/// Страница приходит уже собранной на сервере Telegram: весь текст постов лежит в HTML,
/// выполнять на ней ничего не нужно. Поэтому страницу качает обычный HTTP-запрос, а этот
/// разбор повторяет то, что раньше делал скрипт внутри браузера: номер поста, текст и
/// ссылки, спрятанные за словами вроде «тут».
///
/// Браузерный разбор остался запасным: если вёрстка Telegram изменится и здесь ничего
/// не найдётся, программа откроет канал браузером, как раньше.
/// </summary>
internal static class TelegramHtmlParser
{
    // Начало поста. Кавычки бывают и двойные, и одинарные (в макетах стенда — одинарные).
    private static readonly Regex PostStartRegex = new(
        """<div[^>]*\sdata-post=["']([^"']+)["'][^>]*>""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Блок с текстом поста. Ответ на другой пост лежит в tgme_widget_message_reply_text —
    // это другое имя класса, сюда оно не попадает.
    private static readonly Regex TextBlockRegex = new(
        """<div[^>]*class=["'][^"']*tgme_widget_message_text[^"']*["'][^>]*>""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LinkRegex = new(
        """<a\b[^>]*\shref=["']([^"']*)["']""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DivTagRegex = new(
        """<\s*(/?)div\b""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LineBreakRegex = new(
        """<br\s*/?>|</\s*(?:p|div|blockquote|pre|tr)\s*>""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);

    /// <summary>
    /// Посты страницы в том же порядке, в каком они идут в HTML.
    /// Пустой список значит, что разобрать не вышло — это повод открыть канал браузером.
    /// </summary>
    public static List<(string Text, string PostId)> ExtractPosts(string? html)
    {
        var posts = new List<(string Text, string PostId)>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return posts;
        }

        // Ответ на другой пост тоже носит data-post, но постом канала не является.
        var starts = PostStartRegex.Matches(html)
            .Where(m => !m.Value.Contains("tgme_widget_message_reply", StringComparison.OrdinalIgnoreCase))
            .ToList();

        for (var i = 0; i < starts.Count; i++)
        {
            var start = starts[i];
            var blockEnd = i + 1 < starts.Count ? starts[i + 1].Index : html.Length;
            var block = html[start.Index..blockEnd];
            var postId = WebUtility.HtmlDecode(start.Groups[1].Value).Trim();

            posts.Add((BuildPostText(block), string.IsNullOrEmpty(postId) ? "unknown" : postId));
        }

        return posts;
    }

    /// <summary>
    /// Текст поста плюс адреса всех ссылок поста. Адреса дописываются потому, что ссылку
    /// часто прячут за словом: «Ссылка: тут». Без них ассет из поста не достать.
    /// </summary>
    private static string BuildPostText(string block)
    {
        var text = HtmlToText(ExtractTextBlock(block));

        foreach (Match link in LinkRegex.Matches(block))
        {
            var href = WebUtility.HtmlDecode(link.Groups[1].Value).Trim();
            if (href.Length > 0 && !text.Contains(href, StringComparison.Ordinal))
            {
                text += "\n" + href;
            }
        }

        return text;
    }

    /// <summary>
    /// Внутренность первого блока с текстом. Ищем парный &lt;/div&gt;, считая вложенные:
    /// внутри поста бывают цитаты и блоки кода, а они тоже div.
    /// </summary>
    private static string ExtractTextBlock(string block)
    {
        var open = TextBlockRegex.Match(block);
        if (!open.Success)
        {
            return string.Empty;
        }

        var contentStart = open.Index + open.Length;
        var depth = 1;

        foreach (Match tag in DivTagRegex.Matches(block, contentStart))
        {
            depth += tag.Groups[1].Value == "/" ? -1 : 1;
            if (depth == 0)
            {
                return block[contentStart..tag.Index];
            }
        }

        // Закрывающего тега нет — берём всё до конца поста.
        return block[contentStart..];
    }

    private static string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var withBreaks = LineBreakRegex.Replace(html, "\n");
        var withoutTags = TagRegex.Replace(withBreaks, string.Empty);
        return WebUtility.HtmlDecode(withoutTags).Replace("\r\n", "\n").Trim();
    }
}
