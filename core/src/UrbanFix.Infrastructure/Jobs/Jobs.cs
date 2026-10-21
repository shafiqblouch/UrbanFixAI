using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.Logging;

namespace UrbanFix.Infrastructure.Jobs;

/// <summary>Called when a job has used up its retries and landed in the Failed queue. Replace with paging/Sentry later.</summary>
public interface IJobFailureNotifier
{
    void JobFailed(string jobId, string jobName, string? reason);
}

public sealed class LoggingJobFailureNotifier(ILogger<LoggingJobFailureNotifier> log) : IJobFailureNotifier
{
    public void JobFailed(string jobId, string jobName, string? reason)
        => log.LogError("Background job {JobId} ({Job}) failed permanently: {Reason}", jobId, jobName, reason);
}

/// <summary>Fires the alert hook once a job reaches the Failed state (i.e. after automatic retries are exhausted).</summary>
public sealed class FailureAlertFilter(IJobFailureNotifier notifier) : JobFilterAttribute, IApplyStateFilter
{
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        if (context.NewState is FailedState failed)
            notifier.JobFailed(context.BackgroundJob.Id, context.BackgroundJob.Job.ToString(), failed.Exception?.Message);
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction) { }
}

public sealed class JobOptions
{
    /// <summary>Retries before a job lands in the Failed queue.</summary>
    public int Attempts { get; set; } = 5;
    public int[]? DelaysInSeconds { get; set; }

    public int[] EffectiveDelays => DelaysInSeconds is { Length: > 0 } d ? d : [30, 120, 600, 1800, 7200];
}
