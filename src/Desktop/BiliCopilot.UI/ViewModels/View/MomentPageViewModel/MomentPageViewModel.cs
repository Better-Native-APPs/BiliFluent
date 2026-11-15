// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Services.WatchRecords;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Bili.Moment;
using Richasy.BiliKernel.Models.Moment;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// 动态页面视图模型.
/// </summary>
public sealed partial class MomentPageViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MomentPageViewModel"/> class.
    /// </summary>
    public MomentPageViewModel(
        IMomentDiscoveryService discoveryService,
        CommentMainViewModel comment,
        WatchRecordIndex watchRecords)
    {
        _momentDiscoveryService = discoveryService;
        CommentModule = comment;
        _watchRecords = watchRecords;

        // Both live for the whole run, so this subscription is never removed.
        _watchRecords.Changed += OnWatchRecordsChanged;
    }

    [RelayCommand]
    private void Initialize()
    {
        if (Sections?.Count > 0)
        {
            if (SelectedSection is not null)
            {
                SelectSectionCommand.Execute(SelectedSection);
            }

            return;
        }

        Sections = new List<IMomentSectionDetailViewModel>
        {
            new VideoMomentSectionDetailViewModel(_momentDiscoveryService),
            new ComprehensiveMomentSectionDetailViewModel(_momentDiscoveryService),
        };

        var isLastVideoSection = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.LastMomentSectionIsVideo, true);
        var section = isLastVideoSection ? Sections.First() : Sections.Last();
        SelectSectionCommand.Execute(section);
        this.Get<DispatcherQueue>().TryEnqueue(DispatcherQueuePriority.Low, () => Initialized?.Invoke(this, EventArgs.Empty));
    }

    [RelayCommand]
    private void SelectSection(IMomentSectionDetailViewModel section)
    {
        if (section is null)
        {
            return;
        }

        if (section != SelectedSection)
        {
            SelectedSection = section;
            var isVideo = section is VideoMomentSectionDetailViewModel;
            SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.LastMomentSectionIsVideo, isVideo);
        }

        if (section.InitializeCommand.CanExecute(null))
        {
            section.InitializeCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void ShowComment(MomentInformation data)
    {
        IsCommentsOpened = true;
        if (CommentModule.Id == data.CommentId)
        {
            return;
        }

        CommentModule.Initialize(data.CommentId, data.CommentType!.Value, Richasy.BiliKernel.Models.CommentSortType.Hot);
        CommentModule.RefreshCommand.Execute(default);
    }

    // Watch records arrive after the cards are on screen (history pull, or a video just played in the app).
    private void OnWatchRecordsChanged(string? aid)
    {
        if (Sections is null)
        {
            return;
        }

        foreach (var section in Sections)
        {
            if (section is VideoMomentSectionDetailViewModel video)
            {
                MomentItemViewModel.RefreshWatchMarks(video.Items, _watchRecords, aid);
            }
            else if (section is ComprehensiveMomentSectionDetailViewModel comprehensive)
            {
                foreach (var uper in comprehensive.Upers)
                {
                    MomentItemViewModel.RefreshWatchMarks(uper.Items, _watchRecords, aid);
                }
            }
        }
    }
}
