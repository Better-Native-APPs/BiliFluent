#nullable enable

using System.Runtime.CompilerServices;

namespace BiliCopilot.UI.Live.Application;

// Diagnostic context only: no admission, release decision or exception message is changed.
internal static class LiveCloseDiagnostics
{
    private static readonly AsyncLocal<string?> _reason = new();
    private static readonly ConditionalWeakTable<Exception, Failure> _failures = new();

    internal sealed record Failure(string Step, string Error, string Inner);

    internal static string Reason => _reason.Value ?? "owner";

    internal static Task Invoke(string reason, Func<Task> action)
    {
        var previous = _reason.Value;
        _reason.Value = reason;
        try { return action(); }
        finally { _reason.Value = previous; }
    }

    internal static Failure GetFailure(Exception error)
        => _failures.TryGetValue(error, out var failure) ? failure
            : new("none", error.GetType().Name, error.InnerException?.GetType().Name ?? "none");

    internal static void Record(Exception error, string step)
    {
        try
        {
            _ = _failures.GetValue(error, exception =>
                new(step, exception.GetType().Name, exception.InnerException?.GetType().Name ?? "none"));
        }
        catch { } // Diagnostic metadata must not replace an owner failure.
    }

    internal static InvalidOperationException Wrap(string message, Exception cause)
        => Forward(new InvalidOperationException(message), cause);

    internal static T Forward<T>(T error, Exception cause) where T : Exception
    {
        try { _ = _failures.GetValue(error, _ => GetFailure(cause)); }
        catch { }
        return error;
    }
}
