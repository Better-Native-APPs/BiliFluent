// Copyright (c) Bili Copilot. All rights reserved.

namespace Richasy.MpvKernel.Core;

/// <summary>
/// Tracks output reconstruction separately from user pause and native buffering.
/// </summary>
internal sealed class MpvAudioResumeRecovery
{
    private readonly object _sync = new();
    private bool _enabled;
    private bool _mediaStarted;
    private bool _needsRecovery;
    private bool _allowCorrection = true;
    private long _intent;
    private long _presentationVersion;
    private Operation? _current;

    public bool Enabled
    {
        get { lock (_sync) return _enabled; }
        set
        {
            lock (_sync)
            {
                _enabled = value;
                if (!value) CancelCore();
            }
        }
    }

    public bool NeedsRecovery { get { lock (_sync) return _enabled && _needsRecovery; } }
    public bool IsResumeBuffering { get { lock (_sync) return _current is not null; } }
    public long PresentationVersion { get { lock (_sync) return _presentationVersion; } }
    public long Intent { get { lock (_sync) return _intent; } }
    public bool AllowCorrection { get { lock (_sync) return _allowCorrection; } set { lock (_sync) _allowCorrection = value; } }
    public bool IsDeferredForSource { get { lock (_sync) return _current?.DeferredForSource == true; } }

    public bool OutputReconfigured(bool paused)
    {
        lock (_sync)
        {
            if (!_enabled || !_mediaStarted || !paused) return false;
            _needsRecovery = true;
            return true;
        }
    }

    public void ResetMedia()
    {
        lock (_sync)
        {
            CancelCore();
            _mediaStarted = false;
            _needsRecovery = false;
            _allowCorrection = true;
        }
    }

    public void Cancel()
    {
        lock (_sync) CancelCore();
    }

    public long RequestResume(bool waitingForSource = false)
    {
        lock (_sync)
        {
            CancelCore();
            if (_enabled && (_needsRecovery || waitingForSource))
            {
                _current = new Operation(_intent);
                _presentationVersion++;
            }
            return _intent;
        }
    }

    public bool CheckResumeRequirement(long intent, bool nativePaused, bool missingAudioPts)
    {
        lock (_sync)
        {
            if (intent == _intent && NeedsRecoveryCore(nativePaused, missingAudioPts)) _needsRecovery = true;
            return _enabled && _needsRecovery;
        }
    }

    public Operation? PrepareResume(long intent, bool nativePaused, bool missingAudioPts)
    {
        lock (_sync)
        {
            if (intent != _intent || !_enabled || !nativePaused) return null;
            // The native event queue can lag a command reply. Missing output PTS after an
            // selected paused audio stream also requires the same synchronized restart.
            if (!NeedsRecoveryCore(nativePaused, missingAudioPts)) return null;
            _needsRecovery = true;
            if (_current is null || _current.SeekHasStarted)
            {
                _current = new Operation(intent);
                _presentationVersion++;
            }
            return _current;
        }
    }

    private bool NeedsRecoveryCore(bool nativePaused, bool missingAudioPts)
        => _enabled && (_needsRecovery || nativePaused && missingAudioPts);

    public void SeekStarted()
    {
        lock (_sync)
        {
            // Any real seek resets both chains. Relative zero also coalesces with the
            // latest absolute user seek instead of replacing its target.
            _needsRecovery = false;
            if (_current is not null)
            {
                _current.SeekHasStarted = true;
                _current.Started.TrySetResult(true);
            }
        }
    }

    public void PlaybackRestarted()
    {
        lock (_sync)
        {
            _mediaStarted = true;
            if (_current is not { SeekHasStarted: true }) return;
            _current.Ready = true;
            if (_current.Unpaused)
            {
                _current = null;
                _presentationVersion++;
            }
        }
    }

    public MpvError ExecuteSeek(long intent, Func<MpvError> seek)
    {
        lock (_sync) return intent == _intent ? seek() : MpvError.Success;
    }

    public (MpvError Error, bool DeferredForSource) ExecuteRecoverySeek(long intent, Func<MpvError> seek)
    {
        lock (_sync)
        {
            if (intent != _intent) return (MpvError.Success, false);
            if (DeferForSourceCore(intent)) return (MpvError.Success, true);
            return (seek(), false);
        }
    }

    public (MpvError Error, bool Retry, bool DeferredForSource) CommitResume(long intent, Func<MpvError> unpause)
    {
        lock (_sync)
        {
            if (intent != _intent) return (MpvError.Success, false, false);
            if (DeferForSourceCore(intent)) return (MpvError.Success, false, true);
            if (_enabled && _needsRecovery) return (MpvError.Success, true, false);
            var error = unpause();
            if (error == MpvError.Success && _current is not null)
            {
                _current.Unpaused = true;
                if (_current.Ready)
                {
                    _current = null;
                    _presentationVersion++;
                }
            }
            return (error, false, false);
        }
    }

    private bool DeferForSourceCore(long intent)
    {
        if (!_enabled || !_needsRecovery || _allowCorrection) return false;
        if (_current is null)
        {
            _current = new Operation(intent);
            _presentationVersion++;
        }
        _current.DeferredForSource = true;
        return true;
    }

    private void CancelCore()
    {
        _intent++;
        if (_current is not null) _presentationVersion++;
        _current?.Started.TrySetResult(false);
        _current = null;
    }

    internal sealed class Operation(long intent)
    {
        public long Intent { get; } = intent;
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Ready { get; set; }
        public bool Unpaused { get; set; }
        public bool SeekHasStarted { get; set; }
        public bool DeferredForSource { get; set; }
    }
}
