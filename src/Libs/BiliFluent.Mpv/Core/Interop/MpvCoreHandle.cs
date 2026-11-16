// Copyright (c) Bili Copilot. All rights reserved.

using System.Runtime.InteropServices;
using System.Text;
using HanumanInstitute.LibMpv.Core;
using NativeEvent = HanumanInstitute.LibMpv.Core.MpvEvent;
using NativeFormat = HanumanInstitute.LibMpv.Core.MpvFormat;
using NativeNode = HanumanInstitute.LibMpv.Core.MpvNode;

namespace Richasy.MpvKernel.Core;

/// <summary>
/// One mpv client handle. Every native call of this library goes through the
/// HanumanInstitute.LibMpv bindings (<see cref="MpvApi"/>) from here.
/// </summary>
/// <remarks>
/// Calls return the native status instead of throwing, the way <see cref="MpvClient"/> expects them.
/// </remarks>
internal sealed unsafe partial class MpvCoreHandle
{
    private readonly MpvHandle* _ctx;
    private readonly MpvWorkGate _nativeWork = new();
    private readonly Action _destroy;

    private MpvCoreHandle(MpvHandle* ctx)
    {
        _ctx = ctx;
        _destroy = () => MpvApi.Destroy(_ctx);
    }

    // An already-closed handle lets lifecycle checks run without loading native code.
    internal MpvCoreHandle(Action destroy)
    {
        _destroy = destroy;
        _ = _nativeWork.CloseAsync();
    }

    /// <summary>
    /// Creates a new, not yet initialized, mpv core.
    /// </summary>
    public static MpvCoreHandle Create()
    {
        var ctx = MpvApi.Create();
        return ctx == null
            ? throw new MpvException("Mpv | create failed", MpvError.Nomem)
            : new MpvCoreHandle(ctx);
    }

    public MpvError Initialize()
    {
        using var call = _nativeWork.TryEnter();
        return call.IsActive ? ToError(MpvApi.Initialize(_ctx)) : MpvError.Uninitialized;
    }

    public MpvError SetOptionString(string name, string value)
    {
        using var call = _nativeWork.TryEnter();
        return call.IsActive ? ToError(MpvApi.SetOptionString(_ctx, name, value)) : MpvError.Uninitialized;
    }

    public MpvError SetOption(string name, long value)
    {
        using var call = _nativeWork.TryEnter();
        return call.IsActive ? ToError(MpvApi.SetOption(_ctx, name, NativeFormat.Int64, &value)) : MpvError.Uninitialized;
    }

    public MpvError SetOption(string name, double value)
    {
        using var call = _nativeWork.TryEnter();
        return call.IsActive ? ToError(MpvApi.SetOption(_ctx, name, NativeFormat.Double, &value)) : MpvError.Uninitialized;
    }

