using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Eldorado.Core;

internal static class CatalogChecks
{
    public static async Task RunAsync()
    {
        string folder = Path.Combine(Path.GetTempPath(), "catalog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "prices.json");
            int phase = 0;
            using var http = new HttpClient(new Handler(request =>
            {
                Require(request.RequestUri!.AbsoluteUri == PriceCatalogService.Source, "Unexpected source");
                Require(request.Headers.Authorization is null, "Credentials sent to public catalog");
                Require(request.Headers.CacheControl?.NoCache == true, "Caching must revalidate");
                if (phase == 1)
                {
                    Require(request.Headers.IfNoneMatch.Single().Tag == "\"one\"", "ETag not reused");
                    return new HttpResponseMessage(HttpStatusCode.NotModified);
                }
                if (phase == 3) return Json("{bad json");
                if (phase == 4) throw new HttpRequestException("offline");
                var response = Json(Catalog(phase == 2 ? "2.75" : "1.25"));
                response.Headers.ETag = new EntityTagHeaderValue(phase == 2 ? "\"two\"" : "\"one\"");
                return response;
            }));
            var service = new PriceCatalogService(http, path);
            Require(await service.RefreshAsync(), "Initial download failed");
            Require(service.Current!.Items.Single().Price == 1.25m && service.Current.Currency == "USD", "Wrong initial price");
            Console.WriteLine("PASS Public catalog initial download and exact decimal price");
            phase = 1;
            Require(!await service.RefreshAsync() && service.Current.Items.Single().Price == 1.25m, "304 changed catalog");
            Console.WriteLine("PASS Conditional catalog revalidation preserves items");
            phase = 2;
            await service.RefreshAsync();
            Require(service.Current.Items.Single().Price == 2.75m, "Changed price not applied");
            Console.WriteLine("PASS Remote price changes appear without reinstall");
            phase = 3;
            try { await service.RefreshAsync(); throw new Exception("Invalid catalog accepted"); }
            catch (ArgumentException) { }
            Require(service.Current.Items.Single().Price == 2.75m, "Bad update replaced good catalog");
            Console.WriteLine("PASS Invalid update keeps last valid prices");
            phase = 4;
            var reopened = new PriceCatalogService(http, path);
            Require(reopened.Current!.Items.Single().Price == 2.75m, "Cache lost on restart");
            try { await reopened.RefreshAsync(); throw new Exception("Offline refresh succeeded"); }
            catch (HttpRequestException) { }
            Require(reopened.Current.Items.Single().Price == 2.75m, "Offline read lost cache");
            Console.WriteLine("PASS Restart and offline preserve latest valid cache");
            foreach (string invalid in new[] { Catalog("-1"), Catalog("0"), Catalog("1").Replace("Hour1", "Invalid"), Catalog("1").Replace("USD", "BAD"), Catalog("1").Replace("\"version\":1", "\"version\":2") })
            {
                try { PriceCatalogService.Parse(invalid); throw new Exception("Invalid price policy accepted"); }
                catch (ArgumentException) { }
            }
            Console.WriteLine("PASS Invalid amounts, delivery, currency and version rejected");
            File.WriteAllText(path, "corrupt cache");
            var recovering = new PriceCatalogService(http, path);
            Require(recovering.Current is null, "Corrupt cache accepted");
            phase = 0;
            await recovering.RefreshAsync();
            Require(recovering.Current is not null, "Corrupt cache prevented recovery");
            Console.WriteLine("PASS Corrupt cache recovers from valid remote download");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static string Catalog(string price) => "{\"version\":1,\"currency\":\"USD\",\"updated\":\"2026-11-06\",\"items\":[{\"name\":\"Portal Fruit\",\"price\":" + price + ",\"delivery\":\"Hour1\"}]}";
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
