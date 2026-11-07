using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Eldorado.Core;

/// <summary>Seller endpoints verified against the supplied Eldorado OpenAPI specification.</summary>
public sealed class SellerApi
{
    private readonly HttpClient http;
    private readonly Uri server;
    private readonly Func<Task<Credentials>> credentials;
    private readonly SemaphoreSlim tokenGate = new(1, 1);
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private readonly HashSet<string> submittedRequests = new(StringComparer.OrdinalIgnoreCase);
    private string? accessToken;
    private DateTimeOffset tokenExpires;
    public IReadOnlyList<string> OfferWarnings { get; private set; } = Array.Empty<string>();

    public static IReadOnlyList<string> DeliveryTimes { get; } = new[]
    {
        "Minute5", "Minute20", "Hour1", "Hour2", "Hour3", "Hour5", "Hour8", "Hour12",
        "Day1", "Day2", "Day3", "Day5", "Day7", "Day10", "Day14", "Day28", "Day45",
        "Day60", "Day80", "Day100"
    };
    public static IReadOnlyList<string> Currencies { get; } = new[]
    {
        "USD", "EUR", "GBP", "AUD", "CAD", "JPY", "BRL", "SGD", "CHF", "SEK",
        "DKK", "NOK", "NZD", "THB", "HKD", "CZK", "SAR", "PLN", "AED", "MXN",
        "RON", "ILS", "ARS", "TRY", "QAR", "HUF", "PEN", "KWD", "BGN", "CLP",
        "COP", "IDR", "PHP"
    };

    public SellerApi(HttpClient http, Func<Task<Credentials>> credentials)
    {
        this.http = http;
        this.credentials = credentials;
        server = http.BaseAddress ?? new Uri("https://www.eldorado.gg/");
    }

    public async Task<List<Order>> GetOrdersAsync()
    {
        var root = await SendAsync(HttpMethod.Get,
            "api/v1/orders/me/seller/orders?displayFilter=DisplaySellingOrders&pageSize=50&isAscendingDateOrder=false");
        return Results(root).Select(ReadOrder).ToList();
    }

    public async Task<List<BoostRequest>> GetRequestsAsync()
    {
        var root = await SendAsync(HttpMethod.Get,
            "api/boostingOffers/me/boostingRequests/received?filter=ActiveRequests&pageSize=50");
        return Results(root).Select(x => new BoostRequest(Required(x, "id"), Text(x, "gameId"),
            Text(x, "boostingCategoryTitle"), Text(x, "buyerUsername"),
            Date(x, "createdDate") ?? DateTimeOffset.MinValue)).ToList();
    }

    /// <summary>
    /// Read-only request detail route used by Eldorado's official web frontend.
    /// This route is outside Seller OpenAPI; a seller token may lack permission.
    /// Call for the selected request only and show an explicit website fallback on failure.
    /// </summary>
    public async Task<string?> GetRequestDetailsAsync(string requestId)
    {
        ValidateId(requestId);
        var root = await SendAsync(HttpMethod.Get, $"api/boostingOffers/boostingRequests/{requestId}");
        if (!Guid.TryParse(Text(root, "id"), out var actualId) || actualId != Guid.Parse(requestId))
            throw new ApiException("Chưa xác minh được nội dung thuộc yêu cầu đã chọn. Hãy mở yêu cầu trên Eldorado.");
        var values = Field(Field(root, "boostingRequestDetails"), "descriptionValues");
        if (values.ValueKind != JsonValueKind.Array) return null;
        var lines = new List<string>();
        foreach (var field in values.EnumerateArray())
        {
            var label = Text(field, "label").Trim();
            var value = Text(field, "value").Trim();
            if (value.Length == 0) continue;
            lines.Add(label.Length == 0 ? value : $"{label}\n{value}");
        }
        return lines.Count == 0 ? null : string.Join("\n\n", lines);
    }

