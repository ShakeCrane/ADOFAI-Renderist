using System;
using System.Globalization;
using ADOFAI.Renderist.Ffmpeg;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// Finalizing 的终态判决。它是 **session 侧投影**，不是 L2 状态机的复制：
    /// L2 的 <see cref="FfmpegVideoPipelineState"/> 仍是管线状态的唯一 authority，
    /// 本枚举只回答"本 session 该记成什么结果"。
    /// </summary>
    internal enum Mp4FinalizationVerdict
    {
        /// <summary>已验证并原子发布：本 session 确实产出了可用的最终 MP4。</summary>
        Published = 0,

        /// <summary>失败：没有可用成品（绝不把临时产物呈现为成功）。</summary>
        Failed = 1,

        /// <summary>取消：没有成品（取消后绝不发布）。</summary>
        Cancelled = 2,
    }

    /// <summary>Finalizing 判决 + 可机读证据（全部来自 L2 outcome，不做二次推断）。</summary>
    internal sealed class Mp4FinalizationOutcome
    {
        internal Mp4FinalizationVerdict Verdict { get; set; }
        internal string ErrorCode { get; set; }
        internal string ErrorDetail { get; set; }

        /// <summary>只有 Published 时非 null，且必须与 session 冻结的最终路径完全一致。</summary>
        internal string FinalPath { get; set; }

        internal string TempPath { get; set; }
        internal int? EncoderExitCode { get; set; }
        internal long DeliveredFrameCount { get; set; }

        /// <summary>核验证据（仅在拿到核验结果时非 null）。</summary>
        internal string VerificationStatus { get; set; }
        internal long? DecodedFrameCount { get; set; }
        internal long? PacketCount { get; set; }
        internal string CodecName { get; set; }
        internal string PixelFormat { get; set; }
        internal int? DecodedWidth { get; set; }
        internal int? DecodedHeight { get; set; }
        internal string VerificationErrorCode { get; set; }

        /// <summary>真 = 本 session 拥有的资源仍有未收敛项；绝不伪装成 clean。</summary>
        internal bool ResidualOwnership { get; set; }
        internal string ResidualDetail { get; set; }

        /// <summary>true = 本会话的临时产物在终态后已不存在。</summary>
        internal bool TempFileRemoved { get; set; }

        internal bool IsPublished { get { return Verdict == Mp4FinalizationVerdict.Published; } }
    }

    /// <summary>
    /// L3-C —— MP4 Finalizing 的判决策略（Unity-free，可被独立 net48 harness 直接覆盖）。
    ///
    /// 只做一件事：把 L2 已经得出的终态 outcome 翻译成 session 结果。刻意**不**：
    ///   * 复制 L2 pipeline 状态（不缓存 <c>FfmpegVideoPipelineState</c>）；
    ///   * 放宽"成功"的定义 —— 只有 Completed + 真实核验通过 + 最终路径一致才算产出；
    ///   * 把临时产物或未核验产物当作成品（fail-closed）。
    /// </summary>
    internal static class Mp4FinalizationPolicy
    {
        /// <summary>成功发布的 stopReason（机读）。</summary>
        internal const string PublishedStopReason = "mp4-published-verified";

        /// <summary>session metadata 的 finalizing 状态标签。</summary>
        internal const string PublishedLabel = "completed";
        internal const string FailedLabel = "failed";
        internal const string CancelledLabel = "cancelled";

        internal const string MissingOutcomeError = "mp4-finalize-outcome-missing";
        internal const string UnverifiedOutcomeError = "mp4-finalize-unverified-outcome";
        internal const string FinalPathMissingError = "mp4-finalize-final-path-missing";
        internal const string FinalPathMismatchError = "mp4-finalize-final-path-mismatch";
        internal const string FaultedError = "mp4-finalize-task-faulted";
        internal const string CancelledTaskError = "mp4-finalize-task-canceled";

        /// <summary>
        /// 把 L2 outcome 映射为 session 判决。
        ///
        /// 成功判定刻意同时要求：State == Completed、Verification 非 null 且 IsVerified、
        /// 并且发布路径非空且与 session 冻结路径一致。缺一即 Failed —— **未验证的 MP4
        /// 绝不允许成为成功产物**。
        /// </summary>
        internal static Mp4FinalizationOutcome Map(FfmpegVideoOutcome outcome, string expectedFinalPath)
        {
            var result = new Mp4FinalizationOutcome();
            if (outcome == null)
                return Fail(result, MissingOutcomeError, "L2 FinishAsync returned no outcome");

            result.TempPath = outcome.TempPath;
            result.EncoderExitCode = outcome.EncoderExitCode;
            result.DeliveredFrameCount = outcome.DeliveredFrameCount;
            result.ResidualOwnership = outcome.ResidualOwnership;
            result.ResidualDetail = outcome.ResidualDetail;
            result.TempFileRemoved = outcome.TempFileRemoved;
            FillVerification(result, outcome.Verification);

            if (outcome.State == FfmpegVideoPipelineState.Completed)
            {
                if (string.IsNullOrEmpty(outcome.FinalPath))
                    return Fail(result, FinalPathMissingError, "completed without a published path");

                if (!string.IsNullOrEmpty(expectedFinalPath) &&
                    !string.Equals(outcome.FinalPath, expectedFinalPath, StringComparison.OrdinalIgnoreCase))
                {
                    return Fail(result, FinalPathMismatchError,
                        "expected=" + expectedFinalPath + " actual=" + outcome.FinalPath);
                }

                FfmpegVideoVerificationResult verification = outcome.Verification;
                if (verification == null || !verification.IsVerified)
                {
                    return Fail(result, UnverifiedOutcomeError,
                        verification == null
                            ? "verification-missing"
                            : (verification.ErrorCode + " " + (verification.ErrorDetail ?? string.Empty)));
                }

                result.Verdict = Mp4FinalizationVerdict.Published;
                result.FinalPath = outcome.FinalPath;
                return result;
            }

            if (outcome.State == FfmpegVideoPipelineState.Cancelled)
            {
                result.Verdict = Mp4FinalizationVerdict.Cancelled;
                result.ErrorCode = outcome.Reason ?? outcome.ErrorCode ?? "cancelled";
                result.ErrorDetail = outcome.ErrorDetail;
                return result;
            }

            return Fail(result, outcome.ErrorCode ?? "mp4-finalize-failed", outcome.ErrorDetail);
        }

        /// <summary>完成通知自身 fault / cancel 时的判决（异常绝不等于成功）。</summary>
        internal static Mp4FinalizationOutcome Faulted(string detail)
        {
            return Fail(new Mp4FinalizationOutcome(), FaultedError, detail);
        }

        internal static Mp4FinalizationOutcome CancelledTask(string reason)
        {
            var result = new Mp4FinalizationOutcome
            {
                Verdict = Mp4FinalizationVerdict.Cancelled,
                ErrorCode = CancelledTaskError,
                ErrorDetail = reason,
            };
            return result;
        }

        internal static string Label(Mp4FinalizationVerdict verdict)
        {
            switch (verdict)
            {
                case Mp4FinalizationVerdict.Published: return PublishedLabel;
                case Mp4FinalizationVerdict.Cancelled: return CancelledLabel;
                default: return FailedLabel;
            }
        }

        private static Mp4FinalizationOutcome Fail(Mp4FinalizationOutcome result, string errorCode, string errorDetail)
        {
            result.Verdict = Mp4FinalizationVerdict.Failed;
            result.ErrorCode = errorCode ?? "mp4-finalize-failed";
            result.ErrorDetail = errorDetail;
            result.FinalPath = null;
            return result;
        }

        private static void FillVerification(
            Mp4FinalizationOutcome result, FfmpegVideoVerificationResult verification)
        {
            if (verification == null)
                return;

            result.VerificationStatus = verification.Status.ToString();
            result.VerificationErrorCode = verification.ErrorCode;
            result.DecodedFrameCount = verification.DecodedFrameCount;
            result.PacketCount = verification.ContainerPacketCount;
            result.CodecName = verification.CodecName;
            result.PixelFormat = verification.PixelFormat;
            result.DecodedWidth = verification.DecodedWidth;
            result.DecodedHeight = verification.DecodedHeight;
        }

        /// <summary>把判决写成一行可观察证据（日志与 StateDetail 共用同一表述）。</summary>
        internal static string Describe(Mp4FinalizationOutcome outcome)
        {
            if (outcome == null) return "no-outcome";

            string detail = "verdict=" + Label(outcome.Verdict) +
                " errorCode=" + (outcome.ErrorCode ?? "null") +
                " encoderExitCode=" + (outcome.EncoderExitCode.HasValue
                    ? outcome.EncoderExitCode.Value.ToString(CultureInfo.InvariantCulture) : "unknown") +
                " deliveredFrames=" + outcome.DeliveredFrameCount.ToString(CultureInfo.InvariantCulture) +
                " verification=" + (outcome.VerificationStatus ?? "none") +
                " decodedFrames=" + (outcome.DecodedFrameCount.HasValue
                    ? outcome.DecodedFrameCount.Value.ToString(CultureInfo.InvariantCulture) : "unknown") +
                " finalPath=" + (outcome.FinalPath ?? "null") +
                " residual=" + outcome.ResidualOwnership;

            if (!string.IsNullOrEmpty(outcome.ErrorDetail))
                detail += " detail=" + outcome.ErrorDetail;

            return detail;
        }
    }
}
