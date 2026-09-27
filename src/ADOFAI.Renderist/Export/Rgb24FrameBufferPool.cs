using System;
using System.Collections.Generic;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 一帧**已被 Renderist 独占**的 RGB24 CPU 数据（Unity-free）。
    ///
    /// 身份：generation + absolute frame index + 冻结几何 + 精确 long 长度 + 一次性 token。
    /// 数据本体是有界大小的 managed <c>byte[]</c> 段（会话内复用），不是 Unity 原生内存视图，
    /// 也**不**是单个超大数组。
    ///
    /// 生命周期硬约束（见 Rgb24FrameBufferPool 与交付事务）：
    ///   * 交付被 L2 接受之后，后台写入线程会**直接读取**这些数组；
    ///   * 直到该帧的 Completion 被主线程消费之前，Unity 侧不得修改、不得复用、不得回池；
    ///   * 立即被拒绝（未接受）时不产生长期读取 ownership，lease 可立即释放。
    /// </summary>
    internal sealed class OwnedRgb24Frame
    {
        private readonly byte[][] _segments;

        internal OwnedRgb24Frame(
            long generation, long absoluteFrameIndex, Rgb24FrameLayout layout, byte[][] segments, long tokenId)
        {
            if (layout == null) throw new ArgumentNullException("layout");
            if (segments == null) throw new ArgumentNullException("segments");
            if (segments.Length != layout.SegmentCount)
                throw new ArgumentException("segment count does not match the frozen layout", "segments");

            Generation = generation;
            AbsoluteFrameIndex = absoluteFrameIndex;
            Layout = layout;
            _segments = segments;
            TokenId = tokenId;
        }

        /// <summary>本帧所属的 session generation。</summary>
        internal long Generation { get; private set; }

        /// <summary>absolute output frame index（唯一帧 authority 的编号）。</summary>
        internal long AbsoluteFrameIndex { get; private set; }

        internal Rgb24FrameLayout Layout { get; private set; }

        internal int Width { get { return Layout.Width; } }
        internal int Height { get { return Layout.Height; } }

        /// <summary>精确帧长度（long）。</summary>
        internal long ByteLength { get { return Layout.ByteLength; } }

        /// <summary>一次性 ownership token：交付结果归投时必须与本值一致。</summary>
        internal long TokenId { get; private set; }

        internal int SegmentCount { get { return _segments.Length; } }

        /// <summary>段缓冲（只读视图）。交付适配器把它映射为 L2 的帧分段。</summary>
        internal IReadOnlyList<byte[]> Segments { get { return _segments; } }

        internal byte[] SegmentBuffer(int index)
        {
            if (index < 0 || index >= _segments.Length)
                throw new ArgumentOutOfRangeException("index");
            return _segments[index];
        }

        /// <summary>
        /// 把 managed 源字节写进**交付缓冲**的指定字节偏移。写入可以跨越任意段边界
        /// （段边界与行边界无关），由 <see cref="Rgb24FrameLayout.TryLocate"/> 唯一定位。
        /// </summary>
        internal void WriteDeliveryBytes(long deliveryByteOffset, byte[] source, int sourceOffset, int count)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (count < 0) throw new ArgumentOutOfRangeException("count");
            if (sourceOffset < 0 || (long)sourceOffset + count > source.Length)
                throw new ArgumentOutOfRangeException("sourceOffset");
            if (deliveryByteOffset < 0 || deliveryByteOffset + count > ByteLength)
                throw new ArgumentOutOfRangeException("deliveryByteOffset");
            if (count == 0) return;

            long offset = deliveryByteOffset;
            int sourcePosition = sourceOffset;
            int remaining = count;

            while (remaining > 0)
            {
                int segmentIndex;
                int offsetInSegment;
                if (!Layout.TryLocate(offset, out segmentIndex, out offsetInSegment))
                    throw new InvalidOperationException("rgb24-delivery-offset-not-locatable");

                byte[] segment = _segments[segmentIndex];
                int available = segment.Length - offsetInSegment;
                int take = remaining < available ? remaining : available;

                Buffer.BlockCopy(source, sourcePosition, segment, offsetInSegment, take);

                offset += take;
                sourcePosition += take;
                remaining -= take;
            }
        }

        /// <summary>
        /// 把一块**已暂存**的源数据（staging，按 Unity 源行序排列）中的某一行复制到交付缓冲。
        /// 行重排**只在这里发生**，调用方不得自行计算翻转后的偏移。
        /// </summary>
        /// <param name="sourceRow">Unity 源行号（0 = 底行，即 RenderTexture 坐标系）。</param>
        /// <param name="staging">一块按行紧密排列的源数据。</param>
        /// <param name="stagingOffset">该行在 staging 中的起始偏移。</param>
        internal void CopyStagedSourceRowToDelivery(int sourceRow, byte[] staging, int stagingOffset)
        {
            if (staging == null) throw new ArgumentNullException("staging");

            long rowBytes = Layout.RowByteLength;
            if (rowBytes > int.MaxValue)
                throw new InvalidOperationException("rgb24-row-too-wide");
            int rowLength = (int)rowBytes;

            if (stagingOffset < 0 || (long)stagingOffset + rowLength > staging.Length)
                throw new ArgumentOutOfRangeException("stagingOffset");

            int deliveryRow = Rgb24RowOrderPolicy.MapSourceRowToDeliveryRow(sourceRow, Height);
            long deliveryOffset = Rgb24RowOrderPolicy.DeliveryRowByteOffset(deliveryRow, Width);

            WriteDeliveryBytes(deliveryOffset, staging, stagingOffset, rowLength);
        }
    }

    /// <summary>
    /// 一次独占的帧缓冲租约。它把"本帧正在被使用"这一事实变成显式、可检查、一次性的状态：
    ///   * <see cref="DeliveryPinned"/> = 交付已被接受、后台仍在读取这些数组；
    ///   * 只有 pin 解除之后 <see cref="Rgb24FrameBufferPool.TryRelease"/> 才允许复用缓冲。
    /// </summary>
    internal sealed class Rgb24FrameLease
    {
        private bool _released;
        private bool _consumed;

        internal Rgb24FrameLease(OwnedRgb24Frame frame)
        {
            if (frame == null) throw new ArgumentNullException("frame");
            Frame = frame;
        }

        internal OwnedRgb24Frame Frame { get; private set; }

        /// <summary>true = 交付已接受，Completion 结束前缓冲绝不可复用。</summary>
        internal bool DeliveryPinned { get; set; }

        /// <summary>true = 该帧的交付结果已被消费（成功或失败），且只能被消费一次。</summary>
        internal bool CompletionConsumed
        {
            get { return _consumed; }
        }

        internal bool Released
        {
            get { return _released; }
        }

        /// <summary>标记交付结果为已消费；重复消费返回 false。</summary>
        internal bool TryMarkCompletionConsumed()
        {
            if (_consumed) return false;
            _consumed = true;
            return true;
        }

        internal void MarkReleased()
        {
            _released = true;
        }
    }

    /// <summary>
    /// 会话作用域的 RGB24 帧缓冲池（Unity-free）。
    ///
    /// 契约：
    ///   * 一个 session 只持有一个完整 RGB24 lease（全链路最多一帧在途）；
    ///   * 缓冲按冻结几何分配一次、会话内复用；**不使用 ArrayPool**，不引入任何新依赖；
    ///   * 分配失败如实失败（<c>rgb24-allocation-failed</c>），绝不退化成更低分辨率；
    ///   * Completion 被消费之前，缓冲**不得**复用：<see cref="TryRelease"/> 会拒绝 pinned lease。
    /// </summary>
    internal sealed class Rgb24FrameBufferPool
    {
        private readonly Rgb24FrameLayout _layout;
        private byte[][] _segments;
        private Rgb24FrameLease _outstanding;
        private long _tokenCounter;
        private long _allocatedSegmentCount;
        private long _acquireCount;
        private long _releaseCount;

        internal Rgb24FrameBufferPool(Rgb24FrameLayout layout)
        {
            if (layout == null) throw new ArgumentNullException("layout");
            _layout = layout;
        }

        internal Rgb24FrameLayout Layout { get { return _layout; } }

        internal bool HasOutstandingLease { get { return _outstanding != null; } }

        internal Rgb24FrameLease OutstandingLease { get { return _outstanding; } }

        /// <summary>本 session 真正分配过的段数组总数（诊断 / 测试用）。</summary>
        internal long AllocatedSegmentCount { get { return _allocatedSegmentCount; } }

        /// <summary>成功获得的 lease 次数（诊断 / 测试用）。</summary>
        internal long AcquireCount { get { return _acquireCount; } }

        /// <summary>成功释放的 lease 次数（诊断 / 测试用）。</summary>
        internal long ReleaseCount { get { return _releaseCount; } }

        /// <summary>缓冲是否已经分配（诊断 / 测试用）。</summary>
        internal bool HasAllocatedBuffers { get { return _segments != null; } }

        internal bool TryAcquire(long generation, long frameIndex, out Rgb24FrameLease lease, out string error)
        {
            lease = null;
            error = null;

            if (_outstanding != null)
            {
                // 全链路最多一帧在途：任何重复获取都是 invariant failure，不排队。
                error = "rgb24-lease-busy";
                return false;
            }

            if (!EnsureSegments(out error))
                return false;

            long token = ++_tokenCounter;
            var frame = new OwnedRgb24Frame(generation, frameIndex, _layout, _segments, token);
            lease = new Rgb24FrameLease(frame);
            _outstanding = lease;
            _acquireCount++;
            return true;
        }

        /// <summary>
        /// 归还 lease。只允许"没有等待中的交付读取"时归还；否则返回 false 并保留 ownership
        /// （调用方必须继续观察该帧的 Completion，而不是把它当作已释放）。
        /// </summary>
        internal bool TryRelease(Rgb24FrameLease lease, out string error)
        {
            error = null;

            if (lease == null)
            {
                error = "rgb24-lease-null";
                return false;
            }

            if (!ReferenceEquals(_outstanding, lease))
            {
                error = "rgb24-lease-foreign";
                return false;
            }

            if (lease.DeliveryPinned)
            {
                error = "rgb24-lease-pinned";
                return false;
            }

            _outstanding = null;
            lease.MarkReleased();
            _releaseCount++;
            return true;
        }

        /// <summary>会话结束：丢弃引用，不保留跨 session 状态。</summary>
        internal void Reset()
        {
            _outstanding = null;
            _segments = null;
            _tokenCounter = 0;
        }

        private bool EnsureSegments(out string error)
        {
            error = null;
            if (_segments != null) return true;

            int count = _layout.SegmentCount;
            var segments = new byte[count][];
            try
            {
                for (int i = 0; i < count; i++)
                {
                    segments[i] = new byte[_layout.SegmentLength(i)];
                }
            }
            catch (OutOfMemoryException)
            {
                // 如实失败：不发布半分配结果，也绝不降低分辨率。
                error = "rgb24-allocation-failed";
                return false;
            }

            _segments = segments;
            _allocatedSegmentCount += count;
            return true;
        }
    }
}
