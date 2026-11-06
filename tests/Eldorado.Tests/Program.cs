using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Eldorado.Core;

const string RequestId = "11111111-1111-1111-1111-111111111111";
const string OtherId = "22222222-2222-2222-2222-222222222222";
var request = new BoostRequest(RequestId, "Roblox", "Leveling", "buyer", DateTimeOffset.UtcNow);
string activePage = JsonSerializer.Serialize(new { results = new[] { new { id = RequestId, gameId = "Roblox", boostingCategoryTitle = "Leveling", buyerUsername = "buyer", createdDate = DateTimeOffset.UtcNow } } });
int passed = 0;

await Check("Token reused and Bearer authorization attached", async () =>
{
    int tokenCalls = 0;
    using var server = new FakeHandler(message =>
    {
        if (IsToken(message)) { tokenCalls++; return Json(Token("token-1")); }
        Require(message.Headers.Authorization?.ToString() == "Bearer token-1", "Bearer token missing");
        return Json(activePage);
    });
    var api = Client(server);
    await api.GetRequestsAsync();
    await api.GetRequestsAsync();
    Require(tokenCalls == 1, "Valid token should be reused");
});

await Check("Expired token renewed before next request", async () =>
{
    int tokenCalls = 0;
    using var server = new FakeHandler(message => IsToken(message)
        ? Json(Token($"token-{++tokenCalls}", 0)) : Json(activePage));
    var api = Client(server);
    await api.GetRequestsAsync();
    await api.GetRequestsAsync();
    Require(tokenCalls == 2, "Expired token was reused");
});

await Check("Read-only 401 renews once", async () =>
{
    int tokenCalls = 0, readCalls = 0;
    using var server = new FakeHandler(message =>
    {
        if (IsToken(message)) return Json(Token($"token-{++tokenCalls}"));
        return ++readCalls == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Json(activePage);
    });
    var rows = await Client(server).GetRequestsAsync();
    Require(rows.Single().Id == RequestId && tokenCalls == 2 && readCalls == 2, "Read refresh failed");
});

await Check("Quote binds price and delivery to selected request", async () =>
{
    int writes = 0;
    using var server = new FakeHandler(message =>
    {
        if (IsToken(message)) return Json(Token("token"));
        if (message.Method == HttpMethod.Get) return Json(activePage);
        writes++;
        Require(message.RequestUri!.AbsolutePath == "/api/boostingOffers", "Wrong quote endpoint");
        using var document = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        var details = document.RootElement.GetProperty("details");
        var money = details.GetProperty("pricing").GetProperty("pricePerUnit");
        Require(details.GetProperty("boostingRequestId").GetString() == RequestId, "Wrong buyer request");
        Require(details.GetProperty("guaranteedDeliveryTime").GetString() == "Day2", "Wrong delivery time");
        Require(money.GetProperty("amount").GetDecimal() == 12.34m && money.GetProperty("currency").GetString() == "USD", "Wrong quote money");
        return Json(JsonSerializer.Serialize(new { id = OtherId, boostingRequestId = RequestId }), HttpStatusCode.Created);
    });
    await Client(server).SendQuoteAsync(request, 12.34m, "USD", "Day2");
    Require(writes == 1, "Quote sent more than once");
});

await Check("Closed request blocked before write", async () =>
{
    int writes = 0;
    using var server = new FakeHandler(message =>
    {
        if (IsToken(message)) return Json(Token("token"));
        if (message.Method != HttpMethod.Get) writes++;
        return Json("{\"results\":[]}");
    });
    await ExpectApi(() => Client(server).SendQuoteAsync(request, 10m, "USD", "Day1"), false);
    Require(writes == 0, "Closed request received an offer");
});

await Check("Lost quote response stays unknown and cannot resend", async () =>
{
    int writes = 0;
    using var server = new FakeHandler(message =>
    {
        if (IsToken(message)) return Json(Token("token"));
        if (message.Method == HttpMethod.Get) return Json(activePage);
        writes++;
        throw new HttpRequestException("Connection closed after receiving body");
    });
    var api = Client(server);
    await ExpectApi(() => api.SendQuoteAsync(request, 10m, "USD", "Day1"), true);
    await ExpectApi(() => api.SendQuoteAsync(request, 10m, "USD", "Day1"), true);
    Require(writes == 1, "Unknown mutation was retried");
});

