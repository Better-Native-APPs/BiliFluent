// Copyright (c) Bili Copilot. All rights reserved.

using System.Net;

namespace BiliCopilot.UI.Extensions;

internal static class InternalHttpExtensions
{
    /// <summary>
    /// 视频用户代理.
    /// </summary>
    public const string VideoUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/116.0.0.0 Safari/537.36 Edg/116.0.1938.69";

    /// <summary>
    /// 直播用户代理.
    /// </summary>
    public const string LiveUserAgent = "Mozilla/5.0 BiliDroid/1.12.0 (bbcallen@gmail.com)";

    /// <summary>
    /// 视频来源.
    /// </summary>
    public const string VideoReferer = "https://www.bilibili.com";

    /// <summary>
    /// 直播来源.
    /// </summary>
    public const string LiveReferer = "https://live.bilibili.com";

    public static readonly HttpClient PublicImageClient = GetImageClient(allowSelfSignedCertificates: false);

    public static readonly HttpClient MediaServerImageClient = GetImageClient(allowSelfSignedCertificates: true);

    /// <summary>
    /// 获取图片下载客户端.
    /// </summary>
    /// <returns></returns>
    private static HttpClient GetImageClient(bool allowSelfSignedCertificates)
    {
#if DEBUG
        allowSelfSignedCertificates = true;
#endif
        var handler = new CustomHttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            PreAuthenticate = true,
            ClientCertificateOptions = ClientCertificateOption.Automatic,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli,
            UseCookies = false,
        };

        if (allowSelfSignedCertificates)
        {
            // Only the dedicated self-hosted media server client bypasses release validation.
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

#pragma warning disable CA5400 // Preserve platform defaults; evaluate certificate revocation checks separately.
        var client = new HttpClient(handler);
#pragma warning restore CA5400
        client.DefaultRequestHeaders.Add("User-Agent", VideoUserAgent);
        return client;
    }

    private sealed class CustomHttpClientHandler : HttpClientHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
