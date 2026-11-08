// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.Extensions.Logging;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;

namespace BiliCopilot.UI.Toolkits;

/// <summary>
/// 在释放客户端前请求 mpv 核心退出，让库的事件循环收到关闭事件.
/// </summary>
internal static class MpvShutdownHelper
{
    /// <summary>
    /// 请求核心退出，并在限定时间内等待关闭事件；失败只记录日志.
    /// </summary>
    /// <param name="client">待释放的客户端.</param>
    /// <param name="logger">日志记录器.</param>
    /// <param name="timeout">等待关闭事件的最长时间.</param>
    /// <returns>退出请求及关闭事件等待任务.</returns>
    internal static async Task QuitCoreAsync(MpvClient? client, ILogger logger, TimeSpan timeout)
    {
        if (client is null || client.IsDisposed)
        {
            return;
        }

        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler shutdownHandler = (_, _) => shutdown.TrySetResult();
        client.Shutdown += shutdownHandler;
        try
        {
            // DisposeAsync cancels the event loop without waking its native infinite wait.
            // Quit first so the loop can receive SHUTDOWN and stop using the handle.
            var error = await Task.Run(() => MpvNative.SetCommandString(client.Handle, "quit")).ConfigureAwait(false);
            if (error != MpvError.Success)
            {
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning("mpv 核心退出命令失败: {Error}", error);
                }

                return;
            }

            var completed = await Task.WhenAny(shutdown.Task, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed == shutdown.Task)
            {
                logger.LogInformation("mpv 核心已退出");
            }
            else
            {
                logger.LogWarning("等待 mpv 核心退出超时");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "mpv 核心退出失败");
        }
        finally
        {
            client.Shutdown -= shutdownHandler;
        }
    }
}