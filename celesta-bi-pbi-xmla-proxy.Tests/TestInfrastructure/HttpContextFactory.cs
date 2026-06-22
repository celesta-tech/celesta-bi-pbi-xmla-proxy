using Microsoft.AspNetCore.Http;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Celesta.Bi.Pbi.XmlaProxy.Tests.TestInfrastructure;

internal static class HttpContextFactory
{
    public static DefaultHttpContext CreateContext(
        string method,
        string body = "",
        IDictionary<string, string>? headers = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? string.Empty));
        context.Response.Body = new MemoryStream();

        if (headers != null)
        {
            foreach (var header in headers)
            {
                context.Request.Headers[header.Key] = header.Value;
            }
        }

        return context;
    }

    /// <summary>A POST with all required headers and a single valid query — the starting point
    /// for tests that exercise the connection/query path via an injected fake.</summary>
    public static DefaultHttpContext CreateValidPostContext()
        => CreateContext(
            method: "POST",
            body: """{ "queries": [ { "query": "EVALUATE ROW(\"A\", 1)" } ] }""",
            headers: BuildRequiredHeaders());

    public static Dictionary<string, string> BuildRequiredHeaders() => new()
    {
        ["x-pbi-tenant-id"] = "tenant-id",
        ["x-pbi-client-id"] = "client-id",
        ["x-pbi-client-secret"] = "client-secret",
        ["x-pbi-xmla-endpoint"] = "powerbi://api.powerbi.com/v1.0/myorg/workspace",
        ["x-pbi-dataset-name"] = "dataset-name"
    };

    public static async Task<string> ReadResponseBodyAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
