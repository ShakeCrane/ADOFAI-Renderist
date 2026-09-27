using System;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 交付帧的行序策略。**行重排逻辑集中在这里**，不得散落到 driver / scheduler / 交付适配器。
    /// </summary>
    internal enum Rgb24RowOrder
    {
        /// <summary>
        /// Unity 的 RenderTexture / Texture2D CPU 行序是**底行优先**（行 0 = 图像底部），
        /// 而 rawvideo / MP4 交付要求**顶行优先**。因此交付缓冲的第 0 段字节对应图像顶行。
        /// </summary>
        BottomUpSourceToTopFirst = 0,
    }

    /// <summary>
    /// 唯一的行序 authority。当前设计预期为 top-row-first 交付（见 <see cref="Delivery"/>）。
    ///
    /// 重要边界：**不能**用"PNG 正常显示"反推 raw 行序 —— Unity 的 PNG 编码器自己会处理
    /// 图像方向，因此 PNG 正确并不证明 <c>GetRawTextureData</c> 的行序。该策略必须由
    /// 目标运行时的定向验收确认（上下区域明显不同的 fixture 对照）。若运行时实测与预期
    /// 不同，只改本策略一行，不改 driver / FFmpeg 滤镜。
    /// </summary>
    internal static class Rgb24RowOrderPolicy
    {
        /// <summary>本 session 冻结使用的交付行序。</summary>
        internal const Rgb24RowOrder Delivery = Rgb24RowOrder.BottomUpSourceToTopFirst;

        /// <summary>metadata / 日志用的稳定标签。</summary>
        internal const string DeliveryLabel = "unity-bottom-up-to-top-first";

        /// <summary>
        /// 把 Unity 源行号（0 = 底行）映射为交付缓冲行号（0 = 顶行）。
        /// </summary>
        internal static int MapSourceRowToDeliveryRow(int sourceRow, int height)
        {
            if (height <= 0)
                throw new ArgumentOutOfRangeException("height");
            if (sourceRow < 0 || sourceRow >= height)
                throw new ArgumentOutOfRangeException("sourceRow");
            return (height - 1) - sourceRow;
        }

        /// <summary>交付缓冲中某一行的起始字节偏移。</summary>
        internal static long DeliveryRowByteOffset(int deliveryRow, int width)
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException("width");
            if (deliveryRow < 0)
                throw new ArgumentOutOfRangeException("deliveryRow");
            return (long)deliveryRow * width * Rgb24FrameLayout.BytesPerPixel;
        }
    }
}
