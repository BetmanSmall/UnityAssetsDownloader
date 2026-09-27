using System.Text.Json;
using PuppeteerSharp;

/// <summary>
/// Номера всех ассетов аккаунта («My Assets») — одним запросом из страницы магазина.
///
/// Сам магазин спрашивает это запросом CurrentUser: поле user.myAssets — строка с JSON-массивом
/// номеров, от полученных последним к самым старым (28.09 на аккаунте betmansmall — 3519).
/// Нужен вход, поэтому запрос выполняется внутри страницы магазина с её cookies. Номер
/// пользователя и CSRF-токен лежат в начальном состоянии страницы (currentUserId, cookies._csrf).
/// </summary>
internal static class MyAssetsLibrary
{
    public const string MyAssetsUrl = "https://assetstore.unity.com/account/assets";

    public sealed record Result(List<string> Ids, string? UserName, string? Error);

    public static async Task<Result> FetchOwnedIdsAsync(IPage page)
    {
        var onStore = Uri.TryCreate(page.Url, UriKind.Absolute, out var uri) && uri.Host == "assetstore.unity.com";
        var result = onStore ? await QueryAsync(page) : new Result([], null, "not-on-store");
        if (result.Error is null)
        {
            return result;
        }

        // Не та страница (или состояние без пользователя) — откроем «My Assets»: там оно точно есть.
        await page.GoToAsync(MyAssetsUrl, new NavigationOptions { WaitUntil = [WaitUntilNavigation.DOMContentLoaded] });
        return await QueryAsync(page);
    }

    private static async Task<Result> QueryAsync(IPage page)
    {
        var raw = await page.EvaluateFunctionAsync<string>(@"async () => {
            const html = document.documentElement.innerHTML;
            const pick = (re) => { const m = html.match(re); return m ? m[1] : null; };
            const uid = pick(/""currentUserId"":""(\d+)""/) || pick(/""loginUser"":\{""id"":""(\d+)""/);
            const csrf = pick(/""_csrf"":""([0-9a-zA-Z_-]+)""/) || ((document.cookie.match(/(?:^|;\s*)_csrf=([^;]+)/) || [])[1]) || null;
            if (!uid) return JSON.stringify({ error: 'no-user' });
            const headers = { 'Content-Type': 'application/json;charset=UTF-8', 'X-Requested-With': 'XMLHttpRequest', 'X-Source': 'storefront' };
            if (csrf) headers['X-Csrf-Token'] = csrf;
            try {
                const r = await fetch('/api/graphql/batch', {
                    method: 'POST', credentials: 'same-origin', headers,
                    body: JSON.stringify([{ operationName: 'MyAssetIds', variables: { id: uid },
                        query: 'query MyAssetIds($id: ID!) { user(id: $id) { id name myAssets } }' }])
                });
                return JSON.stringify({ status: r.status, body: await r.text() });
            } catch (e) {
                return JSON.stringify({ error: 'fetch: ' + e });
            }
        }");

        return Parse(raw);
    }

    /// <summary>Разбор ответа страницы. Отдельно — чтобы стенд проверял его на сохранённом ответе.</summary>
    public static Result Parse(string raw)
    {
        try
        {
            using var outer = JsonDocument.Parse(raw);
            var o = outer.RootElement;
            if (o.TryGetProperty("error", out var err))
            {
                return new Result([], null, err.GetString());
            }

            var status = o.GetProperty("status").GetInt32();
            var body = o.GetProperty("body").GetString() ?? string.Empty;
            if (status != 200)
            {
                return new Result([], null, $"HTTP {status}: {(body.Length > 150 ? body[..150] : body)}");
            }

            return ParseGraphQl(body);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new Result([], null, $"непонятный ответ: {ex.Message}");
        }
    }

    /// <summary>Ответ GraphQL (массив batch или один объект) → номера ассетов.</summary>
    public static Result ParseGraphQl(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement[0] : doc.RootElement;
        var user = root.GetProperty("data").GetProperty("user");
        if (user.ValueKind != JsonValueKind.Object)
        {
            return new Result([], null, "магазин не отдал пользователя (вход не подтверждён?)");
        }

        var name = user.TryGetProperty("name", out var n) ? n.GetString() : null;
        var myAssets = user.GetProperty("myAssets");
        // Поле — строка с JSON внутри; на всякий случай понимаем и настоящий массив.
        using var list = myAssets.ValueKind == JsonValueKind.String
            ? JsonDocument.Parse(myAssets.GetString() ?? "[]")
            : JsonDocument.Parse(myAssets.GetRawText());
        var ids = list.RootElement.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.Number ? e.GetRawText() : e.GetString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct()
            .ToList();
        return new Result(ids, name, null);
    }
}