await Check("Concurrent send attempts result in one quote", async () =>
{
    int writes = 0;
    using var server = new FakeHandler(message =>
    {
        if (IsToken(message)) return Json(Token("token"));
        if (message.Method == HttpMethod.Get) return Json(activePage);
        Interlocked.Increment(ref writes);
        return Json(JsonSerializer.Serialize(new { id = OtherId, boostingRequestId = RequestId }), HttpStatusCode.Created);
    });
    var api = Client(server);
    async Task<bool> Send()
    {
        try { await api.SendQuoteAsync(request, 10m, "USD", "Day1"); return true; }
        catch (ApiException) { return false; }
    }
    var results = await Task.WhenAll(Send(), Send());
    Require(writes == 1 && results.Count(x => x) == 1, "Double-click created duplicate quote");
});

await Check("Mismatched quote acknowledgement remains unknown", async () =>
{
    using var server = new FakeHandler(message => IsToken(message) ? Json(Token("token"))
        : message.Method == HttpMethod.Get ? Json(activePage)
        : Json(JsonSerializer.Serialize(new { id = OtherId, boostingRequestId = OtherId }), HttpStatusCode.Created));
    await ExpectApi(() => Client(server).SendQuoteAsync(request, 10m, "USD", "Day1"), true);
});

await Check("Mutation 401 is never automatically retried", async () =>
{
    int writes = 0;
    using var server = new FakeHandler(message =>
    {
        if (IsToken(message)) return Json(Token("token"));
        writes++;
        return new HttpResponseMessage(HttpStatusCode.Unauthorized);
    });
    var offer = new Offer(RequestId, "offer", "Roblox", "Predefined", 1m, "USD", 5);
    await ExpectApi(() => Client(server).ChangePriceAsync(offer, 2m), false);
    Require(writes == 1, "Mutation was retried after 401");
});

await Check("Rate limit preserves server Retry-After", async () =>
{
    using var server = new FakeHandler(message =>
    {
        if (IsToken(message)) return Json(Token("token"));
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(180));
        return response;
    });
    try { await Client(server).GetRequestsAsync(); throw new InvalidOperationException("Expected rate limit"); }
    catch (ApiException error) { Require(error.RetryAfter == TimeSpan.FromSeconds(180), "Retry-After missing"); }
});

await Check("SQLite baseline and drafts survive reopening", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "EldoradoTests", Guid.NewGuid().ToString("N"));
    string path = Path.Combine(directory, "test.db");
    using (var store = new LocalStore(path))
    {
        Require(store.Observe("orders/shop-a", new[] { RequestId }).Count == 0, "First sync notified historic data");
        Require(store.Observe("orders/shop-a", new[] { RequestId, OtherId, OtherId }).SequenceEqual(new[] { OtherId }), "Duplicate IDs notified twice");
        store.SaveDraft(new QuoteDraft(RequestId, 12.34m, "USD", "Day2", "Sending"));
    }
    using (var store = new LocalStore(path))
    {
        Require(store.Observe("orders/shop-a", new[] { RequestId, OtherId }).Count == 0, "Restart repeated notifications");
        Require(store.Observe("requests/shop-a", new[] { RequestId }).Count == 0, "Streams share a baseline");
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("vi-VN");
            Require(store.LoadDraft(RequestId) == new QuoteDraft(RequestId, 12.34m, "USD", "Day2", "Sending"), "Draft or interrupted status lost");
            store.SetStatus(RequestId, "Unknown");
            Require(store.GetStatus(RequestId) == "Unknown", "Unknown state not persisted");
        }
        finally { CultureInfo.CurrentCulture = before; }
    }
    return Task.CompletedTask;
});

