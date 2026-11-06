using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Eldorado.Core;

public sealed record CatalogItem(string Name, decimal Price, string Delivery);
public sealed record PriceCatalog(string Currency, string Updated, IReadOnlyList<CatalogItem> Items);

public sealed class PriceCatalogService
{
    public const string Source = "https://raw.githubusercontent.com/Gia3010Q/rem-el/refs/heads/main/ui-fix/prices.json";
    private readonly HttpClient http;
    private readonly string cachePath;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? etag;
    public PriceCatalog? Current { get; private set; }
    public DateTimeOffset? LastChecked { get; private set; }

    public PriceCatalogService(HttpClient http, string cachePath)
    {
        this.http = http;
        this.cachePath = cachePath;
        if (!File.Exists(cachePath)) return;
        try
        {
            var cached = JsonSerializer.Deserialize<CacheEnvelope>(File.ReadAllText(cachePath));
            if (cached is null) return;
            Current = Parse(cached.Json);
            etag = cached.ETag;
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or UnauthorizedAccessException)
        {
            // A corrupt local cache must not prevent downloading a valid catalog.
        }
    }

    public async Task<bool> RefreshAsync()
    {
        await gate.WaitAsync();
        string temporary = cachePath + ".tmp";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Source);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            if (etag is not null && Current is not null && EntityTagHeaderValue.TryParse(etag, out var tag))
                request.Headers.IfNoneMatch.Add(tag);
            using var response = await http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotModified && Current is not null)
            {
                LastChecked = DateTimeOffset.Now;
                return false;
            }
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync();
            if (json.Length > 2_000_000) throw new ArgumentException("Bảng giá vượt quá kích thước cho phép.");
            var next = Parse(json);
            string? nextTag = response.Headers.ETag?.ToString();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new CacheEnvelope(json, nextTag)));
            File.Move(temporary, cachePath, true);
            Current = next;
            etag = nextTag;
            LastChecked = DateTimeOffset.Now;
            return true;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            gate.Release();
        }
    }

    public static PriceCatalog Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != 1) throw new ArgumentException("Phiên bản bảng giá chưa được hỗ trợ.");
            string currency = root.GetProperty("currency").GetString() ?? "";
            if (!SellerApi.Currencies.Contains(currency)) throw new ArgumentException("Đơn vị tiền trong bảng giá không hợp lệ.");
            string updated = root.TryGetProperty("updated", out var date) ? date.GetString() ?? "" : "";
            var items = new List<CatalogItem>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in root.GetProperty("items").EnumerateArray())
            {
                string name = item.GetProperty("name").GetString()?.Trim() ?? "";
                decimal price = item.GetProperty("price").GetDecimal();
                string delivery = item.GetProperty("delivery").GetString() ?? "";
                if (name.Length == 0 || price <= 0 || !SellerApi.DeliveryTimes.Contains(delivery) || !names.Add(name))
                    throw new ArgumentException("Bảng giá có dịch vụ trùng tên hoặc giá/thời gian không hợp lệ.");
                items.Add(new CatalogItem(name, price, delivery));
            }
            if (items.Count == 0) throw new ArgumentException("Bảng giá đang trống.");
            return new PriceCatalog(currency, updated, items.AsReadOnly());
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new ArgumentException("File prices.json có cấu trúc không hợp lệ.", error);
        }
    }

    private sealed record CacheEnvelope(string Json, string? ETag);
}
