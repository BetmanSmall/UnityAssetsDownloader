using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Данные об ассетах из GraphQL магазина — тем же запросом, что делает сама страница ассета.
///
/// Вход не нужен: данные продукта публичные. Магазин проверяет только CSRF по схеме
/// «cookie _csrf = заголовок X-Csrf-Token», значение может быть любым — генерируем своё.
/// Браузер не нужен: пачка из 50 ассетов — один запрос на ~2 секунды и ~150 КБ, вместо
/// загрузки 50 страниц по 1,4 МБ. Вся библиотека в 3500 ассетов — пара минут.
///
/// Магазин изредка отвечает 500 на пачку, которую со второго раза отдаёт целиком
/// (замечено 28.09): поэтому повтор, а если не помог — пачка делится пополам.
/// </summary>
internal sealed class AssetStoreProductApi : IDisposable
{
    public const string DefaultEndpoint = "https://assetstore.unity.com/api/graphql/batch";
    public const int BatchSize = 50;

    private const string ProductFields = """
        fragment F on Product {
          id name slug state elevatorPitch description aiDescription keyFeatures
          category { slug }
          publisher { name }
          popularTags { name }
          srps { types }
          supportedUnityVersions
          currentVersion { name publishedDate }
          firstPublishedDate downloadSize assetCount
          rating { average count }
          mainImage { small big icon }
          originalPrice { isFree finalPrice currency }
        }
        """;

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _csrf = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private readonly Action<string>? _log;

    public AssetStoreProductApi(Action<string>? log = null, string? endpoint = null, HttpMessageHandler? handler = null)
    {
        _log = log;
        _endpoint = endpoint ?? Environment.GetEnvironmentVariable("ASSETSTORE_GRAPHQL") ?? DefaultEndpoint;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36");
    }

    /// <summary>
    /// Данные по номерам ассетов. В ответе есть каждый номер: значение null — магазин
    /// о нём не знает; номера, которые не удалось получить из-за сети, в ответ не попадают.
    /// </summary>
    public async Task<Dictionary<string, JsonElement?>> FetchAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var result = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        var clean = ids.Where(id => Regex.IsMatch(id, @"^\d+$")).Distinct().ToList();
        for (var start = 0; start < clean.Count; start += BatchSize)
        {
            var batch = clean.Skip(start).Take(BatchSize).ToList();
            await FetchBatchAsync(batch, result, ct);
            if (clean.Count > BatchSize)
            {
                _log?.Invoke($"[Каталог] Данные из магазина: {Math.Min(start + BatchSize, clean.Count)} из {clean.Count}.");
            }
        }

        return result;
    }

    private async Task FetchBatchAsync(List<string> batch, Dictionary<string, JsonElement?> result, CancellationToken ct)
    {
        Exception? error = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                foreach (var (id, product) in await QueryAsync(batch, ct))
                {
                    result[id] = product;
                }

                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException && !ct.IsCancellationRequested)
            {
                error = ex;
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt), ct);
            }
        }

        if (batch.Count == 1)
        {
            _log?.Invoke($"[Каталог] Ассет #{batch[0]}: магазин не отдал данные ({error?.Message}). Попробуем в следующий раз.");
            return;
        }

        var half = batch.Count / 2;
        await FetchBatchAsync(batch.Take(half).ToList(), result, ct);
        await FetchBatchAsync(batch.Skip(half).ToList(), result, ct);
    }

    private async Task<List<(string Id, JsonElement? Product)>> QueryAsync(List<string> batch, CancellationToken ct)
    {
        var query = new StringBuilder("query CatalogProducts {");
        for (var i = 0; i < batch.Count; i++)
        {
            query.Append($" a{i}: product(id: \"{batch[i]}\") {{ ...F }}");
        }

        query.Append(" }\n").Append(ProductFields);
        var body = JsonSerializer.Serialize(new[] { new { operationName = "CatalogProducts", query = query.ToString() } });

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Add("X-Csrf-Token", _csrf);
        request.Headers.Add("Cookie", $"_csrf={_csrf}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            // Одиночный ассет, на котором GraphQL магазина стабильно падает (28.09: #291669 —
            // «data: {a0: null}» и GraphqlError): данных о нём нет, запишется как missing.
            if (batch.Count == 1 && text.Contains("\"data\":{\"a0\":null}", StringComparison.Ordinal))
            {
                return [(batch[0], null)];
            }

            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {Shorten(text)}");
        }

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement[0] : doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"в ответе нет data: {Shorten(text)}");
        }

        var list = new List<(string, JsonElement?)>();
        for (var i = 0; i < batch.Count; i++)
        {
            list.Add(data.TryGetProperty($"a{i}", out var p) && p.ValueKind == JsonValueKind.Object
                ? (batch[i], p.Clone())
                : (batch[i], null));
        }

        return list;
    }

    private static string Shorten(string text) => text.Length > 200 ? text[..200] + "…" : text;

    public void Dispose() => _http.Dispose();
}
