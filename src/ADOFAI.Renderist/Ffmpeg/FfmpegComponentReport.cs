using System;
using System.Collections.Generic;
using System.Threading;

namespace ADOFAI.Renderist.Ffmpeg
{
    /// <summary>
    /// FFmpeg 组件的就绪状态。
    ///
    /// 重要边界：该状态是**信息性**的。它不是 PNG / Log-only 导出的前置条件 ——
    /// FFmpeg 缺失、无效或能力不足都绝不能阻断既有导出路径。
    /// </summary>
    internal enum FfmpegComponentState
    {
        /// <summary>托管安装目录不可用（未配置或非法）。</summary>
        InstallRootUnavailable = 0,

        /// <summary>未发现任何 FFmpeg。</summary>
        NotFound = 1,

        /// <summary>已发现二进制，但本次未做能力探测。</summary>
        Discovered = 2,

        /// <summary>已发现且能力满足未来 MP4 / H.264 输出要求。</summary>
        Ready = 3,

        /// <summary>已发现，但缺少必要能力。</summary>
        Unsupported = 4,

        /// <summary>显式配置的路径无效（不会静默回退到其它来源）。</summary>
        Invalid = 5,
    }

    internal sealed class FfmpegComponentInspectionRequest
    {
        /// <summary>用户显式指定的 FFmpeg 路径（来自 Settings）。</summary>
        public string ExplicitPath { get; set; }

        /// <summary>托管安装根目录；由 GUI 层根据用户可写目录决定。</summary>
        public string InstallRoot { get; set; }

        /// <summary>是否对发现的二进制做能力探测（探测会启动短进程）。</summary>
        public bool ProbeCapabilities { get; set; }

        /// <summary>PATH 内容覆盖（测试可注入）。null = 读取进程环境变量。</summary>
        public string PathEnvironment { get; set; }

        public int ProbeTimeoutSeconds { get; set; }

        public CancellationToken CancellationToken { get; set; }
    }

    internal sealed class FfmpegComponentReport
    {
        public FfmpegComponentState State { get; set; }

        /// <summary>当前使用的二进制身份（未发现时为 null）。</summary>
        public FfmpegCandidate Candidate { get; set; }

        public FfmpegCandidateSource Source { get; set; }

        public FfmpegCapabilityReport Capability { get; set; }

        /// <summary>托管目录中可识别的安装（含无效项）。</summary>
        public IReadOnlyList<FfmpegManagedInstall> ManagedInstalls { get; set; }

        public string InstallRoot { get; set; }

        /// <summary>托管目录不可用时的原因。</summary>
        public string InstallRootError { get; set; }

        /// <summary>发现阶段的失败原因码（机读）。</summary>
        public string DiscoveryErrorCode { get; set; }

        public string DiscoveryErrorDetail { get; set; }

        /// <summary>当前可安装的资产（用于 GUI 展示来源 / 版本 / 许可证）。</summary>
        public FfmpegAsset InstallableAsset { get; set; }

        public bool Found
        {
            get { return Candidate != null && Candidate.Identity != null; }
        }

        /// <summary>可执行文件的绝对路径（未发现时为 null）。</summary>
        public string ExecutablePath
        {
            get { return Found ? Candidate.Identity.AbsolutePath : null; }
        }
    }

    /// <summary>
    /// 组件就绪状态与错误报告的唯一单点：发现 → （可选）能力探测 → 状态判定。
    ///
    /// 只读：不创建目录、不下载、不安装、不修改 PATH，也不改动任何导出状态。
    /// 不接触 Unity / UMM / Harmony（安装目录由调用方注入）。
    /// </summary>
    internal static class FfmpegComponentInspector
    {
        public static FfmpegComponentReport Inspect(FfmpegComponentInspectionRequest request)
        {
            var report = new FfmpegComponentReport
            {
                ManagedInstalls = new FfmpegManagedInstall[0],
                InstallableAsset = FfmpegAssetManifest.Primary,
            };

            if (request == null)
            {
                report.State = FfmpegComponentState.NotFound;
                report.DiscoveryErrorCode = "request-null";
                return report;
            }

            FfmpegInstallLayout layout;
            string layoutError;
            if (FfmpegInstallLayout.TryCreate(request.InstallRoot, out layout, out layoutError))
            {
                report.InstallRoot = layout.InstallRoot;
                report.ManagedInstalls = FfmpegManagedInstallLocator.ListAll(layout, FfmpegAssetManifest.All);
            }
            else
            {
                report.InstallRootError = layoutError;
            }

            var discoveryRequest = new FfmpegDiscoveryRequest
            {
                ExplicitPath = request.ExplicitPath,
                Layout = layout,
                Assets = FfmpegAssetManifest.All,
                PathEnvironment = request.PathEnvironment,
            };

            FfmpegDiscoveryResult discovery = FfmpegDiscovery.Discover(discoveryRequest);
            report.Source = discovery.Source;
            report.Candidate = discovery.Candidate;
            report.DiscoveryErrorCode = discovery.ErrorCode;
            report.DiscoveryErrorDetail = discovery.ErrorDetail;

            if (!discovery.Found)
            {
                if (layout == null)
                {
                    report.State = FfmpegComponentState.InstallRootUnavailable;
                    return report;
                }

                report.State = string.Equals(discovery.ErrorCode, "explicit-path-invalid", StringComparison.Ordinal)
                    ? FfmpegComponentState.Invalid
                    : FfmpegComponentState.NotFound;
                return report;
            }

            if (request.ProbeCapabilities)
            {
                int timeout = request.ProbeTimeoutSeconds > 0
                    ? request.ProbeTimeoutSeconds
                    : FfmpegCapabilityProbe.DefaultTimeoutSeconds;

                report.Capability = FfmpegCapabilityProbe.Probe(
                    discovery.Candidate.Identity.AbsolutePath, timeout, request.CancellationToken);
            }

            if (report.Capability == null)
            {
                report.State = FfmpegComponentState.Discovered;
                return report;
            }

            if (report.Capability.Status != FfmpegCapabilityStatus.Probed)
            {
                report.State = FfmpegComponentState.Unsupported;
                return report;
            }

            report.State = report.Capability.IsUsableForMp4
                ? FfmpegComponentState.Ready
                : FfmpegComponentState.Unsupported;
            return report;
        }
    }
}
