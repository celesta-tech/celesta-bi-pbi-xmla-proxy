using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Celesta.Bi.Pbi.XmlaProxy.Tests.TestInfrastructure;

internal static class FunctionReflectionBridge
{
    private static readonly MethodInfo ExecuteWithRetryMethod = typeof(Function)
        .GetMethod("ExecuteWithRetryAsync", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find ExecuteWithRetryAsync on Function.");

    private static readonly MethodInfo ClassifyTopLevelStatusCodeMethod = typeof(Function)
        .GetMethod("ClassifyTopLevelStatusCode", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find ClassifyTopLevelStatusCode on Function.");

    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<T> operation,
        string operationName,
        string requestId,
        CancellationToken cancellationToken)
    {
        var closedMethod = ExecuteWithRetryMethod.MakeGenericMethod(typeof(T));

        object invocationResult;
        try
        {
            invocationResult = closedMethod.Invoke(null, [operation, operationName, requestId, cancellationToken])
                ?? throw new InvalidOperationException("Retry invocation returned null.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

        if (invocationResult is not Task<T> task)
        {
            throw new InvalidOperationException("Retry invocation did not return Task<T>.");
        }

        return await task.ConfigureAwait(false);
    }

    public static int ClassifyTopLevelStatusCode(Exception ex, bool callerAborted)
    {
        try
        {
            return (int)(ClassifyTopLevelStatusCodeMethod.Invoke(null, [ex, callerAborted])
                ?? throw new InvalidOperationException("ClassifyTopLevelStatusCode returned null."));
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
    }
}
