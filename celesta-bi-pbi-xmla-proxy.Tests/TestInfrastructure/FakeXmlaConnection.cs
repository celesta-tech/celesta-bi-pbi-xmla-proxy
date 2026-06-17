using System;
using System.Collections.Generic;
using System.Linq;
using Celesta.Bi.Pbi.XmlaProxy.Xmla;

namespace Celesta.Bi.Pbi.XmlaProxy.Tests.TestInfrastructure;

/// <summary>
/// A configurable in-memory implementation of the XMLA seam so the function can be driven
/// black-box through HandleAsync without a live endpoint. Behavior is supplied via delegates:
/// <paramref name="onOpen"/> receives the 1-based attempt number and returns an exception to
/// throw (or null to succeed); <paramref name="onExecuteReader"/> returns an exception to throw
/// from ExecuteReader (or null to return a reader over <paramref name="rows"/>).
/// </summary>
internal sealed class FakeXmlaConnection : IXmlaConnection
{
    private readonly Func<int, Exception> _onOpen;
    private readonly Func<Exception> _onExecuteReader;
    private readonly IReadOnlyList<IReadOnlyDictionary<string, object>> _rows;

    public FakeXmlaConnection(
        Func<int, Exception> onOpen = null,
        Func<Exception> onExecuteReader = null,
        IReadOnlyList<IReadOnlyDictionary<string, object>> rows = null)
    {
        _onOpen = onOpen ?? (_ => null);
        _onExecuteReader = onExecuteReader ?? (() => null);
        _rows = rows ?? new[] { new Dictionary<string, object> { ["Value"] = 1 } };
    }

    public int OpenCalls { get; private set; }

    public int CloseCalls { get; private set; }

    public int CreateCommandCalls { get; private set; }

    public bool Disposed { get; private set; }

    public void Open()
    {
        OpenCalls++;
        var ex = _onOpen(OpenCalls);
        if (ex != null)
        {
            throw ex;
        }
    }

    public void Close() => CloseCalls++;

    public IXmlaCommand CreateCommand(string query)
    {
        CreateCommandCalls++;
        return new FakeXmlaCommand(_onExecuteReader, _rows);
    }

    public void Dispose() => Disposed = true;

    /// <summary>Wraps this connection in a factory for injection into the function.</summary>
    public FakeXmlaConnectionFactory AsFactory() => new(this);
}

internal sealed class FakeXmlaConnectionFactory : IXmlaConnectionFactory
{
    private readonly FakeXmlaConnection _connection;

    public FakeXmlaConnectionFactory(FakeXmlaConnection connection) => _connection = connection;

    public int CreateCalls { get; private set; }

    public IXmlaConnection Create(string connectionString)
    {
        CreateCalls++;
        return _connection;
    }
}

internal sealed class FakeXmlaCommand : IXmlaCommand
{
    private readonly Func<Exception> _onExecuteReader;
    private readonly IReadOnlyList<IReadOnlyDictionary<string, object>> _rows;

    public FakeXmlaCommand(Func<Exception> onExecuteReader, IReadOnlyList<IReadOnlyDictionary<string, object>> rows)
    {
        _onExecuteReader = onExecuteReader;
        _rows = rows;
    }

    public IXmlaDataReader ExecuteReader()
    {
        var ex = _onExecuteReader();
        if (ex != null)
        {
            throw ex;
        }

        return new FakeXmlaDataReader(_rows);
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeXmlaDataReader : IXmlaDataReader
{
    private readonly IReadOnlyList<IReadOnlyDictionary<string, object>> _rows;
    private readonly string[] _columns;
    private int _index = -1;

    public FakeXmlaDataReader(IReadOnlyList<IReadOnlyDictionary<string, object>> rows)
    {
        _rows = rows;
        _columns = rows.Count > 0 ? rows[0].Keys.ToArray() : Array.Empty<string>();
    }

    public bool Read()
    {
        _index++;
        return _index < _rows.Count;
    }

    public int FieldCount => _columns.Length;

    public string GetName(int ordinal) => _columns[ordinal];

    public object GetValue(int ordinal) => _rows[_index][_columns[ordinal]];

    public void Dispose()
    {
    }
}
