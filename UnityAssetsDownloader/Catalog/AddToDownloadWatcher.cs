using System.Text.Json;
using PuppeteerSharp;

/// <summary>Что магазин ответил на добавление ассета.</summary>
internal enum StoreAddAnswer
{
    /// <summary>Ответа не видели: запрос не ушёл, оборвался или ещё идёт.</summary>
    None,

    /// <summary>Магазин выдал право на ассет (userEntitlement с датой) — ассет на аккаунте.</summary>
    Granted,

    /// <summary>
    /// Магазин ответил без права на ассет (userEntitlement: null). Это ещё не отказ: 11.10 на сервере style-reference-box
    /// получил null, а следующий прогон нашёл его на аккаунте. Окончательно решает список «My Assets».
    /// </summary>
    Refused
}

/// <summary>
/// Слушает ответ магазина на кнопку «Add to My Assets». Сама страница шлёт мутацию GraphQL AddToDownload и получает
/// в ответ userEntitlement: объект с grantTime — ассет выдан, null — магазин его не добавил (так 09.10 вели себя шесть
/// ассетов UnityAssets2D: плашка «Added to My Assets» есть, а в «My Assets» их нет).
///
/// Программа ничего не отправляет сама — только читает ответ на запрос, который сделала страница после клика. Так
/// отличается «магазин отказал» от «на медленном сервере не дождались страницы», и не нужно ждать, пока ассет
/// появится на странице, если магазин уже ответил «выдан».
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
            // «Выдан» окончательный: повторный клик после выдачи не должен превратить его в отказ.
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
                    result = StoreAddAnswer.Refused;
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