    public async Task<List<Offer>> GetOffersAsync()
    {
        List<Offer> offers = [];
        List<string> warnings = [];
        OfferWarnings = Array.Empty<string>();
        int successfulSources = 0;
        // Different offer families have separate seller APIs and update paths.
        foreach (var source in new[]
        {
            (Path: "api/flexibleOffers/me/search", Kind: "Account", State: ""),
            (Path: "api/predefinedOffers/me", Kind: "Predefined", State: ""),
            (Path: "api/v1/item-management/me/offers/me/search", Kind: "CustomItem", State: ""),
            (Path: "api/v1/currency-management/me/offers", Kind: "Currency", State: "Active"),
            (Path: "api/v1/currency-management/me/offers", Kind: "Currency", State: "Paused"),
            (Path: "api/v1/currency-management/me/offers", Kind: "Currency", State: "Offline")
        })
        {
            try
            {
                for (int page = 1; page <= 1000; page++)
                {
                    // The currency endpoint explicitly declares a GET JSON body in OpenAPI.
                    object? body = source.Kind == "Currency"
                        ? new { augmentedGameFilter = new { category = "Currency" }, offerState = source.State }
                        : null;
                    var root = await SendAsync(HttpMethod.Get,
                        $"{source.Path}?pageIndex={page}&pageSize=50", body);
                    var items = Results(root).ToList();
                    var parsed = items.Select(x => ReadOffer(x, source.Kind)).ToList();
                    offers.AddRange(parsed);
                    if (items.Count < 50 || (Number(root, "totalPages") is decimal total && page >= total))
                        break;
                    if (page == 1000)
                        throw new ApiException("Danh sách offer vượt giới hạn 1.000 trang của API.");
                }
                successfulSources++;
            }
            catch (ApiException error) when (error.StatusCode is not 401 and not 429)
            {
                warnings.Add($"{source.Kind} {source.State}: {error.Message}");
            }
        }
        OfferWarnings = warnings.AsReadOnly();
        if (successfulSources == 0)
            throw new ApiException("Chưa tải được nhóm offer nào. " + string.Join("\n", warnings));
        return offers.DistinctBy(x => (x.Kind, x.Id)).ToList();
    }

    public async Task SendQuoteAsync(BoostRequest request, decimal price, string currency, string deliveryTime)
    {
        ValidateMoney(price, currency);
        if (!DeliveryTimes.Contains(deliveryTime))
            throw new ArgumentException("Chọn thời gian hoàn thành hợp lệ.");
        ValidateId(request.Id);
        await mutationGate.WaitAsync();
        try
        {
            if (submittedRequests.Contains(request.Id))
                throw new ApiException("Yêu cầu này đã được gửi hoặc cần kiểm tra kết quả trên Eldorado.", true);
            var active = await GetRequestsAsync();
            if (!active.Any(x => x.Id == request.Id))
                throw new ApiException("Yêu cầu đã đóng, đã báo giá hoặc không còn trong 50 yêu cầu đang hoạt động. Hãy kiểm tra Eldorado.");
            // Reserve before transmission. Unknown outcomes remain reserved until app restart,
            // while the desktop layer persists its operation journal across restarts.
            submittedRequests.Add(request.Id);
            try
            {
                var response = await SendAsync(HttpMethod.Post, "api/boostingOffers", new
                {
                    details = new
                    {
                        boostingRequestId = request.Id,
                        guaranteedDeliveryTime = deliveryTime,
                        pricing = new
                        {
                            quantity = 1,
                            minQuantity = 1,
                            pricePerUnit = new { amount = price, currency }
                        }
                    }
                }, true);
                if (!Guid.TryParse(Text(response, "boostingRequestId"), out var acknowledgedRequest)
                    || acknowledgedRequest != Guid.Parse(request.Id)
                    || !Guid.TryParse(Text(response, "id"), out _))
                    throw new ApiException("Eldorado đã nhận thao tác nhưng phản hồi chưa xác nhận được báo giá. Kiểm tra trên website.", true);
            }
            catch (ApiException error) when (!error.OutcomeUnknown)
            {
                submittedRequests.Remove(request.Id);
                throw;
            }
        }
        finally { mutationGate.Release(); }
    }

    /// <summary>Call only after the user checks Eldorado and confirms this request has no submitted quote.</summary>
    public async Task AcknowledgeUnsentQuoteAsync(string requestId)
    {
        ValidateId(requestId);
        await mutationGate.WaitAsync();
        try { submittedRequests.Remove(requestId); }
        finally { mutationGate.Release(); }
    }

    public async Task DeliverAsync(Order order)
    {
        ValidateId(order.Id);
        if (order.State != "Paid")
            throw new ApiException("Chỉ có thể xác nhận giao cho đơn đang chờ giao.");
        await MutateAsync($"api/v1/orders/me/{order.Id}/deliver", null);
    }

