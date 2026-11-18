#nullable enable

using System.Net.WebSockets;

namespace BiliCopilot.UI.Live.Application;

// This classifies an already joined worker, never the success of Dispose or a timeout.
internal static class LiveCloseTermination
{
    internal static bool IsExpected(Exception error, bool cancellationRequested)
        => cancellationRequested && (error is OperationCanceledException or WebSocketException or ObjectDisposedException
            || error is AggregateException aggregate && aggregate.InnerExceptions.Count > 0
                && aggregate.InnerExceptions.All(inner => IsExpected(inner, cancellationRequested)));
}
