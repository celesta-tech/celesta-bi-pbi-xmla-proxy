using System;

namespace Celesta.Bi.Pbi.XmlaProxy.Xmla;

/// <summary>
/// Wraps a failure surfaced by the XMLA dependency. The adapter translates ADOMD exceptions into
/// this type at the boundary (preserving the original as <see cref="Exception.InnerException"/>),
/// so the function's core logic and its tests never depend on ADOMD's (non-constructible) types.
/// </summary>
internal class XmlaException : Exception
{
    public XmlaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A query that the model rejected (translated from AdomdErrorResponseException). Maps to the
/// PBI-compatible per-query error with code "ModelQueryExecutionError".
/// </summary>
internal sealed class XmlaModelQueryException : XmlaException
{
    public XmlaModelQueryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