    public Task ChangePriceAsync(Offer offer, decimal price)
    {
        ValidateMoney(price, offer.Currency);
        return MutateAsync(OfferPath(offer, true), new { amount = price, currency = offer.Currency });
    }

    public Task ChangeQuantityAsync(Offer offer, int quantity)
    {
        if (quantity < 0) throw new ArgumentException("Số lượng phải từ 0 trở lên.");
        if (offer.Kind == "Account" || offer.Quantity == null)
            throw new ApiException("Loại offer này cần thay đổi tồn kho trên Eldorado.");
        return MutateAsync(OfferPath(offer, false), quantity);
    }

    private async Task MutateAsync(string path, object? body)
    {
        await mutationGate.WaitAsync();
        try { await SendAsync(HttpMethod.Put, path, body, true); }
        finally { mutationGate.Release(); }
    }

    private static string OfferPath(Offer offer, bool price)
    {
        ValidateId(offer.Id);
        return offer.Kind switch
        {
            "Account" when price => $"api/flexibleOffersUser/me/{offer.Id}/changePrice",
            "Predefined" => $"api/predefinedOffersUser/me/{offer.Id}/" + (price ? "changePrice" : "changeQuantity"),
            "CustomItem" => $"api/v1/item-management/me/offers/{offer.Id}/" + (price ? "price" : "quantity"),
            "Currency" => $"api/v1/currency-management/me/offers/{offer.Id}/" + (price ? "change-price" : "change-quantity"),
            _ => throw new ApiException("Loại offer này cần chỉnh trên Eldorado.")
        };
    }

