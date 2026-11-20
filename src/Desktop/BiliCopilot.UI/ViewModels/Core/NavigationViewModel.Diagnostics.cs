// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class NavigationViewModel
{
    internal string DiagnosticsPageTypeName
        => (IsOverlayOpen ? _overFrame?.GetCurrentContent() : _navFrame?.GetCurrentContent())?.GetType().Name ?? "n/a";
}
