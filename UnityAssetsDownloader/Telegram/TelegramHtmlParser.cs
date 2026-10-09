using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

/// <summary>Пост страницы канала: текст и ссылки из него по отдельности, время публикации (UTC).</summary>
internal sealed record TelegramRawPost(string PostId, DateTime? PostedUtc, string Text, IReadOnlyList<string> Links);

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

    private static readonly Regex TimeRegex = new(
        """<time\b[^>]*\sdatetime=["']([^"']+)["']""",
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
    public static List<(string Text, string PostId)> ExtractPosts(string? html) =>
        SplitPostBlocks(html)
            .Select(b => (BuildPostText(b.Block), b.PostId))
            .ToList();

    /// <summary>
    /// Посты страницы по отдельности: текст без дописанных ссылок, ссылки из самого текста
    /// и время публикации. Для разбора карточек ассетов (<see cref="ChannelCardParser"/>), где
    /// важно, какие ссылки стоят в тексте, а не на странице вокруг него.
    /// </summary>
    public static List<TelegramRawPost> ExtractRawPosts(string? html) =>
        SplitPostBlocks(html)
            .Select(b =>
            {
                var textBlock = ExtractTextBlock(b.Block);
                var links = LinkRegex.Matches(textBlock)
                    .Select(m => DecodeHref(m.Groups[1].Value))
                    .Where(h => h.Length > 0)
                    .ToList();
                return new TelegramRawPost(b.PostId, PostedAt(b.Block), HtmlToText(textBlock), links);
            })
            .ToList();

    // Кусок страницы на каждый пост, в порядке страницы.
    private static List<(string PostId, string Block)> SplitPostBlocks(string? html)
    {
        var blocks = new List<(string PostId, string Block)>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return blocks;
        }

        // Ответ на другой пост тоже носит data-post, но постом канала не является.
        var starts = PostStartRegex.Matches(html)
            .Where(m => !m.Value.Contains("tgme_widget_message_reply", StringComparison.OrdinalIgnoreCase))
            .ToList();

        for (var i = 0; i < starts.Count; i++)
        {
            var start = starts[i];
            var blockEnd = i + 1 < starts.Count ? starts[i + 1].Index : html.Length;
            var postId = WebUtility.HtmlDecode(start.Groups[1].Value).Trim();

            blocks.Add((string.IsNullOrEmpty(postId) ? "unknown" : postId, html[start.Index..blockEnd]));
        }

        return blocks;
    }

    private static DateTime? PostedAt(string block)
    {
        var match = TimeRegex.Match(block);
        return match.Success && DateTime.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)
            ? when
            : null;
    }

    // Telegram иногда кодирует адрес в атрибуте дважды (…pack&amp;#33;/328345 вместо …pack!/328345):
    // после первого разбора остаётся &#33;. Разбираем, пока адрес меняется.
    private static string DecodeHref(string href)
    {
        var value = href.Trim();
        for (var i = 0; i < 3; i++)
        {
            var decoded = WebUtility.HtmlDecode(value);
            if (decoded == value)
            {
                break;
            }

            value = decoded;
        }

        return value;
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
