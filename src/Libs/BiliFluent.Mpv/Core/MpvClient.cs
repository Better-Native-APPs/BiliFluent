// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.
// Modified for BiliFluent: native calls go through HanumanInstitute.LibMpv, results use MpvResult.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Richasy.MpvKernel.Core.Enums.MpvClientProperties;
using NativeEventId = HanumanInstitute.LibMpv.Core.MpvEventId;
using NativeFormat = HanumanInstitute.LibMpv.Core.MpvFormat;

namespace Richasy.MpvKernel.Core;

/// <summary>
/// MPV 客户端.
/// </summary>
public sealed partial class MpvClient : IAsyncDisposable
{
    private static readonly TimeSpan EventLoopExitTimeout = TimeSpan.FromSeconds(2);

    private readonly ILogger _logger;
    private readonly MpvCoreHandle _handle;
    private Task? _eventLoopTask;
    private CancellationTokenSource? _eventCts;

    /// <summary>
    /// Initialize a new instance of the <see cref="MpvClient"/> class.
    /// </summary>
    internal MpvClient(MpvCoreHandle handle, ILogger? logger = null)
    {
        _handle = handle;
        _logger = logger ?? new NullLogger<MpvClient>();
    }

    /// <summary>
    /// 数据通知事件.
    /// </summary>
    public event EventHandler<MpvClientNotifyEventArgs>? DataNotify;

    /// <summary>
    /// 是否已初始化.
    /// </summary>
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// 是否已经被释放.
    /// </summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// 创建一个新的 MPV 实例.
    /// </summary>
    /// <param name="dllPath">libmpv2.dll 的路径.</param>
    /// <param name="options">初始化选项.</param>
    /// <param name="logger">日志记录.</param>
    /// <returns><see cref="MpvClient"/>.</returns>
    public static async Task<MpvClient> CreateAsync(string dllPath, MpvInitializeOptions? options = null, ILogger? logger = null)
    {
        if (!MpvLibrary.Initialize(dllPath))
        {
            logger?.LogWarning("Mpv | libmpv is already loaded from another file; the requested one is ignored until the app restarts.");
        }

        var instanceHandle = MpvCoreHandle.Create();
        var instance = new MpvClient(instanceHandle, logger);
        await instance.InitializeAsync(options).ConfigureAwait(false);
        await ObservePropertyAsync(Duration, NativeFormat.Double);
        await ObservePropertyAsync(TimePosition, NativeFormat.Double);
        await ObservePropertyAsync(Volume, NativeFormat.Double);
        await ObservePropertyAsync(Speed, NativeFormat.Double);
        await ObservePropertyAsync(Pause, NativeFormat.Flag);
        await ObservePropertyAsync(CoreIdle, NativeFormat.Flag);
        await ObservePropertyAsync(Seeking, NativeFormat.Flag);
        await ObservePropertyAsync(FullScreen, NativeFormat.Flag);
        await ObservePropertyAsync(CompactOverlay, NativeFormat.Flag);
        await ObservePropertyAsync(Metadata, NativeFormat.Node);
        await ObservePropertyAsync(TrackCount, NativeFormat.Int64);
        await ObservePropertyAsync(CacheSpeed, NativeFormat.Int64);
        await ObservePropertyAsync(DemuxerCacheState, NativeFormat.Node);
        return instance;

        // 无法预期会出现怎样的错误，这里直接抛出异常.
        async Task ObservePropertyAsync(string name, NativeFormat format)
        {
            var errorCode = MpvError.Success;
            await Task.Run(() => errorCode = instanceHandle.ObserveProperty(name, format));
            ThrowIfFailed(errorCode, $"Mpv | observe property {name} failed");
        }
    }

    /// <summary>
    /// 设置日志等级.
    /// </summary>
    /// <param name="level">等级.</param>
    /// <returns><see cref="Task"/>.</returns>
    public async Task<MpvResult> SetLogLevelAsync(MpvLogLevel level)
    {
        var errorCode = MpvError.Success;
        _logger.LogInformation($"Set Mpv log level to {level}.");
        var levelStr = level switch
        {
            MpvLogLevel.None => "no",
            MpvLogLevel.Fatal => "fatal",
            MpvLogLevel.Error => "error",
            MpvLogLevel.Warn => "warn",
            MpvLogLevel.Info => "info",
            MpvLogLevel.V => "v",
            MpvLogLevel.Debug => "debug",
            MpvLogLevel.Trace => "trace",
            _ => null,
        };

        if (levelStr is null)
        {
            return MpvResult.Fail($"The specified log level ({level}) cannot be converted into a corresponding identifier");
        }

        await Task.Run(() => errorCode = _handle.RequestLogMessages(levelStr));
        return WrapAsResult(errorCode, "Mpv | set log level failed");
    }

    /// <summary>
    /// 设置配置文件.
    /// </summary>
    /// <param name="filePath">配置文件地址.</param>
    /// <returns><see cref="Task"/>.</returns>
    public async Task<MpvResult> SetConfigFileAsync(string filePath)
    {
        var errorCode = MpvError.Success;
        await Task.Run(() => errorCode = _handle.LoadConfigFile(filePath));
        return WrapAsResult(errorCode, "Mpv | load config file failed");
    }

