using FluentAssertions;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Celesta.Bi.Pbi.XmlaProxy.Tests.TestInfrastructure;

namespace Celesta.Bi.Pbi.XmlaProxy.Tests;

public class RetryTests
{
    private const string TestRequestId = "test-request-id";

    [Fact]
    public async Task Transient_then_success_retries_and_returns()
    {
        var attempts = 0;

        var result = await FunctionReflectionBridge.ExecuteWithRetryAsync(
            operation: () =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new TimeoutException("transient timeout");
                }

                return true;
            },
            operationName: "retry-success",
            requestId: TestRequestId,
            cancellationToken: CancellationToken.None);

        result.Should().BeTrue();
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task Transient_exhausted_throws_after_max_attempts()
    {
        var attempts = 0;

        Func<Task> action = async () =>
            await FunctionReflectionBridge.ExecuteWithRetryAsync<bool>(
                operation: () =>
                {
                    attempts++;
                    throw new TimeoutException("always timeout");
                },
                operationName: "retry-exhausted",
                requestId: TestRequestId,
                cancellationToken: CancellationToken.None);

        await action.Should().ThrowAsync<TimeoutException>();
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task Non_transient_fails_without_retry()
    {
        var attempts = 0;

        Func<Task> action = async () =>
            await FunctionReflectionBridge.ExecuteWithRetryAsync<bool>(
                operation: () =>
                {
                    attempts++;
                    throw new InvalidOperationException("semantic/model error");
                },
                operationName: "non-transient",
                requestId: TestRequestId,
                cancellationToken: CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*semantic/model error*");
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task Immediate_success_no_retry()
    {
        var attempts = 0;

        var result = await FunctionReflectionBridge.ExecuteWithRetryAsync(
            operation: () =>
            {
                attempts++;
                return "ok";
            },
            operationName: "immediate-success",
            requestId: TestRequestId,
            cancellationToken: CancellationToken.None);

        result.Should().Be("ok");
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_aborts_retry()
    {
        var attempts = 0;
        using var cts = new CancellationTokenSource();
        using var firstAttemptObserved = new ManualResetEventSlim(initialState: false);

        var retryTask = FunctionReflectionBridge.ExecuteWithRetryAsync<bool>(
            operation: () =>
            {
                attempts++;
                firstAttemptObserved.Set();
                throw new TimeoutException("cancel during backoff");
            },
            operationName: "cancellation",
            requestId: TestRequestId,
            cancellationToken: cts.Token);

        firstAttemptObserved.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue("the first attempt should have started");
        cts.Cancel();

        Func<Task> action = async () => await retryTask;
        await action.Should().ThrowAsync<OperationCanceledException>();
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task Dependency_cancellation_is_transient_and_retries()
    {
        // A dependency-side cancellation (e.g. the XMLA endpoint cancels a task) surfaces as a
        // TaskCanceledException while the caller's token is NOT cancelled. It must be retried.
        var attempts = 0;

        var result = await FunctionReflectionBridge.ExecuteWithRetryAsync(
            operation: () =>
            {
                attempts++;
                if (attempts < 2)
                {
                    throw new TaskCanceledException("A task was canceled.");
                }

                return true;
            },
            operationName: "dependency-cancel",
            requestId: TestRequestId,
            cancellationToken: CancellationToken.None);

        result.Should().BeTrue();
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task Caller_cancellation_does_not_retry()
    {
        // A pre-cancelled token means the caller aborted: the loop-top guard throws before the
        // operation ever runs, so there are zero attempts and no retry.
        var attempts = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> action = async () =>
            await FunctionReflectionBridge.ExecuteWithRetryAsync<bool>(
                operation: () =>
                {
                    attempts++;
                    return true;
                },
                operationName: "caller-cancel",
                requestId: TestRequestId,
                cancellationToken: cts.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        attempts.Should().Be(0);
    }
}
