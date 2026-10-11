using System.Text.Json;
using PuppeteerSharp;

/// <summary>Что магазин ответил на добавление ассета.</summary>
internal enum StoreAddAnswer
{
    /// <summary>Ответа не видели: запрос на добавление не ушёл, оборвался или ещё идёт.</summary>
    None,

    /// <summary>Магазин выдал право на ассет (userEntitlement с датой) — ассет на аккаунте. На 11.10 вживую не встречалось.</summary>
    Granted,

    /// <summary>
    /// Магазин принял запрос и ответил без права на ассет (userEntitlement: null). Это **обычный** ответ: 11.10 на сервере
    /// все 5 успешно добавленных ассетов получили именно его, а «выдан» не пришёл ни разу. Добавлен ли ассет, по этому
    /// ответу не понять — решают страница и список «My Assets».
    /// </summary>
    Answered
}

/// <summary>
/// Слушает ответ магазина на кнопку «Add to My Assets». Сама страница шлёт мутацию GraphQL AddToDownload; программа
/// ничего не отправляет — только читает ответ на запрос страницы.
///
/// Что это даёт (проверено вживую 11.10): видно, дошёл ли запрос на добавление до магазина (Answered) или нет (None) —
/// это пишется в отчёт по ассету (StoreAnswer) и в сообщение «не подтверждён». Сам ответ почти всегда
/// userEntitlement: null — и у добавленных ассетов тоже (09.10 по нему ошибочно решили, что null — отказ). Если магазин
/// когда-нибудь ответит правом с grantTime (Granted), проверка заканчивается сразу, без перезагрузок страницы.
/// </summary>
internal sealed class AddToDownloadWatcher : IDisposable
{
    private readonly IPage _page;
    private readonly string? _packageId;
    private int _answer;

    public AddToDownloadWatcher(IPage page, string? packageId)
    {
        _page = page;
        _packageId = packageId;
        _page.Response += OnResponse;
    }

    public StoreAddAnswer Answer => (StoreAddAnswer)Volatile.Read(ref _answer);

    public void Dispose() => _page.Response -= OnResponse;

    private async void OnResponse(object? sender, ResponseCreatedEventArgs e)
    {
        try
        {
            var request = e.Response.Request;
            if (request is null || request.Method != HttpMethod.Post ||
                !e.Response.Url.Contains("/api/graphql", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var body = request.PostData ?? (request.HasPostData ? await request.FetchPostDataAsync() : null);
            if (body is null || !body.Contains("addToDownload", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var answer = Parse(await e.Response.TextAsync(), _packageId);
            // «Выдан» окончательный: ответ на повторный клик его не заменяет.
            if (answer != StoreAddAnswer.None && Answer != StoreAddAnswer.Granted)
            {
                Volatile.Write(ref _answer, (int)answer);
            }
        }
        catch
        {
            // Ответ не прочитался (вкладку перезагрузили) — остаётся проверка по странице и по «My Assets».
        }
    }

    /// <summary>
    /// Разбор ответа: один объект GraphQL или массив (batch). Учитывается только ответ про этот ассет (packageId
    /// null — про любой). Ошибка GraphQL или незнакомый ответ — None.
    /// </summary>
    internal static StoreAddAnswer Parse(string json, string? packageId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var answers = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : [doc.RootElement];

            var result = StoreAddAnswer.None;
            foreach (var item in answers)
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                    !data.TryGetProperty("addToDownload", out var product) || product.ValueKind != JsonValueKind.Object ||
                    !product.TryGetProperty("userEntitlement", out var entitlement))
                {
                    continue;
                }

                var id = product.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
                if (packageId is not null && id is not null && id != packageId)
                {
                    continue;
                }

                if (entitlement.ValueKind == JsonValueKind.Object)
                {
                    return StoreAddAnswer.Granted;
                }

                if (entitlement.ValueKind == JsonValueKind.Null)
                {
                    result = StoreAddAnswer.Answered;
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return StoreAddAnswer.None;
        }
    }
}
