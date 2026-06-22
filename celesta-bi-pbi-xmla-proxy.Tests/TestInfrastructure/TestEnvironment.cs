using System;
using System.Runtime.CompilerServices;

namespace Celesta.Bi.Pbi.XmlaProxy.Tests.TestInfrastructure;

internal static class TestEnvironment
{
    [ModuleInitializer]
    public static void Initialize()
    {
        // Keep retry tests fast and deterministic on local machines.
        Environment.SetEnvironmentVariable("PBI_XMLA_RETRY_MAX_ATTEMPTS", "3");
        Environment.SetEnvironmentVariable("PBI_XMLA_RETRY_BACKOFF_SECONDS", "1,1");
    }
}