await Check("Order schema retains state, money, category and deadline", async () =>
{
    using var server = new FakeHandler(message =>
    {
        if (IsToken(message)) return Json(Token("token"));
        Require(message.RequestUri!.AbsolutePath == "/api/v1/orders/me/seller/orders", "Wrong orders endpoint");
        Require(message.RequestUri.Query.Contains("displayFilter=DisplaySellingOrders"), "Seller filter missing");
        return Json(JsonSerializer.Serialize(new
        {
            results = new[]
            {
                new
                {
                    id = RequestId,
                    buyerUsername = "boost-buyer",
                    state = new { state = "Paid", createdDate = "2026-11-06T00:00:00Z" },
                    totalPrice = new { amount = 19.25m, currency = "EUR" },
                    orderOfferDetails = new { offerTitle = "Complete quest", gameId = "Roblox", category = "RequestedBoosting" },
                    deliveryStartedDate = "2026-11-06T00:00:00Z",
                    deliveryTime = "1.02:00:00"
                }
            }
        }));
    });
    var order = (await Client(server).GetOrdersAsync()).Single();
    Require(order.Id == RequestId && order.Title == "Complete quest" && order.Game == "Roblox", "Order identity lost");
    Require(order.State == "Paid" && order.Category == "RequestedBoosting" && order.Buyer == "boost-buyer", "Order classification lost");
    Require(order.Price == 19.25m && order.Currency == "EUR", "Order money lost");
    Require(order.Deadline == DateTimeOffset.Parse("2026-11-07T02:00:00Z", CultureInfo.InvariantCulture), "Delivery deadline incorrect");
});

await Check("Deliver uses current endpoint and refuses completed orders", async () =>
{
    int writes = 0;
    using var server = new FakeHandler(message =>
    {
        if (IsToken(message)) return Json(Token("token"));
        writes++;
        Require(message.Method == HttpMethod.Put, "Delivery must use PUT");
        Require(message.RequestUri!.AbsolutePath == $"/api/v1/orders/me/{RequestId}/deliver", "Wrong delivery endpoint");
        Require(message.Content is null, "Unexpected delivery body");
        return Json("{}");
    });
    var api = Client(server);
    var paid = new Order(RequestId, "Quest", "Roblox", "Paid", "RequestedBoosting", "buyer", 10m, "USD", null);
    await api.DeliverAsync(paid);
    await ExpectApi(() => api.DeliverAsync(paid with { State = "Completed" }), false);
    Require(writes == 1, "Completed order was mutated");
});

await Check("Offer families use dedicated endpoints and scalar quantity", async () =>
{
    var expected = new Queue<(string Path, bool Price)>(new[]
    {
        ($"/api/flexibleOffersUser/me/{RequestId}/changePrice", true),
        ($"/api/predefinedOffersUser/me/{RequestId}/changePrice", true),
        ($"/api/predefinedOffersUser/me/{RequestId}/changeQuantity", false),
        ($"/api/v1/item-management/me/offers/{RequestId}/price", true),
        ($"/api/v1/item-management/me/offers/{RequestId}/quantity", false),
        ($"/api/v1/currency-management/me/offers/{RequestId}/change-price", true),
        ($"/api/v1/currency-management/me/offers/{RequestId}/change-quantity", false)
    });
    using var server = new FakeHandler(message =>
    {
        if (IsToken(message)) return Json(Token("token"));
        Require(message.Method == HttpMethod.Put, "Offer mutation must use PUT");
        var next = expected.Dequeue();
        Require(message.RequestUri!.AbsolutePath == next.Path, "Wrong offer-family endpoint");
        using var body = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        if (next.Price)
        {
            Require(body.RootElement.GetProperty("amount").GetDecimal() == 8.75m, "Price amount incorrect");
            Require(body.RootElement.GetProperty("currency").GetString() == "EUR", "Existing currency must be retained");
        }
        else Require(body.RootElement.ValueKind == JsonValueKind.Number && body.RootElement.GetInt32() == 7, "Quantity must be a bare integer");
        return Json("{}");
    });
    var api = Client(server);
    foreach (string kind in new[] { "Account", "Predefined", "CustomItem", "Currency" })
    {
        var offer = new Offer(RequestId, "Offer", "Roblox", kind, 1m, "EUR", kind == "Account" ? null : 10);
        await api.ChangePriceAsync(offer, 8.75m);
        if (kind != "Account") await api.ChangeQuantityAsync(offer, 7);
    }
    Require(expected.Count == 0, "Expected offer updates missing");
});

