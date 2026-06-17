using System;

namespace Celesta.Bi.Pbi.XmlaProxy.Xmla;

/// <summary>
/// Creates XMLA connections. This is the seam that lets the function be tested without a live
/// XMLA endpoint: production uses <see cref="AdomdXmlaConnectionFactory"/>, tests inject a fake.
/// </summary>
internal interface IXmlaConnectionFactory
{
    IXmlaConnection Create(string connectionString);
}

/// <summary>A connection to an XMLA endpoint. Mirrors the slice of AdomdConnection the function uses.</summary>
internal interface IXmlaConnection : IDisposable
{
    void Open();

    void Close();

    IXmlaCommand CreateCommand(string query);
}

/// <summary>A command to execute against an XMLA endpoint.</summary>
internal interface IXmlaCommand : IDisposable
{
    IXmlaDataReader ExecuteReader();
}

/// <summary>A forward-only reader over a query result. Mirrors the slice of AdomdDataReader the function uses.</summary>
internal interface IXmlaDataReader : IDisposable
{
    bool Read();

    int FieldCount { get; }

    string GetName(int ordinal);

    object GetValue(int ordinal);
}
