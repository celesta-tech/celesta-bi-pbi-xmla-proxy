using Google.Cloud.Functions.Framework;
using Microsoft.AspNetCore.Http;
using System;
using System.Threading.Tasks;
using Microsoft.AnalysisServices.AdomdClient;
using System.Text.Json;
using System.IO;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Celesta.Bi.Pbi.XmlaProxy;

public sealed class ExecuteQueryRequestPayload
{
    [Required, MinLength(1)]
    public List<QueryItem> Queries { get; init; }

    [EmailAddress]
    public string? ImpersonatedUserName { get; init; }

}

public sealed class QueryItem
{
    [Required, MinLength(1)]
    public string Query { get; init; }
}

public class Function : IHttpFunction
{
    private static readonly string[] AuthScopes = ["https://analysis.windows.net/powerbi/api/.default"];

    /// <summary>
    /// Mimic the PowerBI service endpoint to execute DAX queries against a PowerBI dataset using XMLA endpoint.
    /// POST https://api.powerbi.com/v1.0/myorg/datasets/{datasetId}/executeQueries
    /// </summary>
    public async Task HandleAsync(HttpContext context)
    {

        // Accept POST method only
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status501NotImplemented;
            return;
        }


        // The response will be in JSON format, always.
        context.Response.ContentType = "application/json";
        // By default and if any error happens, we return a 400 Bad Request response
        context.Response.StatusCode = StatusCodes.Status400BadRequest;

        // Get the necessary parameters from request headers.
        // If any of these are missing, return a 400 Bad Request response
        // Expected headers:
        // - x-pbi-tenant-id : The Azure tenant ID, can be found in the Azure portal under Microsoft Entra Id > Overview > Tenant ID
        // - x-pbi-client-id : The client ID of the Azure AD app, can be found/created in the Azure portal under App registrations > bi-ci-powerbi-xmla-client > Overview > Application (client) ID
        // - x-pbi-client-secret : The client secret of the Azure AD app, can be found/created in the Azure portal under App registrations > bi-ci-powerbi-xmla-client > Certificates & secrets
        // - x-pbi-xmla-endpoint : The XMLA endpoint URL, can be found in the PowerBI portal under Workspace settings > License info > Connection link
        // - x-pbi-dataset-name : The name of the semantic model to send the query against
        if (!context.Request.Headers.TryGetValue("x-pbi-tenant-id", out var tenantId))
        {
            var errorResponse = new
            {
                error = "Invalid header",
                detail = "x-pbi-tenant-id header is required"
            };

            await context.Response.WriteAsync(JsonSerializer.Serialize(errorResponse));
            return;
        }

        if (!context.Request.Headers.TryGetValue("x-pbi-client-id", out var clientId))
        {
            var errorResponse = new
            {
                error = "Invalid header",
                detail = "x-pbi-client-id header is required"
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(errorResponse));
            return;
        }

        if (!context.Request.Headers.TryGetValue("x-pbi-client-secret", out var clientSecret))
        {
            var errorResponse = new
            {
                error = "Invalid header",
                detail = "x-pbi-client-secret header is required"
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(errorResponse));
            return;
        }

        if (!context.Request.Headers.TryGetValue("x-pbi-xmla-endpoint", out var xmlaEndpoint))
        {
            var errorResponse = new
            {
                error = "Invalid header",
                detail = "x-pbi-xmla-endpoint header is required"
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(errorResponse));
            return;
        }

        if (!context.Request.Headers.TryGetValue("x-pbi-dataset-name", out var datasetName))
        {
            var errorResponse = new
            {
                error = "Invalid header",
                detail = "x-pbi-dataset-name header is required"
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(errorResponse));
            return;
        }

        string bodyRaw;
        using (var reader = new StreamReader(context.Request.Body))
        {
            bodyRaw = await reader.ReadToEndAsync();
        }

        // Get the necessary parameters from request body.
        // The body must have a JSON object with the following properties:
        // - queries: an array of query objects with at least one "query" property containing the DAX query to execute
        // - impersonatedUserName: optional user to impersonate (can be omitted, null, or empty)
        // If queries are missing, return a 400 Bad Request response
        if (string.IsNullOrWhiteSpace(bodyRaw))
        {

            var errorResponse = new
            {
                error = "Invalid body",
                detail = "Request body is required"
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(errorResponse));
            return;
        }

        var body = JsonSerializer.Deserialize<ExecuteQueryRequestPayload>(bodyRaw);

        if (body?.Queries == null || body.Queries.Count == 0 || string.IsNullOrWhiteSpace(body.Queries[0]?.Query))
        {
            var errorResponse = new
            {
                error = "Invalid body",
                detail = "Request body must contain at least one Query"
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(errorResponse));
            return;
        }

        // Create ADOMD connection.
        // EffectiveUserName is only included when a non-empty impersonatedUserName is provided.
        string connectionString = $"Data Source={xmlaEndpoint};User ID=app:{clientId}@{tenantId};Password={clientSecret};Catalog={datasetName};";
        if (!string.IsNullOrWhiteSpace(body.ImpersonatedUserName))
        {
            connectionString += $"EffectiveUserName={body.ImpersonatedUserName};";
        }
        using AdomdConnection connection = new(connectionString);

        try
        {
            // Opening the connection to Pbi XMLA endpoint
            connection.Open();

            var allGood = true;

            // Now we process the query one by one.
            // The result object, will be return in the response body
            // The format return matches the PowerBI executeQueries response
            // Refer to https://learn.microsoft.com/en-us/rest/api/power-bi/datasets/execute-queries for more information
            var results = new List<object>();
            foreach (var queryItem in body.Queries)
            {
                using var command = new AdomdCommand(queryItem.Query, connection);
                try
                {
                    using var reader = command.ExecuteReader();
                    var rows = new List<Dictionary<string, object>>();

                    while (reader.Read())
                    {
                        var row = new Dictionary<string, object>();
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            row[reader.GetName(i)] = reader.GetValue(i);
                        }
                        rows.Add(row);
                    }

                    results.Add(new
                    {
                        tables = new[]
                        {
                            new { rows }
                        }
                    });
                }
                catch (AdomdErrorResponseException ex)
                {
                    allGood = false;
                    results.Add(new
                    {
                        error = new
                        {
                            code = "ModelQueryExecutionError",
                            message = ex.Message
                        }
                    });
                }
                catch (AdomdException ex)
                {
                    allGood = false;
                    results.Add(new
                    {
                        error = new
                        {
                            code = "AdomdException",
                            message = ex.Message
                        }
                    });
                }
            }

            // Closing the connection to Pbi XMLA endpoint
            connection.Close();

            // The powerbi executeQueries response return 200 OK only if all queries are sucessful
            // If any query fails, the response is 400 Bad Request
            if (allGood)
                context.Response.StatusCode = StatusCodes.Status200OK;

            // Finally returning the response
            var response = new
            {
                results
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(response));
            return;
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            var errorResponse = new
            {
                error = "An unhandled error occurred",
                detail = $"{ex.Message}"
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(errorResponse));
            return;
        }
        finally
        {
            connection.Close();
        }
    }
}
