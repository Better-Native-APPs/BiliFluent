// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using Microsoft.UI.Xaml.Data;
using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.Converters;

internal sealed partial class RecommendationFeedSourceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is RecommendationFeedSource source
            ? source switch
            {
                RecommendationFeedSource.Web => ResourceToolkit.GetLocalizedString(StringNames.RecommendationFeedWeb),
                RecommendationFeedSource.App => ResourceToolkit.GetLocalizedString(StringNames.RecommendationFeedApp),
                _ => string.Empty,
            }
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}
