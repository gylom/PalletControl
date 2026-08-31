using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Diagnostics;

public sealed record SystemTelemetrySample(
    DateTime TimestampUtc,
    double CpuPercent,
    long ProcessMemoryBytes,
    long ManagedMemoryBytes,
    long RequestsPerMinute,
    double AverageResponseMs,
    long Http4xx,
    long Http5xx);

public sealed record EndpointTelemetry(string Path, long Requests, double AverageMs, long Errors, int LastStatusCode);
public sealed record RecentServerError(DateTime TimestampUtc, string Path, string Message);

public sealed record SystemTelemetrySnapshot(
    DateTime StartedAtUtc,
    double UptimeSeconds,
    int ActiveUsersLast15Minutes,
    long TotalRequests,
    long RequestsLastMinute,
    double AverageResponseMs,
    long Http4xx,
    long Http5xx,
    long Unauthorized401,
    long Forbidden403,
    long RateLimited429,
    double CpuPercent,
    long ProcessMemoryBytes,
    long ManagedMemoryBytes,
    List<SystemTelemetrySample> History,
    List<EndpointTelemetry> Endpoints,
    List<RecentServerError> RecentErrors);

public sealed class SystemTelemetryService : BackgroundService
{
    private sealed class EndpointCounter
    {
        public long Count;
        public long TotalTicks;
        public long Errors;
        public int LastStatus;
    }

