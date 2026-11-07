namespace Eldorado.Core;

public sealed record Credentials(string ClientId, string ClientSecret);

public sealed record Order(string Id, string Title, string Game, string State,
    string Category, string Buyer, decimal Price, string Currency, DateTimeOffset? Deadline,
    string? Description = null);

public sealed record BoostRequest(string Id, string Game, string Category,
    string Buyer, DateTimeOffset Created, string? Description = null);

public sealed record Offer(string Id, string Title, string Game, string Kind,
    decimal Price, string Currency, int? Quantity);

public sealed class ApiException : Exception
{
    public bool OutcomeUnknown { get; }
    public TimeSpan? RetryAfter { get; }
    public int? StatusCode { get; }

    public ApiException(string message, bool outcomeUnknown = false,
        TimeSpan? retryAfter = null, int? statusCode = null) : base(message)
    {
        OutcomeUnknown = outcomeUnknown;
        RetryAfter = retryAfter;
        StatusCode = statusCode;
    }
}
