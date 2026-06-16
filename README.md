# celesta-bi-pbi-xmla-proxy

A Google Cloud Function that acts as a proxy for Power BI XMLA endpoints, enabling secure execution of DAX queries against Power BI datasets with Service Principale authentication and supporting user impersonation.

It is primarily designed to enable RLS testing, which is otherwise not natively supported by the PowerBI service API.

## Overview

This function mimics the Power BI service endpoint `POST https://api.powerbi.com/v1.0/myorg/datasets/{datasetId}/executeQueries` by connecting to Power BI datasets through their XMLA endpoints using Azure AD authentication.

## Prerequisite
- The PowerBI workspace containing the dataset must be premium-enabled.
- The service principal must have the `Build` privilege on the queries dataset.

## Functionality

The `Function.cs` implements an HTTP-triggered Google Cloud Function that:

- **Accepts DAX Queries**: Processes POST requests containing DAX queries to execute against Power BI semantic models
- **User Impersonation**: Supports executing queries on behalf of specific users using the `EffectiveUserName` parameter
- **Azure AD Authentication**: Uses client credentials (tenant ID, client ID, client secret) for secure authentication
- **XMLA Connectivity**: Connects to Power BI datasets via their XMLA endpoints using ADOMD.NET
- **Power BI API Compatibility**: Returns responses in the same format as the official Power BI executeQueries API

## Required Headers

- `x-pbi-tenant-id`: The Azure tenant ID, can be found in the Azure portal under Microsoft Entra Id > Overview > Tenant ID
- `x-pbi-client-id`: The client ID of the Azure AD app, can be found/created in the Azure portal under App registrations > bi-ci-powerbi-xmla-client > Overview > Application (client) ID 
- `x-pbi-client-secret`: The client secret of the Azure AD app, can be found/created in the Azure portal under App registrations > bi-ci-powerbi-xmla-client > Certificates & secrets
- `x-pbi-xmla-endpoint`: The XMLA endpoint URL, can be found in the PowerBI portal under Workspace settings > License info > Connection link
- `x-pbi-dataset-name`: The name of the semantic model to send the query against

## Request Body

```json
{
  "queries": [
    {
      "query": "EVALUATE 'Table'"
    }
  ],
  "impersonatedUserName": null
}
```

`impersonatedUserName` is optional and can be omitted, `null`, or an empty string (`""`).

## Response Format

Returns JSON responses matching the Power BI executeQueries API format, with query results organized in tables and rows structure. Handles both successful query execution and error scenarios with appropriate HTTP status codes.

Every response — success, validation failure, or error — carries an `x-request-id` header containing a per-request GUID. The same id appears in the structured logs (see below) and, for `5xx` responses, in the JSON body, so a caller can correlate a failed call with its server-side logs.

### HTTP status codes

| Status | Meaning |
| --- | --- |
| `200 OK` | All queries executed successfully. |
| `400 Bad Request` | Request validation failed (missing/invalid header or body), **or** at least one query returned a model/ADOMD error (the PBI-compatible per-query error contract; the `results[]` array reports which queries failed). |
| `499 Client Closed Request` | The caller disconnected before the response completed. No body is returned. |
| `500 Internal Server Error` | An unclassified unhandled error. |
| `501 Not Implemented` | The request used a method other than `POST`. |
| `502 Bad Gateway` | A dependency/transport fault talking to the XMLA endpoint (ADOMD, socket, IO or HTTP error). |
| `504 Gateway Timeout` | The upstream operation timed out, or a non-caller cancellation occurred. |

`5xx` responses use the body shape `{ "error", "detail", "requestId", "exceptionType" }`. Top-level status classification is **type-driven** (based on the exception type in the chain), not derived from message text.

### Structured logging

The function writes single-line JSON log entries to stdout, which Cloud Logging ingests directly. The `severity` and `message` fields are promoted by Cloud Logging.

Fields: `severity`, `message`, `requestId`, `phase`, and where applicable `operationName`, `attempt`, `maxAttempts`, `exceptionType`, `exceptionMessage`. The connection string, the `x-pbi-client-secret` header, and raw request headers are never logged.

Phases (`phase`): `handler_entry`, `body_parse`, `connection_open`, `query_execution`, `connection_close`, `retry`, `response`, `error`.

Severities: `INFO` for normal phases and the `499` case; `WARNING` for each retry attempt and for validation failures (`400`/`501`); `ERROR` for retry exhaustion and top-level `5xx` errors.

### Retry behaviour

Transient failures opening the connection or executing a query are retried with exponential-style backoff and jitter. Cancellations are classified at the point of failure: a dependency-side cancellation while the caller is still connected is treated as transient (retried), whereas a caller-aborted request is not retried.

Two environment variables tune the policy:

| Variable | Default | Meaning |
| --- | --- | --- |
| `PBI_XMLA_RETRY_MAX_ATTEMPTS` | `4` | Maximum number of attempts (including the first) per operation. |
| `PBI_XMLA_RETRY_BACKOFF_SECONDS` | `5,10,30` | Comma-separated backoff delays in seconds. The last value is reused once exhausted; a small random jitter is added to each delay. |

## Deployment on GCP
```shell
## Change project
gcloud config set project YOUR_PROJECT

## Change region at your convenience
gcloud functions deploy executeQueries --gen2 --region=europe-west2 --runtime=dotnet8 --source=. --entry-point=Celesta.Bi.Pbi.XmlaProxy.Function --trigger-http

## Grant access to a service account
gcloud functions add-iam-policy-binding my-xmla-proxy --region=europe-west2 --member=serviceAccount:MY-SERVICE-ACCOUNT@YOUR_PROJECT.iam.gserviceaccount.com --role=roles/cloudfunctions.invoker

```
