using System;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 一帧 RGB24 的几何与**分段规划**（Unity-free，可被独立 net48 harness 直接编译）。
    ///
    /// 设计约束：
    ///   * 精确长度用 <c>checked((long)width * height * 3)</c> 计算。任何 int 级别的
    ///     中间乘法都不允许：合法分辨率只受 int 正整数表达能力与真实硬件能力约束，
    ///     不受单个 <c>byte[]</c> 上限约束。
    ///   * 长度溢出、分段数溢出、非法几何一律 fail-closed，**绝不** clamp、也绝不
    ///     偷偷降低产品分辨率。
    ///   * 帧缓冲由**有界大小的 managed byte[] 段**表达。段边界是纯字节边界，因此
    ///     一个段边界可以落在某一行中间（segment boundary 跨行）；本类的
    ///     <see cref="TryLocate"/> 是唯一的字节偏移 → (段, 段内偏移) 映射点。
    ///
    /// 本类只做几何 / 长度 / 分段规划，不持有任何运行时资源，也不拥有时间推进。
    /// </summary>
    internal sealed class Rgb24FrameLayout
    {
        /// <summary>
        /// 默认段大小 1 MiB。它只是**内存表达**的分块粒度，不是分辨率或帧长度上限：
        /// 更大的帧只是被分成更多段。选择 1 MiB 是为了让每段的分配/使用保持有界。
        /// </summary>
        internal const int DefaultSegmentByteSize = 1 << 20;

        /// <summary>每个像素的字节数（RGB24 = 3）。</summary>
        internal const int BytesPerPixel = 3;

        private readonly long[] _segmentOffsets;
        private readonly int[] _segmentLengths;

        private Rgb24FrameLayout(
            int width, int height, long byteLength, int segmentByteSize,
            long[] segmentOffsets, int[] segmentLengths)
        {
            Width = width;
            Height = height;
            ByteLength = byteLength;
            SegmentByteSize = segmentByteSize;
            _segmentOffsets = segmentOffsets;
            _segmentLengths = segmentLengths;
        }

        internal int Width { get; private set; }
        internal int Height { get; private set; }

        /// <summary>精确帧长度（long）：<c>width * height * 3</c>。</summary>
        internal long ByteLength { get; private set; }

        /// <summary>规划时使用的段大小（字节）；最后一段通常小于它。</summary>
        internal int SegmentByteSize { get; private set; }

        internal int SegmentCount { get { return _segmentLengths.Length; } }

        /// <summary>一行的字节数（long：width 接近 int 上限时 int 会溢出）。</summary>
        internal long RowByteLength { get { return (long)Width * BytesPerPixel; } }

        internal static bool TryCreate(int width, int height, out Rgb24FrameLayout layout, out string error)
        {
            return TryCreate(width, height, DefaultSegmentByteSize, out layout, out error);
        }

        internal static bool TryCreate(
            int width, int height, int segmentByteSize, out Rgb24FrameLayout layout, out string error)
        {
            layout = null;
            error = null;

            if (width <= 0)
            {
                error = "rgb24-width-invalid";
                return false;
            }

            if (height <= 0)
            {
                error = "rgb24-height-invalid";
                return false;
            }

            if (segmentByteSize <= 0)
            {
                error = "rgb24-segment-size-invalid";
                return false;
            }

            // checked 是刻意的：这是"长度以 long 精确表达"的唯一形式。
            long byteLength;
            try
            {
                byteLength = checked((long)width * height * BytesPerPixel);
            }
            catch (OverflowException)
            {
                error = "rgb24-length-overflow";
                return false;
            }

            if (byteLength <= 0)
            {
                error = "rgb24-length-invalid";
                return false;
            }

            // 段数用除法+余数算，避免 (byteLength + segmentByteSize - 1) 在接近 long 上限时溢出。
            long segmentCount = byteLength / segmentByteSize;
            if (byteLength % segmentByteSize != 0) segmentCount++;
            if (segmentCount <= 0 || segmentCount > int.MaxValue)
            {
                error = "rgb24-segment-count-overflow";
                return false;
            }

            int count = (int)segmentCount;
            var offsets = new long[count];
            var lengths = new int[count];
            long offset = 0;
            for (int i = 0; i < count; i++)
            {
                long remaining = byteLength - offset;
                long take = remaining < segmentByteSize ? remaining : segmentByteSize;
                offsets[i] = offset;
                lengths[i] = (int)take;
                offset += take;
            }

            layout = new Rgb24FrameLayout(width, height, byteLength, segmentByteSize, offsets, lengths);
            return true;
        }

        internal long SegmentOffset(int index)
        {
            if (index < 0 || index >= _segmentOffsets.Length)
                throw new ArgumentOutOfRangeException("index");
            return _segmentOffsets[index];
        }

        internal int SegmentLength(int index)
        {
            if (index < 0 || index >= _segmentLengths.Length)
                throw new ArgumentOutOfRangeException("index");
            return _segmentLengths[index];
        }

        /// <summary>
        /// 唯一的字节偏移 → (段号, 段内偏移) 映射。段的边界与行边界无关，
        /// 因此跨行的字节范围会自然跨越多个段。
        /// </summary>
        internal bool TryLocate(long byteOffset, out int segmentIndex, out int offsetInSegment)
        {
            segmentIndex = -1;
            offsetInSegment = -1;
            if (byteOffset < 0 || byteOffset >= ByteLength)
                return false;

            // 段大小固定，绝大多数情况是 O(1) 除法；段数为 1 时也自然成立。
            long index = byteOffset / SegmentByteSize;
            if (index >= _segmentOffsets.Length)
                index = _segmentOffsets.Length - 1;   // 末段可能小于 SegmentByteSize
            segmentIndex = (int)index;
            offsetInSegment = (int)(byteOffset - _segmentOffsets[segmentIndex]);
            return offsetInSegment >= 0 && offsetInSegment < _segmentLengths[segmentIndex];
        }
    }
}