await Check("Unsupported account quantity blocked without transmission", async () =>
{
    int calls = 0;
    using var server = new FakeHandler(message => { calls++; return Json("{}"); });
    var account = new Offer(RequestId, "Account", "Roblox", "Account", 1m, "USD", null);
    await ExpectApi(() => Client(server).ChangeQuantityAsync(account, 2), false);
    Require(calls == 0, "Unsupported quantity sent to server");
});

await Check("Reconciled unsent quote can retry after an unknown outcome", async () =>
{
    int writes = 0;
    using var server = new FakeHandler(message =>
    {
        if (IsToken(message)) return Json(Token("token"));
        if (message.Method == HttpMethod.Get) return Json(activePage);
        if (++writes == 1) throw new HttpRequestException("Lost response");
        return Json(JsonSerializer.Serialize(new { id = OtherId, boostingRequestId = RequestId }), HttpStatusCode.Created);
    });
    var api = Client(server);
    await ExpectApi(() => api.SendQuoteAsync(request, 10m, "USD", "Day1"), true);
    await ExpectApi(() => api.SendQuoteAsync(request, 10m, "USD", "Day1"), true);
    Require(writes == 1, "Unknown quote was automatically replayed");
    await api.AcknowledgeUnsentQuoteAsync(RequestId);
    await api.SendQuoteAsync(request, 10m, "USD", "Day1");
    Require(writes == 2, "Explicitly reconciled quote could not be sent again");
});

await Check("Malformed currency offers preserve healthy families and report warnings", async () =>
{
    using var server = new FakeHandler(message =>
    {
        if (IsToken(message)) return Json(Token("token"));
        string path = message.RequestUri!.AbsolutePath;
        if (path == "/api/flexibleOffers/me/search")
            return Json(JsonSerializer.Serialize(new { results = new[] { new { id = RequestId, offerTitle = "Account offer", gameId = "Roblox", quantity = 1, pricePerUnit = new { amount = 10m, currency = "USD" } } } }));
        if (path == "/api/v1/currency-management/me/offers")
            return Json(JsonSerializer.Serialize(new { results = new[] { new { id = OtherId, opaqueField = true } } }));
        return Json("{\"results\":[]}");
    });
    var api = Client(server);
    var offers = await api.GetOffersAsync();
    Require(offers.Count == 1 && offers[0].Kind == "Account" && offers[0].Price == 10m, "Healthy offers lost due to another family");
    Require(api.OfferWarnings.Count > 0, "Malformed family was silently skipped");
});

Console.WriteLine($"PASS: {passed} regression scenarios");
return;

async Task Check(string name, Func<Task> test)
{
    await test();
    passed++;
    Console.WriteLine($"PASS {name}");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static SellerApi Client(FakeHandler handler) => new(new HttpClient(handler, disposeHandler: false)
    { BaseAddress = new Uri("https://example.invalid/") }, () => Task.FromResult(new Credentials("test-client", "test-secret")));

static bool IsToken(HttpRequestMessage message) => message.RequestUri!.AbsolutePath == "/api/authentication/seller/token";
static string Token(string value, int expires = 900) => JsonSerializer.Serialize(new { accessToken = value, expiresIn = expires, tokenType = "Bearer" });
static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(value, Encoding.UTF8, "application/json") };

static async Task ExpectApi(Func<Task> operation, bool unknown)
{
    try { await operation(); }
    catch (ApiException error)
    {
        Require(error.OutcomeUnknown == unknown, "Unexpected outcome certainty");
        return;
    }
    throw new InvalidOperationException("Expected API operation to fail");
}

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(send(request));
}
