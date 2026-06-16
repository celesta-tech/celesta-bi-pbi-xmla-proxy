using Celesta.Bi.Pbi.XmlaProxy.Tests.TestInfrastructure;
using FluentAssertions;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using Xunit;

namespace Celesta.Bi.Pbi.XmlaProxy.Tests;

public class StatusClassificationTests
{
    [Fact]
    public void TimeoutException_maps_to_504()
    {
        FunctionReflectionBridge.ClassifyTopLevelStatusCode(
            new TimeoutException("operation timed out"), callerAborted: false)
            .Should().Be(504);
    }

    [Fact]
    public void Non_caller_cancellation_maps_to_504()
    {
        FunctionReflectionBridge.ClassifyTopLevelStatusCode(
            new OperationCanceledException("A task was canceled."), callerAborted: false)
            .Should().Be(504);
    }

    [Fact]
    public void Caller_cancellation_maps_to_499()
    {
        FunctionReflectionBridge.ClassifyTopLevelStatusCode(
            new OperationCanceledException("A task was canceled."), callerAborted: true)
            .Should().Be(499);
    }

    [Fact]
    public void Caller_cancellation_takes_precedence_over_timeout()
    {
        // A cancellation wrapping a timeout, with the caller aborted, is still a 499.
        var ex = new OperationCanceledException("canceled", new TimeoutException("timed out"));
        FunctionReflectionBridge.ClassifyTopLevelStatusCode(ex, callerAborted: true)
            .Should().Be(499);
    }

    [Fact]
    public void SocketException_maps_to_502()
    {
        FunctionReflectionBridge.ClassifyTopLevelStatusCode(
            new SocketException(), callerAborted: false)
            .Should().Be(502);
    }

    [Fact]
    public void IOException_maps_to_502()
    {
        FunctionReflectionBridge.ClassifyTopLevelStatusCode(
            new IOException("connection forcibly closed"), callerAborted: false)
            .Should().Be(502);
    }

    [Fact]
    public void HttpRequestException_maps_to_502()
    {
        FunctionReflectionBridge.ClassifyTopLevelStatusCode(
            new HttpRequestException("transport error"), callerAborted: false)
            .Should().Be(502);
    }

    [Fact]
    public void Generic_exception_maps_to_500()
    {
        FunctionReflectionBridge.ClassifyTopLevelStatusCode(
            new InvalidOperationException("something went wrong"), callerAborted: false)
            .Should().Be(500);
    }

    [Fact]
    public void Generic_exception_with_misleading_text_still_maps_to_500()
    {
        // Classification is TYPE-driven, not text-driven: a "504"/"timeout" message on a
        // generic exception must NOT be promoted to 504.
        FunctionReflectionBridge.ClassifyTopLevelStatusCode(
            new InvalidOperationException("received 504 gateway timeout"), callerAborted: false)
            .Should().Be(500);
    }
}
