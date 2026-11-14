// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel;

/// <summary>
/// Indicates a failure while performing a BiliKernel operation.
/// </summary>
public class KernelException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="KernelException"/> class.
    /// </summary>
    public KernelException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="KernelException"/> class with an error message.
    /// </summary>
    /// <param name="message">The message describing the failure.</param>
    public KernelException(string? message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="KernelException"/> class with an error message and its cause.
    /// </summary>
    /// <param name="message">The message describing the failure.</param>
    /// <param name="innerException">The exception that caused the failure.</param>
    public KernelException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
