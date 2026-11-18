// Copyright (c) Bili Copilot. All rights reserved.

using System.Runtime.InteropServices;
using HanumanInstitute.LibMpv.Core;

namespace Richasy.MpvKernel.Core;

internal sealed unsafe partial class MpvCoreHandle
{
    // mpv copies the arguments before this call returns. Completion comes through CommandReply.
    public MpvError SendTrackCommand(ulong requestId, string[] args)
    {
        using var call = _nativeWork.TryEnter();
        if (!call.IsActive) return MpvError.Uninitialized;
        var pointers = new nint[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                pointers[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            }

            fixed (nint* list = pointers)
            {
                return ToError(MpvApi.CommandAsync(_ctx, requestId, (byte**)list));
            }
        }
        finally
        {
            foreach (var pointer in pointers)
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    public void AbortTrackCommand(ulong requestId)
    {
        using var call = _nativeWork.TryEnter();
        if (call.IsActive) MpvApi.AbortAsyncCommand(_ctx, requestId);
    }
}
