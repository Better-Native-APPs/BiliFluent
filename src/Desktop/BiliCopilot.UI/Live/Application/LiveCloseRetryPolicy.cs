#nullable enable

namespace BiliCopilot.UI.Live.Application;

internal static class LiveCloseRetryPolicy
{
    // Per explicit opening request; never an unbounded background release loop.
    internal const int MaximumAttempts = 3;
    internal const int IntervalMilliseconds = 200;
}
