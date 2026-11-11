// Copyright (c) Bili Copilot. All rights reserved.

namespace Richasy.MpvKernel.Core;

/// <summary>
/// The outcome of a client call that reports failure without throwing.
/// </summary>
public interface IMpvResult
{
    /// <summary>
    /// Gets a value indicating whether the call succeeded.
    /// </summary>
    bool IsSuccess { get; }

    /// <summary>
    /// Gets a value indicating whether the call failed.
    /// </summary>
    bool IsFailed { get; }

    /// <summary>
    /// Gets what went wrong, or <c>null</c> on success.
    /// </summary>
    string? ErrorMessage { get; }

    /// <summary>
    /// Gets the native error behind the failure. It is <c>null</c> on success and for failures that have no native status.
    /// </summary>
    MpvException? Exception { get; }
}

/// <summary>
/// The outcome of a client call without a return value.
/// </summary>
public readonly struct MpvResult : IMpvResult
{
    private MpvResult(string errorMessage, MpvException? exception)
    {
        ErrorMessage = errorMessage;
        Exception = exception;
    }

    /// <inheritdoc/>
    public bool IsSuccess => ErrorMessage is null;

    /// <inheritdoc/>
    public bool IsFailed => ErrorMessage is not null;

    /// <inheritdoc/>
    public string? ErrorMessage { get; }

    /// <inheritdoc/>
    public MpvException? Exception { get; }

    /// <summary>
    /// A successful result.
    /// </summary>
    public static MpvResult Ok() => default;

    /// <summary>
    /// A failed result that has no native status.
    /// </summary>
    public static MpvResult Fail(string errorMessage) => new(errorMessage, null);

    /// <summary>
    /// A failed result caused by a native error.
    /// </summary>
    public static MpvResult Fail(MpvException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new(exception.Message, exception);
    }
}

/// <summary>
/// The outcome of a client call that returns a value.
/// </summary>
/// <typeparam name="T">The type of the value.</typeparam>
public readonly struct MpvResult<T> : IMpvResult
{
    private readonly T _value;

    private MpvResult(T value)
    {
        _value = value;
        ErrorMessage = null;
        Exception = null;
    }

    private MpvResult(MpvResult failure)
    {
        _value = default!;
        ErrorMessage = failure.ErrorMessage ?? "Mpv | the call failed";
        Exception = failure.Exception;
    }

    /// <inheritdoc/>
    public bool IsSuccess => ErrorMessage is null;

    /// <inheritdoc/>
    public bool IsFailed => ErrorMessage is not null;

    /// <inheritdoc/>
    public string? ErrorMessage { get; }

    /// <inheritdoc/>
    public MpvException? Exception { get; }

    /// <summary>
    /// Gets the value of a successful call. Reading it from a failed result throws.
    /// </summary>
    public T Value => IsSuccess
        ? _value
        : throw new InvalidOperationException($"The result has no value because the call failed: {ErrorMessage}");

#pragma warning disable CA2225 // The conversions are the whole interface; a generic type should not get static factory methods (CA1000).
    /// <summary>
    /// Wraps a value in a successful result.
    /// </summary>
    public static implicit operator MpvResult<T>(T value) => new(value);

    /// <summary>
    /// Carries a failure over to a result that would have returned a value.
    /// </summary>
    public static implicit operator MpvResult<T>(MpvResult failure) => new(failure);
#pragma warning restore CA2225
}
