// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace BiliFluent.Mpv.RelayCheck;

internal static partial class PlaybackSmokeSupport
{
    internal sealed unsafe class NativeMpv : IDisposable
    {
        private readonly nint _library;
        private readonly nint _handle;
        private readonly delegate* unmanaged[Cdecl]<nint, int> _initialize;
        private readonly delegate* unmanaged[Cdecl]<nint, void> _destroy;
        private readonly delegate* unmanaged[Cdecl]<nint, byte*, byte*, int> _setOption;
        private readonly delegate* unmanaged[Cdecl]<nint, byte*, byte*, int> _setProperty;
        private readonly delegate* unmanaged[Cdecl]<nint, byte*, int, void*, int> _setTypedProperty;
        private readonly delegate* unmanaged[Cdecl]<nint, byte*, int, void*, int> _getProperty;
        private readonly delegate* unmanaged[Cdecl]<nint, byte*, nint> _getString;
        private readonly delegate* unmanaged[Cdecl]<nint, void> _free;
        private readonly delegate* unmanaged[Cdecl]<nint, byte**, int> _command;
        private readonly delegate* unmanaged[Cdecl]<nint, double, nint> _waitEvent;
        private readonly delegate* unmanaged[Cdecl]<nint, void> _wakeup;
        private Thread? _eventThread;
        private bool _stop;
        private bool _disposed;
        private int _audioReconfigs;
        private int _playbackRestarts;

        internal NativeMpv(string path)
        {
            _library = NativeLibrary.Load(path);
            try
            {
                var create = (delegate* unmanaged[Cdecl]<nint>)NativeLibrary.GetExport(_library, "mpv_create");
                _initialize = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(_library, "mpv_initialize");
                _destroy = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(_library, "mpv_terminate_destroy");
                _setOption = (delegate* unmanaged[Cdecl]<nint, byte*, byte*, int>)NativeLibrary.GetExport(_library, "mpv_set_option_string");
                _setProperty = (delegate* unmanaged[Cdecl]<nint, byte*, byte*, int>)NativeLibrary.GetExport(_library, "mpv_set_property_string");
                _setTypedProperty = (delegate* unmanaged[Cdecl]<nint, byte*, int, void*, int>)NativeLibrary.GetExport(_library, "mpv_set_property");
                _getProperty = (delegate* unmanaged[Cdecl]<nint, byte*, int, void*, int>)NativeLibrary.GetExport(_library, "mpv_get_property");
                _getString = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)NativeLibrary.GetExport(_library, "mpv_get_property_string");
                _free = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(_library, "mpv_free");
                _command = (delegate* unmanaged[Cdecl]<nint, byte**, int>)NativeLibrary.GetExport(_library, "mpv_command");
                _waitEvent = (delegate* unmanaged[Cdecl]<nint, double, nint>)NativeLibrary.GetExport(_library, "mpv_wait_event");
                _wakeup = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(_library, "mpv_wakeup");
                _handle = create();
                if (_handle == 0) throw new InvalidOperationException("mpv_create failed.");
            }
            catch
            {
                NativeLibrary.Free(_library);
                throw;
            }
        }

        internal int AudioReconfigs => Volatile.Read(ref _audioReconfigs);
        internal int PlaybackRestarts => Volatile.Read(ref _playbackRestarts);

        internal void SetOption(string name, string value)
        {
            Span<byte> encodedName = stackalloc byte[Encoding.UTF8.GetByteCount(name) + 1];
            Span<byte> encodedValue = stackalloc byte[Encoding.UTF8.GetByteCount(value) + 1];
            Encode(name, encodedName);
            Encode(value, encodedValue);
            fixed (byte* n = encodedName)
            fixed (byte* v = encodedValue)
                RequireSuccess(_setOption(_handle, n, v), "option " + name);
        }

        internal void Initialize()
        {
            RequireSuccess(_initialize(_handle), "initialize");
            _eventThread = new Thread(PumpEvents) { IsBackground = true, Name = "playback-smoke-events" };
            _eventThread.Start();
        }

        internal CallResult SetProperty(string name, string value)
        {
            Span<byte> encodedName = stackalloc byte[Encoding.UTF8.GetByteCount(name) + 1];
            Span<byte> encodedValue = stackalloc byte[Encoding.UTF8.GetByteCount(value) + 1];
            Encode(name, encodedName);
            Encode(value, encodedValue);
            fixed (byte* n = encodedName)
            fixed (byte* v = encodedValue)
            {
                var started = Stopwatch.GetTimestamp();
                var error = _setProperty(_handle, n, v);
                return new(started, Stopwatch.GetTimestamp(), error);
            }
        }

        internal CallResult SetSpeed(double value)
        {
            fixed (byte* name = "speed\0"u8)
            {
                var started = Stopwatch.GetTimestamp();
                var error = _setTypedProperty(_handle, name, 5, &value);
                return new(started, Stopwatch.GetTimestamp(), error);
            }
        }