    private async Task<string> GetTokenAsync()
    {
        await tokenGate.WaitAsync();
        try
        {
            if (accessToken != null && tokenExpires > DateTimeOffset.UtcNow.AddSeconds(60))
                return accessToken;
            var login = await credentials();
            if (string.IsNullOrWhiteSpace(login.ClientId) || string.IsNullOrWhiteSpace(login.ClientSecret))
                throw new ApiException("Nhập Client ID và Client Secret để kết nối shop.");
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, "api/authentication/seller/token"))
            {
                Content = JsonContent.Create(new { clientId = login.ClientId, clientSecret = login.ClientSecret })
            };
            using var response = await http.SendAsync(request);
            await CheckResponseAsync(response, false);
            var root = await ReadJsonAsync(response, false);
            accessToken = Required(root, "accessToken");
            var seconds = Number(root, "expiresIn") ?? 900;
            tokenExpires = DateTimeOffset.UtcNow.AddSeconds((double)seconds);
            return accessToken;
        }
        catch (HttpRequestException) { throw new ApiException("Không kết nối được máy chủ xác thực Eldorado."); }
        catch (TaskCanceledException) { throw new ApiException("Kết nối xác thực hết thời gian chờ."); }
        finally { tokenGate.Release(); }
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body = null, bool mutation = false)
    {
        string token = await GetTokenAsync();
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(method, new Uri(server, path));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Add("swagger", "Swager request");
                if (method == HttpMethod.Get)
                    request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                if (body != null) request.Content = JsonContent.Create(body);
                using var response = await http.SendAsync(request);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    accessToken = null;
                    if (!mutation && attempt == 0)
                    {
                        token = await GetTokenAsync();
                        continue;
                    }
                }
                await CheckResponseAsync(response, mutation);
                return await ReadJsonAsync(response, mutation);
            }
        }
        catch (HttpRequestException)
        {
            throw new ApiException(mutation
                ? "Mất kết nối lúc gửi. Kết quả chưa xác định, hãy kiểm tra Eldorado trước khi gửi lại."
                : "Không kết nối được Eldorado. Kiểm tra mạng rồi làm mới.", mutation);
        }
        catch (TaskCanceledException)
        {
            throw new ApiException(mutation
                ? "Hết thời gian chờ. Kết quả chưa xác định, hãy kiểm tra Eldorado trước khi gửi lại."
                : "Eldorado phản hồi quá chậm. Hãy thử làm mới sau.", mutation);
        }
    }

    private static async Task CheckResponseAsync(HttpResponseMessage response, bool mutation)
    {
        if (response.IsSuccessStatusCode) return;
        var retry = response.Headers.RetryAfter;
        TimeSpan? delay = retry?.Delta;
        if (delay == null && retry?.Date is DateTimeOffset date)
            delay = date - DateTimeOffset.UtcNow;
        int code = (int)response.StatusCode;
        string message = code switch
        {
            401 => "Phiên truy cập hết hạn hoặc credentials không hợp lệ. Hãy kết nối lại.",
            403 => "Shop hoặc credentials chưa có quyền thực hiện thao tác này.",
            404 => "Dữ liệu không còn tồn tại trên Eldorado.",
            409 => "Dữ liệu đã thay đổi hoặc yêu cầu đã được xử lý. Hãy làm mới.",
            429 => "Eldorado đang giới hạn lượt gọi. App sẽ chờ trước khi đồng bộ tiếp.",
            _ when code >= 500 => "Máy chủ Eldorado gặp lỗi. Hãy kiểm tra kết quả trên website.",
            _ => $"Eldorado từ chối dữ liệu (HTTP {code}). Kiểm tra giá, đơn vị tiền, thời gian và trạng thái yêu cầu."
        };
        // Do not expose arbitrary response bodies, which can contain customer or credential data.
        await Task.CompletedTask;
        throw new ApiException(message, mutation && (code >= 500 || code == 408), delay, code);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, bool mutation)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text)) return default;
        try { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
        catch (JsonException)
        {
            throw new ApiException("Phản hồi Eldorado có định dạng không hợp lệ. Kiểm tra website trước khi tiếp tục.", mutation);
        }
    }

    private static IEnumerable<JsonElement> Results(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray().ToArray();
        if (Field(root, "results") is var results && results.ValueKind == JsonValueKind.Array)
            return results.EnumerateArray().ToArray();
        throw new ApiException("API trả về danh sách không đúng đặc tả. Hãy kiểm tra tài liệu hoặc website Eldorado.");
    }

    private static Order ReadOrder(JsonElement root)
    {
        var offer = Field(root, "orderOfferDetails");
        var money = Field(root, "totalPrice");
        DateTimeOffset? deadline = null;
        var started = Date(root, "deliveryStartedDate");
        if (started.HasValue && TimeSpan.TryParse(Text(root, "deliveryTime"), CultureInfo.InvariantCulture, out var span))
            deadline = started.Value + span;
        return new Order(Required(root, "id"), Text(offer, "offerTitle"), Text(offer, "gameId"),
            Required(Field(root, "state"), "state"), Text(offer, "category"), Text(root, "buyerUsername"),
            Number(money, "amount") ?? throw new ApiException("Phản hồi đơn hàng thiếu giá trị thanh toán."),
            Required(money, "currency"), deadline, Text(offer, "description"));
    }

    private static Offer ReadOffer(JsonElement root, string kind)
    {
        var money = Field(root, "pricePerUnit");
        // Currency list items have an open object schema. Refuse unknown shapes rather than display misleading zeroes.
        if (money.ValueKind != JsonValueKind.Object || Number(money, "amount") == null)
            throw new ApiException($"Dữ liệu offer {kind} thiếu giá theo đặc tả. Hãy quản lý loại này trên website.");
        var quantity = Number(root, "quantity");
        return new Offer(Required(root, "id"), Text(root, "offerTitle", Text(root, "gameCategoryTitle")),
            Text(root, "gameId"), kind, Number(money, "amount")!.Value, Required(money, "currency"),
            kind == "Account" || !quantity.HasValue ? null : checked((int)quantity.Value));
    }

    private static JsonElement Field(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var result) ? result : default;
    private static string Text(JsonElement root, string name, string fallback = "") =>
        Field(root, name) is var field && field.ValueKind == JsonValueKind.String ? field.GetString() ?? fallback : fallback;
    private static string Required(JsonElement root, string name) =>
        Text(root, name) is var value && value.Length > 0 ? value : throw new ApiException($"Phản hồi API thiếu {name}.");
    private static decimal? Number(JsonElement root, string name) =>
        Field(root, name) is var field && field.ValueKind == JsonValueKind.Number && field.TryGetDecimal(out var value) ? value : null;
    private static DateTimeOffset? Date(JsonElement root, string name) =>
        DateTimeOffset.TryParse(Text(root, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : null;
    private static void ValidateId(string id)
    {
        if (!Guid.TryParse(id, out _)) throw new ArgumentException("Mã Eldorado không hợp lệ.");
    }
    private static void ValidateMoney(decimal price, string currency)
    {
        if (price <= 0) throw new ArgumentException("Giá phải lớn hơn 0.");
        if (!Currencies.Contains(currency)) throw new ArgumentException("Đơn vị tiền chưa được Eldorado hỗ trợ.");
    }
}
