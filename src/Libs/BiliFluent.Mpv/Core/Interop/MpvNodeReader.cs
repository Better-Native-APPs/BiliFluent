// Copyright (c) Bili Copilot. All rights reserved.

using System.Runtime.InteropServices;
using HanumanInstitute.LibMpv.Core;
using NativeFormat = HanumanInstitute.LibMpv.Core.MpvFormat;
using NativeNode = HanumanInstitute.LibMpv.Core.MpvNode;

namespace Richasy.MpvKernel.Core;

/// <summary>
/// Copies an <c>mpv_node</c> into managed values, so that nothing keeps pointing into mpv's memory.
/// </summary>
/// <remarks>
/// A map becomes a <see cref="Dictionary{TKey, TValue}"/> of <see cref="string"/> to value, an array becomes
/// a <see cref="List{T}"/>, and the leaves are <see cref="string"/>, <see cref="bool"/>, <see cref="long"/>
/// or <see cref="double"/>. Anything else is <c>null</c>.
/// </remarks>
internal static unsafe class MpvNodeReader
{
    public static object? Read(NativeNode* node)
    {
        if (node == null)
        {
            return null;
        }

        var value = node->U;
        switch (node->Format)
        {
            case NativeFormat.String:
            case NativeFormat.OsdString:
                return Marshal.PtrToStringUTF8((nint)value.String);
            case NativeFormat.Flag:
                return value.Flag != 0;
            case NativeFormat.Int64:
                return value.Int64;
            case NativeFormat.Double:
                return value.Double;
            case NativeFormat.NodeArray:
                return ReadArray(value.List);
            case NativeFormat.NodeMap:
                return ReadMap(value.List);
            default:
                return null;
        }
    }

    public static string? GetString(this Dictionary<string, object?> map, string key)
        => map.TryGetValue(key, out var value) ? value as string : null;

    public static bool GetFlag(this Dictionary<string, object?> map, string key)
        => map.TryGetValue(key, out var value) && value is true;

    public static long? GetInteger(this Dictionary<string, object?> map, string key)
        => map.TryGetValue(key, out var value) && value is long number ? number : null;

    public static double? GetDouble(this Dictionary<string, object?> map, string key)
        => map.TryGetValue(key, out var value) ? value switch { double number => number, long number => number, _ => null } : null;

    private static List<object?> ReadArray(MpvNodeList* list)
    {
        var result = new List<object?>();
        if (list == null)
        {
            return result;
        }

        for (var i = 0; i < list->Num; i++)
        {
            result.Add(Read(&list->Values[i]));
        }

        return result;
    }

    private static Dictionary<string, object?> ReadMap(MpvNodeList* list)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (list == null || list->Keys == null)
        {
            return result;
        }

        for (var i = 0; i < list->Num; i++)
        {
            var key = Marshal.PtrToStringUTF8((nint)list->Keys[i]);
            if (key is not null)
            {
                result[key] = Read(&list->Values[i]);
            }
        }

        return result;
    }
}
