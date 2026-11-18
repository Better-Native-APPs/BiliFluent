// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>Serializes navigation continuations without owning a playback or closing gate.</summary>
internal sealed class NavigationOperationQueue
{
    internal Task LastTask { get; private set; } = Task.CompletedTask;

    internal Task Enqueue(Func<Task> startClose, Func<Task> navigate)
    {
        var previous = LastTask;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LastTask = done.Task; // Publish before a close/open callback can enqueue a successor.
        Task close;
        try { close = startClose(); }
        catch (Exception error) { close = Task.FromException(error); }
        _ = CompleteAsync(done, previous, close, navigate);
        return done.Task;
    }

    private static async Task CompleteAsync(TaskCompletionSource done, Task previous, Task close, Func<Task> navigate)
    {
        try
        {
            // A successor never becomes a prerequisite of the independently started predecessor.
#pragma warning disable VSTHRD003
            try { await previous; } catch { /* The existing navigation observer reports its failure. */ }
            await close; // A failed real close cannot authorize a replacement or a page switch.
#pragma warning restore VSTHRD003
            await navigate();
            done.TrySetResult();
        }
        catch (Exception error) { done.TrySetException(error); }
    }
}
