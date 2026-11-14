using Richasy.Danmaku.Enums;
using Windows.Foundation;

namespace Richasy.Danmaku.Legacy
{
    /// <summary>
    /// An immutable snapshot of an ordinary danmaku hit on the canvas.
    /// </summary>
    public readonly struct DanmakuHitInfo
    {
        public string SourceId { get; }

        public string Text { get; }

        public uint StartMs { get; }

        public DanmakuMode Mode { get; }

        /// <summary>
        /// The text rectangle in canvas-relative device-independent pixels.
        /// </summary>
        public Rect Bounds { get; }

        internal DanmakuHitInfo(string sourceId, string text, uint startMs, DanmakuMode mode, Rect bounds)
        {
            SourceId = sourceId;
            Text = text;
            StartMs = startMs;
            Mode = mode;
            Bounds = bounds;
        }
    }
}