    /// <summary>
    /// 是否开启闲置状态.
    /// </summary>
    /// <param name="idleEnable"><c>null</c> 对应 once, <c>true</c> 对应 yes, <c>false</c> 对应 no.</param>
    /// <returns><see cref="Task"/>.</returns>
    public async Task<MpvResult> UseIdleAsync(bool? idleEnable)
    {
        var state = idleEnable == null ? "once" : idleEnable == true ? "yes" : "no";
        var errorCode = MpvError.Success;
        await Task.Run(() => errorCode = _handle.SetOptionString("idle", state));
        return WrapAsResult(errorCode, "Mpv | set idle failed");
    }

    /// <summary>
    /// 是否允许保持开启状态（播放完成后保持最后一帧显示）.
    /// </summary>
    /// <param name="isKeepOpen">是否开启.</param>
    /// <returns><see cref="Task"/>.</returns>
    public async Task<MpvResult> UseKeepOpenAsync(bool isKeepOpen)
    {
        var errorCode = MpvError.Success;
        var state = isKeepOpen ? "yes" : "no";
        await Task.Run(() => errorCode = _handle.SetOptionString("keep-open", state));
        return WrapAsResult(errorCode, "Mpv | set keep open failed");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;

        if (_eventCts != null)
        {
            await _eventCts.CancelAsync();
        }

        await Task.Run(() => _handle.CommandString("stop"));
        await StopEventLoopAsync();
        _eventCts?.Dispose();
        _eventCts = null;
        await Task.Run(_handle.Destroy);
    }

    internal void ThrowError(MpvError errorCode)
        => ErrorOccurred?.Invoke(this, errorCode);

    private static void ThrowIfFailed(MpvError errorCode, string message)
    {
        if (errorCode != MpvError.Success)
        {
            throw new MpvException(message, errorCode);
        }
    }

    private static MpvResult WrapAsResult(MpvError errorCode, string message)
        => errorCode == MpvError.Success ? MpvResult.Ok() : MpvResult.Fail(new MpvException(message, errorCode));

    /// <summary>
    /// 初始化（启动事件轮询）.
    /// </summary>
    private void Run()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, typeof(MpvClient));

        if (_eventCts != null)
        {
            return;
        }

        _eventCts = new CancellationTokenSource();
        var token = _eventCts.Token;

        // The loop blocks in the native wait for the whole life of the client, so it gets its own thread.
        _eventLoopTask = Task.Factory.StartNew(() => RunEventLoop(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private unsafe void RunEventLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var eventPtr = _handle.WaitEvent(-1);
            if (eventPtr == null || eventPtr->EventId == NativeEventId.None)
            {
                // Woken up without an event, which is how disposal interrupts the wait.
                continue;
            }

            var eventId = eventPtr->EventId;
            HandleEvent(eventPtr);

            if (eventId == NativeEventId.Shutdown)
            {
                _logger.LogInformation("Mpv | Shutdown event received.");
                Shutdown?.Invoke(this, EventArgs.Empty);
                break;
            }
        }
    }

    /// <summary>
    /// Makes sure the event loop no longer touches the handle before the handle is destroyed.
    /// </summary>
    private async Task StopEventLoopAsync()
    {
        var loop = _eventLoopTask;
        if (loop is null || loop.IsCompleted)
        {
            return;
        }

        // The loop may be blocked in the native wait, which cancellation alone does not interrupt.
        _handle.Wakeup();
        var finished = await Task.WhenAny(loop, Task.Delay(EventLoopExitTimeout));
        if (finished != loop)
        {
            _logger.LogWarning("Mpv | The event loop did not stop in time.");
        }
    }

    /// <summary>
    /// 初始化 MPV 实例.
    /// </summary>
    /// <param name="options">初始化选项.</param>
    /// <returns><see cref="Task"/>.</returns>
    private async Task InitializeAsync(MpvInitializeOptions? options = null)
    {
        if (IsInitialized)
        {
            return;
        }

        var errorCode = MpvError.Success;
        await Task.Run(() =>
        {
            if (options != null)
            {
                if (options.UseConfig != null)
                {
                    errorCode = _handle.SetOptionString("config", options.UseConfig.Value ? "yes" : "no");
                }

                ThrowIfFailed(errorCode, "Instance | set --config failed");

                if (!string.IsNullOrEmpty(options.ConfigDirectory))
                {
                    errorCode = _handle.SetOptionString("config-dir", options.ConfigDirectory);
                }

                ThrowIfFailed(errorCode, "Instance | set --config-dir failed");

                if (!string.IsNullOrEmpty(options.InputConfigPath))
                {
                    errorCode = _handle.SetOptionString("input-conf", options.InputConfigPath);
                }

                ThrowIfFailed(errorCode, "Instance | set --input-conf failed");

                if (options.LoadScripts != null)
                {
                    errorCode = _handle.SetOptionString("load-scripts", options.LoadScripts.Value ? "yes" : "no");
                }

                ThrowIfFailed(errorCode, "Instance | set --load-scripts failed");

                if (!string.IsNullOrEmpty(options.ScriptPath))
                {
                    errorCode = _handle.SetOptionString("script", options.ScriptPath);
                }

                ThrowIfFailed(errorCode, "Instance | set --script failed");

                if (options.PlayerOperationMode != null)
                {
                    var mode = options.PlayerOperationMode switch
                    {
                        MpvPlayerOperationMode.PseudoGui => "pseudo-gui",
                        _ => "cplayer",
                    };
                    errorCode = _handle.SetOptionString("player-operation-mode", mode);
                }
            }

            errorCode = _handle.Initialize();
        });

        ThrowIfFailed(errorCode, "Instance | initialize failed");
        Run();
        IsInitialized = true;
    }

    private void SendNotify(MpvClientEventId id, object? data) => DataNotify?.Invoke(this, new(id, data));
}
