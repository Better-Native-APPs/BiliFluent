// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Authorization;

/// <summary>An opaque, disposable login attempt with its own in-memory cookies.</summary>
public abstract class BiliLoginSession : IDisposable
{
    /// <summary>Initializes a new instance of the <see cref="BiliLoginSession"/> class.</summary>
    protected BiliLoginSession()
    {
    }

    /// <summary>Releases the attempt and invalidates every captcha and context belonging to it.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the in-memory resources owned by a derived session.</summary>
    protected abstract void Dispose(bool disposing);

    /// <inheritdoc/>
    public sealed override string ToString() => "BiliLoginSession [redacted]";
}

/// <summary>An opaque security-center continuation. Valid only while its originating session is alive.</summary>
public abstract class BiliLoginVerificationContext
{
    /// <summary>Initializes a new instance of the <see cref="BiliLoginVerificationContext"/> class.</summary>
    protected BiliLoginVerificationContext()
    {
    }

    /// <inheritdoc/>
    public sealed override string ToString() => "BiliLoginVerificationContext [redacted]";
}

/// <summary>An opaque SMS captcha key, bound to one session, destination, and login flow.</summary>
public abstract class BiliLoginSmsContext
{
    /// <summary>Initializes a new instance of the <see cref="BiliLoginSmsContext"/> class.</summary>
    protected BiliLoginSmsContext()
    {
    }

    /// <inheritdoc/>
    public sealed override string ToString() => "BiliLoginSmsContext [redacted]";
}
