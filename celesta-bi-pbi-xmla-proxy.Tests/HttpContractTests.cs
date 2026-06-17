using Celesta.Bi.Pbi.XmlaProxy.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Celesta.Bi.Pbi.XmlaProxy.Tests;

public class HttpContractTests
{
    [Fact]
    public async Task Non_post_returns_501()
    {
        var sut = new Function();
        var context = HttpContextFactory.CreateContext(method: "GET");

        await sut.HandleAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
    }

    [Fact]
    public async Task Missing_required_header_returns_400_with_error_shape()
    {
        var sut = new Function();
        var headers = HttpContextFactory.BuildRequiredHeaders();
        headers.Remove("x-pbi-client-secret");
        var context = HttpContextFactory.CreateContext(
            method: "POST",
            body: """{ "queries": [ { "query": "EVALUATE ROW(\"A\", 1)" } ] }""",
            headers: headers);

        await sut.HandleAsync(context);
        var responseBody = await HttpContextFactory.ReadResponseBodyAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        context.Response.ContentType.Should().Be("application/json");
        var json = JsonDocument.Parse(responseBody).RootElement;
        json.GetProperty("error").GetString().Should().Be("Invalid header");
        json.GetProperty("detail").GetString().Should().Be("x-pbi-client-secret header is required");
    }

    [Fact]
    public async Task Empty_body_returns_400_with_expected_shape()
    {
        var sut = new Function();
        var context = HttpContextFactory.CreateContext(
            method: "POST",
            body: "",
            headers: HttpContextFactory.BuildRequiredHeaders());

        await sut.HandleAsync(context);
        var responseBody = await HttpContextFactory.ReadResponseBodyAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        context.Response.ContentType.Should().Be("application/json");
        var json = JsonDocument.Parse(responseBody).RootElement;
        json.GetProperty("error").GetString().Should().Be("Invalid body");
        json.GetProperty("detail").GetString().Should().Be("Request body is required");
    }

    [Fact]
    public async Task Missing_queries_returns_400_with_expected_shape()
    {
        var sut = new Function();
        var context = HttpContextFactory.CreateContext(
            method: "POST",
            body: "{}",
            headers: HttpContextFactory.BuildRequiredHeaders());

        await sut.HandleAsync(context);
        var responseBody = await HttpContextFactory.ReadResponseBodyAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        context.Response.ContentType.Should().Be("application/json");
        var json = JsonDocument.Parse(responseBody).RootElement;
        json.GetProperty("error").GetString().Should().Be("Invalid body");
        json.GetProperty("detail").GetString().Should().Be("Request body must contain at least one Query");
    }

    [Fact]
    public async Task Validation_path_keeps_json_content_type()
    {
        var sut = new Function();
        var headers = HttpContextFactory.BuildRequiredHeaders();
        headers.Remove("x-pbi-tenant-id");
        var context = HttpContextFactory.CreateContext(
            method: "POST",
            body: """{ "queries": [ { "query": "EVALUATE ROW(\"A\", 1)" } ] }""",
            headers: headers);

        await sut.HandleAsync(context);

        context.Response.ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task Malformed_json_body_returns_400()
    {
        var sut = new Function();
        var context = HttpContextFactory.CreateContext(
            method: "POST",
            body: "{ not valid json",
            headers: HttpContextFactory.BuildRequiredHeaders());

        await sut.HandleAsync(context);
        var responseBody = await HttpContextFactory.ReadResponseBodyAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var json = JsonDocument.Parse(responseBody).RootElement;
        json.GetProperty("error").GetString().Should().Be("Invalid body");
    }
}
