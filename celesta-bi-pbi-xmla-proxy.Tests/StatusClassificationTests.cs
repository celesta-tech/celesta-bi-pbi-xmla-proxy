using Celesta.Bi.Pbi.XmlaProxy.Tests.TestInfrastructure;
using Celesta.Bi.Pbi.XmlaProxy.Xmla;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Xunit;

namespace Celesta.Bi.Pbi.XmlaProxy.Tests;

/// <summary>
/// Top-level status-code classification, observed black-box: each test makes Open() throw a
/// given exception and asserts the resulting HTTP status. Classification is type-driven.
/// </summary>
public class StatusClassificationTests
{
    private static async Task<int> StatusWhenOpenThrows(Exception exception)
    {
        var fake = new FakeXmlaConnection(onOpen: _ => exception);
        var sut = new Function(fake.AsFactory());
        var context = HttpContextFactory.CreateValidPostContext();

        await sut.HandleAsync(context);

        return context.Response.StatusCode;
    }

    [Fact]
    public async Task Non_caller_cancellation_maps_to_504()
        => (await StatusWhenOpenThrows(new OperationCanceledException("A task was canceled.")))
            .Should().Be(StatusCodes.Status504GatewayTimeout);

    [Fact]
    public async Task SocketException_maps_to_502()
        => (await StatusWhenOpenThrows(new SocketException()))
            .Should().Be(StatusCodes.Status502BadGateway);

    [Fact]
    public async Task IOException_maps_to_502()
        => (await StatusWhenOpenThrows(new IOException("connection forcibly closed")))
            .Should().Be(StatusCodes.Status502BadGateway);

    [Fact]
    public async Task HttpRequestException_maps_to_502()
        => (await StatusWhenOpenThrows(new HttpRequestException("transport error")))
            .Should().Be(StatusCodes.Status502BadGateway);

    [Fact]
    public async Task Translated_xmla_error_maps_to_502()
        => (await StatusWhenOpenThrows(new XmlaException("connection refused", new Exception("inner"))))
            .Should().Be(StatusCodes.Status502BadGateway);

    [Fact]
    public async Task Generic_exception_maps_to_500()
        => (await StatusWhenOpenThrows(new InvalidOperationException("something went wrong")))
            .Should().Be(StatusCodes.Status500InternalServerError);

    [Fact]
    public async Task Generic_exception_with_misleading_text_still_maps_to_500()
    {
        // Classification is TYPE-driven, not text-driven: a "504"/"timeout" message on a generic
        // exception must NOT be promoted to 504 (even though the retry layer may treat it as transient).
        (await StatusWhenOpenThrows(new InvalidOperationException("received 504 gateway timeout")))
            .Should().Be(StatusCodes.Status500InternalServerError);
    }
}