        internal CallResult Command(params string[] arguments)
        {
            var size = arguments.Sum(argument => Encoding.UTF8.GetByteCount(argument) + 1);
            Span<byte> encoded = stackalloc byte[size];
            var pointers = stackalloc byte*[arguments.Length + 1];
            fixed (byte* start = encoded)
            {
                var offset = 0;
                for (var index = 0; index < arguments.Length; index++)
                {
                    pointers[index] = start + offset;
                    var length = Encoding.UTF8.GetByteCount(arguments[index]) + 1;
                    Encode(arguments[index], encoded.Slice(offset, length));
                    offset += length;
                }

                pointers[arguments.Length] = null;
                var started = Stopwatch.GetTimestamp();
                var error = _command(_handle, pointers);
                return new(started, Stopwatch.GetTimestamp(), error);
            }
        }

        internal string? GetString(string name)
        {
            Span<byte> encoded = stackalloc byte[Encoding.UTF8.GetByteCount(name) + 1];
            Encode(name, encoded);
            fixed (byte* n = encoded)
            {
                var value = _getString(_handle, n);
                if (value == 0) return null;
                try { return Marshal.PtrToStringUTF8(value); }
                finally { _free(value); }
            }
        }

        internal double? GetNumber(string name)
        {
            Span<byte> encoded = stackalloc byte[Encoding.UTF8.GetByteCount(name) + 1];
            Encode(name, encoded);
            double value = 0;
            fixed (byte* n = encoded)
                return _getProperty(_handle, n, 5, &value) >= 0 && double.IsFinite(value) ? value : null;
        }

        internal long? GetCounter(string name)
        {
            Span<byte> encoded = stackalloc byte[Encoding.UTF8.GetByteCount(name) + 1];
            Encode(name, encoded);
            long value = 0;
            fixed (byte* n = encoded)
                return _getProperty(_handle, n, 4, &value) >= 0 ? value : null;
        }

        internal ProbeSample Probe()
        {
            var started = Stopwatch.GetTimestamp();
            var position = ReadNumber("time-pos\0"u8);
            var audio = ReadNumber("audio-pts\0"u8);
            var speed = ReadNumber("speed\0"u8);
            var avsync = ReadNumber("avsync\0"u8);
            var idle = ReadFlag("core-idle\0"u8);
            var cached = ReadFlag("paused-for-cache\0"u8);
            var paused = ReadFlag("pause\0"u8);
            return new(started, Stopwatch.GetTimestamp(), position, audio, speed, avsync, idle, cached, paused);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Volatile.Write(ref _stop, true);
            _wakeup(_handle);
            _eventThread?.Join();
            _destroy(_handle);
            NativeLibrary.Free(_library);
        }

        private NumericRead ReadNumber(ReadOnlySpan<byte> name)
        {
            double value = 0;
            fixed (byte* n = name)
            {
                var started = Stopwatch.GetTimestamp();
                var error = _getProperty(_handle, n, 5, &value);
                return new(started, Stopwatch.GetTimestamp(), error < 0 ? double.NaN : value, error);
            }
        }

        private FlagRead ReadFlag(ReadOnlySpan<byte> name)
        {
            var value = 0;
            fixed (byte* n = name)
            {
                var started = Stopwatch.GetTimestamp();
                var error = _getProperty(_handle, n, 3, &value);
#pragma warning disable CA1508 // The unmanaged call writes to the flag's address.
                return new(started, Stopwatch.GetTimestamp(), value != 0, error);
#pragma warning restore CA1508
            }
        }

        private void PumpEvents()
        {
            while (!Volatile.Read(ref _stop))
            {
                var nativeEvent = _waitEvent(_handle, 0.1);
                if (nativeEvent == 0) continue;
                var id = *(int*)nativeEvent;
                if (id == 18) Interlocked.Increment(ref _audioReconfigs);
                if (id == 21) Interlocked.Increment(ref _playbackRestarts);
            }
        }

        private static void Encode(string value, Span<byte> destination)
        {
            var length = Encoding.UTF8.GetBytes(value, destination);
            destination[length] = 0;
        }
    }

    internal readonly record struct CallResult(long Started, long Ended, int Error)
    {
        internal double Milliseconds => MillisecondsBetween(Started, Ended);
    }

    internal readonly record struct NumericRead(long Started, long Ended, double Value, int Error)
    {
        internal double Milliseconds => MillisecondsBetween(Started, Ended);
        internal bool Available => Error >= 0 && double.IsFinite(Value);
    }

    internal readonly record struct FlagRead(long Started, long Ended, bool Value, int Error)
    {
        internal double Milliseconds => MillisecondsBetween(Started, Ended);
    }

    internal readonly record struct ProbeSample(long Started, long Ended, NumericRead Position, NumericRead Audio,
        NumericRead Speed, NumericRead Avsync, FlagRead Idle, FlagRead Cached, FlagRead Paused);

    internal static double MillisecondsBetween(long started, long ended) => (ended - started) * 1000d / Stopwatch.Frequency;

    internal static void RequireSuccess(int error, string operation)
    {
        if (error < 0) throw new InvalidOperationException("mpv " + operation + " failed: " + error.ToString(CultureInfo.InvariantCulture));
    }
}
