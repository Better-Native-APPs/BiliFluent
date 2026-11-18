// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Resolvers;
using Richasy.MpvKernel.Player;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private IMpvMediaSourceResolver? _sourceResolver;

    // DASH selection, URL deadlines and relay address tables belong only to on-demand resolvers.
    private MediaSourceResolverBase? OnDemandResolver => _sourceResolver as MediaSourceResolverBase;
}
