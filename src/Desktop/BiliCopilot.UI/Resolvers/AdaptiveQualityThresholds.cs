// Copyright (c) BiliFluent. All rights reserved.

namespace BiliCopilot.UI.Resolvers;

/// <summary>Quality adaptation has its own budget and never changes node-failover thresholds.</summary>
internal static class AdaptiveQualityThresholds
{
    public const int InitialQuality = 64;
    public const int HdrQuality = 125;
    public const int DolbyVisionQuality = 126;
    public const int EightKQuality = 127;
    public const int EightKWidth = 7680;
    public const int EightKHeight = 4320;
    public const double BandwidthPercentile = 0.25;
    public const double InitialBandwidthRatio = 1.5;
    public const double InitialUpgradeCacheSeconds = 12;
    public const double RecoveryBandwidthRatio = 1.8;
    public const double RecoveryUpgradeCacheSeconds = 20;
    public const int RebufferCount = 2;
    public const double RebufferDurationSeconds = 8;
    public const int MaxSwitchesPerMedia = 6;
    public const double SpeedChangeTolerance = 0.001;
    public const double BitsPerByte = 8;
    public const double BitsPerKilobit = 1000;

    public static readonly TimeSpan EvidenceLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan BandwidthWindow = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan PreviousMediaStableTime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan InitialUpgradeDuration = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan RecoveryCooldown = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan RecoveryUpgradeDuration = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RebufferCountWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RebufferDurationWindow = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DiscontinuitySettle = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan NodeObservationSettle = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan ReloadObservationSettle = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan SampleGapReset = TimeSpan.FromSeconds(5);
}
