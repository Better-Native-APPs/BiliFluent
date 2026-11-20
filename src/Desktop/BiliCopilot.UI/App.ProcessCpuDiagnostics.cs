// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Diagnostics;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.Extensions.Logging;
using System.ComponentModel;

namespace BiliCopilot.UI;

public partial class App
{
    private static readonly object ProcessCpuSync = new();
    private static ProcessCpuDiagnosticsSession? _processCpuDiagnostics;
    private static bool _processCpuExiting;
    private uint _processCpuUiThreadId;
    private bool _processCpuInitialized;
    private AppViewModel? _processCpuApp;
    private NavigationViewModel? _processCpuNavigation;

    private void InitializeProcessCpuDiagnostics()
    {
        // OnLaunched and the main window use the same dispatcher thread.
        _processCpuUiThreadId = ProcessCpuThreadReader.CurrentThreadId;
        _processCpuInitialized = true;
        RefreshProcessCpuDiagnostics();
    }

    internal void RefreshProcessCpuDiagnostics()
    {
        if (!_processCpuInitialized) return;
        if (!SettingsToolkit.ReadLocalSetting(SettingNames.PlaybackStutterDiagnosticsEnabled, false))
        {
            DetachProcessCpuContext();
            StopProcessCpuDiagnostics();
            return;
        }

        lock (ProcessCpuSync)
        {
            if (_processCpuExiting || _processCpuDiagnostics is not null) return;
            _processCpuApp = this.Get<AppViewModel>();
            _processCpuNavigation = this.Get<NavigationViewModel>();
            _processCpuDiagnostics = new(_processCpuUiThreadId, this.Get<ILogger<App>>());
            _processCpuApp.PlaybackActivityChanged += OnProcessCpuPlaybackChanged;
            _processCpuApp.PropertyChanged += OnProcessCpuWindowChanged;
            _processCpuNavigation.PropertyChanged += OnProcessCpuNavigationChanged;
            PublishProcessCpuContext();
        }
    }

    internal void StopProcessCpuDiagnosticsForWindowClose()
    {
        DetachProcessCpuContext();
        StopProcessCpuDiagnostics(exiting: true);
    }

    private static void StopProcessCpuDiagnostics(bool exiting = false)
    {
        lock (ProcessCpuSync)
        {
            _processCpuExiting |= exiting;
            var session = _processCpuDiagnostics;
            _processCpuDiagnostics = null;
            session?.Stop();
        }
    }

    private void DetachProcessCpuContext()
    {
        if (_processCpuApp is { } app)
        {
            app.PlaybackActivityChanged -= OnProcessCpuPlaybackChanged;
            app.PropertyChanged -= OnProcessCpuWindowChanged;
        }
        if (_processCpuNavigation is { } navigation) navigation.PropertyChanged -= OnProcessCpuNavigationChanged;
        _processCpuApp = null;
        _processCpuNavigation = null;
    }

    private void OnProcessCpuPlaybackChanged(object? sender, EventArgs args) => PublishProcessCpuContext();
    private void OnProcessCpuNavigationChanged(object? sender, PropertyChangedEventArgs args) => PublishProcessCpuContext();

    private void OnProcessCpuWindowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppViewModel.ActivatedWindow)) PublishProcessCpuContext();
    }

    private void PublishProcessCpuContext()
    {
        // Existing UI events publish only a boolean and a type name; the sampling worker never reads XAML.
        var session = Volatile.Read(ref _processCpuDiagnostics);
        if (session is null || _processCpuApp is not { } app || _processCpuNavigation is not { } navigation) return;
        var page = app.ActivatedWindow is { } window && window is not Forms.MainWindow
            ? window.Content?.GetType().Name ?? window.GetType().Name
            : app.EmbeddedPlayer is { IsPlaybackClosing: false }
                ? nameof(Controls.Player.EmbeddedPlayerView) : navigation.DiagnosticsPageTypeName;
        session.UpdateContext(app.HasActivePlayback, page);
    }
}
