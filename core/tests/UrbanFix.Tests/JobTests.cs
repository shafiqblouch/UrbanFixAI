using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using UrbanFix.Infrastructure.Jobs;
using UrbanFix.Tests.Support;

namespace UrbanFix.Tests;

public static class FlakyJobs
{
    public static int Calls;
    public static void AlwaysFails() { Interlocked.Increment(ref Calls); throw new InvalidOperationException("job boom"); }
}

[Collection("db")]
public class JobTests(TestApp app)
{
    [Fact]
    public async Task Failing_job_retries_then_lands_in_the_failed_queue_and_raises_the_alert_hook()
    {
        var storage = app.Services.GetRequiredService<JobStorage>();
        var notifier = (RecordingNotifier)app.Services.GetRequiredService<IJobFailureNotifier>();
        var monitoring = storage.GetMonitoringApi();
        var failedBefore = monitoring.FailedCount();
        FlakyJobs.Calls = 0;

        using var server = new BackgroundJobServer(new BackgroundJobServerOptions
        {
            SchedulePollingInterval = TimeSpan.FromMilliseconds(250),
            WorkerCount = 2,
            Queues = ["default"],
        }, storage);
        var client = app.Services.GetRequiredService<IBackgroundJobClient>();
        var jobId = client.Enqueue(() => FlakyJobs.AlwaysFails());

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!notifier.Failures.Any(x => x.JobId == jobId) && DateTime.UtcNow < deadline) await Task.Delay(250);

        var det = monitoring.JobDetails(jobId);
        var hist = string.Join(" -> ", det.History.Select(h => h.StateName + ":" + h.Reason));
        Assert.True(failedBefore + 1 == monitoring.FailedCount(), $"history: {hist}; calls={FlakyJobs.Calls}");
        Assert.Equal(1 + 2, FlakyJobs.Calls); // first run + Jobs:Attempts retries (configured to 2 for tests)
        Assert.Contains(notifier.Failures, f => f.JobId == jobId);
    }
}

public sealed class RecordingNotifier : IJobFailureNotifier
{
    public List<(string JobId, string Job, string? Reason)> Failures { get; } = [];
    public void JobFailed(string jobId, string jobName, string? reason) { lock (Failures) Failures.Add((jobId, jobName, reason)); }
}
