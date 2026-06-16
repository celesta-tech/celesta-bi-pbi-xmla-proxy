using Celesta.Bi.Pbi.XmlaProxy.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Celesta.Bi.Pbi.XmlaProxy.Tests;

public class DiagnosticsTests
{
    [Fact]
    public async Task Request_id_header_present_on_501()
    {
        var sut = new Function();
        var context = HttpContextFactory.CreateContext(method: "GET");

        await sut.HandleAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
        context.Response.Headers.TryGetValue("x-request-id", out var requestId).Should().BeTrue();
        Guid.TryParse(requestId.ToString(), out _).Should().BeTrue("the request id should be a GUID");
    }

    [Fact]
    public async Task Request_id_header_present_on_validation_400()
    {
        var sut = new Function();
        var headers = HttpContextFactory.BuildRequiredHeaders();
        headers.Remove("x-pbi-client-secret");
        var context = HttpContextFactory.CreateContext(
            method: "POST",
            body: """{ "queries": [ { "query": "EVALUATE ROW(\"A\", 1)" } ] }""",
            headers: headers);

        await sut.HandleAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        context.Response.Headers.TryGetValue("x-request-id", out var requestId).Should().BeTrue();
        Guid.TryParse(requestId.ToString(), out _).Should().BeTrue("the request id should be a GUID");
    }

    [Fact]
    public async Task Validation_path_emits_warning_structured_log()
    {
        // Parallelism is disabled via xunit.runner.json, so swapping Console.Out is safe.
        var sut = new Function();
        var headers = HttpContextFactory.BuildRequiredHeaders();
        headers.Remove("x-pbi-tenant-id");
        var context = HttpContextFactory.CreateContext(
            method: "POST",
            body: """{ "queries": [ { "query": "EVALUATE ROW(\"A\", 1)" } ] }""",
            headers: headers);

        var originalOut = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            await sut.HandleAsync(context);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = captured.ToString();
        output.Should().Contain("\"severity\":\"WARNING\"");
        output.Should().Contain("\"phase\":\"body_parse\"");
    }
}