    /// <summary>
    /// Sets an option through <c>MPV_FORMAT_STRING</c>, which mpv parses like a command line value.
    /// </summary>
    public MpvError SetOptionFormatted(string name, string value)
    {
        using var call = _nativeWork.TryEnter();
        if (!call.IsActive) return MpvError.Uninitialized;
        var text = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return ToError(MpvApi.SetOption(_ctx, name, NativeFormat.String, &text));
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    public MpvError SetProperty(string name, bool value)
    {
        using var call = _nativeWork.TryEnter();
        if (!call.IsActive) return MpvError.Uninitialized;
        var flag = value ? 1 : 0;
        return ToError(MpvApi.SetProperty(_ctx, name, NativeFormat.Flag, &flag));
    }

    public MpvError SetProperty(string name, long value)
    {
        using var call = _nativeWork.TryEnter();
        return call.IsActive ? ToError(MpvApi.SetProperty(_ctx, name, NativeFormat.Int64, &value)) : MpvError.Uninitialized;
    }

    public MpvError SetProperty(string name, double value)
    {
        using var call = _nativeWork.TryEnter();
        return call.IsActive ? ToError(MpvApi.SetProperty(_ctx, name, NativeFormat.Double, &value)) : MpvError.Uninitialized;
    }

    public MpvError SetPropertyString(string name, string value)
    {
        using var call = _nativeWork.TryEnter();
        return call.IsActive ? ToError(MpvApi.SetPropertyString(_ctx, name, value)) : MpvError.Uninitialized;
    }

    /// <summary>
    /// Sets a property from a node that holds an integer.
    /// </summary>
    public MpvError SetPropertyNode(string name, long value)
    {
        using var call = _nativeWork.TryEnter();
        if (!call.IsActive) return MpvError.Uninitialized;
        var node = new NativeNode { Format = NativeFormat.Int64, U = new MpvNodeU { Int64 = value } };
        return ToError(MpvApi.SetProperty(_ctx, name, NativeFormat.Node, &node));
    }

    /// <summary>
    /// Sets a property from a node that holds a string.
    /// </summary>
    public MpvError SetPropertyNode(string name, string value)
    {
        using var call = _nativeWork.TryEnter();
        if (!call.IsActive) return MpvError.Uninitialized;
        var text = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            var node = new NativeNode { Format = NativeFormat.String, U = new MpvNodeU { String = (byte*)text } };
            return ToError(MpvApi.SetProperty(_ctx, name, NativeFormat.Node, &node));
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    public MpvError GetProperty(string name, out bool value)
    {
        using var call = _nativeWork.TryEnter();
        if (!call.IsActive)
        {
            value = default;
            return MpvError.Uninitialized;
        }
        var flag = 0;
        var error = ToError(MpvApi.GetProperty(_ctx, name, NativeFormat.Flag, &flag));
        value = flag != 0;
        return error;
    }

    public MpvError GetProperty(string name, out long value)
    {
        using var call = _nativeWork.TryEnter();
        if (!call.IsActive)
        {
            value = default;
            return MpvError.Uninitialized;
        }
        long result = 0;
        var error = ToError(MpvApi.GetProperty(_ctx, name, NativeFormat.Int64, &result));
        value = result;
        return error;
    }

    public MpvError GetProperty(string name, out double value)
    {
        using var call = _nativeWork.TryEnter();
        if (!call.IsActive)
        {
            value = default;
            return MpvError.Uninitialized;
        }
        double result = 0;
        var error = ToError(MpvApi.GetProperty(_ctx, name, NativeFormat.Double, &result));
        value = result;
        return error;
    }

    /// <summary>
    /// Reads a property as a managed value tree, see <see cref="MpvNodeReader"/>.
    /// </summary>
    public MpvError GetPropertyNode(string name, out object? value)
    {
        using var call = _nativeWork.TryEnter();
        if (!call.IsActive)
        {
            value = default;
            return MpvError.Uninitialized;
        }
        var node = default(NativeNode);
        var error = ToError(MpvApi.GetProperty(_ctx, name, NativeFormat.Node, &node));
        if (error != MpvError.Success)
        {
            value = null;
            return error;
        }

        try
        {
            value = MpvNodeReader.Read(&node);
        }
        finally
        {
            MpvApi.FreeNodeContents(&node);
        }

        return error;
    }

    /// <summary>
    /// Sums the <c>peak</c> and <c>avg</c> times of the fresh frame passes in <c>vo-passes</c>.
    /// The node is walked in place, so the sample arrays of the passes are never copied.
    /// </summary>
    public MpvError GetFreshPassTimes(out long peak, out long average)
    {
        peak = 0;
        average = 0;
        using var call = _nativeWork.TryEnter();
        if (!call.IsActive) return MpvError.Uninitialized;
        var node = default(NativeNode);
        var error = ToError(MpvApi.GetProperty(_ctx, "vo-passes", NativeFormat.Node, &node));
        if (error != MpvError.Success)
        {
            return error;
        }

        try
        {
            var types = node.Format == NativeFormat.NodeMap ? node.U.List : null;
            if (types == null || types->Keys == null)
            {
                return MpvError.PropertyUnavailable;
            }

            for (var i = 0; i < types->Num; i++)
            {
                if (!KeyEquals(types->Keys[i], "fresh"u8))
                {
                    continue;
                }

                var passes = types->Values[i].Format == NativeFormat.NodeArray ? types->Values[i].U.List : null;
                if (passes == null || passes->Num == 0)
                {
                    return MpvError.PropertyUnavailable;
                }

                for (var pass = 0; pass < passes->Num; pass++)
                {
                    var fields = passes->Values[pass].Format == NativeFormat.NodeMap ? passes->Values[pass].U.List : null;
                    if (fields == null || fields->Keys == null)
                    {
                        continue;
                    }

                    for (var field = 0; field < fields->Num; field++)
                    {
                        if (fields->Values[field].Format != NativeFormat.Int64)
                        {
                            continue;
                        }

                        if (KeyEquals(fields->Keys[field], "peak"u8))
                        {
                            peak += fields->Values[field].U.Int64;
                        }
                        else if (KeyEquals(fields->Keys[field], "avg"u8))
                        {
                            average += fields->Values[field].U.Int64;
                        }
                    }
                }

                return MpvError.Success;
            }

            return MpvError.PropertyUnavailable;
        }
        finally
        {
            MpvApi.FreeNodeContents(&node);
        }
    }

    /// <summary>
    /// Reads a property as text. Returns <c>null</c> when the property is unavailable.
    /// </summary>
    public string? GetPropertyString(string name)
    {
        using var call = _nativeWork.TryEnter();
        if (!call.IsActive) return null;
        var text = MpvApi.GetPropertyString(_ctx, name);
        if (text == null)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8((nint)text);
        }
        finally
        {
            MpvApi.Free(text);
        }
    }

    public MpvError ObserveProperty(string name, NativeFormat format)
    {
        using var call = _nativeWork.TryEnter();
        return call.IsActive ? ToError(MpvApi.ObserveProperty(_ctx, 0, name, format)) : MpvError.Uninitialized;
    }

    public MpvError RequestLogMessages(string minLevel)
    {
        using var call = _nativeWork.TryEnter();
        return call.IsActive ? ToError(MpvApi.RequestLogMessages(_ctx, minLevel)) : MpvError.Uninitialized;
    }

    public MpvError LoadConfigFile(string filePath)
    {
        using var call = _nativeWork.TryEnter();
        return call.IsActive ? ToError(MpvApi.LoadConfigFile(_ctx, filePath)) : MpvError.Uninitialized;
    }

    /// <summary>
    /// Runs a command that mpv parses itself, like a line of input.conf.
    /// </summary>
    public MpvError CommandString(string command)
    {
        using var call = _nativeWork.TryEnter();
        return call.IsActive ? ToError(MpvApi.CommandString(_ctx, command)) : MpvError.Uninitialized;
    }

    /// <summary>
    /// Runs a command whose arguments are passed as they are, without any parsing or quoting.
    /// </summary>
    public MpvError Command(string[] args)
    {
        using var call = _nativeWork.TryEnter();
        if (!call.IsActive) return MpvError.Uninitialized;
        var pointers = (byte**)NativeMemory.AllocZeroed((nuint)(args.Length + 1), (nuint)sizeof(byte*));
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                var bytes = Encoding.UTF8.GetBytes(args[i]);
                var buffer = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
                bytes.CopyTo(new Span<byte>(buffer, bytes.Length));
                buffer[bytes.Length] = 0;
                pointers[i] = buffer;
            }

            return ToError(MpvApi.Command(_ctx, pointers));
        }
        finally
        {
            for (var i = 0; i < args.Length; i++)
            {
                NativeMemory.Free(pointers[i]);
            }

            NativeMemory.Free(pointers);
        }
    }

    /// <summary>
    /// Blocks until the next event. The returned pointer is only valid until the next call.
    /// </summary>
    public NativeEvent* WaitEvent(double timeout)
    {
        using var call = _nativeWork.TryEnter();
        return call.IsActive ? MpvApi.WaitEvent(_ctx, timeout) : null;
    }

    /// <summary>
    /// Makes a blocked <see cref="WaitEvent"/> return.
    /// </summary>
    public void Wakeup()
    {
        using var call = _nativeWork.TryEnter();
        if (call.IsActive) MpvApi.Wakeup(_ctx);
    }

    internal Task DrainNativeCallsAsync() => _nativeWork.CloseAsync();

    /// <summary>
    /// Detaches and frees the handle. Nothing may use this object afterwards.
    /// </summary>
    public void Destroy()
    {
        if (!_nativeWork.CloseAsync().IsCompletedSuccessfully)
            throw new InvalidOperationException("Mpv | Native calls have not drained.");
        _destroy();
    }

    private static MpvError ToError(int code) => code >= 0 ? MpvError.Success : (MpvError)code;

    private static bool KeyEquals(byte* key, ReadOnlySpan<byte> expected)
        => key != null && MemoryMarshal.CreateReadOnlySpanFromNullTerminated(key).SequenceEqual(expected);
}
