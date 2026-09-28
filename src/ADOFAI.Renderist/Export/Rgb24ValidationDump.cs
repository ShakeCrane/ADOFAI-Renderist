using System;
using System.Globalization;
using System.IO;
using System.Text;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 【临时验证资产 · L3-A 像素语义验收专用】
    ///
    /// 目的：在**正式** RGB24 帧事务已经形成稳定 managed frame、且该 frame 仍由本 session
    /// 独占持有的时点，把它按**正式 delivery layout** 原样 dump 一次，用来判定：
    ///   * 第一 delivery row 对应画面顶部还是底部（row orientation）；
    ///   * RGB 三通道顺序；
    ///   * band / segment 拼接是否连续（无重复行 / 缺行 / stale scratch 行）。
    ///
    /// 刻意遵守的边界：
    ///   * 复用正式 <see cref="FrameCaptureDriver"/> 的 RGB24 readback 结果，**不**建立第二
    ///     capture path / EOF coroutine / scheduler，也**不**从 RenderTexture 另行截图；
    ///   * 只在调用方明确指定的时点（正式 frame 已形成、交付之前）dump，且每个 session 只 dump 一帧；
    ///   * 只读取、不修改任何 buffer，不改变正式 ownership；不把 buffer 交给任何后台线程；
    ///   * 输出是 **raw RGB24**，不经过任何会自行改变 row orientation 的高层编码流程。
    ///
    /// 这是**一次性验收资产**：验收结束后整个文件与它的两个调用点必须删除。
    /// </summary>
    internal static class Rgb24ValidationDump
    {
        /// <summary>临时开关。清理时连同本文件一起删除。</summary>
        internal const bool Enabled = true;

        internal const string DirectoryName = "rgb24-validation";

        private static string _root;
        private static bool _dumped;

        /// <summary>由启动路径在 session 开始时调用（Unity 主线程）。</summary>
        internal static void Arm(string sessionDirectory)
        {
            _dumped = false;
            _root = string.IsNullOrEmpty(sessionDirectory)
                ? null
                : Path.Combine(sessionDirectory, DirectoryName);
        }

        internal static void Disarm()
        {
            _root = null;
            _dumped = false;
        }

        /// <summary>
        /// 在正式 frame 已形成、尚未交付（本 session 独占持有）时 dump 一次。
        /// 失败只记录，绝不影响正式帧事务。
        /// </summary>
        internal static void TryDumpOnce(OwnedRgb24Frame frame, long generation)
        {
            if (!Enabled || frame == null || _root == null || _dumped) return;
            _dumped = true;

            try
            {
                Directory.CreateDirectory(_root);

                Rgb24FrameLayout layout = frame.Layout;
                string rawPath = Path.Combine(_root, "frame_" +
                    frame.AbsoluteFrameIndex.ToString(CultureInfo.InvariantCulture) + ".raw");

                // 按 delivery 字节顺序原样写出：段顺序拼接即完整帧，不做任何行重排。
                using (var stream = new FileStream(rawPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    for (int i = 0; i < frame.SegmentCount; i++)
                    {
                        byte[] segment = frame.SegmentBuffer(i);
                        stream.Write(segment, 0, segment.Length);
                    }
                }

                WriteMetadata(frame, layout, generation, rawPath);
            }
            catch (Exception ex)
            {
                // 诊断绝不影响正式事务。
                Log.Warn("Rgb24ValidationDump: dump failed: " + ex.Message);            }
        }

        private static void WriteMetadata(
            OwnedRgb24Frame frame, Rgb24FrameLayout layout, long generation, string rawPath)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\n");
            Append(sb, "note", "TEMPORARY L3-A pixel-semantics validation dump (raw RGB24, delivery order)", true);
            Append(sb, "sessionGeneration", generation.ToString(CultureInfo.InvariantCulture), true);
            Append(sb, "absoluteFrameIndex", frame.AbsoluteFrameIndex.ToString(CultureInfo.InvariantCulture), true);
            Append(sb, "tokenId", frame.TokenId.ToString(CultureInfo.InvariantCulture), true);
            Append(sb, "width", layout.Width.ToString(CultureInfo.InvariantCulture), true);
            Append(sb, "height", layout.Height.ToString(CultureInfo.InvariantCulture), true);
            Append(sb, "rowBytes", layout.RowByteLength.ToString(CultureInfo.InvariantCulture), true);
            Append(sb, "byteLength", layout.ByteLength.ToString(CultureInfo.InvariantCulture), true);
            Append(sb, "byteOrder", "RGB (3 bytes per pixel, R first)", true);
            Append(sb, "rowOrderPolicy", Rgb24RowOrderPolicy.DeliveryLabel, true);
            Append(sb, "firstDeliveryRowMeans", "see rowOrderPolicy: unity-bottom-up-to-top-first => row 0 = image top", true);
            Append(sb, "segmentByteSize", layout.SegmentByteSize.ToString(CultureInfo.InvariantCulture), true);
            Append(sb, "segmentCount", layout.SegmentCount.ToString(CultureInfo.InvariantCulture), true);
            Append(sb, "geometryMode", DeterministicFrameScheduler.GeometryModeLabel, true);
            Append(sb, "outputWidth", DeterministicFrameScheduler.OutputWidth.ToString(CultureInfo.InvariantCulture), true);
            Append(sb, "outputHeight", DeterministicFrameScheduler.OutputHeight.ToString(CultureInfo.InvariantCulture), true);
            Append(sb, "renderWidth", DeterministicFrameScheduler.RenderWidth.ToString(CultureInfo.InvariantCulture), true);
            Append(sb, "renderHeight", DeterministicFrameScheduler.RenderHeight.ToString(CultureInfo.InvariantCulture), true);
            Append(sb, "supersamplingScale", DeterministicFrameScheduler.SupersamplingScale.ToString(CultureInfo.InvariantCulture), true);
            Append(sb, "downsampleLevelCount", DeterministicFrameScheduler.DownsampleLevelCount.ToString(CultureInfo.InvariantCulture), true);
            Append(sb, "captureWidth", DeterministicFrameScheduler.CaptureWidth.ToString(CultureInfo.InvariantCulture), true);
            Append(sb, "captureHeight", DeterministicFrameScheduler.CaptureHeight.ToString(CultureInfo.InvariantCulture), true);
            Append(sb, "outputFps", DeterministicFrameScheduler.OutputFps.ToString(CultureInfo.InvariantCulture), true);
            Append(sb, "rawFileName", Path.GetFileName(rawPath), false);
            sb.Append("}\n");

            string metaPath = Path.ChangeExtension(rawPath, ".json");
            File.WriteAllText(metaPath, sb.ToString(), new UTF8Encoding(false));
        }

        private static void Append(StringBuilder sb, string name, string value, bool comma)
        {
            sb.Append("  \"").Append(name).Append("\": \"").Append(value ?? string.Empty).Append('"');
            sb.Append(comma ? ",\n" : "\n");
        }
    }
}
