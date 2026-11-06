namespace Eldorado.Core;

/// <summary>Only fresh snapshots may replace displayed data or advance notification baselines.</summary>
public sealed record StreamSnapshot<T>(IReadOnlyList<T> Items, bool IsFresh,
    DateTimeOffset? LastSuccess, string? Error, DateTimeOffset NextAttempt);

/// <summary>Orders and boosting requests have independent schedules, errors and in-flight guards.</summary>
public sealed class ShopPoller
{
    public static TimeSpan RefreshInterval { get; } = TimeSpan.FromSeconds(15);
    private readonly PollingStream<Order> orders;
    private readonly PollingStream<BoostRequest> requests;

    public ShopPoller(SellerApi api, TimeProvider? clock = null)
        : this(api.GetOrdersAsync, api.GetRequestsAsync, clock) { }

    public ShopPoller(Func<Task<List<Order>>> orders,
        Func<Task<List<BoostRequest>>> requests, TimeProvider? clock = null)
    {
        this.orders = new PollingStream<Order>(orders, clock ?? TimeProvider.System);
        this.requests = new PollingStream<BoostRequest>(requests, clock ?? TimeProvider.System);
    }

    public Task<StreamSnapshot<Order>> PollOrdersAsync(bool force = false) => orders.PollAsync(force);
    public Task<StreamSnapshot<BoostRequest>> PollRequestsAsync(bool force = false) => requests.PollAsync(force);

    private sealed class PollingStream<T>(Func<Task<List<T>>> read, TimeProvider clock)
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private StreamSnapshot<T> snapshot = new(Array.Empty<T>(), false, null, null, DateTimeOffset.MinValue);
        private int failures;
        private bool serverLimited;

        public async Task<StreamSnapshot<T>> PollAsync(bool force)
        {
            if (!await gate.WaitAsync(0)) return snapshot with { IsFresh = false };
            try
            {
                var now = clock.GetUtcNow();
                if (snapshot.NextAttempt > now && (!force || serverLimited))
                    return snapshot with { IsFresh = false };
                try
                {
                    // Commit only a complete, successful read. A failed read never becomes fresh data.
                    var items = await read();
                    var completed = clock.GetUtcNow();
                    // Anchor cadence to the read start so a 15-second UI timer does not
                    // skip every other tick merely because a successful HTTP read took time.
                    var nextAttempt = now + RefreshInterval;
                    if (nextAttempt < completed) nextAttempt = completed;
                    snapshot = new StreamSnapshot<T>(Array.AsReadOnly(items.ToArray()), true,
                        completed, null, nextAttempt);
                    failures = 0;
                    serverLimited = false;
                    return snapshot;
                }
                catch (Exception error)
                {
                    failures = Math.Min(failures + 1, 6);
                    var retry = TimeSpan.FromSeconds(Math.Min(300, 15 * Math.Pow(2, failures - 1)));
                    var apiError = error as ApiException;
                    serverLimited = apiError?.StatusCode == 429;
                    if (apiError?.RetryAfter is TimeSpan serverRetry && serverRetry > retry)
                        retry = serverRetry;
                    var message = error switch
                    {
                        ApiException => error.Message,
                        HttpRequestException => "Không kết nối được Eldorado. Đang chờ thử lại.",
                        TaskCanceledException => "Kết nối quá thời gian chờ. Đang chờ thử lại.",
                        _ => "Chưa đọc được dữ liệu Eldorado. Đang chờ thử lại."
                    };
                    snapshot = snapshot with
                    {
                        IsFresh = false,
                        Error = message,
                        NextAttempt = clock.GetUtcNow() + retry
                    };
                    return snapshot;
                }
            }
            finally { gate.Release(); }
        }
    }
}
