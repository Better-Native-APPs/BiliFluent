// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Core;

namespace BiliCopilot.UI.Extensions;

internal sealed partial class PlayerComponentTemplateSelector : DataTemplateSelector
{
    public DataTemplate VideoTemplate { get; set; }

    public DataTemplate PgcTemplate { get; set; }

    public DataTemplate LiveTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is VideoConnectorViewModel)
        {
            return VideoTemplate;
        }
        else if (item is PgcConnectorViewModel)
        {
            return PgcTemplate;
        }
        else if (item is LiveConnectorViewModel)
        {
            // Every host sets it, an empty template included: without one the host would print the type name.
            return LiveTemplate;
        }

        return default;
    }
}
