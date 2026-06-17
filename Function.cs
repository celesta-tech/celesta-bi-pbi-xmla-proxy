using Google.Cloud.Functions.Framework;
using Microsoft.AspNetCore.Http;
using System;
using System.Threading.Tasks;
using Celesta.Bi.Pbi.XmlaProxy.Xmla;
using System.Text.Json;
using System.IO;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net;
using System.Threading;
using System.Text.RegularExpressions;

namespace Celesta.Bi.Pbi.XmlaProxy;

#nullable enable annotations
public sealed class ExecuteQueryRequestPayload
{
    [Required, MinLength(1)]
    public List<QueryItem> Queries { get; init; }

    [EmailAddress]
    public string? ImpersonatedUserName { get; init; }

}
#nullable restore annotations

public sealed class QueryItem
{
    [Required, MinLength(1)]
    public string Query { get; init; }
}

public class Function : IHttpFunction
{
    private readonly IXmlaConnectionFactory _connectionFactory;

    /// <summary>Production constructor used by the Functions Framework: connects via real ADOMD.</summary>
    public Function() : this(new AdomdXmlaConnectionFactory())
    {
    }

    /// <summary>Test seam: injects an XMLA connection factory (e.g. a fake) without a live endpoint.</summary>
    internal Function(IXmlaConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    private static readonly string[] AuthScopes = ["https://analysis.windows.net/powerbi/api/.default"];
    private static readonly RetryPolicySettings RetryPolicy = LoadRetryPolicySettings();
    private static readonly HttpStatusCode[] TransientHttpStatusCodes =
    [
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout
    ];
    private static readonly string[] TransientMessageMarkers =
    [
        "timeout",
        "timed out",
        "temporarily unavailable",
        "service unavailable",
        "gateway",
        "transport-level error",
        "connection reset",
        "forcibly closed",
        "network",
        "throttl",
        "rate limit"
    ];
    private static readonly Regex TransientStatusCodeRegex =
        new(@"\b(429|502|503|504)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ASP.NET Core's StatusCodes class does not define 499 (it is a non-standard
    // "Client Closed Request" code, originally from nginx), so we declare it here.
    private const int Status499ClientClosedRequest = 499;

    // Canonical structured-logging severities, promoted by Cloud Logging.
    private const string SeverityInfo = "INFO";
    private const string SeverityWarning = "WARNING";
    private const string SeverityError = "ERROR";

    // The PBI executeQueries payload uses lowercase property names; accept them case-insensitively.
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Mimic the PowerBI service endpoint to execute DAX queries against a PowerBI dataset using XMLA endpoint.
    /// POST https://api.powerbi.com/v1.0/myorg/datasets/{datasetId}/executeQueries
    /// </summary>
    public async Task HandleAsync(HttpContext context)
    {
        // A per-request correlation id, generated before anything else so it can be
        // attached to every structured log line and returned to the caller. It is set
        // as the x-request-id response header immediately, on every code path.
        var requestId = Guid.NewGuid().ToString();
        context.Response.Headers["x-request-id"] = requestId;

        Log(SeverityInfo, "XMLA proxy request received.", requestId, "handler_entry");

        // Accept POST method only
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            Log(SeverityWarning,
                $"Rejected non-POST method '{context.Request.Method}'.",
                requestId, "response");
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
            await WriteValidationFailureAsync(context, requestId,
                error: "Invalid header", detail: "x-pbi-tenant-id header is required");
            return;
        }

        if (!context.Request.Headers.TryGetValue("x-pbi-client-id", out var clientId))
        {
            await WriteValidationFailureAsync(context, requestId,
                error: "Invalid header", detail: "x-pbi-client-id header is required");
            return;
        }

        if (!context.Request.Headers.TryGetValue("x-pbi-client-secret", out var clientSecret))
        {
            await WriteValidationFailureAsync(context, requestId,
                error: "Invalid header", detail: "x-pbi-client-secret header is required");
            return;
        }

        if (!context.Request.Headers.TryGetValue("x-pbi-xmla-endpoint", out var xmlaEndpoint))
        {
            await WriteValidationFailureAsync(context, requestId,
                error: "Invalid header", detail: "x-pbi-xmla-endpoint header is required");
            return;
        }

        if (!context.Request.Headers.TryGetValue("x-pbi-dataset-name", out var datasetName))
        {
            await WriteValidationFailureAsync(context, requestId,
                error: "Invalid header", detail: "x-pbi-dataset-name header is required");
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
            await WriteValidationFailureAsync(context, requestId,
                error: "Invalid body", detail: "Request body is required");
            return;
        }

        // The Power BI executeQueries contract uses lowercase property names (queries/query),
        // so deserialization must be case-insensitive to accept a standard payload.
        var body = JsonSerializer.Deserialize<ExecuteQueryRequestPayload>(bodyRaw, JsonOptions);

        if (body?.Queries == null || body.Queries.Count == 0 || string.IsNullOrWhiteSpace(body.Queries[0]?.Query))
        {
            await WriteValidationFailureAsync(context, requestId,
                error: "Invalid body", detail: "Request body must contain at least one Query");
            return;
        }

        Log(SeverityInfo,
            $"Request body parsed: {body.Queries.Count} query(ies).",
            requestId, "body_parse");

        // Create ADOMD connection.
        // EffectiveUserName is only included when a non-empty impersonatedUserName is provided.
        string connectionString = $"Data Source={xmlaEndpoint};User ID=app:{clientId}@{tenantId};Password={clientSecret};Catalog={datasetName};";
        if (!string.IsNullOrWhiteSpace(body.ImpersonatedUserName))
        {
            connectionString += $"EffectiveUserName={body.ImpersonatedUserName};";
        }
        using IXmlaConnection connection = _connectionFactory.Create(connectionString);

        try
        {
            // Opening the connection to Pbi XMLA endpoint
            await ExecuteWithRetryAsync(
                operation: () =>
                {
                    connection.Open();
                    return true;
                },
                operationName: "connection open",
                requestId: requestId,
                cancellationToken: context.RequestAborted);

            Log(SeverityInfo, "XMLA connection opened.", requestId, "connection_open");

            var allGood = true;

            // Now we process the query one by one.
            // The result object, will be return in the response body
            // The format return matches the PowerBI executeQueries response
            // Refer to https://learn.microsoft.com/en-us/rest/api/power-bi/datasets/execute-queries for more information
            var results = new List<object>();
            for (var queryIndex = 0; queryIndex < body.Queries.Count; queryIndex++)
            {
                var queryItem = body.Queries[queryIndex];
                var operationName = $"query execution ({queryIndex + 1}/{body.Queries.Count})";
                using var command = connection.CreateCommand(queryItem.Query);
                try
                {
                    using var reader = await ExecuteWithRetryAsync(
                        operation: () => command.ExecuteReader(),
                        operationName: operationName,
                        requestId: requestId,
                        cancellationToken: context.RequestAborted);
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

                    Log(SeverityInfo,
                        $"Query {queryIndex + 1}/{body.Queries.Count} executed: {rows.Count} row(s).",
                        requestId, "query_execution", operationName: operationName);
                }
                catch (XmlaModelQueryException ex)
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
                    Log(SeverityWarning,
                        $"Query {queryIndex + 1}/{body.Queries.Count} returned a model error.",
                        requestId, "query_execution", operationName: operationName,
                        exceptionType: ex.GetType().FullName, exceptionMessage: GetShortExceptionMessage(ex));
                }
                catch (XmlaException ex)
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
                    Log(SeverityWarning,
                        $"Query {queryIndex + 1}/{body.Queries.Count} failed with an ADOMD error.",
                        requestId, "query_execution", operationName: operationName,
                        exceptionType: ex.GetType().FullName, exceptionMessage: GetShortExceptionMessage(ex));
                }
            }

            // Closing the connection to Pbi XMLA endpoint
            connection.Close();
            Log(SeverityInfo, "XMLA connection closed.", requestId, "connection_close");

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
            Log(SeverityInfo,
                $"Response sent with status {context.Response.StatusCode}.",
                requestId, "response");
            return;
        }
        catch (Exception ex)
        {
            var callerAborted = context.RequestAborted.IsCancellationRequested;
            var statusCode = ClassifyTopLevelStatusCode(ex, callerAborted);
            context.Response.StatusCode = statusCode;

            if (statusCode == Status499ClientClosedRequest)
            {
                // The caller disconnected before we finished. Setting the status code is
                // sufficient: no body is written, so there is nothing here that can fail.
                Log(SeverityInfo,
                    "Caller closed the request before completion.",
                    requestId, "error",
                    exceptionType: ex.GetType().FullName, exceptionMessage: GetShortExceptionMessage(ex));
                return;
            }

            Log(SeverityError,
                "An unhandled error occurred while processing the request.",
                requestId, "error",
                exceptionType: ex.GetType().FullName, exceptionMessage: GetShortExceptionMessage(ex));

            var errorResponse = new
            {
                error = "An unhandled error occurred",
                detail = $"{ex.Message}",
                requestId,
                exceptionType = ex.GetType().FullName
            };
            try
            {
                await context.Response.WriteAsync(JsonSerializer.Serialize(errorResponse));
            }
            catch
            {
                // Intentionally ignored: the client may have disconnected mid-write.
            }
            return;
        }
        finally
        {
            connection.Close();
        }
    }

    private static async Task WriteValidationFailureAsync(
        HttpContext context, string requestId, string error, string detail)
    {
        Log(SeverityWarning, $"{error}: {detail}", requestId, "body_parse");
        var errorResponse = new { error, detail };
        await context.Response.WriteAsync(JsonSerializer.Serialize(errorResponse));
    }

    private static RetryPolicySettings LoadRetryPolicySettings()
    {
        const int defaultMaxAttempts = 4;
        var defaultBackoffDelays = new[]
        {
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        };

        var maxAttempts = defaultMaxAttempts;
        var maxAttemptsRaw = Environment.GetEnvironmentVariable("PBI_XMLA_RETRY_MAX_ATTEMPTS");
        if (int.TryParse(maxAttemptsRaw, out var parsedMaxAttempts) && parsedMaxAttempts > 0)
        {
            maxAttempts = parsedMaxAttempts;
        }

        var backoffDelays = defaultBackoffDelays;
        var delaysRaw = Environment.GetEnvironmentVariable("PBI_XMLA_RETRY_BACKOFF_SECONDS");
        if (!string.IsNullOrWhiteSpace(delaysRaw))
        {
            var parsedDelays = delaysRaw
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.TryParse(value, out var seconds) ? seconds : -1)
                .Where(seconds => seconds > 0)
                .Select(seconds => TimeSpan.FromSeconds(seconds))
                .ToArray();

            if (parsedDelays.Length > 0)
            {
                backoffDelays = parsedDelays;
            }
        }

        return new RetryPolicySettings(maxAttempts, backoffDelays);
    }

    private static async Task<T> ExecuteWithRetryAsync<T>(
        Func<T> operation,
        string operationName,
        string requestId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= RetryPolicy.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = operation();
                if (attempt > 1)
                {
                    Log(SeverityInfo,
                        $"{operationName} succeeded on attempt {attempt}/{RetryPolicy.MaxAttempts}.",
                        requestId, "retry", operationName: operationName,
                        attempt: attempt, maxAttempts: RetryPolicy.MaxAttempts);
                }
                return result;
            }
            catch (Exception ex) when (IsTransientFailure(ex, cancellationToken) && attempt < RetryPolicy.MaxAttempts)
            {
                var delay = GetRetryDelay(attempt);
                Log(SeverityWarning,
                    $"{operationName} attempt {attempt}/{RetryPolicy.MaxAttempts} failed; retrying in {delay.TotalSeconds:F1}s.",
                    requestId, "retry", operationName: operationName,
                    attempt: attempt, maxAttempts: RetryPolicy.MaxAttempts,
                    exceptionType: ex.GetType().FullName, exceptionMessage: GetShortExceptionMessage(ex));
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex) when (IsTransientFailure(ex, cancellationToken))
            {
                Log(SeverityError,
                    $"{operationName} attempt {attempt}/{RetryPolicy.MaxAttempts} failed; retry policy exhausted.",
                    requestId, "retry", operationName: operationName,
                    attempt: attempt, maxAttempts: RetryPolicy.MaxAttempts,
                    exceptionType: ex.GetType().FullName, exceptionMessage: GetShortExceptionMessage(ex));
                throw;
            }
        }

        throw new InvalidOperationException("Retry policy exhausted unexpectedly.");
    }

    private static TimeSpan GetRetryDelay(int failedAttempt)
    {
        var delayIndex = Math.Min(failedAttempt - 1, RetryPolicy.BackoffDelays.Length - 1);
        var baseDelay = RetryPolicy.BackoffDelays[delayIndex];
        var jitterMilliseconds = Random.Shared.Next(100, 751);
        return baseDelay + TimeSpan.FromMilliseconds(jitterMilliseconds);
    }

    private static bool IsTransientFailure(Exception ex, CancellationToken cancellationToken)
    {
        // A cancellation is transient only when it did NOT originate from the caller.
        // A dependency-side XMLA cancel ("A task was canceled") while the caller is still
        // connected should be retried; a caller-aborted request should not.
        if (ex is OperationCanceledException)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        if (TryGetExceptionInChain<TimeoutException>(ex, out _))
        {
            return true;
        }

        if (TryGetExceptionInChain<System.Net.Sockets.SocketException>(ex, out _))
        {
            return true;
        }

        if (TryGetExceptionInChain<IOException>(ex, out _))
        {
            return true;
        }

        if (TryGetExceptionInChain<System.Net.Http.HttpRequestException>(ex, out var httpRequestException)
            && httpRequestException.StatusCode.HasValue
            && TransientHttpStatusCodes.Contains(httpRequestException.StatusCode.Value))
        {
            return true;
        }

        var message = GetShortExceptionMessage(ex).ToLowerInvariant();
        return TransientMessageMarkers.Any(marker => message.Contains(marker))
            || TransientStatusCodeRegex.IsMatch(message);
    }

    /// <summary>
    /// Maps a top-level (unhandled) exception to an HTTP status code. Ordered, first match
    /// wins, and TYPE-DRIVEN only — message markers and the transient status-code regex are
    /// deliberately NOT consulted here (they belong to the retry decision, not status mapping).
    /// </summary>
    private static int ClassifyTopLevelStatusCode(Exception ex, bool callerAborted)
    {
        // 1. Caller closed the connection while a cancellation propagated -> 499.
        if (callerAborted && TryGetExceptionInChain<OperationCanceledException>(ex, out _))
        {
            return Status499ClientClosedRequest;
        }

        // 2. A timeout, or a (non-caller) cancellation, surfaced as a gateway timeout -> 504.
        if (TryGetExceptionInChain<TimeoutException>(ex, out _)
            || TryGetExceptionInChain<OperationCanceledException>(ex, out _))
        {
            return StatusCodes.Status504GatewayTimeout;
        }

        // 3. Dependency/transport faults (translated XMLA errors, sockets, IO, HTTP) -> 502
        //    Bad Gateway.
        if (TryGetExceptionInChain<XmlaException>(ex, out _)
            || TryGetExceptionInChain<System.Net.Sockets.SocketException>(ex, out _)
            || TryGetExceptionInChain<IOException>(ex, out _)
            || TryGetExceptionInChain<System.Net.Http.HttpRequestException>(ex, out _))
        {
            return StatusCodes.Status502BadGateway;
        }

        // 4. Anything else -> 500.
        return StatusCodes.Status500InternalServerError;
    }

    /// <summary>
    /// Writes a single-line JSON log entry to stdout for Cloud Logging. The "severity" and
    /// "message" fields are promoted by Cloud Logging. Message/exception text is stripped of
    /// CR/LF so each entry stays on one line. The connection string, secrets and raw headers
    /// are never passed in by callers and must never be logged.
    /// </summary>
    private static void Log(
        string severity,
        string message,
        string requestId,
        string phase,
        string operationName = null,
        int? attempt = null,
        int? maxAttempts = null,
        string exceptionType = null,
        string exceptionMessage = null)
    {
        var entry = new Dictionary<string, object>
        {
            ["severity"] = severity,
            ["message"] = StripNewlines(message),
            ["requestId"] = requestId,
            ["phase"] = phase
        };

        if (operationName != null) entry["operationName"] = operationName;
        if (attempt.HasValue) entry["attempt"] = attempt.Value;
        if (maxAttempts.HasValue) entry["maxAttempts"] = maxAttempts.Value;
        if (exceptionType != null) entry["exceptionType"] = exceptionType;
        if (exceptionMessage != null) entry["exceptionMessage"] = StripNewlines(exceptionMessage);

        Console.WriteLine(JsonSerializer.Serialize(entry));
    }

    private static string StripNewlines(string value)
        => value?.Replace("\r", " ").Replace("\n", " ");

    private static string GetShortExceptionMessage(Exception ex)
    {
        var message = ex.Message ?? ex.GetType().Name;
        return message.Length <= 220 ? message : $"{message[..220]}...";
    }

    private static bool TryGetExceptionInChain<TException>(Exception ex, out TException matchingException)
        where TException : Exception
    {
        Exception current = ex;
        while (current != null)
        {
            if (current is TException typedException)
            {
                matchingException = typedException;
                return true;
            }

            current = current.InnerException;
        }

        matchingException = null;
        return false;
    }

    private sealed class RetryPolicySettings
    {
        public RetryPolicySettings(int maxAttempts, TimeSpan[] backoffDelays)
        {
            MaxAttempts = maxAttempts;
            BackoffDelays = backoffDelays.Length > 0 ? backoffDelays : [TimeSpan.FromSeconds(5)];
        }

        public int MaxAttempts { get; }

        public TimeSpan[] BackoffDelays { get; }
    }
}
