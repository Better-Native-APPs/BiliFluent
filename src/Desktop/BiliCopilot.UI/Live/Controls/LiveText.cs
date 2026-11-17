#nullable enable

using System.Globalization;

namespace BiliCopilot.UI.Live.Controls;

/// <summary>
/// Local, fixed Chinese copy for the shared player's Live controls. The
/// controls do not touch the shared resw files. Session/chat error strings are never
/// shown verbatim; the UI derives its wording from typed state only.
/// </summary>
internal static class LiveText
{
    public const string UntitledRoom = "直播间";

    public const string ChatToggle = "弹幕列表";
    public const string ChatToggleName = "显示或隐藏右侧弹幕列表";

    public const string ChatHeader = "弹幕列表";
    public const string EmptyConnected = "还没有新弹幕";
    public const string JumpToLatest = "回到最新弹幕";
    public const string ComposerName = "弹幕内容";
    public const string ComposerPlaceholder = "发个弹幕…";
    public const string ComposerPlaceholderUnavailable = "现在还不能发送弹幕";
    public const string SendName = "发送弹幕";

    public const string Sending = "正在发送…";
    public const string SendAccepted = "已发送。";
    public const string SendUnavailable = "现在还不能发送弹幕，请等直播和弹幕连接就绪后再试。";
    public const string SendNotSent = "弹幕没有发出，内容已保留。请检查登录状态后再试。";
    public const string SendUnknown = "无法确认弹幕是否已发出。请先看看弹幕列表，确认没有发出后再手动发送。";
    public const string AnonymousUser = "观众";

    public static string SendRejected(int? apiCode)
        => apiCode is { } code
            ? string.Format(CultureInfo.CurrentCulture, "弹幕被拒绝（代码 {0}），内容已保留。", code)
            : "弹幕被拒绝，内容已保留。";

    public static string NewMessages(int count)
        => count <= 0
            ? JumpToLatest
            : count > 99 ? "99+ 条新弹幕" : string.Format(CultureInfo.CurrentCulture, "{0} 条新弹幕", count);

    public static string Playback(LivePlaybackState state) => state switch
    {
        LivePlaybackState.Idle => "准备中",
        LivePlaybackState.Opening => "正在加载直播…",
        LivePlaybackState.Playing => "直播中",
        LivePlaybackState.Paused => "已暂停",
        LivePlaybackState.Ended => "直播已结束或连接中断",
        LivePlaybackState.Failed => "直播加载失败",
        LivePlaybackState.Closing => "正在关闭…",
        LivePlaybackState.Closed => "已关闭",
        _ => string.Empty,
    };

    public static string Chat(LiveChatState state) => state switch
    {
        LiveChatState.Idle => "弹幕未连接",
        LiveChatState.Connecting => "正在连接弹幕…",
        LiveChatState.Authenticating => "正在验证弹幕连接…",
        LiveChatState.Connected => "弹幕已连接",
        LiveChatState.Retrying => "弹幕连接中断，正在重连…",
        LiveChatState.Unavailable => "弹幕暂时不可用",
        LiveChatState.Failed => "弹幕连接失败",
        LiveChatState.Closed => "弹幕已断开",
        _ => string.Empty,
    };
}
