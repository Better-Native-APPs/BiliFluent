// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Media;

/// <summary>
/// The source of the home recommendation feed.
/// </summary>
public enum RecommendationFeedSource
{
    /// <summary>
    /// Mobile App recommendations, with an optional access token.
    /// </summary>
    App = 0,

    /// <summary>
    /// Web recommendations, with optional cookies.
    /// </summary>
    Web = 1,
}
