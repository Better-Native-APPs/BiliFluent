// Copyright (c) Bili Copilot. All rights reserved.

namespace Richasy.MpvKernel.Core;

/// <summary>
/// Direct access to mpv options, properties and commands by name, for everything
/// that has no dedicated method on the client.
/// </summary>
/// <remarks>
/// These calls run on the calling thread and block until mpv has answered.
/// They report the native status and do not throw. A disposed client answers
/// <see cref="MpvError.Uninitialized"/>, or <c>null</c> where a value is returned.
/// </remarks>
public sealed partial class MpvClient
{
    /// <summary>
    /// Sets an option from its text form, the way a command line or a config file would.
    /// </summary>
    public MpvError SetOptionString(string name, string value)
        => IsDisposed ? MpvError.Uninitialized : _handle.SetOptionString(name, value);

    /// <summary>
    /// Sets a property from its text form.
    /// </summary>
    public MpvError SetPropertyString(string name, string value)
    {
        var trace = name is "sid" or "secondary-sid" or "sub-visibility" or "secondary-sub-visibility";
        var operation = trace ? "set " + name + "=" + SubtitleDiagnosticText(value) : string.Empty;
        if (trace) TraceSubtitleState(operation, "before");
        var error = IsDisposed ? MpvError.Uninitialized : _handle.SetPropertyString(name, value);
        if (trace) TraceSubtitleState(operation, "after", error.ToString());
        return error;
    }

    /// <summary>
    /// Sets a yes/no property.
    /// </summary>
    public MpvError SetProperty(string name, bool value)
        => IsDisposed ? MpvError.Uninitialized : _handle.SetProperty(name, value);

    /// <summary>
    /// Sets an integer property.
    /// </summary>
    public MpvError SetProperty(string name, long value)
        => IsDisposed ? MpvError.Uninitialized : _handle.SetProperty(name, value);

    /// <summary>
    /// Sets a floating point property.
    /// </summary>
    public MpvError SetProperty(string name, double value)
        => IsDisposed ? MpvError.Uninitialized : _handle.SetProperty(name, value);

    /// <summary>
    /// Reads a property as text. Returns <c>null</c> when the property is unavailable.
    /// </summary>
    public string? GetPropertyString(string name)
        => IsDisposed ? null : _handle.GetPropertyString(name);

    /// <summary>
    /// Reads a yes/no property.
    /// </summary>
    public MpvError GetProperty(string name, out bool value)
    {
        if (IsDisposed)
        {
            value = default;
            return MpvError.Uninitialized;
        }

        return _handle.GetProperty(name, out value);
    }

    /// <summary>
    /// Reads an integer property.
    /// </summary>
    public MpvError GetProperty(string name, out long value)
    {
        if (IsDisposed)
        {
            value = default;
            return MpvError.Uninitialized;
        }

        return _handle.GetProperty(name, out value);
    }

    /// <summary>
    /// Reads a floating point property.
    /// </summary>
    public MpvError GetProperty(string name, out double value)
    {
        if (IsDisposed)
        {
            value = default;
            return MpvError.Uninitialized;
        }

        return _handle.GetProperty(name, out value);
    }

    /// <summary>
    /// Runs a command. Each argument is passed to mpv as it is, so nothing needs quoting.
    /// </summary>
    public MpvError Command(params string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var trace = args.Length > 0 && args[0] is "sub-add" or "sub-remove" or "sub-reload";
        var operation = trace ? args[0] + " " + (args.Length > 1 ? SubtitleDiagnosticText(args[1]) : "current")
            + (args.Length > 2 ? " flag=" + SubtitleDiagnosticText(args[2]) : string.Empty) : string.Empty;
        if (trace) TraceSubtitleState(operation, "before");
        var error = IsDisposed ? MpvError.Uninitialized : _handle.Command(args);
        if (trace) TraceSubtitleState(operation, "after", error.ToString());
        return error;
    }

    /// <summary>
    /// Runs a command written like a line of input.conf, which mpv splits and unquotes itself.
    /// </summary>
    public MpvError CommandString(string command)
        => IsDisposed ? MpvError.Uninitialized : _handle.CommandString(command);
}
