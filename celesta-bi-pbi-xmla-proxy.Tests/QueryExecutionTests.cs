using Celesta.Bi.Pbi.XmlaProxy.Tests.TestInfrastructure;
using Celesta.Bi.Pbi.XmlaProxy.Xmla;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Celesta.Bi.Pbi.XmlaProxy.Tests;

/// <summary>
/// The query-execution path and the PBI-compatible per-query error contract, black-box.
/// </summary>
public class QueryExecutionTests
{
    [Fact]
    public async Task Lowercase_payload_is_accepted()
    {
        // The Power BI executeQueries contract (and our README) uses lowercase queries/query.
        // Deserialization must be case-insensitive so a standard payload is not rejected as a 400.
        var fake = new FakeXmlaConnection();
        var sut = new Function(fake.AsFactory());
        var context = HttpContextFactory.CreateContext(
            method: "POST",
            body: """{ "queries": [ { "query": "EVALUATE ROW(\"A\", 1)" } ] }""",
            headers: HttpContextFactory.BuildRequiredHeaders());

        await sut.HandleAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Successful_query_returns_200_with_rows()
    {
        var rows = new[] { new Dictionary<string, object> { ["A"] = 1 } };
        var fake = new FakeXmlaConnection(rows: rows);
        var sut = new Function(fake.AsFactory());
        var context = HttpContextFactory.CreateValidPostContext();

        await sut.HandleAsync(context);
        var body = await HttpContextFactory.ReadResponseBodyAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        var root = JsonDocument.Parse(body).RootElement;
        var firstRow = root.GetProperty("results")[0].GetProperty("tables")[0].GetProperty("rows")[0];
        firstRow.GetProperty("A").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Model_query_error_returns_400_with_model_error_code()
    {
        var fake = new FakeXmlaConnection(
            onExecuteReader: () => new XmlaModelQueryException("invalid DAX", new Exception("inner")));
        var sut = new Function(fake.AsFactory());
        var context = HttpContextFactory.CreateValidPostContext();

        await sut.HandleAsync(context);
        var body = await HttpContextFactory.ReadResponseBodyAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var error = JsonDocument.Parse(body).RootElement
            .GetProperty("results")[0].GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("ModelQueryExecutionError");
    }

    [Fact]
    public async Task Generic_xmla_query_error_returns_400_with_adomd_code()
    {
        var fake = new FakeXmlaConnection(
            onExecuteReader: () => new XmlaException("dependency failed", new Exception("inner")));
        var sut = new Function(fake.AsFactory());
        var context = HttpContextFactory.CreateValidPostContext();

        await sut.HandleAsync(context);
        var body = await HttpContextFactory.ReadResponseBodyAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var error = JsonDocument.Parse(body).RootElement
            .GetProperty("results")[0].GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("AdomdException");
    }
}
