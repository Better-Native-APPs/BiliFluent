// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Core;
using Richasy.WinUIKernel.Share;

namespace BiliCopilot.UI.Extensions;

public sealed class XamlRootProvider : IXamlRootProvider
{
    public XamlRoot? XamlRoot => GlobalDependencies.Services.GetRequiredService<AppViewModel>().ActivatedWindow.Content.XamlRoot;
}
