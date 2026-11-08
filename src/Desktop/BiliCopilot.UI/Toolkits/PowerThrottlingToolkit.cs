// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32.System.Threading;

namespace BiliCopilot.UI.Toolkits;

/// <summary>
/// Windows "Efficiency mode" = EcoQoS (execution-speed throttling) + idle priority class.
/// Task Manager only shows the leaf when both are applied.
/// </summary>
internal static class PowerThrottlingToolkit
{
    internal static unsafe bool TrySetEfficiencyMode(bool enabled, ILogger logger)
    {
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PInvoke.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PInvoke.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = enabled ? PInvoke.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0U,
            };
            if (!PInvoke.SetProcessInformation(PInvoke.GetCurrentProcess(),
                PROCESS_INFORMATION_CLASS.ProcessPowerThrottling, &state, (uint)sizeof(PROCESS_POWER_THROTTLING_STATE)))
            {
                var error = Marshal.GetLastPInvokeError();
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("Process power throttling update failed with Win32 error {ErrorCode}.", error);
                }

                return false;
            }

            using var process = Process.GetCurrentProcess();
            process.PriorityClass = enabled ? ProcessPriorityClass.Idle : ProcessPriorityClass.Normal;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Process efficiency mode update failed.");
        }

        return false;
    }
}
