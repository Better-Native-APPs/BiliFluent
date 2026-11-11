// Copyright (c) Bili Copilot. All rights reserved.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.Storage;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Subtitle;
using Richasy.WinUIKernel.Share.ViewModels;
using System.Globalization;

namespace BiliCopilot.UI.ViewModels.Items;

public sealed partial class SubtitleItemViewModel : ViewModelBase<SubtitleMeta>
{
    private readonly Func<SubtitleItemViewModel, Task> _activeFunc;
    private readonly Func<SubtitleItemViewModel, Task> _deactiveFunc;
    private readonly Action<string> _showError;
    private readonly CancellationToken _cancellationToken;
    private List<SubtitleInformation>? _subtitles;
    internal string? _srtFilePath;

    public SubtitleItemViewModel(SubtitleMeta meta, Func<SubtitleItemViewModel, Task> activeFunc, Func<SubtitleItemViewModel, Task> deactiveFunc,
        Action<string> showError, CancellationToken cancellationToken)
        : base(meta)
    {
        _activeFunc = activeFunc;
        _deactiveFunc = deactiveFunc;
        _showError = showError;
        _cancellationToken = cancellationToken;
        Name = meta.LanguageName;
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public override bool Equals(object? obj) => obj is SubtitleItemViewModel model && Data.Id == model.Data.Id;

    public override int GetHashCode() => HashCode.Combine(Data.Id);

    [RelayCommand]
    private async Task ActiveAsync()
    {
        if (IsSelected)
        {
            await _deactiveFunc(this);
            return;
        }

        await SelectAsync();
    }

    [RelayCommand]
    private async Task SelectAsync()
    {
        try
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_subtitles is null)
            {
                var subtitles = await this.Get<ISubtitleService>().GetSubtitleDetailAsync(Data, _cancellationToken);
                _subtitles = [.. subtitles];
            }

            if (string.IsNullOrEmpty(_srtFilePath))
            {
                var subtitles = _subtitles.Where(subtitle => double.IsFinite(subtitle.StartPosition)
                    && double.IsFinite(subtitle.EndPosition) && subtitle.StartPosition >= 0
                    && subtitle.EndPosition > subtitle.StartPosition && !string.IsNullOrWhiteSpace(subtitle.Content)).ToList();
                if (subtitles.Count == 0)
                {
                    _subtitles = null;
                    _showError("字幕没有可显示的内容，请重试。");
                    return;
                }

                var srtContent = ConvertToSrt(subtitles);
                var folderPath = Path.Combine(ApplicationData.GetDefault().TemporaryPath, "Subtitles");
                Directory.CreateDirectory(folderPath);
                var filePath = Path.Combine(folderPath, $"{Data.Id}.srt");
                await File.WriteAllTextAsync(filePath, srtContent, Encoding.UTF8, _cancellationToken);
                // Publish the cache path only after a successful write, so IO failures can be retried.
                _srtFilePath = filePath;
            }

            _cancellationToken.ThrowIfCancellationRequested();
            await _activeFunc(this);
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            this.Get<ILogger<SubtitleItemViewModel>>().LogWarning("加载字幕失败：{ErrorType}", ex.GetType().Name);
            _showError("字幕加载失败，请重试。");
        }
    }

    /// <summary>
    /// 将一组 SubtitleInformation 转换为标准 SRT 字幕内容（字符串）。
    /// </summary>
    /// <param name="subtitles">字幕信息集合</param>
    /// <returns>符合 .srt 格式的字符串</returns>
    private static string ConvertToSrt(IEnumerable<SubtitleInformation> subtitles)
    {
        var sb = new StringBuilder();
        var index = 1;
        foreach (var subtitle in subtitles)
        {
            sb.AppendLine(index.ToString(CultureInfo.InvariantCulture));

            var start = SecondsToSrtTime(subtitle.StartPosition);
            var end = SecondsToSrtTime(subtitle.EndPosition);

            sb.AppendLine($"{start} --> {end}");
            sb.AppendLine(subtitle.Content ?? string.Empty);
            sb.AppendLine();
            index++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// 将秒数转换为 SRT 格式时间字符串 "hh:mm:ss,fff"。
    /// </summary>
    private static string SecondsToSrtTime(double seconds)
    {
        var hours = (int)(seconds / 3600);
        var minutes = (int)(seconds % 3600 / 60);
        var secs = (int)(seconds % 60);
        var millis = (int)((seconds - (int)seconds) * 1000);

        // 注意格式用逗号和前导零
        return string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}:{2:D2},{3:D3}", hours, minutes, secs, millis);
    }
}