    private readonly DateTime _startedAtUtc = DateTime.UtcNow;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly ConcurrentDictionary<string, EndpointCounter> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _activeUsers = new();
    private readonly ConcurrentQueue<(DateTime TimestampUtc, long Ticks)> _recentRequests = new();
    private readonly ConcurrentQueue<SystemTelemetrySample> _history = new();
    private readonly ConcurrentQueue<RecentServerError> _recentErrors = new();
    private long _totalRequests;
    private long _http4xx;
    private long _http5xx;
    private long _unauthorized401;
    private long _forbidden403;
    private long _rateLimited429;
    private double _cpuPercent;
    private TimeSpan _lastCpu = TimeSpan.Zero;
    private DateTime _lastCpuAtUtc = DateTime.UtcNow;

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var sw = Stopwatch.StartNew();
        Exception? error = null;
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            error = ex;
            throw;
        }
        finally
        {
            sw.Stop();
            // Production exception middleware converts exceptions to a generic 500 response.
            // The exception feature lets SuperAdmin monitoring still record the internal
            // exception summary without exposing it to the public response.
            error ??= context.Features.Get<IExceptionHandlerFeature>()?.Error;
            Record(context, sw.Elapsed, error);
        }
    }

    public SystemTelemetrySnapshot Snapshot()
    {
        TrimRequestTimes();
        _process.Refresh();
        var total = Interlocked.Read(ref _totalRequests);
        var endpoints = _endpoints
            .Select(kvp => new EndpointTelemetry(
                kvp.Key,
                Interlocked.Read(ref kvp.Value.Count),
                TicksToMs(Interlocked.Read(ref kvp.Value.TotalTicks), Interlocked.Read(ref kvp.Value.Count)),
                Interlocked.Read(ref kvp.Value.Errors),
                Volatile.Read(ref kvp.Value.LastStatus)))
            .OrderByDescending(x => x.Requests)
            .Take(20)
            .ToList();

        var cutoff = DateTime.UtcNow.AddMinutes(-15);
        var active = _activeUsers.Count(x => x.Value >= cutoff);
        return new SystemTelemetrySnapshot(
            _startedAtUtc,
            (DateTime.UtcNow - _startedAtUtc).TotalSeconds,
            active,
            total,
            _recentRequests.Count,
            RecentAverageMs(),
            Interlocked.Read(ref _http4xx),
            Interlocked.Read(ref _http5xx),
            Interlocked.Read(ref _unauthorized401),
            Interlocked.Read(ref _forbidden403),
            Interlocked.Read(ref _rateLimited429),
            _cpuPercent,
            _process.WorkingSet64,
            GC.GetTotalMemory(false),
            _history.ToList(),
            endpoints,
            _recentErrors.Reverse().Take(20).ToList());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lastCpu = _process.TotalProcessorTime;
        _lastCpuAtUtc = DateTime.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            Sample();
        }
    }

    private void Record(HttpContext context, TimeSpan elapsed, Exception? error)
    {
        var path = context.Request.Path.Value ?? "/";
        if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)) return;
        if (path.StartsWith("/api/health", StringComparison.OrdinalIgnoreCase)) return;

        Interlocked.Increment(ref _totalRequests);
        _recentRequests.Enqueue((DateTime.UtcNow, elapsed.Ticks));
        TrimRequestTimes();

        var status = context.Response.StatusCode;
        if (status >= 400 && status < 500) Interlocked.Increment(ref _http4xx);
        if (status >= 500 || error is not null) Interlocked.Increment(ref _http5xx);
        if (status == 401) Interlocked.Increment(ref _unauthorized401);
        if (status == 403) Interlocked.Increment(ref _forbidden403);
        if (status == 429) Interlocked.Increment(ref _rateLimited429);

        var route = context.GetEndpoint() is RouteEndpoint routeEndpoint
            ? routeEndpoint.RoutePattern.RawText ?? path
            : path;
        var counter = _endpoints.GetOrAdd(route, _ => new EndpointCounter());
        Interlocked.Increment(ref counter.Count);
        Interlocked.Add(ref counter.TotalTicks, elapsed.Ticks);
        if (status >= 500 || error is not null) Interlocked.Increment(ref counter.Errors);
        Volatile.Write(ref counter.LastStatus, status);

        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(userId)) _activeUsers[userId] = DateTime.UtcNow;

        if (error is not null)
        {
            _recentErrors.Enqueue(new RecentServerError(DateTime.UtcNow, path, error.Message));
            while (_recentErrors.Count > 50) _recentErrors.TryDequeue(out _);
        }
    }

    private void Sample()
    {
        _process.Refresh();
        var now = DateTime.UtcNow;
        var cpuNow = _process.TotalProcessorTime;
        var wallMs = Math.Max(1, (now - _lastCpuAtUtc).TotalMilliseconds);
        var cpuMs = Math.Max(0, (cpuNow - _lastCpu).TotalMilliseconds);
        _cpuPercent = Math.Clamp(cpuMs / (wallMs * Math.Max(1, Environment.ProcessorCount)) * 100.0, 0, 100);
        _lastCpu = cpuNow;
        _lastCpuAtUtc = now;

        TrimRequestTimes();
        _history.Enqueue(new SystemTelemetrySample(
            now,
            Math.Round(_cpuPercent, 1),
            _process.WorkingSet64,
            GC.GetTotalMemory(false),
            _recentRequests.Count,
            Math.Round(RecentAverageMs(), 1),
            Interlocked.Read(ref _http4xx),
            Interlocked.Read(ref _http5xx)));
        while (_history.Count > 240) _history.TryDequeue(out _); // two hours at 30-second sampling

        var activeCutoff = now.AddHours(-1);
        foreach (var entry in _activeUsers.Where(x => x.Value < activeCutoff).ToList())
            _activeUsers.TryRemove(entry.Key, out _);
    }

    private void TrimRequestTimes()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        while (_recentRequests.TryPeek(out var request) && request.TimestampUtc < cutoff)
            _recentRequests.TryDequeue(out _);
    }

    private double RecentAverageMs()
    {
        var rows = _recentRequests.ToArray();
        if (rows.Length == 0) return 0;
        var ticks = rows.Sum(x => x.Ticks);
        return TicksToMs(ticks, rows.Length);
    }

    private static double TicksToMs(long ticks, long count) =>
        count <= 0 ? 0 : TimeSpan.FromTicks(ticks / count).TotalMilliseconds;
}
