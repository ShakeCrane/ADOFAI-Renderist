using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 只读运行时渲染环境 inventory（Phase 3.7.0）。
    ///
    /// 目的：把“本 session 究竟在什么色彩空间 / 什么 GPU 能力 / 什么 RenderTexture
    /// 形态下渲染”变成可核对的 session metadata 与一行启动日志，而不是靠推测。
    /// custom resolution 的宽高比与色彩空间是后续 supersampling / 降采样工作的直接
    /// 前提，因此必须先把当前基线记录清楚。
    ///
    /// 只读契约：不写任何 Unity 状态；任何单项读取失败都折成 null / "unavailable"，
    /// 绝不让 inventory 采集本身阻断导出。
    /// </summary>
    internal sealed class RenderEnvironmentInventory
    {
        internal const string UnavailableLabel = "unavailable";

        // ---- session 开始时即可读取的环境事实 ----
        /// <summary>QualitySettings.activeColorSpace（Gamma / Linear）。</summary>
        internal string ColorSpace;

        internal string GraphicsDeviceType;
        internal string GraphicsDeviceName;
        internal string GraphicsDeviceVersion;
        internal int? GraphicsShaderLevel;
        /// <summary>SystemInfo.maxTextureSize：OutputGeometryPolicy 的硬件能力上限来源。</summary>
        internal int? MaxTextureSize;
        internal bool? SupportsComputeShaders;
        internal int? SystemMemorySizeMb;

        // ---- capture target 创建后才存在的事实（activation 时填）----
        internal string RenderTextureFormat;
        internal string RenderTextureGraphicsFormat;
        internal int? RenderTextureAntiAliasing;
        internal bool? RenderTextureUseMipMap;
        internal int? RenderTextureWidth;
        internal int? RenderTextureHeight;

        /// <summary>采集不依赖 capture target 的环境事实。无副作用。</summary>
        internal static RenderEnvironmentInventory Capture()
        {
            var inventory = new RenderEnvironmentInventory
            {
                ColorSpace = ReadString(ReadActiveColorSpace),
                GraphicsDeviceType = ReadString(() => SystemInfo.graphicsDeviceType.ToString()),
                GraphicsDeviceName = ReadString(() => SystemInfo.graphicsDeviceName),
                GraphicsDeviceVersion = ReadString(() => SystemInfo.graphicsDeviceVersion),
                GraphicsShaderLevel = ReadInt(() => SystemInfo.graphicsShaderLevel),
                MaxTextureSize = ReadInt(() => SystemInfo.maxTextureSize),
                SupportsComputeShaders = ReadBool(() => SystemInfo.supportsComputeShaders),
                SystemMemorySizeMb = ReadInt(() => SystemInfo.systemMemorySize),
            };
            return inventory;
        }

        /// <summary>
        /// 记录 capture RenderTexture 的实际形态。只读；异常折成 unavailable。
        /// 必须在 RenderTexture 创建成功后调用，不得改变其任何属性。
        /// </summary>
        internal void CaptureRenderTarget(RenderTexture target, int width, int height)
        {
            RenderTextureWidth = width;
            RenderTextureHeight = height;

            if (target == null)
                return;

            RenderTextureFormat = ReadString(() => target.format.ToString());
            RenderTextureGraphicsFormat = ReadString(() => target.graphicsFormat.ToString());
            RenderTextureAntiAliasing = ReadInt(() => target.antiAliasing);
            RenderTextureUseMipMap = ReadBool(() => target.useMipMap);
        }

        /// <summary>
        /// 一行 key=value 摘要，供 session 启动 / capture source activation 日志使用。
        /// 不逐帧调用。
        /// </summary>
        internal string Describe()
        {
            var sb = new StringBuilder(256);
            sb.Append("colorSpace=").Append(Value(ColorSpace));
            sb.Append(" graphicsDeviceType=").Append(Value(GraphicsDeviceType));
            sb.Append(" graphicsDeviceName=").Append(Value(GraphicsDeviceName));
            sb.Append(" graphicsDeviceVersion=").Append(Value(GraphicsDeviceVersion));
            sb.Append(" graphicsShaderLevel=").Append(Value(GraphicsShaderLevel));
            sb.Append(" maxTextureSize=").Append(Value(MaxTextureSize));
            sb.Append(" supportsComputeShaders=").Append(Value(SupportsComputeShaders));
            sb.Append(" systemMemorySizeMb=").Append(Value(SystemMemorySizeMb));
            sb.Append(" renderTextureFormat=").Append(Value(RenderTextureFormat));
            sb.Append(" renderTextureGraphicsFormat=").Append(Value(RenderTextureGraphicsFormat));
            sb.Append(" renderTextureAntiAliasing=").Append(Value(RenderTextureAntiAliasing));
            sb.Append(" renderTextureUseMipMap=").Append(Value(RenderTextureUseMipMap));
            sb.Append(" renderTextureSize=").Append(Value(RenderTextureWidth)).Append("x").Append(Value(RenderTextureHeight));
            return sb.ToString();
        }

        private static string ReadActiveColorSpace()
        {
            return QualitySettings.activeColorSpace.ToString();
        }

        private static string Value(string value)
        {
            return string.IsNullOrEmpty(value) ? UnavailableLabel : value;
        }

        private static string Value(int? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : UnavailableLabel;
        }

        private static string Value(bool? value)
        {
            return value.HasValue ? (value.Value ? "true" : "false") : UnavailableLabel;
        }

        private static string ReadString(Func<string> read)
        {
            try
            {
                return read();
            }
            catch
            {
                return null;
            }
        }

        private static int? ReadInt(Func<int> read)
        {
            try
            {
                return read();
            }
            catch
            {
                return null;
            }
        }

        private static bool? ReadBool(Func<bool> read)
        {
            try
            {
                return read();
            }
            catch
            {
                return null;
            }
        }
    }
}
