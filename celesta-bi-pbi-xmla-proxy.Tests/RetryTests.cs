using Celesta.Bi.Pbi.XmlaProxy.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Celesta.Bi.Pbi.XmlaProxy.Tests;

/// <summary>
/// Retry behaviour, driven black-box through HandleAsync with a fake connection whose Open()
/// fails in controlled ways. (MaxAttempts is forced to 3 with 1s backoff by TestEnvironment.)
/// </summary>
public class RetryTests
{
    [Fact]
    public async Task Dependency_cancellation_retries_then_succeeds()
    {
        // A dependency-side cancellation (caller token NOT aborted) is transient: fail twice, then succeed.
        var fake = new FakeXmlaConnection(
            onOpen: attempt => attempt <= 2 ? new TaskCanceledException("A task was canceled.") : null);
        var sut = new Function(fake.AsFactory());
        var context = HttpContextFactory.CreateValidPostContext();

        await sut.HandleAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        fake.OpenCalls.Should().Be(3);
    }

    [Fact]
    public async Task Transient_failure_exhausts_retries_and_returns_504()
    {
        var fake = new FakeXmlaConnection(onOpen: _ => new TimeoutException("always times out"));
        var sut = new Function(fake.AsFactory());
        var context = HttpContextFactory.CreateValidPostContext();

        await sut.HandleAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status504GatewayTimeout);
        fake.OpenCalls.Should().Be(3);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_retried_and_returns_499()
    {
        // Even though the failure would be transient, a caller-aborted request must not retry.
        var fake = new FakeXmlaConnection(onOpen: _ => new TimeoutException("would be transient"));
        var sut = new Function(fake.AsFactory());
        var context = HttpContextFactory.CreateValidPostContext();
        context.RequestAborted = new CancellationToken(canceled: true);

        await sut.HandleAsync(context);

        context.Response.StatusCode.Should().Be(499);
        fake.OpenCalls.Should().Be(0); // the loop-top guard throws before the operation runs
    }

    [Fact]
    public async Task Non_transient_open_error_is_not_retried_and_returns_500()
    {
        var fake = new FakeXmlaConnection(onOpen: _ => new InvalidOperationException("boom"));
        var sut = new Function(fake.AsFactory());
        var context = HttpContextFactory.CreateValidPostContext();

        await sut.HandleAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        fake.OpenCalls.Should().Be(1);
    }
}
