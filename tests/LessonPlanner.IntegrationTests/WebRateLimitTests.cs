using System.Diagnostics;
using System.Net;
using System.Threading.RateLimiting;
using LessonPlanner.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LessonPlanner.IntegrationTests;

public sealed class WebRateLimitTests
{
    [Fact]
    public async Task Public_requests_share_one_600_per_minute_partition_across_direct_ips_and_spoofed_headers()
    {
        await using var host = await FixedOriginTestHost.StartAsync();
        var elapsed = Stopwatch.StartNew();
        for (var index = 0; index < 600; index++)
        {
            var directIp = new IPAddress(new byte[] { 192, 0, 2, (byte)(index % 200 + 1) });
            var result = await host.SendAsync(directIp: directIp, spoofHeaders: true);
            Assert.Equal(StatusCodes.Status204NoContent, result.Response.StatusCode);
            Assert.Equal(directIp.ToString(), result.Response.Headers["X-Test-Remote-IP"]);
            if (index == 59) Assert.True(elapsed.Elapsed < TimeSpan.FromMinutes(1), "60 participants must fit in a single window without concurrency saturation.");
        }
        Assert.True(elapsed.Elapsed < TimeSpan.FromMinutes(1), "The test must exhaust one window, not cross a replenishment boundary.");
        var rejected = await host.SendAsync(directIp: IPAddress.Parse("203.0.113.55"), spoofHeaders: true)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.Response.StatusCode);
        Assert.InRange(int.Parse(rejected.Response.Headers.RetryAfter.ToString(), System.Globalization.CultureInfo.InvariantCulture), 1, 60);
        Assert.Equal("no-store", rejected.Response.Headers.CacheControl);
        using var body = new StreamReader(rejected.Response.Body);
        Assert.True((await body.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5))).Length < 1024);
    }

    [Fact]
    public async Task Anonymous_partition_uses_minute_replenishment_and_never_queues_or_refunds_requests()
    {
        var policy = new AnonymousRequestRateLimitPolicy();
        var firstContext = new DefaultHttpContext();
        firstContext.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        var secondContext = new DefaultHttpContext();
        secondContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.20");
        secondContext.Request.Headers["X-Forwarded-For"] = "198.51.100.30";
        secondContext.Request.Headers["X-Forwarded-Host"] = "other.example.org";
        Assert.Equal(policy.GetPartition(firstContext).PartitionKey, policy.GetPartition(secondContext).PartitionKey);
        Assert.Equal(policy.GetPartition(firstContext).PartitionKey, policy.GetPartition(new DefaultHttpContext()).PartitionKey);
        var partition = policy.GetPartition(firstContext);
        using var limiter = Assert.IsType<FixedWindowRateLimiter>(partition.Factory(partition.PartitionKey));
        // RateLimitPartition delegates replenishment to the shared partitioned limiter's timer.
        Assert.False(limiter.IsAutoReplenishing);
        Assert.True(limiter.TryReplenish());
        Assert.Equal(TimeSpan.FromMinutes(1), limiter.ReplenishmentPeriod);
        for (var index = 0; index < 600; index++)
        {
            using var lease = limiter.AttemptAcquire();
            Assert.True(lease.IsAcquired);
        }
        var overflow = limiter.AcquireAsync();
        Assert.True(overflow.IsCompletedSuccessfully);
        using var rejected = await overflow;
        Assert.False(rejected.IsAcquired);
        Assert.True(rejected.TryGetMetadata(MetadataName.RetryAfter, out var retry));
        Assert.Equal(TimeSpan.FromMinutes(1), retry);
        var statistics = limiter.GetStatistics();
        Assert.NotNull(statistics);
        Assert.Equal(0, statistics.CurrentQueuedCount);
        Assert.Equal(0, statistics.CurrentAvailablePermits);
        Assert.Equal(600, statistics.TotalSuccessfulLeases);
    }

    [Fact]
    public async Task Global_concurrency_still_allows_only_32_in_flight_requests_without_a_queue()
    {
        await using var host = await FixedOriginTestHost.StartAsync();
        var limiter = host.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter;
        Assert.NotNull(limiter);
        var leases = new List<RateLimitLease>();
        try
        {
            for (var index = 0; index < 32; index++)
            {
                var lease = limiter.AttemptAcquire(new DefaultHttpContext());
                leases.Add(lease);
                Assert.True(lease.IsAcquired);
            }
            var overflow = limiter.AcquireAsync(new DefaultHttpContext());
            Assert.True(overflow.IsCompletedSuccessfully);
            using var rejected = await overflow;
            Assert.False(rejected.IsAcquired);
            var statistics = limiter.GetStatistics(new DefaultHttpContext());
            Assert.NotNull(statistics);
            Assert.Equal(0, statistics.CurrentQueuedCount);
            leases[0].Dispose();
            leases.RemoveAt(0);
            using var resumed = limiter.AttemptAcquire(new DefaultHttpContext());
            Assert.True(resumed.IsAcquired);
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
        }
    }
}
