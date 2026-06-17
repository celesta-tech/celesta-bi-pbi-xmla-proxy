using System;
using Microsoft.AnalysisServices.AdomdClient;

namespace Celesta.Bi.Pbi.XmlaProxy.Xmla;

/// <summary>Production factory: creates connections backed by the real ADOMD client.</summary>
internal sealed class AdomdXmlaConnectionFactory : IXmlaConnectionFactory
{
    public IXmlaConnection Create(string connectionString)
        => new AdomdXmlaConnection(new AdomdConnection(connectionString));
}

/// <summary>
/// Adapts <see cref="AdomdConnection"/> to <see cref="IXmlaConnection"/>. ADOMD exceptions are
/// translated to <see cref="XmlaException"/> (preserving the original as the inner exception);
/// non-ADOMD exceptions (timeouts, sockets, IO, HTTP) are left to propagate untouched so the
/// retry and status-classification logic can inspect them in the chain.
/// </summary>
internal sealed class AdomdXmlaConnection : IXmlaConnection
{
    private readonly AdomdConnection _connection;

    public AdomdXmlaConnection(AdomdConnection connection) => _connection = connection;

    public void Open()
    {
        try
        {
            _connection.Open();
        }
        catch (AdomdException ex)
        {
            throw new XmlaException(ex.Message, ex);
        }
    }

    public void Close() => _connection.Close();

    public IXmlaCommand CreateCommand(string query)
        => new AdomdXmlaCommand(new AdomdCommand(query, _connection));

    public void Dispose() => _connection.Dispose();
}

internal sealed class AdomdXmlaCommand : IXmlaCommand
{
    private readonly AdomdCommand _command;

    public AdomdXmlaCommand(AdomdCommand command) => _command = command;

    public IXmlaDataReader ExecuteReader()
    {
        try
        {
            return new AdomdXmlaDataReader(_command.ExecuteReader());
        }
        catch (AdomdErrorResponseException ex)
        {
            throw new XmlaModelQueryException(ex.Message, ex);
        }
        catch (AdomdException ex)
        {
            throw new XmlaException(ex.Message, ex);
        }
    }

    public void Dispose() => _command.Dispose();
}

internal sealed class AdomdXmlaDataReader : IXmlaDataReader
{
    private readonly AdomdDataReader _reader;

    public AdomdXmlaDataReader(AdomdDataReader reader) => _reader = reader;

    public bool Read()
    {
        try
        {
            return _reader.Read();
        }
        catch (AdomdErrorResponseException ex)
        {
            throw new XmlaModelQueryException(ex.Message, ex);
        }
        catch (AdomdException ex)
        {
            throw new XmlaException(ex.Message, ex);
        }
    }

    public int FieldCount => _reader.FieldCount;

    public string GetName(int ordinal) => _reader.GetName(ordinal);

    public object GetValue(int ordinal) => _reader.GetValue(ordinal);

    public void Dispose() => _reader.Dispose();
}
