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

## Deployment on GCP
```shell
## Change project
gcloud config set project YOUR_PROJECT

## Change region at your convenience
gcloud functions deploy executeQueries --gen2 --region=europe-west2 --runtime=dotnet8 --source=. --entry-point=Celesta.Bi.Pbi.XmlaProxy.Function --trigger-http

## Grant access to a service account
gcloud functions add-iam-policy-binding my-xmla-proxy --region=europe-west2 --member=serviceAccount:MY-SERVICE-ACCOUNT@YOUR_PROJECT.iam.gserviceaccount.com --role=roles/cloudfunctions.invoker

```
