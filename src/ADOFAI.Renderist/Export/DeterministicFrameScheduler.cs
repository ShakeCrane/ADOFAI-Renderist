using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// MasterTimeline Deterministic Editor Export 的逐帧调度器。
    ///
    /// 数据流：
    ///   outputFrameIndex
    ///     → PrepareFrame（scrConductor.Update Prefix：设置本帧视觉时间）
    ///     → ADOFAI 原生 Update
    ///     → Conductor.Update Postfix（RenderistAutoPlay due-floor / Hit(true)）
    ///     → WaitForEndOfFrame
    ///     → FrameCaptureDriver 帧末事务
    ///        （PNG：ReadPixels → EncodeToPNG → File.WriteAllBytes；log-only：仅 source / generation /
    ///          pending-index 校验后返回成功帧末结果）
    ///     → CommitFrame（成功后 outputFrameIndex++）
    ///
    /// 关键不变量：Frame N 未成功捕获，就不提交 N，也不开始 N+1。
    ///
    /// 时间 authority 必须分两层理解：
    ///   * MasterTimeline 是 **export / chart timeline** 的唯一 authority：
    ///     outputTime = frameIndex / OutputFps，chartTime = canonicalStart + outputTime × pitch。
    ///   * Time.captureFramerate 提供 deterministic Unity engine timestep
    ///     （captureDeltaTime = deltaTime = 1 / OutputFps），使 1 个 output frame
    ///     对应 1 个连续 Unity frame；它不是 chart timeline authority。
    ///   * wall clock（Time.realtimeSinceStartupAsDouble）仅用于 watchdog 失败保护，
    ///     绝不推进 timeline。
    ///   * audible audio 不推进 chart timeline（Renderist 不拥有 audio timeline）。
    /// </summary>
    internal static class DeterministicFrameScheduler
    {
        public enum SchedulerStatus
        {
            Idle,
            Preparing,
            InitializationHold,
            Capturing,
            Completed,
            Cancelled,
            Failed,
        }

        private const int DefaultOutputFps = OutputFpsPolicy.Default;
        /// <summary>End Tail 默认值；玩家可改为 Frames / Seconds / Beats。</summary>
        public const long DefaultTailFrameCount = 12;
        private const string FramePrefix = "frame_";
        private const int ZeroPadWidth = 6;
        private const double AnchorInvalidThresholdSeconds = 3600.0;
        private const double GameplayAnchorConsistencyToleranceSeconds = 0.05;
        private const double PlaybackReadyTimeoutSeconds = 30.0;
        private const double CaptureTimeoutSeconds = 30.0;
        private const double FrameProgressWatchdogSeconds = 30.0;
        private const double MinPitch = 0.0001;

        // PreEntryClock 的 deterministic 边界：最后一个 pre-entry commit 之后，
        // 下一个 grid point 已到达 canonicalStart。从该 frame 起不再输出 pre-entry PNG
        // （否则会把 chart time 推过 canonicalStart，handoff 只能靠倒回 canonicalStart 造成
        // rewind，或让 gameplay 迟到造成整段 chart 偏移），而是把 forced time 钉在该 grid
        // point 上等 native Countdown 边界触发。若 hidden lifecycle 无进展，由既有
        // capture transaction / no-progress watchdog fail-closed（不设固定帧数窗口）。

        // boundary path 的数值容差上限：一帧（step）的千分之一。
        // 容差随 step 一起缩小，因此不会在低 FPS（step 大）或高 FPS / 小 pitch（step 极小）
        // 下变成隐式时间窗口，也不构成任何 Output FPS / pitch 的隐式合法区间。
        private const double PreEntryBoundaryToleranceStepFraction = 1e-3;

        private static SchedulerStatus _status = SchedulerStatus.Idle;
        private static bool _running;
        private static bool _restored;
        private static bool _cleanupInProgress;
        private static string _terminalStopReason;

        private static int _outputFps = DefaultOutputFps;
        // safety policy 由 SafetyFrameLimitPolicy 在 Start 时一次性解析。
        // _safetyFrameLimit == 0 表示未配置（disabled / unbounded）：不存在默认总帧数或
        // 总时长上限，导出只由 canonical completion、manual cancel、异常 fail-closed 与
        // 无进展 watchdog 终止。
        private static long _safetyFrameLimit;
        private static SafetyLimitKind _safetyPolicyKind = SafetyLimitKind.Disabled;
        private static int _configuredSafetyFrameLimit;
        private static long _resolvedTailFrameCount = DefaultTailFrameCount;
        private static EndTailInput _endTailInput =
            new EndTailInput(EndTailPolicy.DefaultValue, EndTailPolicy.DefaultUnit);
        private static double _resolvedTailSeconds;
        private static double? _resolvedTailBeats;
        private static double? _completionBpm;
        // canonical output frame 编号 / 计数全部是 long：合法 Output FPS 是任意正 int，
        // 且默认 safety unbounded，因此帧号不能依赖 int。
        private static long _outputFrameIndex;             // 已提交的逻辑输出帧数量，同时也是“下一帧”编号
        // 逻辑帧事务（frame transaction）请求数：PNG 与 log-only 都计入。
        // PNG 模式下它与 _captureRequestCount 同步递增；log-only 模式下 _captureRequestCount 恒为 0。
        private static long _frameTransactionRequestCount;
        // PNG 图像请求 / 成功写盘计数。log-only 模式下两者恒为 0（不请求、不写盘）。
        private static long _captureRequestCount;
        private static long _capturedFrameCount;
        // 尾帧计数拆成逻辑与 PNG 两个维度：completion / End Tail / safety 判定只使用逻辑提交数。
        private static long _tailFramesCommitted;
        private static long _tailFramesCaptured;

        /// <summary>
        /// 本 session 冻结的输出模式：true = PNG 序列，false = log-only（image output disabled）。
        /// 只在 TryStart 赋值，session 期间不读 Settings，因此运行中修改 GUI 不影响当前 session。
        /// </summary>
        private static bool _imageOutputEnabled = true;

        private static double _outputTime;
        private static double _forcedSongPosition;
        private static double _canonicalStartTime;
        private static double _floor0EntryTime;
        private static double _pitch = 1.0;
        private static bool _pitchUnavailable;

        // Render Source：本 session 实际采用的 capture source 与冻结尺寸。
        // 只作为 metadata 记录，cleanup 后保留供 session finalize 读取。
        private static string _captureSource;
        private static int _captureWidth;
        private static int _captureHeight;

        // ---- Phase 3.7.0: 输出几何（session 开始时一次性冻结）----
        //
        // 唯一 authority：OutputGeometryPolicy.TryResolve 在 TryStart 里解析一次，之后
        // FrameCaptureDriver 只消费这里的冻结值，**不再**读取 Screen。这样 session 中途
        // 改变窗口尺寸不会造成 capture RT 尺寸与 Camera aspect ownership 不一致。
        private static GeometryMode _geometryMode = GeometryMode.LegacyWindow;
        private static bool _geometryCustomResolutionEnabled;
        private static int _geometryConfiguredWidth;
        private static int _geometryConfiguredHeight;
        private static int _geometryConfiguredSupersamplingScale =
            OutputGeometryPolicy.DefaultSupersamplingScale;
        private static int _outputWidth;
        private static int _outputHeight;
        // 第二闭环：render = output × scale；scale=1 时与 output 相同。
        private static int _supersamplingScale = OutputGeometryPolicy.DefaultSupersamplingScale;
        private static int _renderWidth;
        private static int _renderHeight;
        /// <summary>
        /// 规划器给出的**计划**降采样级数（仅用于 session 启动时的 frozen 日志行）。
        /// 它不代表链真的被创建了：log-only 与 scale=1 的计划值也可能 > 0 / = 0。
        /// </summary>
        private static int _downsamplePlannedLevelCount;
        /// <summary>
        /// **实际创建**的降采样级数：只在 Source 成功激活之后由 FrameCaptureDriver 快照。
        /// 未激活（含 activation 失败）时为 0，因此 metadata 绝不会在链不存在时声称已创建。
        /// 该值在 cleanup 释放链之后仍然保留，直到下一次 ResetRunStateForStart。
        /// </summary>
        private static int _downsampleActualLevelCount;

        /// <summary>session 开始时采集的只读运行时渲染环境 inventory（metadata 用）。</summary>
        private static RenderEnvironmentInventory _environmentInventory;

        // Unity frame 域（Time.frameCount）与逐帧命中数保持 int：
        // 前者是 Unity API 的帧计数器，后者上界是当前谱面 floor 数。
        private static int _awaitFrameCount;
        private static int _activationUnityFrame = -1;
        private static int _lastPrepareUnityFrame = -1;
        private static bool _clockActive;
        private static bool _pendingCapture;
        private static long _pendingCaptureIndex = -1;
        private static string _prefixSongPosition;
        private static string _lastFrame0Stage;
        private static int _hitsThisFrame;
        private static string _pendingStopEvent;
        private static string _pendingStopReason;

        // Canonical completion is observed from the native OnLandOnPortal path,
        // then corroborated by the committed controller state Won. A floor hit
        // alone never sets these flags.
        private static bool _canonicalCompletionCallbackSeen;
        private static bool _canonicalCompletionStateSeen;
        private static long _canonicalCompletionFrameIndex = -1;
        private static string _canonicalCompletionSignal;

        // wall-clock watchdog deadlines（仅失败保护，不推进 timeline）
        private static double _initializationDeadlineRealtime;
        private static double _captureDeadlineRealtime;
        private static double _progressDeadlineRealtime;

        // 保存需要恢复的状态
        private static int _savedCaptureFramerate;
        private static int _savedTargetFrameRate;
        private static int _savedVSyncCount;
        private static bool? _savedRdcAuto;
        private static readonly List<int> _savedSelectedFloorSeqs = new List<int>();
        private static bool _ownsPlayback;
        private static MasterTimeline _timeline;
        private static PlaybackLifecycleHandoff _handoff;

        // PreEntry state. outputFrameIndex 始终是全局输出编号；
        // gameplayStartOutputFrameIndex 只定义 GameplayCapture 的 local frame 0。
        // PreEntryClock 在 Countdown 首次可安全识别时以原生 clock 的 schedule-origin
        // 锁定；source 尚未 ready 时冻结在 anchor，实际 PNG commit 才推进 output clock。
        private static bool _preEntryCapturing;
        private static bool _preEntryClockLatched;
        private static bool _preEntrySourceActivationAttempted;
        private static bool _preEntryCandidateRejectLogged;
        private static int _preEntryLastCaptureUnityFrame = -1;
        private static long _preEntryCapturedFrameCount;
        private static long _preEntryFrameIndex;
        private static long _preEntryGameplayStartOutputFrameIndex = -1;
        private static double _preEntryAnchor;
        private static double _preEntryStep;
        private static long _preEntryBoundaryOutputFrameIndex = -1L;
        private static double _preEntryBoundaryForcedTime;
        private static double _preEntryBoundaryPreviousTime;
        private static double _preEntryBoundaryCanonicalStart;
        private static int _preEntryInjectedBeatNumber;
        private static bool _preEntryLifecycleInjectionArmed;

        // PreEntry lifecycle bridge：scoped beat override 的真实 ownership。
        // Prefix 建立 ownership（保存 exact conductor instance / exact FieldInfo / 原始 beat），
        // Postfix、Finalizer 与 cleanup 共用 TryRestorePreEntryBeatOverride 恢复；
        // **只有恢复成功后才清空**上述状态，失败则保留 ownership 并由 residual gate 阻止下一 session。
        private static bool _preEntryBeatOverrideOwned;
        private static object _preEntryBeatOverrideConductor;
        private static FieldInfo _preEntryBeatOverrideField;
        private static int _preEntryBeatOverrideOriginalBeat;

        // PreEntry lifecycle bridge：hidden lifecycle-only boundary phase 的
        // Time.timeScale ownership（唯一表达“是否已接管/原值/是否已写 0/是否已恢复”）。
        // 只在 frame B-1 的 PNG 已成功写盘并完成 commit 后、同一 Unity frame 结束前建立，
        // 确保下一 hidden Unity frame 从 frame begin 起 timeScale 已为 0。PlayerControl 被
        // 观测到后以 canonicalStart - previousBoundaryTime 对应的 partial scale 驱动 G，
        // 仅在 G 的 PNG 成功 commit 后恢复原值。不修改 Time.fixedDeltaTime。
        private static bool _preEntryLifecycleTimeScaleOwned;
        private static float _preEntryLifecycleSavedTimeScale;
        private static bool _preEntryLifecycleTimeScaleFrozen;
        private static bool _preEntryLifecycleTimeScaleRestored;
        private static bool _preEntryLifecyclePartialScaleArmed;
        private static double _preEntryLifecyclePartialStep;
        private static double _preEntryLifecyclePartialFraction;
        private static float _preEntryLifecycleRequestedPartialScale;

        // run-owned hook tracking：实际成功注册的 original MethodInfo（精确撤销，不依赖单一 bool）
        private static MethodInfo _patchedConductorUpdate;
        private static MethodInfo _patchedAsyncInputAdjustAngle;
        private static MethodInfo _patchedControllerOnLandOnPortal;
        private static bool _conductorPrefixPatched;
        private static bool _conductorPostfixPatched;
        private static bool _asyncInputAdjustAnglePatched;
        private static bool _controllerOnLandOnPortalPatched;

        // native editor playback teardown observer（scnEditor.SwitchToEditMode Postfix）
        private static MethodInfo _patchedEditorSwitchToEditMode;
        private static bool _editorSwitchToEditModePatched;

        // PreEntry lifecycle boundary 的 run-owned hook tracking。
        // 安装在 Start（playback 之前），撤销走既有 RestoreAll 精确 Unpatch 路径。
        private static MethodInfo _patchedControllerCountdownUpdate;
        private static bool _countdownUpdatePrefixPatched;
        private static bool _countdownUpdatePostfixPatched;
        private static bool _countdownUpdateFinalizerPatched;

        /// <summary>
        /// Input Guard run-owned hook：记录实际成功 Patch 的 target MethodInfo 与 Prefix 名称，
        /// 以便精确 Unpatch 并在残留学时被 HasResidualOwnership 识别。
        /// 所有 guard 必须 all-or-nothing：任一安装失败即全部撤销并拒绝启动。
        /// </summary>
        private sealed class InputGuardHook
        {
            public MethodInfo Target;
            public string PrefixName;
            public bool Patched;
        }

        private static readonly List<InputGuardHook> _inputGuardHooks = new List<InputGuardHook>();

        // FrameCaptureDriver generation 隔离
        private static long _captureGeneration;

        // saved Unity timing state is itself ownership until restoration succeeds
        private static bool _ownsUnityTiming;

        public static bool IsRunning => _running;
        public static SchedulerStatus Status => _status;
        public static string StopReason => _terminalStopReason;
        public static long OutputFrameIndex => _outputFrameIndex;
        public static int OutputFps => _outputFps;
        /// <summary>
        /// 本 session 实际生效的 output-frame safety 上限；<c>null</c> = 未配置
        /// （无总帧数 / 总时长上限），不是 0 帧或某个 sentinel。
        /// </summary>
        public static long? SafetyFrameLimit =>
            _safetyFrameLimit > 0 ? _safetyFrameLimit : (long?)null;
        /// <summary>
        /// 与 <see cref="SafetyFrameLimit"/> 对应的逻辑 output duration（秒）；
        /// unbounded 时无真实含义，返回 <c>null</c>。
        /// </summary>
        public static double? SafetyDurationSeconds =>
            _safetyFrameLimit > 0 && _outputFps > 0 ? _safetyFrameLimit / (double)_outputFps : (double?)null;
        /// <summary>实际采用的 safety policy 标签（metadata 记录用）：unbounded | explicit-frames。</summary>
        public static string SafetyPolicy => SafetyFrameLimitPolicy.KindLabel(_safetyPolicyKind);
        /// <summary>Settings 里的原始配置值（诊断用，未解析）。</summary>
        public static int ConfiguredSafetyFrameLimit => _configuredSafetyFrameLimit;
        public static long ResolvedTailFrameCount => _resolvedTailFrameCount;
        /// <summary>成功提交的**逻辑**尾帧数（PNG 与 log-only 都计入）。completion / End Tail 判定使用它。</summary>
        public static long TailFramesCommitted => _tailFramesCommitted;
        /// <summary>成功写盘的 PNG 尾帧数。log-only 模式下恒为 0。</summary>
        public static long TailFramesCaptured => _tailFramesCaptured;
        public static double EndTailInputValue => _endTailInput.Value;
        public static EndTailUnit EndTailInputUnit => _endTailInput.Unit;
        public static double ResolvedTailSeconds => _resolvedTailSeconds;
        public static double? ResolvedTailBeats => _resolvedTailBeats;
        public static double? CompletionBpm => _completionBpm;
        /// <summary>本 session 冻结的输出模式（true = PNG 序列，false = log-only）。</summary>
        public static bool ImageOutputEnabled => _imageOutputEnabled;
        /// <summary>本 session 请求过的逻辑帧事务数（PNG 与 log-only 都计入）。</summary>
        public static long FrameTransactionRequestCount => _frameTransactionRequestCount;
        /// <summary>
        /// 已提交的**逻辑**输出帧数。权威来源就是 <see cref="_outputFrameIndex"/>，
        /// 不额外维护同步计数器；log-only 与 PNG 都按同一逻辑 commit 推进。
        /// </summary>
        public static long LogicalFrameCount => _outputFrameIndex;
        /// <summary>成功写盘 PNG 的帧数（log-only 模式下恒为 0）。</summary>
        public static long WrittenPngFrameCount => _capturedFrameCount;
        /// <summary>PNG 图像请求数（log-only 模式下恒为 0）。</summary>
        public static long CaptureRequestCount => _captureRequestCount;
        /// <summary>成功写盘 PNG 的帧数；与 <see cref="WrittenPngFrameCount"/> 是同一个计数。</summary>
        public static long CapturedFrameCount => _capturedFrameCount;
        public static double OutputTime => _outputTime;
        public static double ForcedSongPosition => _forcedSongPosition;
        public static double CanonicalStartTime => _canonicalStartTime;
        public static double Pitch => _pitch;
        public static bool PitchUnavailable => _pitchUnavailable;
        public static string CaptureSource => _captureSource;
        public static int CaptureWidth => _captureWidth;
        public static int CaptureHeight => _captureHeight;
        /// <summary>本 session 冻结的输出几何来源方式标签：legacy-window | custom-resolution。</summary>
        public static string GeometryModeLabel => OutputGeometryPolicy.KindLabel(_geometryMode);
        /// <summary>本 session 开始时冻结的“是否使用自定义分辨率”。</summary>
        public static bool GeometryCustomResolutionEnabled => _geometryCustomResolutionEnabled;
        /// <summary>Settings 里 persisted 的自定义宽度（诊断用，未解析）。</summary>
        public static int GeometryConfiguredWidth => _geometryConfiguredWidth;
        /// <summary>Settings 里 persisted 的自定义高度（诊断用，未解析）。</summary>
        public static int GeometryConfiguredHeight => _geometryConfiguredHeight;
        /// <summary>Settings 里 persisted 的超采样倍率（诊断用，未解析）。</summary>
        public static int GeometryConfiguredSupersamplingScale => _geometryConfiguredSupersamplingScale;
        /// <summary>本 session 冻结的最终输出宽度（= PNG / ReadPixels 尺寸）。</summary>
        public static int OutputWidth => _outputWidth;
        /// <summary>本 session 冻结的最终输出高度（= PNG / ReadPixels 尺寸）。</summary>
        public static int OutputHeight => _outputHeight;
        /// <summary>本 session 冻结的统一输出 aspect（三台原生 Camera 与 capture RT 共用）。</summary>
        public static double OutputAspect =>
            _outputHeight > 0 ? (double)_outputWidth / _outputHeight : 0.0;
        /// <summary>本 session 冻结的超采样倍率；1 = 关闭。</summary>
        public static int SupersamplingScale => _supersamplingScale;
        /// <summary>本 session 冻结的 Source RenderTexture 宽度（= OutputWidth × SupersamplingScale）。</summary>
        public static int RenderWidth => _renderWidth;
        /// <summary>本 session 冻结的 Source RenderTexture 高度（= OutputHeight × SupersamplingScale）。</summary>
        public static int RenderHeight => _renderHeight;
        /// <summary>
        /// 本 session **实际创建**的降采样级数（不含 source）；metadata 的唯一来源。
        /// 未成功激活 Source（含 activation 失败、scale=1、log-only）时为 0。
        /// </summary>
        public static int DownsampleLevelCount => _downsampleActualLevelCount;
        /// <summary>session 开始时采集的运行时渲染环境 inventory；未启动时为 null。</summary>
        public static RenderEnvironmentInventory EnvironmentInventory => _environmentInventory;
        public static bool CanonicalCompletionCallbackSeen => _canonicalCompletionCallbackSeen;
        public static bool CanonicalCompletionStateSeen => _canonicalCompletionStateSeen;
        public static long CanonicalCompletionFrameIndex => _canonicalCompletionFrameIndex;
        public static string CompletionSignal => _canonicalCompletionSignal;
        public static string TerminationKind => ClassifyTermination(_terminalStopReason, _status);

        private static bool Terminal => _status == SchedulerStatus.Completed ||
                                       _status == SchedulerStatus.Cancelled ||
                                       _status == SchedulerStatus.Failed;

        // ================================================================
        // 启动 / 停止
        // ================================================================

        /// <summary>
        /// 尝试启动调度器。成功返回 null；失败返回机器可读拒绝原因。
        /// 启动成功后进入 Preparing → InitializationHold，由 Tick 继续推进。
        ///
        /// configuredSafetyFrameLimit 是 Settings 的原始配置值（未解析）；
        /// safety policy 在这里结合 outputFps 一次性解析，scheduler 内部只消费
        /// 解析后的 long frame 上限与逻辑时长。
        ///
        /// imageOutputEnabled 是 session 开始时冻结的输出模式（PNG / log-only）。
        /// 它在这里一次性冻结，session 期间不再读取 Settings。
        ///
        /// geometryInput 是 session 开始时冻结的输出几何配置（自定义分辨率或沿用窗口）。
        /// 解析结果同时决定 capture RenderTexture 尺寸与三台原生 Camera 的统一 aspect；
        /// session 期间不再读取 Settings，也不再读取 Screen（legacy 模式在解析时读一次）。
        /// </summary>
        public static string TryStart(string outputDirectory, int outputFps, int configuredSafetyFrameLimit,
            EndTailInput endTailInput, GeometryInput geometryInput, bool imageOutputEnabled,
            bool allowExpectedTerminalControllerFail = false)
        {
            if (_running || !Terminal && _status != SchedulerStatus.Idle)
            {
                return "scheduler-already-running";
            }

            if (!EnsurePreviousRunCleanedUp(out string residualError))
            {
                return "cleanup-failed:" + residualError;
            }

            try
            {
                EditorGameReflection.EnsureTypes();

                string reject = ValidateStartConditions(allowExpectedTerminalControllerFail);
                if (reject != null)
                {
                    return reject;
                }

                if (string.IsNullOrEmpty(outputDirectory))
                {
                    return "output-directory-empty";
                }
                if (!EndTailPolicy.TryValidateInput(endTailInput, out string endTailInputError))
                {
                    return "end-tail-invalid:" + endTailInputError;
                }
                // outputFps 范围是 fail-closed gate，不做静默替换：preflight 与 GUI
                // 使用同一条 OutputFpsPolicy 规则。
                if (!OutputFpsPolicy.TryValidate(outputFps, out string outputFpsError))
                {
                    return "output-fps-invalid:" + outputFpsError;
                }

                // 输出几何：在 session 开始时一次性解析并冻结。自定义分辨率关闭时等价于
                // 冻结当时的 Screen 尺寸（保持 0.3.6.4 行为）；非法 persisted 值 fail-closed，
                // **不**自动修复、也不替换为默认值。
                if (!OutputGeometryPolicy.TryResolve(
                        geometryInput, out GeometryResolution geometry, out string geometryError))
                {
                    return "output-geometry-invalid:" + geometryError;
                }


                _status = SchedulerStatus.Preparing;
                _running = true;
                _restored = false;
                _terminalStopReason = null;

                ResetRunStateForStart();

                _outputFps = outputFps;
                _imageOutputEnabled = imageOutputEnabled;
                SafetyLimitResolution safety = SafetyFrameLimitPolicy.Resolve(configuredSafetyFrameLimit);
                _safetyFrameLimit = safety.FrameLimit;
                _safetyPolicyKind = safety.Kind;
                _configuredSafetyFrameLimit = safety.ConfiguredFrameLimit;
                _endTailInput = endTailInput;
                _geometryMode = geometry.Mode;
                _geometryCustomResolutionEnabled = geometryInput.CustomResolutionEnabled;
                _geometryConfiguredWidth = geometryInput.Width;
                _geometryConfiguredHeight = geometryInput.Height;
                _geometryConfiguredSupersamplingScale = geometryInput.SupersamplingScale;
                _outputWidth = geometry.Width;
                _outputHeight = geometry.Height;
                _supersamplingScale = geometry.Scale;
                _renderWidth = geometry.RenderWidth;
                _renderHeight = geometry.RenderHeight;
                _downsamplePlannedLevelCount = geometry.DownsampleLevelCount;
                // 环境 inventory 只在 session 开始时采集一次；capture target 形态在
                // source activation 成功后补填（见 FrameCaptureDriver.CaptureRenderTargetInventory）。
                _environmentInventory = RenderEnvironmentInventory.Capture();
                _initializationDeadlineRealtime = Time.realtimeSinceStartupAsDouble + PlaybackReadyTimeoutSeconds;

                SaveState();

                // ---- Unity 时间设置（复用 PoC 已验证 baseline）----
                Time.captureFramerate = _outputFps;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = OutputFpsPolicy.ResolveUnityTargetFrameRate(_outputFps);

                // pitch 在 editor.Play() 内部最终确定，因此 Play 返回后读取。

                // ---- 1) SelectFloor(floor0) ----
                if (!TrySelectFloor0(out string selectError))
                {
                    return FailStart("select-floor0-failed:" + selectError);
                }

                // ---- 2) 读取 runtime floors[0].entryTime chart 基准 ----
                if (!EditorGameReflection.TryReadFloor0EntryTime(out double canonical, out string entryError))
                {
                    return FailStart("floor0-entry-time-unavailable:" + entryError);
                }
                _floor0EntryTime = canonical;
                _canonicalStartTime = canonical;
                _forcedSongPosition = canonical;

                Log.Info(UiText.Format(UiText.LogSchedulerCanonicalAnchorFormat,
                    _canonicalStartTime.ToString("0.######", CultureInfo.InvariantCulture)));

                // ---- 3) 安装 Conductor / AsyncInput / Forced Clock hook ----
                if (!RegisterConductorUpdateHook())
                {
                    return FailStart("conductor-update-hook-failed");
                }
                if (!RegisterAsyncInputAngleHook())
                {
                    return FailStart("async-input-angle-hook-failed");
                }
                if (!RegisterCanonicalCompletionHook())
                {
                    return FailStart("canonical-completion-hook-failed");
                }
        // PreEntry lifecycle boundary：必须在 playback 之前安装，
        // Countdown_Update 首次执行时 hook 就位。
                if (!RegisterPreEntryLifecycleBridgeHook())
                {
                    return FailStart("preentry-lifecycle-bridge-hooks-failed");
                }
                if (!EditorVisualClock.RegisterForcedClockHooks())
                {
                    return FailStart("forced-clock-hooks-failed");
                }
                // Input Guard 必须在官方 editor.Play() 之前安装：scnEditor.Play 会调用
                // playerManager.UnlockAllPlayerInput()，之后到 session cleanup 为止玩家的
                // hit / 暂停 / 缩放都必须被抑制。
                if (!RegisterInputGuardHooks())
                {
                    return FailStart("input-guard-hooks-failed");
                }
                EditorVisualClock.SetForcedSongPosition(_canonicalStartTime);
                EditorVisualClock.SetActive(false);
                Log.Info(UiText.LogSchedulerForcedClockInstalled);
                Log.Info(UiText.LogSchedulerInitHoldStarted);

                // ---- 4) Handoff observer：必须在本次 editor.Play() 前安装 ----
                _handoff = new PlaybackLifecycleHandoff(ModEntry.Harmony);
                if (!_handoff.Begin(out string handoffError))
                {
                    return FailStart("lifecycle-handoff-unavailable:" + handoffError);
                }

                if (!RenderistAutoPlay.EnsureAvailable(out string autoPlayError))
                {
                    return FailStart("autoplay-api-unavailable:" + autoPlayError);
                }

                // ---- 5) 帧末事务后端（PNG / log-only 由冻结模式决定；两者共用同一套 host / EOF / cleanup）----
                if (!FrameCaptureDriver.Start(outputDirectory, FramePrefix, ZeroPadWidth,
                        OnCaptureResult, _imageOutputEnabled, out long captureGeneration, out string captureError))
                {
                    return FailStart("capture-driver-start-failed:" + captureError);
                }
                _captureGeneration = captureGeneration;

                // ---- 6) 官方 editor.Play() ----
                _handoff.MarkPlayRequested();
                if (!TryPlay(out string playError))
                {
                    return FailStart("start-playback-failed:" + playError);
                }
                _handoff.MarkPlayReturned();

                // native playback teardown observer：必须在 editor.Play() 返回后安装。
                // 静态 IL 已确认 scnEditor.Play 不会调用 SwitchToEditMode（调用者只有
                // scnEditor.Start 与 scnEditor.Update 的 Esc 分支），因此 Play 返回后
                // 安装不会被自身初始化误触发，同时足以覆盖 InitializationHold 与
                // Capturing 期间用户按 Esc。
                if (!RegisterNativePlaybackStopObserver())
                {
                    return FailStart("native-playback-stop-observer-failed");
                }

                // pitch 必须可用且有效，否则不得构造 MasterTimeline。
                _pitch = EditorGameReflection.ReadPitch(out _pitchUnavailable);
                if (_pitchUnavailable)
                {
                    return FailStart("pitch-unavailable");
                }
                if (double.IsNaN(_pitch) || double.IsInfinity(_pitch) || _pitch <= MinPitch)
                {
                    return FailStart("pitch-invalid");
                }

                double? finalEffectiveBpm = EditorGameReflection.TryReadFinalEffectiveBpm(
                    out double finalBpm, out string finalBpmError)
                    ? finalBpm
                    : (double?)null;
                if (!EndTailPolicy.TryResolve(
                        _endTailInput,
                        _outputFps,
                        finalEffectiveBpm,
                        _pitch,
                        _safetyFrameLimit,
                        out EndTailResolution endTailResolution,
                        out string endTailResolveError))
                {
                    return FailStart("end-tail-resolution-failed:" +
                        (endTailResolveError ?? finalBpmError ?? "unknown"));
                }
                ApplyEndTailResolution(endTailResolution, finalEffectiveBpm);

                _timeline = null;
                Log.Info(UiText.LogSchedulerEditorPlayCalled);

                _status = SchedulerStatus.InitializationHold;
                Log.Info(UiText.Format(UiText.LogSchedulerStartedFormat,
                    _outputFps.ToString(CultureInfo.InvariantCulture),
                    SafetyPolicy,
                    SafetyFrameLimitPolicy.DescribeFrameLimit(_safetyFrameLimit),
                    outputDirectory));
                Log.Info("DeterministicFrameScheduler frozen output mode: imageOutputEnabled=" +
                         (_imageOutputEnabled ? "true" : "false") +
                         " mode=" + (_imageOutputEnabled ? "png-sequence" : "log-only"));
                Log.Info("DeterministicFrameScheduler frozen output geometry: " +
                         OutputGeometryPolicy.DescribeGeometryWithMode(geometry) +
                         " customResolutionEnabled=" + (_geometryCustomResolutionEnabled ? "true" : "false") +
                         " configuredCustomSize=" +
                         _geometryConfiguredWidth.ToString(CultureInfo.InvariantCulture) + "x" +
                         _geometryConfiguredHeight.ToString(CultureInfo.InvariantCulture) +
                         " maxTextureSize=" +
                         OutputGeometryPolicy.TryReadMaxTextureSize().ToString(CultureInfo.InvariantCulture));
                // 第二闭环：仅在真正启用超采样时额外输出一行，避免 scale=1 的第一闭环
                // 日志文本发生变化（该文本是既有验收判据的一部分）。
                if (_supersamplingScale > 1)
                {
                    Log.Info("DeterministicFrameScheduler frozen supersampling: scale=" +
                             _supersamplingScale.ToString(CultureInfo.InvariantCulture) +
                             " renderSize=" +
                             _renderWidth.ToString(CultureInfo.InvariantCulture) + "x" +
                             _renderHeight.ToString(CultureInfo.InvariantCulture) +
                             " downsampleLevels=" +
                             _downsamplePlannedLevelCount.ToString(CultureInfo.InvariantCulture) +
                             " downsampleAlgorithm=" + OutputGeometryPolicy.DownsampleAlgorithmLabel +
                             " renderTargetMode=" + (_imageOutputEnabled ? "png-sequence" : "log-only"));
                }
                Log.Info("DeterministicFrameScheduler render environment inventory: " +
                         (_environmentInventory == null
                             ? RenderEnvironmentInventory.UnavailableLabel
                             : _environmentInventory.Describe()));
                Log.Info("DeterministicFrameScheduler End Tail: input=" +
                         _endTailInput.Value.ToString("0.######", CultureInfo.InvariantCulture) +
                         " " + _endTailInput.Unit +
                         " resolvedTailFrames=" +
                         _resolvedTailFrameCount.ToString(CultureInfo.InvariantCulture) +
                         " resolvedTailSeconds=" +
                         _resolvedTailSeconds.ToString("0.######", CultureInfo.InvariantCulture) +
                         " completionBpm=" +
                         (_completionBpm.HasValue
                             ? _completionBpm.Value.ToString("0.######", CultureInfo.InvariantCulture)
                             : "unavailable") +
                         " safetyPolicy=" + SafetyPolicy +
                         " configuredSafetyFrameLimit=" +
                         _configuredSafetyFrameLimit.ToString(CultureInfo.InvariantCulture) +
                         " safetyFrameLimit=" + SafetyFrameLimitPolicy.DescribeFrameLimit(_safetyFrameLimit) +
                         " safetyDurationSeconds=" +
                         (SafetyDurationSeconds.HasValue
                             ? SafetyDurationSeconds.Value.ToString("0.######", CultureInfo.InvariantCulture)
                             : SafetyFrameLimitPolicy.UnboundedLabel));
                return null;
            }
            catch (Exception ex)
            {
                Log.Exception("DeterministicFrameScheduler: 启动异常", ex);
                FailStart("start-exception:" + ex.Message);
                return "start-exception";
            }
        }

        /// <summary>
        /// 启动前 session gate。用于在 Controller 创建 session 目录前完成
        /// scheduler busy / residual cleanup / game API gate 判断；不创建输出目录、
        /// 不创建 capture generation，也不进入 Editor.Play。
        /// </summary>
        internal static string ValidatePreStartConditions(bool allowExpectedTerminalControllerFail = false)
        {
            if (_running || !Terminal && _status != SchedulerStatus.Idle)
                return "scheduler-already-running";

            if (!EnsurePreviousRunCleanedUp(out string residualError))
                return "cleanup-failed:" + residualError;

            try
            {
                EditorGameReflection.EnsureTypes();
                return ValidateStartConditions(allowExpectedTerminalControllerFail);
            }
            catch (Exception ex)
            {
                Log.Exception("DeterministicFrameScheduler: 启动前校验异常", ex);
                return "pre-start-exception:" + ex.Message;
            }
        }

        /// <summary>
        /// terminal session 后的 ADOFAI controller Fail/Fail2 是可重置的旧播放
        /// 状态，而不是 Renderist residual ownership。该判定只供 Controller
        /// 在已通过 pre-start cleanup gate 后启动一次官方 re-arm。
        /// </summary>
        internal static bool IsTerminalControllerFailure =>
            EditorGameReflection.IsFailureState(EditorGameReflection.ReadControllerState());

        /// <summary>
        /// 调用 ADOFAI 已确认的公开状态迁移入口。此处不调用 editor.Play、
        /// 不建立 capture generation，也不创建 Renderist session。
        /// </summary>
        internal static bool TryBeginTerminalControllerRearm(out string error)
        {
            error = null;
            try
            {
                object controller = EditorGameReflection.Controller();
                if (controller == null)
                {
                    error = "controller-null";
                    return false;
                }

                if (!IsTerminalControllerFailure)
                    return true;

                MethodInfo changeToStart = EditorGameReflection.ControllerChangeToStartStateMethod;
                if (changeToStart == null)
                {
                    error = "change-to-start-state-unavailable";
                    return false;
                }

                changeToStart.Invoke(controller, null);
                Log.Info("DeterministicFrameScheduler: terminal controller re-arm requested via ChangeToStartState");
                return true;
            }
            catch (Exception ex)
            {
                error = "change-to-start-state-failed";
                Log.Exception("DeterministicFrameScheduler: terminal controller re-arm 失败", ex);
                return false;
            }
        }

        /// <summary>只接受官方状态机已完成迁移到 Start 的结果。</summary>
        internal static bool IsControllerStartState =>
            string.Equals(ToState(EditorGameReflection.ReadControllerState()),
                "Start", StringComparison.Ordinal);

        /// <summary>每次新 session 显式重置 run-owned 静态状态，避免继承上一 session。</summary>
        private static void ResetRunStateForStart()
        {
            _outputFrameIndex = 0;
            _outputTime = 0.0;
            _forcedSongPosition = 0.0;
            _canonicalStartTime = 0.0;
            _floor0EntryTime = 0.0;
            _pitch = 1.0;
            _pitchUnavailable = false;
            _safetyFrameLimit = 0L;
            _safetyPolicyKind = SafetyLimitKind.Disabled;
            _configuredSafetyFrameLimit = 0;
            _captureSource = null;
            _captureWidth = 0;
            _captureHeight = 0;
            // 输出几何在 TryStart 内解析后重新冻结；这里先归零，避免继承上一 session。
            _geometryMode = GeometryMode.LegacyWindow;
            _geometryCustomResolutionEnabled = false;
            _geometryConfiguredWidth = 0;
            _geometryConfiguredHeight = 0;
            _geometryConfiguredSupersamplingScale = OutputGeometryPolicy.DefaultSupersamplingScale;
            _supersamplingScale = OutputGeometryPolicy.DefaultSupersamplingScale;
            _renderWidth = 0;
            _renderHeight = 0;
            _downsamplePlannedLevelCount = 0;
            _downsampleActualLevelCount = 0;
            _outputWidth = 0;
            _outputHeight = 0;
            _environmentInventory = null;
            _captureRequestCount = 0;
            _capturedFrameCount = 0;
            _frameTransactionRequestCount = 0;
            _tailFramesCommitted = 0;
            _tailFramesCaptured = 0;
            _endTailInput = new EndTailInput(EndTailPolicy.DefaultValue, EndTailPolicy.DefaultUnit);
            _resolvedTailFrameCount = DefaultTailFrameCount;
            _resolvedTailSeconds = 0.0;
            _resolvedTailBeats = null;
            _completionBpm = null;
            _awaitFrameCount = 0;
            _activationUnityFrame = -1;
            _lastPrepareUnityFrame = -1;
            _clockActive = false;
            _pendingCapture = false;
            _pendingCaptureIndex = -1;
            _prefixSongPosition = null;
            _lastFrame0Stage = null;
            _hitsThisFrame = 0;
            _pendingStopEvent = null;
            _pendingStopReason = null;
            _canonicalCompletionCallbackSeen = false;
            _canonicalCompletionStateSeen = false;
            _canonicalCompletionFrameIndex = -1;
            _canonicalCompletionSignal = null;
            _initializationDeadlineRealtime = 0.0;
            _captureDeadlineRealtime = 0.0;
            _progressDeadlineRealtime = 0.0;
            _timeline = null;
            _preEntryCapturing = false;
            _preEntryClockLatched = false;
            _preEntrySourceActivationAttempted = false;
            _preEntryCandidateRejectLogged = false;
            _preEntryLastCaptureUnityFrame = -1;
            _preEntryCapturedFrameCount = 0L;
            _preEntryFrameIndex = 0L;
            _preEntryGameplayStartOutputFrameIndex = -1L;
            _preEntryAnchor = 0.0;
            _preEntryStep = 0.0;
            _preEntryBoundaryOutputFrameIndex = -1L;
            _preEntryBoundaryForcedTime = 0.0;
            _preEntryBoundaryCanonicalStart = 0.0;
            _preEntryBoundaryPreviousTime = 0.0;
            _preEntryInjectedBeatNumber = 0;
            _preEntryLifecycleInjectionArmed = false;
            _preEntryBeatOverrideOwned = false;
            _preEntryBeatOverrideConductor = null;
            _preEntryBeatOverrideField = null;
            _preEntryBeatOverrideOriginalBeat = 0;
            _preEntryLifecycleTimeScaleOwned = false;
            _preEntryLifecycleSavedTimeScale = 0f;
            _preEntryLifecycleTimeScaleFrozen = false;
            _preEntryLifecycleTimeScaleRestored = false;
            _preEntryLifecyclePartialScaleArmed = false;
            _preEntryLifecyclePartialStep = 0.0;
            _preEntryLifecyclePartialFraction = 0.0;
            _preEntryLifecycleRequestedPartialScale = 0f;
        }

        private static bool HasResidualOwnership()
        {
            return _ownsPlayback || _ownsUnityTiming || _handoff != null ||
                   _captureGeneration != 0 || FrameCaptureDriver.IsRunning ||
                   FrameCaptureDriver.HasActiveCameraSource ||
                   // 第二闭环：降采样链与 GPU 状态（RenderTexture.active / GL.sRGBWrite）
                   // 也是 residual ownership —— 未收敛前绝不允许开始下一 session。
                   FrameCaptureDriver.HasOwnedDownsampleChain ||
                   FrameCaptureDriver.HasResidualGpuState ||
                   _inputGuardHooks.Count > 0 ||
                   EditorVisualClock.HasTrackedHooks ||
                   _patchedConductorUpdate != null || _patchedAsyncInputAdjustAngle != null ||
                   _patchedControllerOnLandOnPortal != null ||
                   _patchedControllerCountdownUpdate != null ||
                   _preEntryBeatOverrideOwned ||
                   _patchedEditorSwitchToEditMode != null ||
        // PreEntry lifecycle bridge：timeScale ownership 也是 residual ownership，
        // session 结束后绝不允许遗留。
                   _preEntryLifecycleTimeScaleOwned ||
                   _savedRdcAuto.HasValue || _savedSelectedFloorSeqs.Count > 0;
        }

        private static bool EnsurePreviousRunCleanedUp(out string error)
        {
            error = null;
            if (!HasResidualOwnership())
            {
                _restored = true;
                return true;
            }

            if (!RestoreAll(out error))
            {
                _running = false;
                _status = SchedulerStatus.Failed;
                _terminalStopReason = "cleanup-failed:" + (error ?? "unknown");
                return false;
            }

            if (!HasResidualOwnership())
                return true;

            error = string.IsNullOrEmpty(error) ? "residual-ownership" : error;
            _running = false;
            _status = SchedulerStatus.Failed;
            _terminalStopReason = "cleanup-failed:" + error;
            return false;
        }

        private static string FailStart(string reason)
        {
            _terminalStopReason = reason;
            if (!RestoreAll(out string cleanupError))
            {
                _terminalStopReason = "cleanup-failed:" + cleanupError;
            }
            _status = SchedulerStatus.Failed;
            _running = false;
            Log.Warn(UiText.Format(UiText.LogSchedulerStartRejectedFormat, _terminalStopReason));
            return _terminalStopReason;
        }

        /// <summary>请求异步停止；由下一 Tick 统一恢复。幂等。</summary>
        public static void RequestStop(string stopEvent, string stopReason)
        {
            if (!_running) return;
            if (_pendingStopReason != null) return;
            _pendingStopEvent = stopEvent ?? "cancelled";
            _pendingStopReason = stopReason ?? "cancelled";
        }

        /// <summary>同步停止并恢复（Controller.Stop / Cancel / Mod Disable 使用）。幂等。</summary>
        public static void StopNow(string stopEvent, string stopReason)
        {
            RequestStop(stopEvent, stopReason);
            ProcessStop();
        }

        /// <summary>
        /// 统一 ownership-aware 收敛入口。幂等、可重复调用，且不依赖 controller
        /// session 是否 terminal（invariant：session terminal != scheduler owns nothing）：
        ///   * _running                → 走现有 StopNow 收敛路线（不复制第二套 cleanup）；
        ///   * 非 running 但仍有 residual → 与启动前 gate 同一 retry 语义，重试 RestoreAll；
        ///   * 无任何 ownership          → no-op。
        /// 返回 false 表示仍有 residual ownership（恢复未完成，可再次调用补做剩余项）。
        /// </summary>
        public static bool EnsureCleanedUp(string stopEvent, string stopReason)
        {
            if (_running)
            {
                StopNow(stopEvent, stopReason);
            }
            return EnsurePreviousRunCleanedUp(out _);
        }

        // ================================================================
        // Tick（由 EditorExportController.Tick 调用，每 OnUpdate 一次）
        // ================================================================

        public static void Tick()
        {
            if (!_running) return;

            try
            {
                if (_pendingStopReason != null)
                {
                    ProcessStop();
                    return;
                }

                if (!ModEntry.Enabled)
                {
                    RequestStop("mod-disabled", "mod-disabled");
                    ProcessStop();
                    return;
                }

                switch (_status)
                {
                    case SchedulerStatus.Preparing:
                    case SchedulerStatus.InitializationHold:
                        TickInitializationHold();
                        break;
                    case SchedulerStatus.Capturing:
                        TickCapturing();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("DeterministicFrameScheduler: Tick 异常", ex);
                RequestStop("tick-exception", ex.Message);
                ProcessStop();
            }
        }

        private static void TickInitializationHold()
        {
            _awaitFrameCount++;

            object state = EditorGameReflection.ReadControllerState();
            if (_awaitFrameCount == 1)
            {
                Log.Info("MasterTimeline InitializationHold entered: state=" + ToState(state) + " handoff=" + (_handoff == null ? "null" : _handoff.MarkerStatus) + " " + BuildRuntimeSnapshot());
            }
            if (EditorGameReflection.IsFailureState(state))
            {
                RequestStop("unexpected-fail", "controller-" + ToState(state));
                return;
            }

            // paused 是 **runtime** precondition：生命周期真正到达 PlayerControl / playerAlive
            // 之后仍 paused == true（或 paused 不可读）才是异常，立即 fail-closed，
            // 不等待 readiness 超时、也不写入 paused。
            if (TryDetectAbnormalPlaybackPause(state, out string playbackPauseReason))
            {
                Log.Warn(UiText.Format(UiText.LogSchedulerPlaybackPausedFormat,
                    playbackPauseReason, ToState(state),
                    _handoff == null ? "null" : _handoff.MarkerStatus,
                    BuildRuntimeSnapshot()));
                RequestStop("controller-paused", playbackPauseReason);
                return;
            }

            if (!PreEntryCaptureWatchdogActive() &&
                Time.realtimeSinceStartupAsDouble > _initializationDeadlineRealtime)
            {
                Log.Warn("MasterTimeline InitializationHold readiness timeout: state=" + ToState(state) + " handoff=" + (_handoff == null ? "null" : _handoff.MarkerStatus) + " " + BuildRuntimeSnapshot());
                RequestStop("playback-ready-timeout", "playback-ready-timeout");
                return;
            }

            // deterministic pre-entry 已正式进入 capture transaction 之后，initialization
            // readiness deadline 不再适用（否则合法但较慢的同步 PNG 编码/写盘会被误判）；
            // 改由与 Capturing 完全相同的已 armed watchdog 保护。
            if (PreEntryCaptureWatchdogActive() && CheckArmedCaptureWatchdogs()) return;

            string early = CheckEarlyTermination();
            if (early != null)
            {
                RequestStop(early, early);
                return;
            }

            // 只在 native Countdown 已完整可识别、且尚未越过第一个 beat 时锁定。
            // 这不是重启或回放：已错过该安全点即 fail-closed，绝不把已推进的
            // gameplay 状态倒回去。source 尚未 ready 时仍由该 anchor 冻结视觉 clock。
            if (!_preEntryClockLatched &&
                string.Equals(ToState(state), "Countdown", StringComparison.Ordinal))
            {
                if (!TryLatchPreEntryClock(out string latchDetail))
                {
                    if (latchDetail != null &&
                        !string.Equals(latchDetail, "lifecycle-incomplete", StringComparison.Ordinal))
                    {
                        Log.Warn("PreEntry clock latch failed: " +
                                 latchDetail + "");
                        RequestStop("preentry-clock-latch-failed", latchDetail);
                        return;
                    }
                }
            }

            if (_preEntryClockLatched &&
                !_preEntryCapturing && !_preEntrySourceActivationAttempted &&
                string.Equals(ToState(state), "Countdown", StringComparison.Ordinal))
            {
                if (!IsPreEntryCaptureCandidate(state, out string preEntryCandidate))
                {
                    if (!_preEntryCandidateRejectLogged)
                    {
                        _preEntryCandidateRejectLogged = true;
                        Log.Info("PreEntry Countdown not capture-ready: " +
                                 preEntryCandidate + "");
                    }
                }
                else
                {
                    _preEntrySourceActivationAttempted = true;
                    Log.Info("PreEntry source activation candidate: " +
                             preEntryCandidate + "");

                    if (!FrameCaptureDriver.TryActivateCameraSource(
                            _captureGeneration, _renderWidth, _renderHeight,
                            _outputWidth, _outputHeight, _supersamplingScale,
                            out string earlyCameraSourceError))
                    {
                        Log.Warn("PreEntry source activation failed: " +
                                 (earlyCameraSourceError ?? "unknown") + "");
                        RequestStop("capture-source-failed",
                            "preentry-capture-source-unavailable:" +
                            (earlyCameraSourceError ?? "unknown"));
                        return;
                    }

                    _captureSource = FrameCaptureDriver.CameraSourceLabel;
                    _captureWidth = FrameCaptureDriver.CaptureWidth;
                    _captureHeight = FrameCaptureDriver.CaptureHeight;
                    // 只在 activation 成功之后才快照**实际创建**的降采样级数；
                    // 失败路径不写该值，因此 metadata 不会伪称链已创建。
                    _downsampleActualLevelCount = FrameCaptureDriver.DownsampleLevelCount;
                    FrameCaptureDriver.CaptureRenderTargetInventory(_environmentInventory);
                    _preEntryCapturing = true;
                    _preEntryLastCaptureUnityFrame = -1;
                    Log.Info("PreEntry source active: first successful frame transaction " +
                             "commit after this point defines absolute output frame 0; ");
                }
            }

            if (!IsPlaybackReady(state)) return;

            // Never switch to the canonical gameplay clock while the last native
            // pre-entry frame transaction is unresolved. Its successful commit owns
            // the preceding absolute output index.
            if (_preEntryCapturing && _pendingCapture)
                return;

            object lifecycleSongPositionValue = ReadUnforcedSongPositionValue();
            string lifecycleSongPosition = ToValue(lifecycleSongPositionValue);
            if (!EditorGameReflection.TryReadGameplayStartOffset(
                    out double gameplayStartOffset, out string anchorError))
            {
                RequestStop("canonical-start-unavailable", anchorError);
                return;
            }

            double gameplayStart = _floor0EntryTime + gameplayStartOffset;
            if (double.IsNaN(gameplayStart) || double.IsInfinity(gameplayStart) ||
                Math.Abs(gameplayStart) > AnchorInvalidThresholdSeconds)
            {
                RequestStop("canonical-start-invalid", "gameplay-start-out-of-range");
                return;
            }

            double? observedSongPosition = ToDouble(lifecycleSongPositionValue);
            string observedSongPositionSource = "unforced-conductor-songposition";
            bool boundaryInvariant = false;
            if (_preEntryClockLatched)
            {
        // PreEntry：handoff 判据迁移到 deterministic boundary phase。
        // raw backing field（ReadUnforcedSongPositionValue）在本 probe 下不再是稳定的
        // native 证据：Conductor 对 songposition_minusi 的写回是否经过被 Harmony patch
        // 的 setter 会随 session 变化，因此改用 deterministic forced pre-entry clock 取值。
        // 判据本身也不再是「observed ≈ canonicalStart」这种时间窗口比较（那会隐含一个
        // 与 Output FPS 相关的窗口：step = pitch / OutputFps 在低 FPS 下可以远大于 0.05 s），
        // 而是「native 边界被观测时 forced time 仍停在 boundary grid point」，
        // 只吸收浮点表示/运算误差（见 PreEntryBoundaryNumericalTolerance）。
                observedSongPosition = _forcedSongPosition;
                observedSongPositionSource = "deterministic-forced-preentry-clock";
                boundaryInvariant = true;
            }

            bool anchorConsistent;
            if (boundaryInvariant)
            {
        // lifecycle-only 语义：隐藏 transition 期间 visual clock 必须**始终**停在
        // previousBoundaryTime；绝不能出现被推进到 boundaryForcedTime 的取值。
        // 判据只吸收 ULP 级数值误差，tolerance 不变。
                anchorConsistent = observedSongPosition.HasValue &&
                    Math.Abs(observedSongPosition.Value - _preEntryBoundaryPreviousTime) <=
                    PreEntryBoundaryNumericalTolerance(observedSongPosition.Value,
                        _preEntryBoundaryPreviousTime, _preEntryStep);
            }
            else
            {
                anchorConsistent = observedSongPosition.HasValue &&
                    Math.Abs(observedSongPosition.Value - gameplayStart) <=
                    GameplayAnchorConsistencyToleranceSeconds;
            }

            if (!anchorConsistent)
            {
                if (boundaryInvariant)
                {
                    Log.Warn("PreEntry lifecycle-only transition clock mismatch: " +
                             "boundaryOutputFrameIndex=" +
                             _preEntryBoundaryOutputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                             " expectedPreviousBoundaryTime=" +
                             _preEntryBoundaryPreviousTime.ToString("0.######", CultureInfo.InvariantCulture) +
                             " boundaryForcedTime=" +
                             _preEntryBoundaryForcedTime.ToString("0.######", CultureInfo.InvariantCulture) +
                             " observedSongPosition=" + ToValue(observedSongPosition) +
                             " canonicalStart=" + gameplayStart.ToString("0.######", CultureInfo.InvariantCulture) +
                             " controllerState=" + ToState(state) +
                             " lifecycleSongPosition=" + lifecycleSongPosition);
                    RequestStop("preentry-lifecycle-transition-clock-mismatch",
                        "native-playercontrol-visual-clock-not-at-previous-boundary-time");
                    return;
                }

                Log.Warn("MasterTimeline lifecycle anchor mismatch: floor0EntryTime=" +
                         _floor0EntryTime.ToString("0.######", CultureInfo.InvariantCulture) +
                         " countdownOffset=" + gameplayStartOffset.ToString("0.######", CultureInfo.InvariantCulture) +
                         " deterministicGameplayStart=" + gameplayStart.ToString("0.######", CultureInfo.InvariantCulture) +
                         " observedSongPosition=" + ToValue(observedSongPosition) +
                         " observedSource=" + observedSongPositionSource +
                         " lifecycleSongPosition=" + lifecycleSongPosition);
                RequestStop("canonical-start-mismatch", "native-gameplay-anchor-mismatch");
                return;
            }

            // TEMP Trail-aging probe：PlayerControl 与 anchor invariant 已通过。G 的 chart
            // time 是 canonicalStart，但上一帧仍是 previousBoundaryTime，因此本次 Unity
            // timestep 只应推进两者的差值。ownership 保留到 G 成功 commit，避免 PNG
            // 编码 wall time 或完整 1/FPS timestep 额外缩短 Trail history。
            if (_preEntryLifecycleTimeScaleOwned)
            {
                if (!TryArmGameplayBoundaryPartialStep(gameplayStart, out string partialScaleError))
                {
                    Log.Warn("PreEntry partial timeScale arm failed before gameplay handoff: " +
                             (partialScaleError ?? "unknown"));
                    RequestStop("preentry-lifecycle-partial-time-scale-failed",
                        partialScaleError ?? "partial-time-scale-arm-failed");
                    return;
                }
            }

            _canonicalStartTime = gameplayStart;
            _forcedSongPosition = gameplayStart;
            _timeline = new MasterTimeline(_outputFps, _canonicalStartTime, _pitch);
            {
                _preEntryGameplayStartOutputFrameIndex = _outputFrameIndex;
                _preEntryCapturing = false;
                _preEntryLifecycleInjectionArmed = false;
                Log.Info("PreEntry GameplayCapture handoff: " +
                         "gameplayStartOutputFrameIndex=" +
                         _preEntryGameplayStartOutputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                         " capturedPreEntryFrameCount=" +
                         _preEntryCapturedFrameCount.ToString(CultureInfo.InvariantCulture) +
                         " boundaryOutputFrameIndex=" +
                         _preEntryBoundaryOutputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                         " previousBoundaryTime=" +
                         _preEntryBoundaryPreviousTime.ToString("0.######", CultureInfo.InvariantCulture) +
                         " boundaryForcedTime=" +
                         _preEntryBoundaryForcedTime.ToString("0.######", CultureInfo.InvariantCulture) +
                         " observedDeterministicBoundaryTime=" + ToValue(observedSongPosition) +
                         " observedSource=" + observedSongPositionSource +
                         " injectedBeatNumber=" + _preEntryInjectedBeatNumber.ToString(CultureInfo.InvariantCulture) +
                         " controllerState=" + ToState(state) +
                         " lifecycleSongPosition=" + lifecycleSongPosition +
                         " canonicalStart=" + _canonicalStartTime.ToString("0.######", CultureInfo.InvariantCulture) +
                         " timeScale=" + Time.timeScale.ToString("0.######", CultureInfo.InvariantCulture) +
                         " timeScaleOwned=" + _preEntryLifecycleTimeScaleOwned +
                         " timeScaleRestored=" + _preEntryLifecycleTimeScaleRestored);
            }
            Log.Info("MasterTimeline lifecycle-ready: lifecycleReadySongPosition=" + lifecycleSongPosition +
                     " floor0EntryTime=" + _floor0EntryTime.ToString("0.######", CultureInfo.InvariantCulture) +
                     " countdownOffset=" + gameplayStartOffset.ToString("0.######", CultureInfo.InvariantCulture) +
                     " gameplayStart=" + _canonicalStartTime.ToString("0.######", CultureInfo.InvariantCulture) +
                     " forcedSongPosition=" + _canonicalStartTime.ToString("0.######", CultureInfo.InvariantCulture) +
                     " absoluteOutputFrameIndex=" + _outputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                     " " + BuildRuntimeSnapshot());

            EditorVisualClock.SetForcedSongPosition(_canonicalStartTime);
            EditorVisualClock.SetActive(true);

            // 正式基线在 gameplay readiness 接管 source。PreEntry 若已经在经
            // Countdown + native visual readiness 检查后接管，则复用同一 ownership，
            // 不重复写 Camera targetTexture。
            if (!FrameCaptureDriver.HasActiveCameraSource &&
                !FrameCaptureDriver.TryActivateCameraSource(
                    _captureGeneration, _renderWidth, _renderHeight,
                    _outputWidth, _outputHeight, _supersamplingScale,
                    out string cameraSourceError))
            {
                Log.Warn("DeterministicFrameScheduler: capture source activation failed: " +
                         (cameraSourceError ?? "unknown"));
                RequestStop("capture-source-failed",
                    "capture-source-unavailable:" + (cameraSourceError ?? "unknown"));
                return;
            }
            _captureSource = FrameCaptureDriver.CameraSourceLabel;
            _captureWidth = FrameCaptureDriver.CaptureWidth;
            _captureHeight = FrameCaptureDriver.CaptureHeight;
            // 只在 activation 成功之后才快照**实际创建**的降采样级数（log-only 恒为 0）。
            _downsampleActualLevelCount = FrameCaptureDriver.DownsampleLevelCount;
            FrameCaptureDriver.CaptureRenderTargetInventory(_environmentInventory);

            _activationUnityFrame = Time.frameCount + 1;
            _status = SchedulerStatus.Capturing;
            _clockActive = false;
            _lastPrepareUnityFrame = -1;
            _progressDeadlineRealtime = Time.realtimeSinceStartupAsDouble + FrameProgressWatchdogSeconds;

            Log.Info(UiText.Format(UiText.LogSchedulerInitHoldReleasedFormat,
                _canonicalStartTime.ToString("0.######", CultureInfo.InvariantCulture),
                _pitch.ToString("0.######", CultureInfo.InvariantCulture)));
        }

        /// <summary>
        /// deterministic pre-entry 是否已经进入正式 capture transaction：PreEntryClock 已 latch、
        /// capture source 已激活（<c>_preEntryCapturing</c>），且 progress deadline 已被第一笔
        /// pre-entry capture request armed。满足后 initialization readiness deadline 不再适用。
        /// </summary>
        private static bool PreEntryCaptureWatchdogActive()
        {
            return _preEntryClockLatched && _preEntryCapturing && _progressDeadlineRealtime > 0.0;
        }

        /// <summary>
        /// 检查已 armed 的 capture transaction / no-progress watchdog。
        ///
        /// 正式 Capturing 与 deterministic pre-entry（仍运行于 InitializationHold）**共用同一套**
        /// 失败保护语义，不复制第二套 timeout 语义。
        ///
        /// 只基于最近一次真实 progress（capture request 或成功 commit）刷新过的 deadline 判定，
        /// 因此：不限制总导出时长、不限制总帧数、不从 Output FPS 推导 timeout、不限制 pitch，
        /// 也不会因为导出参数合法但较慢而主动拒绝——单纯耗时较长的同步 PNG 编码/写盘会在
        /// CommitFrame 成功写盘后刷新 progress deadline。
        /// </summary>
        private static bool CheckArmedCaptureWatchdogs()
        {
            if (_pendingCapture && Time.realtimeSinceStartupAsDouble > _captureDeadlineRealtime)
            {
                RequestStop("capture-timeout", "capture-timeout");
                return true;
            }

            if (_progressDeadlineRealtime > 0.0 &&
                Time.realtimeSinceStartupAsDouble > _progressDeadlineRealtime)
            {
                RequestStop("watchdog-timeout", "frame-progress-watchdog-timeout");
                return true;
            }

            return false;
        }

        private static void TickCapturing()
        {
            object state = EditorGameReflection.ReadControllerState();
            if (EditorGameReflection.IsFailureState(state))
            {
                RequestStop("unexpected-fail", "controller-" + ToState(state));
                return;
            }

            if (CheckArmedCaptureWatchdogs()) return;

            ObserveCanonicalCompletion("tick");

            string early = CheckEarlyTermination();
            if (early != null)
            {
                RequestStop(early, early);
                return;
            }

            // 捕获与提交由 EndOfFrame 回调完成；Tick 只负责环境检测与完成收尾。
        }

        // ================================================================
        // Frame Begin：scrConductor.Update Prefix / Postfix
        // ================================================================

        private static void ConductorUpdatePrefix()
        {
            if (!_running || _pendingStopReason != null) return;

            if (_status == SchedulerStatus.InitializationHold)
            {
                // TEMP advancing PreEntryClock：Countdown 可识别后先冻结在 schedule
                // anchor；capture source ready 后仅由已提交 output frame 的下一个 index
                // 推进。raw DSP 与 PNG encode/write wall time 都不能影响 forced time。
                if (_preEntryClockLatched)
                {
                    int preEntryUnityFrame = Time.frameCount;
                    if (preEntryUnityFrame != _preEntryLastCaptureUnityFrame)
                    {
                        _preEntryLastCaptureUnityFrame = preEntryUnityFrame;
                        string preEntryState = ToState(EditorGameReflection.ReadControllerState());
                        if (_preEntryCapturing &&
                            string.Equals(preEntryState, "Countdown", StringComparison.Ordinal))
                        {
        // 已到 deterministic 边界就不再输出 pre-entry frame：见
        // HoldPreEntryBoundary / _preEntryBoundaryOutputFrameIndex。
                            if (_preEntryBoundaryOutputFrameIndex >= 0 &&
                                _outputFrameIndex >= _preEntryBoundaryOutputFrameIndex)
                            {
                                HoldPreEntryBoundary();
                            }
                            else
                            {
                                PreparePreEntryFrame();
                            }
                        }
                        else
                        {
                            // source-ready 之前冻结在 anchor；若 native 已在上一事务
                            // 转入 PlayerControl，则保留上一笔 forced value，交给下一次
                            // scheduler Tick 完成 canonical handoff，绝不回跳到 anchor。
                            if (!_preEntryCapturing)
                            {
                                _forcedSongPosition = _preEntryAnchor;
                                EditorVisualClock.SetForcedSongPosition(_forcedSongPosition);
                                EditorVisualClock.SetActive(true);
                            }
                            else
                            {
                            }
                        }
                    }
                    return;
                }

                EditorVisualClock.SetForcedSongPosition(_canonicalStartTime);
                return;
            }

            if (_status != SchedulerStatus.Capturing) return;

            int frame = Time.frameCount;

            if (!_clockActive)
            {
                if (frame < _activationUnityFrame) return;
                _clockActive = true;
                _lastPrepareUnityFrame = frame;
                LogFrame0Stage(_outputFrameIndex, "BEFORE_CONDUCTOR");
                ObserveCanonicalCompletion("before-conductor");
                PrepareFrame();
                _prefixSongPosition = ToValue(EditorGameReflection.ReadConductorSongPosition());
                LogFrameConductor("Prefix");
                return;
            }

            if (frame != _lastPrepareUnityFrame)
            {
                _lastPrepareUnityFrame = frame;
                LogFrame0Stage(_outputFrameIndex, "BEFORE_CONDUCTOR");
                ObserveCanonicalCompletion("before-conductor");
                PrepareFrame();
                _prefixSongPosition = ToValue(EditorGameReflection.ReadConductorSongPosition());
                LogFrameConductor("Prefix");
            }
        }

        private static void ConductorUpdatePostfix()
        {
            if (_running && _pendingStopReason == null &&
                _status == SchedulerStatus.InitializationHold &&
                _preEntryClockLatched)
            {
                if (_preEntryCapturing)
                {
                }
                return;
            }

            if (!_running || _pendingStopReason != null || _status != SchedulerStatus.Capturing || !_clockActive)
                return;

            LogFrame0Stage(_outputFrameIndex, "AFTER_CONDUCTOR");
            LogFrame0Stage(_outputFrameIndex, "BEFORE_AUTOPLAY");
            if (!RenderistAutoPlay.CatchUp(_outputFrameIndex, _forcedSongPosition, out int hits, out string error))
            {
                Log.Warn("DeterministicFrameScheduler: RenderistAutoPlay 失败：" + error);
                RequestStop("autoplay-failed", error ?? "autoplay-failed");
                return;
            }

            _hitsThisFrame = hits;
            LogFrame0Stage(_outputFrameIndex, "AFTER_AUTOPLAY");
            ObserveCanonicalCompletion("after-autoplay");
            if (_outputFrameIndex < 4 || hits > 0)
            {
                Log.Info("MasterTimeline Conductor Postfix: frameIndex=" +
                         _outputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                         " expectedChartTime=" + _forcedSongPosition.ToString("0.######", CultureInfo.InvariantCulture) +
                         " songpositionPrefix=" + (_prefixSongPosition ?? "null") +
                         " songpositionPostfix=" + ToValue(EditorGameReflection.ReadConductorSongPosition()) +
                         " hitsThisFrame=" + hits.ToString(CultureInfo.InvariantCulture));
            }

            if (hits > 0)
            {
                Log.Debug("DeterministicFrameScheduler: frame " +
                          _outputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                          " due hits=" + hits.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void PrepareFrame()
        {
            // 关键不变量：上一帧必须已捕获并提交，才允许开始下一帧。
            if (_pendingCapture)
            {
                RequestStop("capture-failed", "previous-frame-capture-unresolved");
                return;
            }

            // 结构性 fail-closed（**不是**产品级帧数上限）：帧号是 long，当已提交计数到达
            // long.MaxValue 时，"下一帧"编号无法再用 long 表达，因此显式停止，
            // 绝不依赖 unchecked 递增回绕。
            if (_outputFrameIndex == long.MaxValue)
            {
                RequestStop("frame-index-exhausted", "output-frame-index-exceeds-long-range");
                return;
            }

            if (SafetyFrameLimitPolicy.IsFrameLimitReached(_safetyFrameLimit, _outputFrameIndex))
            {
                if (_canonicalCompletionStateSeen &&
                    _tailFramesCommitted >= _resolvedTailFrameCount)
                    RequestStop("completed", "canonical-completion-tail-drained");
                else
                    RequestStop("safety-limit", "safety-frame-limit");
                return;
            }

            long index = _outputFrameIndex;
            LogFrame0Stage(index, "BEGIN");
            long gameplayFrameIndex = GetGameplayFrameIndex(index);
            MasterTimeline.FrameSample sample = _timeline.Prepare(gameplayFrameIndex);
            // outputTime 始终属于全视频的 absolute output timeline；只有 chartTime
            // 在 GameplayCapture 使用 gameplay-local index。
            _outputTime = index / (double)_outputFps;
            _forcedSongPosition = sample.ChartTime;
            _hitsThisFrame = 0;

            EditorVisualClock.SetForcedSongPosition(_forcedSongPosition);

            if (index == 0)
            {
                Log.Info("MasterTimeline Frame0 prepared: outputTime=" +
                         _outputTime.ToString("0.######", CultureInfo.InvariantCulture) +
                         " chartTime=" +
                         _forcedSongPosition.ToString("0.######", CultureInfo.InvariantCulture) +
                         " " + BuildRuntimeSnapshot());
            }
            else if (gameplayFrameIndex == 0 || gameplayFrameIndex == 1)
            {
        // gameplay frame 0 / 1 PRE 取证：证明 capture request 之前 forced chart time 已经
        // 是 canonicalStart（frame 0）/ canonicalStart + step（frame 1），且 getter 读回的
        // effectiveSongposition 与之相同 —— 即 native visual state 会在本帧重新求值于
        // canonicalStart，而不是停留在 boundary hold 的 forced time。
        // 日志只读：每层最多读一次，null-safe，不产生 gameplay side effect。
                object gameplayController = EditorGameReflection.Controller();
                object gameplayPlayer = ReadInstanceMember(gameplayController, "playerOne");
                object gameplaySystem = ReadInstanceMember(gameplayPlayer, "planetarySystem");
                object gameplayPlanet = ReadInstanceMember(gameplaySystem, "chosenPlanet");
                object gameplayPlanetAngle = gameplayPlanet == null
                    ? null
                    : ReadInstanceMember(gameplayPlanet, "angle");
                Log.Info("PreEntry GameplayCapture frame " +
                         gameplayFrameIndex.ToString(CultureInfo.InvariantCulture) + " prepared: " +
                         "absoluteOutputFrameIndex=" + index.ToString(CultureInfo.InvariantCulture) +
                         " gameplayFrameIndex=" + gameplayFrameIndex.ToString(CultureInfo.InvariantCulture) +
                         " outputTime=" + _outputTime.ToString("0.######", CultureInfo.InvariantCulture) +
                         " forcedChartTime=" + _forcedSongPosition.ToString("0.######", CultureInfo.InvariantCulture) +
                         " canonicalStart=" +
                         _canonicalStartTime.ToString("0.######", CultureInfo.InvariantCulture) +
                         " effectiveSongposition=" +
                         ToValue(EditorGameReflection.ReadConductorSongPosition()) +
                         " controllerState=" + ToState(EditorGameReflection.ReadControllerState()) +
                         " chosenPlanetAngle=" + ToValue(gameplayPlanetAngle) +
                         " timeScale=" + Time.timeScale.ToString("0.######", CultureInfo.InvariantCulture) +
                         " deltaTime=" + Time.deltaTime.ToString("0.######", CultureInfo.InvariantCulture) +
                         " unscaledDeltaTime=" +
                         Time.unscaledDeltaTime.ToString("0.######", CultureInfo.InvariantCulture) +
                         " timeScaleOwned=" + _preEntryLifecycleTimeScaleOwned +
                         "");
            }

            _frameTransactionRequestCount++;
            // captureRequestCount 只统计 PNG 图像请求；log-only 模式下保持 0。
            if (_imageOutputEnabled) _captureRequestCount++;
            if (!FrameCaptureDriver.RequestCapture(_captureGeneration, index))
            {
                RequestStop("capture-failed", "capture-request-rejected");
                return;
            }
            _pendingCapture = true;
            _pendingCaptureIndex = index;
            _captureDeadlineRealtime = Time.realtimeSinceStartupAsDouble + CaptureTimeoutSeconds;
            _progressDeadlineRealtime = Time.realtimeSinceStartupAsDouble + FrameProgressWatchdogSeconds;
            LogFrame0Stage(index, "BEFORE_EOF");

            string forcedText = _forcedSongPosition.ToString("0.######", CultureInfo.InvariantCulture);
            if (index == 0)
            {
                Log.Info(UiText.Format(UiText.LogSchedulerFrame0ForcedFormat, forcedText));
            }
            else
            {
                Log.Debug("DeterministicFrameScheduler: prepare frame " + index +
                          " forcedSongPosition=" + forcedText +
                          " unityFrame=" + Time.frameCount.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// TEMP lifecycle-only boundary phase：最后一个 pre-entry frame（B-1）已 commit。
        ///
        /// 与上一版的区别：**不再把 forced visual clock 推到 boundaryForcedTime**。
        /// visual/effective songposition 在整个隐藏 transition 期间保持
        /// previousBoundaryTime = anchor + (B-1)*step，因此 scrPlanet / TrailRenderer 等
        /// stateful visual 组件看不到任何“未来”chart time。Countdown 到 PlayerControl 改由
        /// Countdown_Update 作用域内的 threshold beat 注入触发（见 CountdownUpdatePrefix）。
        ///
        /// boundaryForcedTime 仍然保留其数学意义（B grid 定义 / 日志 / bracket invariant /
        /// 诊断），只是不再作为 visual clock 取值。PlayerControl 出现后由既有 handoff 以
        /// 由既有 capture transaction / no-progress watchdog fail-closed。
        /// </summary>
        private static void HoldPreEntryBoundary()
        {
            if (!_preEntryLifecycleInjectionArmed)
            {
        // ownership 必须已经在 CommitFrame(B-1) 成功 commit 后建立；
        // hidden frame Prefix 只验证，绝不在本帧首次写 timeScale，否则本帧
        // deltaTime 已预先计算，仍会泄漏一个完整 output step 的 scaled aging。
                if (!TryVerifyPreEntryBoundaryTime(out string initialTimeScaleError))
                {
                    Log.Warn("PreEntry lifecycle timeScale not ready at boundary entry: " +
                             (initialTimeScaleError ?? "unknown") +
                             " unityFrame=" + Time.frameCount.ToString(CultureInfo.InvariantCulture));
                    RequestStop("preentry-time-scale-not-ready",
                        initialTimeScaleError ?? "time-scale-not-ready-before-hidden-frame");
                    return;
                }

                _preEntryLifecycleInjectionArmed = true;
                Log.Info("PreEntry lifecycle-only boundary phase begin: " +
                         "boundaryOutputFrameIndex=" +
                         _preEntryBoundaryOutputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                         " lastPreEntryOutputFrameIndex=" +
                         (_preEntryBoundaryOutputFrameIndex - 1).ToString(CultureInfo.InvariantCulture) +
                         " previousBoundaryTime=" +
                         _preEntryBoundaryPreviousTime.ToString("0.######", CultureInfo.InvariantCulture) +
                         " canonicalStart=" +
                         _preEntryBoundaryCanonicalStart.ToString("0.######", CultureInfo.InvariantCulture) +
                         " boundaryForcedTime=" +
                         _preEntryBoundaryForcedTime.ToString("0.######", CultureInfo.InvariantCulture) +
                         " step=" + _preEntryStep.ToString("0.######", CultureInfo.InvariantCulture) +
                         " forcedClockAtPhaseBegin=" +
                         _forcedSongPosition.ToString("0.######", CultureInfo.InvariantCulture) +
                         " absoluteOutputFrameIndex=" + _outputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                         " controllerState=" + ToState(EditorGameReflection.ReadControllerState()) +
                         " injectedBeatNumber=" + _preEntryInjectedBeatNumber.ToString(CultureInfo.InvariantCulture) +
                         " adjustedCountdownTicks=" +
                         ToValue(ReadInstanceMember(EditorGameReflection.Conductor(), "adjustedCountdownTicks")) +
                         " capturedPreEntryFrameCount=" +
                         _preEntryCapturedFrameCount.ToString(CultureInfo.InvariantCulture) +
                         "");
            }

            // hidden phase 每 Unity frame 校验 timeScale 仍为 0：被外部改动时不静默覆盖，
            // 记录异常并 fail-closed，避免与其他系统争夺 ownership。
            if (!TryVerifyPreEntryBoundaryTime(out string timeScaleError))
            {
                Log.Warn("PreEntry lifecycle timeScale ownership violated: " +
                         (timeScaleError ?? "unknown") +
                         " unityFrame=" + Time.frameCount.ToString(CultureInfo.InvariantCulture) +
                         " originalTimeScale=" +
                         _preEntryLifecycleSavedTimeScale.ToString("0.######", CultureInfo.InvariantCulture));
                RequestStop("preentry-time-scale-ownership-lost",
                    timeScaleError ?? "time-scale-ownership-lost");
                return;
            }


            // 关键：visual clock 保持 previousBoundaryTime，绝不前进到 boundaryForcedTime。
            _forcedSongPosition = _preEntryBoundaryPreviousTime;
            EditorVisualClock.SetForcedSongPosition(_forcedSongPosition);
            EditorVisualClock.SetActive(true);
        }

        // ================================================================
        // PreEntry lifecycle bridge：hidden boundary phase 的 Time.timeScale ownership
        // ================================================================
        //
        // 目的：判定 hidden lifecycle-only boundary phase 中的 TrailRenderer / native history
        // aging 是否来自 scaled Unity time。只在 frame B-1 成功 commit 的回调中接管，
        // PlayerControl 被观测到后让 G 使用 partial scale，G 成功 commit 后恢复。
        // 不修改 Time.fixedDeltaTime，不碰任何 Trail API。

        /// <summary>
        /// 建立 timeScale ownership 并写 0。失败即 fail-closed，且**保留 ownership 标记**
        /// （不静默清空），以便后续 cleanup 重试恢复。
        /// </summary>
        private static bool TryFreezePreEntryBoundaryTime(long committedFrameIndex, out string error)
        {
            error = null;

            if (!_preEntryLifecycleTimeScaleOwned)
            {
                float original = Time.timeScale;
                if (float.IsNaN(original) || float.IsInfinity(original))
                {
                    error = "time-scale-not-finite:" + original.ToString("0.######", CultureInfo.InvariantCulture);
                    return false;
                }

                _preEntryLifecycleSavedTimeScale = original;
                _preEntryLifecycleTimeScaleOwned = true;
                _preEntryLifecycleTimeScaleRestored = false;
            }

            try
            {
                Time.timeScale = 0f;
            }
            catch (Exception)
            {
                error = "time-scale-write-failed";
                return false;
            }

            float observed = Time.timeScale;
            if (observed != 0f)
            {
                error = "time-scale-readback-not-zero:" + observed.ToString("0.######", CultureInfo.InvariantCulture);
                return false;
            }

            _preEntryLifecycleTimeScaleFrozen = true;
            Log.Info("PreEntry lifecycle-time-freeze begin: " +
                     "unityFrame=" + Time.frameCount.ToString(CultureInfo.InvariantCulture) +
                     " lastCommittedFrameIndex=" +
                     committedFrameIndex.ToString(CultureInfo.InvariantCulture) +
                     " nextAbsoluteOutputFrameIndex=" +
                     _outputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                     " originalTimeScale=" +
                     _preEntryLifecycleSavedTimeScale.ToString("0.######", CultureInfo.InvariantCulture) +
                     " requestedTimeScale=0" +
                     " observedTimeScale=" + observed.ToString("0.######", CultureInfo.InvariantCulture) +
                     " deltaTime=" + Time.deltaTime.ToString("0.######", CultureInfo.InvariantCulture) +
                     " unscaledDeltaTime=" + Time.unscaledDeltaTime.ToString("0.######", CultureInfo.InvariantCulture) +
                     " forcedSongPosition=" +
                     _forcedSongPosition.ToString("0.######", CultureInfo.InvariantCulture) +
                     " controllerState=" + ToState(EditorGameReflection.ReadControllerState()));
            return true;
        }

        /// <summary>
        /// hidden phase 每 Unity frame 校验 timeScale 仍为 0。若被外部改动则不静默覆盖，
        /// 记录异常并 fail-closed，避免与其他系统争夺 ownership。
        /// </summary>
        private static bool TryVerifyPreEntryBoundaryTime(out string error)
        {
            error = null;
            if (!_preEntryLifecycleTimeScaleOwned)
            {
                error = "time-scale-ownership-not-acquired";
                return false;
            }
            if (!_preEntryLifecycleTimeScaleFrozen)
            {
                error = "time-scale-freeze-not-established";
                return false;
            }

            float observed = Time.timeScale;
            if (observed != 0f)
            {
                error = "time-scale-changed-during-ownership:" +
                        observed.ToString("0.######", CultureInfo.InvariantCulture);
                return false;
            }

            return true;
        }

        /// <summary>
        /// PlayerControl handoff 后，把 timeScale 从 0 改为仅覆盖
        /// previousBoundaryTime 到 canonicalStart 的 partial timestep。所有范围判定只复用
        /// deterministic boundary 的 ULP-based tolerance；写入后必须精确读回。
        /// </summary>
        private static bool TryArmGameplayBoundaryPartialStep(double canonicalStart, out string error)
        {
            error = null;
            if (!_preEntryLifecycleTimeScaleOwned)
            {
                error = "time-scale-ownership-not-acquired";
                return false;
            }
            if (!_preEntryLifecycleTimeScaleFrozen)
            {
                error = "time-scale-freeze-not-established";
                return false;
            }
            if (_preEntryLifecyclePartialScaleArmed)
            {
                error = "partial-time-scale-already-armed";
                return false;
            }

            double step = _preEntryStep;
            double partialStep = canonicalStart - _preEntryBoundaryPreviousTime;
            double tolerance = PreEntryBoundaryNumericalTolerance(
                canonicalStart, _preEntryBoundaryPreviousTime, step);
            if (double.IsNaN(step) || double.IsInfinity(step) || step <= 0.0 ||
                double.IsNaN(_preEntryBoundaryForcedTime) ||
                double.IsInfinity(_preEntryBoundaryForcedTime) ||
                double.IsNaN(partialStep) || double.IsInfinity(partialStep) ||
                partialStep <= 0.0 || partialStep > step + tolerance ||
                canonicalStart > _preEntryBoundaryForcedTime + tolerance)
            {
                error = "partial-step-out-of-range:partialStep=" +
                        partialStep.ToString("R", CultureInfo.InvariantCulture) +
                        ",step=" + step.ToString("R", CultureInfo.InvariantCulture) +
                        ",boundaryForcedTime=" +
                        _preEntryBoundaryForcedTime.ToString("R", CultureInfo.InvariantCulture) +
                        ",canonicalStart=" + canonicalStart.ToString("R", CultureInfo.InvariantCulture) +
                        ",tolerance=" + tolerance.ToString("R", CultureInfo.InvariantCulture);
                return false;
            }

            double partialFraction = partialStep / step;
            double fractionTolerance = tolerance / step;
            if (double.IsNaN(partialFraction) || double.IsInfinity(partialFraction) ||
                partialFraction <= 0.0 || partialFraction > 1.0 + fractionTolerance)
            {
                error = "partial-fraction-out-of-range:" +
                        partialFraction.ToString("R", CultureInfo.InvariantCulture);
                return false;
            }
            if (partialFraction > 1.0)
                partialFraction = 1.0;

            if (float.IsNaN(_preEntryLifecycleSavedTimeScale) ||
                float.IsInfinity(_preEntryLifecycleSavedTimeScale) ||
                _preEntryLifecycleSavedTimeScale <= 0f)
            {
                error = "saved-time-scale-not-positive-finite:" +
                        _preEntryLifecycleSavedTimeScale.ToString("R", CultureInfo.InvariantCulture);
                return false;
            }

            double requestedDouble = _preEntryLifecycleSavedTimeScale * partialFraction;
            float requested = (float)requestedDouble;
            if (float.IsNaN(requested) || float.IsInfinity(requested) || requested <= 0f ||
                requested > _preEntryLifecycleSavedTimeScale)
            {
                error = "requested-partial-time-scale-out-of-range:" +
                        requested.ToString("R", CultureInfo.InvariantCulture);
                return false;
            }

            try
            {
                Time.timeScale = requested;
            }
            catch (Exception)
            {
                error = "partial-time-scale-write-failed";
                return false;
            }

            float observed = Time.timeScale;
            if (observed != requested)
            {
                error = "partial-time-scale-readback-mismatch:expected=" +
                        requested.ToString("R", CultureInfo.InvariantCulture) +
                        ",observed=" + observed.ToString("R", CultureInfo.InvariantCulture);
                return false;
            }

            _preEntryLifecycleTimeScaleFrozen = false;
            _preEntryLifecyclePartialScaleArmed = true;
            _preEntryLifecyclePartialStep = partialStep;
            _preEntryLifecyclePartialFraction = partialFraction;
            _preEntryLifecycleRequestedPartialScale = requested;

            Log.Info("PreEntry partial timeScale armed for gameplay frame G: " +
                     "unityFrame=" + Time.frameCount.ToString(CultureInfo.InvariantCulture) +
                     " gameplayStartOutputFrameIndex=" +
                     _outputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                     " previousBoundaryTime=" +
                     _preEntryBoundaryPreviousTime.ToString("0.######", CultureInfo.InvariantCulture) +
                     " canonicalStart=" + canonicalStart.ToString("0.######", CultureInfo.InvariantCulture) +
                     " step=" + step.ToString("0.######", CultureInfo.InvariantCulture) +
                     " partialStep=" + partialStep.ToString("0.######", CultureInfo.InvariantCulture) +
                     " partialFraction=" + partialFraction.ToString("0.######", CultureInfo.InvariantCulture) +
                     " originalTimeScale=" +
                     _preEntryLifecycleSavedTimeScale.ToString("0.######", CultureInfo.InvariantCulture) +
                     " requestedPartialTimeScale=" + requested.ToString("0.######", CultureInfo.InvariantCulture) +
                     " observedTimeScale=" + observed.ToString("0.######", CultureInfo.InvariantCulture) +
                     " deltaTime=" + Time.deltaTime.ToString("0.######", CultureInfo.InvariantCulture) +
                     " unscaledDeltaTime=" +
                     Time.unscaledDeltaTime.ToString("0.######", CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>
        /// 恢复**实际保存的**原始 timeScale（不假设为 1）并读回验证，成功后释放 ownership。
        /// 失败时保留 ownership，由 cleanup 路径重试。
        /// </summary>
        private static bool TryRestorePreEntryTimeScale(string phase, out string error)
        {
            error = null;
            if (!_preEntryLifecycleTimeScaleOwned)
                return true;

            float observedBeforeRestore = Time.timeScale;
            try
            {
                Time.timeScale = _preEntryLifecycleSavedTimeScale;
            }
            catch (Exception)
            {
                error = "time-scale-restore-write-failed";
                return false;
            }

            float restored = Time.timeScale;
            if (Math.Abs(restored - _preEntryLifecycleSavedTimeScale) > 1e-6f)
            {
                error = "time-scale-restore-readback-mismatch:expected=" +
                        _preEntryLifecycleSavedTimeScale.ToString("0.######", CultureInfo.InvariantCulture) +
                        ",observed=" + restored.ToString("0.######", CultureInfo.InvariantCulture);
                return false;
            }

            _preEntryLifecycleTimeScaleOwned = false;
            _preEntryLifecycleTimeScaleFrozen = false;
            _preEntryLifecycleTimeScaleRestored = true;
            _preEntryLifecyclePartialScaleArmed = false;

            Log.Info("PreEntry " + phase + ": " +
                     "unityFrame=" + Time.frameCount.ToString(CultureInfo.InvariantCulture) +
                     " absoluteOutputFrameIndex=" + _outputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                     " originalTimeScale=" +
                     _preEntryLifecycleSavedTimeScale.ToString("0.######", CultureInfo.InvariantCulture) +
                     " observedTimeScaleBeforeRestore=" +
                     observedBeforeRestore.ToString("0.######", CultureInfo.InvariantCulture) +
                     " restoredTimeScale=" + restored.ToString("0.######", CultureInfo.InvariantCulture) +
                     " partialStep=" +
                     _preEntryLifecyclePartialStep.ToString("0.######", CultureInfo.InvariantCulture) +
                     " partialFraction=" +
                     _preEntryLifecyclePartialFraction.ToString("0.######", CultureInfo.InvariantCulture) +
                     " requestedPartialTimeScale=" +
                     _preEntryLifecycleRequestedPartialScale.ToString("0.######", CultureInfo.InvariantCulture) +
                     " deltaTime=" + Time.deltaTime.ToString("0.######", CultureInfo.InvariantCulture) +
                     " unscaledDeltaTime=" + Time.unscaledDeltaTime.ToString("0.######", CultureInfo.InvariantCulture) +
                     " controllerState=" + ToState(EditorGameReflection.ReadControllerState()) +
                     " forcedSongPosition=" +
                     _forcedSongPosition.ToString("0.######", CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>
        /// TEMP advancing PreEntryClock：source 已在经过严格候选检查的 Countdown 中
        /// 激活后，只以成功 commit 的 output index 推进 songposition。该临时调查不得
        /// 触碰 DSP / AudioSource / countdown 的 native state，也不进入 autoplay。
        /// </summary>
        private static void PreparePreEntryFrame()
        {
            if (_pendingCapture)
            {
                RequestStop("capture-failed", "previous-preentry-capture-unresolved");
                return;
            }

            if (_outputFrameIndex == long.MaxValue)
            {
                RequestStop("frame-index-exhausted", "output-frame-index-exceeds-long-range");
                return;
            }

            if (SafetyFrameLimitPolicy.IsFrameLimitReached(_safetyFrameLimit, _outputFrameIndex))
            {
                RequestStop("safety-limit", "safety-frame-limit-during-native-preentry");
                return;
            }

            long index = _outputFrameIndex;
            if (_preEntryFrameIndex != _preEntryCapturedFrameCount)
            {
                RequestStop("capture-failed", "preentry-frame-index-commit-mismatch");
                return;
            }

            double forcedPreEntryTime = _preEntryAnchor +
                                         _preEntryFrameIndex * _preEntryStep;
            if (double.IsNaN(forcedPreEntryTime) || double.IsInfinity(forcedPreEntryTime) ||
                Math.Abs(forcedPreEntryTime) > AnchorInvalidThresholdSeconds)
            {
                RequestStop("preentry-clock-invalid", "forced-preentry-time-out-of-range");
                return;
            }

            _outputTime = index / (double)_outputFps;
            _hitsThisFrame = 0;
            _forcedSongPosition = forcedPreEntryTime;
            EditorVisualClock.SetForcedSongPosition(_forcedSongPosition);
            EditorVisualClock.SetActive(true);


            _frameTransactionRequestCount++;
            // captureRequestCount 只统计 PNG 图像请求；log-only 模式下保持 0。
            if (_imageOutputEnabled) _captureRequestCount++;
            if (!FrameCaptureDriver.RequestCapture(_captureGeneration, index))
            {
                RequestStop("capture-failed", "preentry-capture-request-rejected");
                return;
            }

            _pendingCapture = true;
            _pendingCaptureIndex = index;
            _captureDeadlineRealtime = Time.realtimeSinceStartupAsDouble + CaptureTimeoutSeconds;
            _progressDeadlineRealtime = Time.realtimeSinceStartupAsDouble + FrameProgressWatchdogSeconds;
        }

        // ================================================================
        // Capture / Commit（WaitForEndOfFrame）
        // ================================================================

        private static void OnCaptureResult(
            long generation, long frameIndex, bool success, bool imageWritten, string filePath, string error)
        {
            if (!_running) return;

            // A timeout/cancel requested by Tick owns the transaction. A late
            // callback must not commit a frame after the scheduler has decided
            // that the session failed or was cancelled.
            if (_pendingStopReason != null) return;

            if (generation != _captureGeneration)
            {
                Log.Debug("DeterministicFrameScheduler: stale capture generation " + generation);
                return;
            }

            if (_pendingCaptureIndex != frameIndex)
            {
                Log.Debug("DeterministicFrameScheduler: stale capture result " + frameIndex);
                return;
            }

            _pendingCapture = false;
            _pendingCaptureIndex = -1;
            _captureDeadlineRealtime = 0.0;
            ObserveCanonicalCompletion("after-capture");

            if (success)
            {
                Log.Debug("MasterTimeline FrameBoundary: frameIndex=" + frameIndex.ToString(CultureInfo.InvariantCulture) +
                          " outputTime=" + (frameIndex / (double)_outputFps).ToString("0.######", CultureInfo.InvariantCulture) +
                          " timeline=" + DescribeFrameTimeline(frameIndex) +
                          " hitsThisFrame=" + _hitsThisFrame.ToString(CultureInfo.InvariantCulture) +
                          " " + BuildRuntimeSnapshot());
            }

            if (!success)
            {
                Log.Error(UiText.Format(UiText.LogSchedulerCaptureFailedFormat,
                    frameIndex.ToString(CultureInfo.InvariantCulture),
                    error ?? "unknown"));
                // 只有 PNG 模式才可能写盘失败；log-only 不写图像，失败原因是帧末事务本身。
                RequestStop("capture-failed",
                    _imageOutputEnabled ? "write-png-failed" : "frame-transaction-failed");
                return;
            }

            // 冻结模式与回调给出的 imageWritten 必须一致：这是 metadata 计数等式
            // （writtenPngFrameCount == logicalFrameCount 仅限 PNG 模式）的前提，
            // 不一致时 fail-closed，绝不把不清楚来源的帧记成已写盘或已提交。
            if (imageWritten != _imageOutputEnabled)
            {
                Log.Warn("DeterministicFrameScheduler: image-output mode mismatch at frame " +
                         frameIndex.ToString(CultureInfo.InvariantCulture) +
                         " frozenImageOutputEnabled=" + (_imageOutputEnabled ? "true" : "false") +
                         " imageWritten=" + (imageWritten ? "true" : "false"));
                RequestStop("capture-failed", "frame-transaction-mode-mismatch");
                return;
            }

            CommitFrame(frameIndex, imageWritten, filePath);
        }

        private static void CommitFrame(long frameIndex, bool imageWritten, string filePath)
        {
            if (frameIndex != _outputFrameIndex)
            {
                RequestStop("capture-failed", "frame-index-mismatch");
                return;
            }

            // 与 PrepareFrame 同一结构性边界：帧号是 long，到达 long.MaxValue 时无法再
            // 表达"下一个帧号"。此处按不可达性不该出现（PrepareFrame 已拒绝），但绝不依赖
            // "不可达"来避免 unchecked 溢出；不推进任何计数即 fail-closed。
            if (_outputFrameIndex == long.MaxValue)
            {
                RequestStop("frame-index-exhausted", "output-frame-index-exceeds-long-range");
                return;
            }

            LogFrame0Stage(frameIndex, "COMMIT");
            // 逻辑帧推进是唯一的帧 authority：_outputFrameIndex 全局受 long.MaxValue 边界守卫，
            // 因此不可能回绕。PNG 写盘计数只在 imageWritten 时递增（log-only 恒为 0）。
            if (imageWritten) _capturedFrameCount++;
            _outputFrameIndex++;

            if (_preEntryCapturing)
            {
                _preEntryCapturedFrameCount++;
                _preEntryFrameIndex++;
            }

            // TEMP Trail-aging probe：只在 B-1 的帧末事务成功（PNG 模式下即 ReadPixels / Encode /
            // Write 全部成功；log-only 模式下即帧末校验通过）
            // 且上一 commit counters 已推进后，才在同一 Unity frame 结束前接管 timeScale。
            // 因此写盘失败绝不会 freeze；下一 hidden Unity frame 从 frame begin 起就应看到
            // timeScale=0 / deltaTime=0，而已完成的 B-1 视觉事务不受影响。
            if (_preEntryClockLatched &&
                _preEntryCapturing && _preEntryBoundaryOutputFrameIndex > 0 &&
                frameIndex == _preEntryBoundaryOutputFrameIndex - 1)
            {
                if (!TryFreezePreEntryBoundaryTime(frameIndex, out string freezeError))
                {
                    Log.Warn("PreEntry lifecycle-time-freeze failed after B-1 commit: " +
                             (freezeError ?? "unknown"));
                    RequestStop("preentry-lifecycle-time-freeze-failed",
                        freezeError ?? "time-scale-freeze-failed-after-b-minus-one-commit");
                    return;
                }
            }

            // G 的帧末事务已成功且 commit counters 已推进；现在
            // 才恢复 session 开始时保存的原始 timeScale，使 G+1 从完整 timestep 开始。
            // G 失败、取消或异常均不会走到这里，而由统一 cleanup 恢复。
            if (_preEntryLifecyclePartialScaleArmed &&
                _preEntryGameplayStartOutputFrameIndex >= 0 &&
                frameIndex == _preEntryGameplayStartOutputFrameIndex)
            {
                if (!TryRestorePreEntryTimeScale(
                        "partial timeScale restored after gameplay frame G commit",
                        out string partialRestoreError))
                {
                    Log.Warn("PreEntry partial timeScale restore failed after G commit: " +
                             (partialRestoreError ?? "unknown"));
                    RequestStop("preentry-time-scale-restore-failed",
                        partialRestoreError ?? "time-scale-restore-failed-after-g-commit");
                    return;
                }
            }

            _progressDeadlineRealtime = Time.realtimeSinceStartupAsDouble + FrameProgressWatchdogSeconds;

            // 尾帧计数拆成逻辑 / PNG 两个维度：completion 判定只使用逻辑提交数，
            // 因此两个模式在相同谱面上的 completionFrameIndex / tailFramesCommitted 必须一致。
            if (_canonicalCompletionStateSeen && frameIndex > _canonicalCompletionFrameIndex)
            {
                _tailFramesCommitted++;
                if (imageWritten) _tailFramesCaptured++;
            }

            Log.Debug("DeterministicFrameScheduler: commit frame " + frameIndex +
                      " imageWritten=" + (imageWritten ? "true" : "false") +
                      " imageOutputEnabled=" + (_imageOutputEnabled ? "true" : "false") +
                      " -> " + (filePath ?? "(no image output)"));

            if (_canonicalCompletionStateSeen &&
                _tailFramesCommitted >= _resolvedTailFrameCount)
            {
                RequestStop("completed", "canonical-completion-tail-drained");
                return;
            }

            if (SafetyFrameLimitPolicy.IsFrameLimitReached(_safetyFrameLimit, _outputFrameIndex))
            {
                RequestStop("safety-limit", _canonicalCompletionStateSeen
                    ? "safety-frame-limit-before-tail-drained"
                    : "safety-frame-limit-before-canonical-completion");
            }
        }

        /// <summary>
        /// 观察当前 ADOFAI 的原生完成路径。OnLandOnPortal 是完成请求，
        /// controller.state == Won 是状态机提交；二者都满足后才开始 tail。
        /// </summary>
        private static void ObserveCanonicalCompletion(string observationPoint)
        {
            if (!_canonicalCompletionCallbackSeen || _canonicalCompletionStateSeen)
                return;

            if (!EditorGameReflection.IsWonState(EditorGameReflection.ReadControllerState()))
                return;

            if (!TryResolveEndTailAtCompletion(out string tailResolveError))
            {
                RequestStop("completion-tail-resolution-failed",
                    tailResolveError ?? "completion-tail-resolution-failed");
                return;
            }

            _canonicalCompletionStateSeen = true;
            _canonicalCompletionFrameIndex = _outputFrameIndex;
            _canonicalCompletionSignal = "scrController.OnLandOnPortal+state=Won";
            Log.Info("DeterministicFrameScheduler: canonical completion observed at " +
                     observationPoint + ", outputFrame=" +
                     _canonicalCompletionFrameIndex.ToString(CultureInfo.InvariantCulture) +
                     ", tailFrames=" + _resolvedTailFrameCount.ToString(CultureInfo.InvariantCulture));
        }

        private static bool TryResolveEndTailAtCompletion(out string error)
        {
            error = null;
            double? completionBpm = EditorGameReflection.TryReadCompletionEffectiveBpm(
                out double observedBpm, out string bpmError)
                ? observedBpm
                : _completionBpm;

            if (!EndTailPolicy.TryResolve(
                    _endTailInput,
                    _outputFps,
                    completionBpm,
                    _pitch,
                    _safetyFrameLimit,
                    out EndTailResolution resolution,
                    out string resolveError))
            {
                error = resolveError ?? bpmError ?? "completion-tail-resolution-failed";
                return false;
            }

            ApplyEndTailResolution(resolution, completionBpm);
            return true;
        }

        private static void ApplyEndTailResolution(
            EndTailResolution resolution, double? completionBpm)
        {
            _resolvedTailFrameCount = resolution.FrameCount;
            _resolvedTailSeconds = resolution.Seconds;
            _resolvedTailBeats = resolution.Beats;
            _completionBpm = completionBpm;
        }

        private static void OnCanonicalCompletionRequested(object instance)
        {
            if (!_running || _pendingStopReason != null ||
                _status != SchedulerStatus.Capturing)
                return;

            object controller = EditorGameReflection.Controller();
            if (controller == null || !ReferenceEquals(controller, instance))
                return;

            if (!_canonicalCompletionCallbackSeen)
            {
                _canonicalCompletionCallbackSeen = true;
                Log.Info("DeterministicFrameScheduler: native OnLandOnPortal observed at outputFrame=" +
                         _outputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                         ", state=" + ToState(EditorGameReflection.ReadControllerState()));
            }

            ObserveCanonicalCompletion("OnLandOnPortal-postfix");
        }

        // ================================================================
        // Input Guard（session 期间抑制玩家输入 / 暂停 / 编辑器缩放）
        // ================================================================
        //
        // 只 Patch 精确的查询 / 入口方法，不 Patch scrPlayer.Hit、scrConductor.Update 或
        // scnEditor.Update：
        //   * RenderistAutoPlay 通过官方 scrPlayer.Hit(true) 命中，Hit 不经过本组方法。
        //   * Esc cancellation 由 scnEditor.Update 中一个更早的 KeyCode.Escape 分支直接
        //     SwitchToEditMode(false) 并 ret 完成，不经过 TogglePauseGame。
        // 因此本组 guard 既不会影响确定性自动命中，也不会阻断 Esc。

        private static bool RegisterInputGuardHooks()
        {
            if (!UnregisterInputGuardHooks())
                return false;

            if (!EditorGameReflection.InputGuardApiAvailable)
            {
                Log.Warn("MasterTimeline input guard unavailable: " +
                         (EditorGameReflection.DescribeMissingInputGuardApi() ?? "unknown"));
                return false;
            }

            var descriptors = new[]
            {
                new InputGuardHook
                {
                    Target = EditorGameReflection.PlayerManagerAnyValidInputWasTriggeredMethod,
                    PrefixName = nameof(AnyValidInputWasTriggeredPrefix),
                },
                new InputGuardHook
                {
                    Target = EditorGameReflection.PlayerValidInputWasTriggeredMethod,
                    PrefixName = nameof(ValidInputWasTriggeredPrefix),
                },
                new InputGuardHook
                {
                    Target = EditorGameReflection.PlayerValidInputWasReleasedMethod,
                    PrefixName = nameof(ValidInputWasReleasedPrefix),
                },
                new InputGuardHook
                {
                    Target = EditorGameReflection.PlayerCountValidKeysPressedMethod,
                    PrefixName = nameof(CountValidKeysPressedPrefix),
                },
                new InputGuardHook
                {
                    Target = EditorGameReflection.EditorZoomCameraMethod,
                    PrefixName = nameof(ZoomCameraPrefix),
                },
                new InputGuardHook
                {
                    Target = EditorGameReflection.ControllerTogglePauseGameMethod,
                    PrefixName = nameof(TogglePauseGamePrefix),
                },
            };

            Harmony harmony = ModEntry.Harmony;
            if (harmony == null)
            {
                Log.Warn("MasterTimeline input guard unavailable: harmony-null");
                return false;
            }

            _inputGuardHooks.AddRange(descriptors);
            var descriptions = new List<string>();

            foreach (InputGuardHook hook in _inputGuardHooks)
            {
                if (hook.Target == null)
                {
                    Log.Warn("MasterTimeline input guard target missing: " + hook.PrefixName);
                    UnregisterInputGuardHooks();
                    return false;
                }

                MethodInfo prefix = AccessTools.Method(typeof(DeterministicFrameScheduler), hook.PrefixName);
                if (prefix == null)
                {
                    Log.Warn("MasterTimeline input guard prefix missing: " + hook.PrefixName);
                    UnregisterInputGuardHooks();
                    return false;
                }

                try
                {
                    hook.Patched = true;
                    harmony.Patch(hook.Target, prefix: new HarmonyMethod(prefix));
                }
                catch (Exception ex)
                {
                    Log.Exception("DeterministicFrameScheduler: 注册 input guard 失败 " + hook.PrefixName, ex);
                    UnregisterInputGuardHooks();
                    return false;
                }

                descriptions.Add(hook.Target.DeclaringType?.Name + "." + hook.Target.Name);
            }

            Log.Info("MasterTimeline input guard installed: " + string.Join(", ", descriptions.ToArray()));
            return true;
        }

        private static bool UnregisterInputGuardHooks()
        {
            if (_inputGuardHooks.Count == 0) return true;

            Harmony harmony = ModEntry.Harmony;
            if (harmony == null) return false;

            bool success = true;
            for (int i = _inputGuardHooks.Count - 1; i >= 0; i--)
            {
                InputGuardHook hook = _inputGuardHooks[i];
                if (!hook.Patched || hook.Target == null)
                {
                    _inputGuardHooks.RemoveAt(i);
                    continue;
                }

                try
                {
                    MethodInfo prefix = AccessTools.Method(typeof(DeterministicFrameScheduler), hook.PrefixName);
                    if (prefix == null)
                    {
                        success = false;
                        continue;
                    }
                    harmony.Unpatch(hook.Target, prefix);
                    hook.Patched = false;
                    _inputGuardHooks.RemoveAt(i);
                }
                catch (Exception ex)
                {
                    success = false;
                    Log.Exception("DeterministicFrameScheduler: 撤销 input guard 失败 " + hook.PrefixName, ex);
                }
            }

            return success && _inputGuardHooks.Count == 0;
        }

        /// <summary>
        /// Guard 只在本次 Renderist session 从官方 editor.Play() 起、到 cleanup 前生效。
        /// 与 RenderistAutoPlay 使用同一状态范围；它不调用本组任何方法。
        /// </summary>
        private static bool IsInputGuardActive()
        {
            return _running && _pendingStopReason == null && _inputGuardHooks.Count > 0 &&
                   (_status == SchedulerStatus.InitializationHold ||
                    _status == SchedulerStatus.Capturing);
        }

        private static bool AnyValidInputWasTriggeredPrefix(ref bool __result)
        {
            if (!IsInputGuardActive()) return true;
            __result = false;
            return false;
        }

        private static bool ValidInputWasTriggeredPrefix(ref bool __result)
        {
            if (!IsInputGuardActive()) return true;
            __result = false;
            return false;
        }

        private static bool ValidInputWasReleasedPrefix(ref bool __result)
        {
            if (!IsInputGuardActive()) return true;
            __result = false;
            return false;
        }

        private static bool CountValidKeysPressedPrefix(ref int __result)
        {
            if (!IsInputGuardActive()) return true;
            __result = 0;
            return false;
        }

        /// <summary>滚轮缩放：只跳过原方法，void 无返回值。</summary>
        private static bool ZoomCameraPrefix()
        {
            return !IsInputGuardActive();
        }

        /// <summary>
        /// 暂停入口：跳过原方法并返回当前 paused 状态，语义等同于“没有发生切换”。
        /// 官方 TogglePauseGame 在所有返回点都返回 controller.paused。
        /// </summary>
        private static bool TogglePauseGamePrefix(ref bool __result)
        {
            if (!IsInputGuardActive()) return true;
            __result = EditorGameReflection.ReadControllerPaused() ?? false;
            return false;
        }

        // ================================================================
        // 恢复
        // ================================================================

        private static void ProcessStop()
        {
            if (_restored && !HasResidualOwnership()) return;
            if (_cleanupInProgress) return;

            bool priorCleanupFailure = _pendingStopEvent == null &&
                !string.IsNullOrEmpty(_terminalStopReason) &&
                _terminalStopReason.StartsWith("cleanup-failed", StringComparison.Ordinal);
            string stopEvent = _pendingStopEvent ?? (priorCleanupFailure ? "cleanup-failed" : "cancelled");
            string stopReason = _pendingStopReason ?? (priorCleanupFailure ? _terminalStopReason : "cancelled");
            _pendingStopEvent = null;
            _pendingStopReason = null;
            _terminalStopReason = stopReason;

            _running = false;
            _clockActive = false;

            try
            {
                if (!RestoreAll(out string cleanupError))
                {
                    _terminalStopReason = "cleanup-failed:" + cleanupError;
                    _status = SchedulerStatus.Failed;
                    Log.Info(UiText.Format(UiText.LogSchedulerStoppedFormat,
                        _terminalStopReason, _status.ToString()));
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("DeterministicFrameScheduler: 恢复异常", ex);
                _restored = false;
                _terminalStopReason = "cleanup-failed:restore-exception";
                _status = SchedulerStatus.Failed;
                Log.Info(UiText.Format(UiText.LogSchedulerStoppedFormat,
                    _terminalStopReason, _status.ToString()));
                return;
            }

            _status = MapTerminal(stopEvent);
            Log.Info(UiText.Format(UiText.LogSchedulerStoppedFormat, stopReason ?? "?", _status.ToString()));
        }

        private static bool RestoreAll(out string error)
        {
            error = null;
            if (_cleanupInProgress)
            {
                error = "reentrant";
                return false;
            }

            _cleanupInProgress = true;
            var failures = new List<string>();
            try
            {
        // 优先恢复 transient ownership（scoped beat override 与 pre-entry timeScale），
        // 再做 Hook / 捕获后端 / native playback teardown。
                if (!TryRestorePreEntryBeatOverride(out string beatOverrideCleanupError))
                    failures.Add("preentry-beat-override-restore:" + (beatOverrideCleanupError ?? "unknown"));
                _preEntryLifecycleInjectionArmed = false;

                if (_preEntryLifecycleTimeScaleOwned)
                {
                    if (!TryRestorePreEntryTimeScale("timeScale restore (cleanup)", out string timeScaleCleanupError))
                    {
                        failures.Add("preentry-time-scale-restore:" + (timeScaleCleanupError ?? "unknown"));
                        Log.Warn("PreEntry timeScale restore failed during cleanup: " +
                                 (timeScaleCleanupError ?? "unknown") +
                                 " originalTimeScale=" +
                                 _preEntryLifecycleSavedTimeScale.ToString("0.######", CultureInfo.InvariantCulture));
                    }
                }

                // 先撤 Hook 与捕获后端，再恢复 RDC.auto 与 Editor 播放状态，最后恢复 Unity 时间。
                bool captureStopped;
                try { captureStopped = FrameCaptureDriver.Stop(); }
                catch (Exception ex)
                {
                    captureStopped = false;
                    failures.Add("capture-stop-exception");
                    Log.Exception("DeterministicFrameScheduler: 捕获后端停止失败", ex);
                }
                if (!captureStopped || FrameCaptureDriver.IsRunning)
                    failures.Add("capture-stop");
                else
                {
                    _pendingCapture = false;
                    _pendingCaptureIndex = -1;
                    _captureGeneration = 0;
                    _captureDeadlineRealtime = 0.0;
                }

                EditorVisualClock.SetActive(false);
                try
                {
                    if (!EditorVisualClock.UnregisterForcedClockHooks())
                        failures.Add("visual-clock-unpatch");
                }
                catch (Exception ex)
                {
                    failures.Add("visual-clock-unpatch-exception");
                    Log.Exception("DeterministicFrameScheduler: forced clock 清理异常", ex);
                }

                // Input Guard 尽早撤销，让玩家输入在 playback 恢复前重新生效。
                try
                {
                    if (!UnregisterInputGuardHooks())
                        failures.Add("input-guard-unpatch");
                }
                catch (Exception ex)
                {
                    failures.Add("input-guard-unpatch-exception");
                    Log.Exception("DeterministicFrameScheduler: input guard 清理异常", ex);
                }

                // native playback teardown observer 与其它 hook 一起精确撤销。
                try
                {
                    if (!UnregisterNativePlaybackStopObserver())
                        failures.Add("native-playback-stop-observer-unpatch");
                }
                catch (Exception ex)
                {
                    failures.Add("native-playback-stop-observer-unpatch-exception");
                    Log.Exception("DeterministicFrameScheduler: native playback stop observer 清理异常", ex);
                }

                try
                {
                    if (!UnregisterAsyncInputAngleHook())
                        failures.Add("async-input-unpatch");
                }
                catch (Exception ex)
                {
                    failures.Add("async-input-unpatch-exception");
                    Log.Exception("DeterministicFrameScheduler: AsyncInput hook 清理异常", ex);
                }
                try
                {
                    if (!UnregisterCanonicalCompletionHook())
                        failures.Add("canonical-completion-unpatch");
                }
                catch (Exception ex)
                {
                    failures.Add("canonical-completion-unpatch-exception");
                    Log.Exception("DeterministicFrameScheduler: canonical completion hook 清理异常", ex);
                }
        // PreEntry lifecycle boundary hooks：注入标记必须先失效，
        // 再精确 Unpatch Countdown_Update。
                _preEntryLifecycleInjectionArmed = false;
                try
                {
                    if (!UnregisterPreEntryLifecycleBridgeHook())
                        failures.Add("lifecycle-boundary-probe-unpatch");
                }
                catch (Exception ex)
                {
                    failures.Add("lifecycle-boundary-probe-unpatch-exception");
                    Log.Exception("DeterministicFrameScheduler: lifecycle boundary probe hooks 清理异常", ex);
                }
                try
                {
                    if (!UnregisterConductorUpdateHook())
                        failures.Add("conductor-unpatch");
                }
                catch (Exception ex)
                {
                    failures.Add("conductor-unpatch-exception");
                    Log.Exception("DeterministicFrameScheduler: conductor hook 清理异常", ex);
                }

                if (_handoff != null)
                {
                    try
                    {
                        if (_handoff.TryDispose())
                            _handoff = null;
                        else
                            failures.Add("lifecycle-dispose");
                    }
                    catch (Exception ex)
                    {
                        failures.Add("lifecycle-dispose-exception");
                        Log.Exception("DeterministicFrameScheduler: lifecycle handoff 清理异常", ex);
                    }
                }

                _timeline = null;

                try
                {
                    if (!RestoreRdcAuto())
                        failures.Add("rdc-auto-restore");
                }
                catch (Exception ex)
                {
                    failures.Add("rdc-auto-restore-exception");
                    Log.Exception("DeterministicFrameScheduler: RDC.auto 清理异常", ex);
                }

                bool selectedOk;
                try { selectedOk = RestoreSelectedFloorSeqs(); }
                catch (Exception ex)
                {
                    selectedOk = false;
                    Log.Exception("DeterministicFrameScheduler: selected floor 恢复异常", ex);
                }
                bool playbackOk;
                try { playbackOk = RestorePlayback(); }
                catch (Exception ex)
                {
                    playbackOk = false;
                    Log.Exception("DeterministicFrameScheduler: playback 恢复异常", ex);
                }
                if (playbackOk && selectedOk)
                    _ownsPlayback = false;
                else
                {
                    if (!playbackOk) failures.Add("playback-restore");
                    if (!selectedOk) failures.Add("selected-floor-restore");
                }

                if (_ownsUnityTiming)
                {
                    bool timingOk = true;
                    try { Time.captureFramerate = _savedCaptureFramerate; }
                    catch (Exception ex) { timingOk = false; Log.Exception("DeterministicFrameScheduler: captureFramerate 恢复失败", ex); }
                    try { Application.targetFrameRate = _savedTargetFrameRate; }
                    catch (Exception ex) { timingOk = false; Log.Exception("DeterministicFrameScheduler: targetFrameRate 恢复失败", ex); }
                    try { QualitySettings.vSyncCount = _savedVSyncCount; }
                    catch (Exception ex) { timingOk = false; Log.Exception("DeterministicFrameScheduler: vSyncCount 恢复失败", ex); }
                    if (timingOk)
                        _ownsUnityTiming = false;
                    else
                        failures.Add("unity-timing-restore");
                }

                // PreEntry lifecycle bridge：任何离开 session 的路径（normal completion /
        // user cancel / failure / exception / mod disable）都必须尝试恢复**实际保存的**
        // 原始 timeScale。恢复失败时保留 ownership 并计入 cleanup failure，
        // 绝不静默清空。
                if (_preEntryLifecycleTimeScaleOwned)
                {
                    _preEntryLifecycleInjectionArmed = false;
                    if (TryRestorePreEntryTimeScale("timeScale restore (cleanup)", out string timeScaleCleanupError))
                    {
                        _preEntryLifecycleTimeScaleRestored = true;
                    }
                    else
                    {
                        failures.Add("preentry-time-scale-restore:" +
                                     (timeScaleCleanupError ?? "unknown"));
                        Log.Warn("PreEntry timeScale restore failed during cleanup: " +
                                 (timeScaleCleanupError ?? "unknown") +
                                 " originalTimeScale=" +
                                 _preEntryLifecycleSavedTimeScale.ToString("0.######", CultureInfo.InvariantCulture));
                    }
                }

                _clockActive = false;
                _activationUnityFrame = -1;
                _lastPrepareUnityFrame = -1;

                if (failures.Count == 0)
                {
                    _restored = true;
                    _savedRdcAuto = null;
                    _savedSelectedFloorSeqs.Clear();
                    Log.Debug("DeterministicFrameScheduler: restored captureFramerate=" + _savedCaptureFramerate +
                              " targetFrameRate=" + _savedTargetFrameRate +
                              " vSyncCount=" + _savedVSyncCount +
                              " rdcAuto=" + FormatAuto(EditorGameReflection.ReadRdcAuto()));
                    return true;
                }

                _restored = false;
                error = string.Join(",", failures.ToArray());
                return false;
            }
            catch (Exception ex)
            {
                _restored = false;
                failures.Add("restore-exception:" + ex.Message);
                error = string.Join(",", failures.ToArray());
                Log.Exception("DeterministicFrameScheduler: 恢复异常", ex);
                return false;
            }
            finally
            {
                _cleanupInProgress = false;
            }
        }

        private static SchedulerStatus MapTerminal(string stopEvent)
        {
            // Completed 只能来自 canonical completion + 已提交全部 tail。
            // 判定使用逻辑提交数，因此 PNG 与 log-only 的 completion authority 完全相同。
            if (string.Equals(stopEvent, "completed", StringComparison.Ordinal) &&
                _canonicalCompletionStateSeen &&
                _tailFramesCommitted >= _resolvedTailFrameCount)
            {
                return SchedulerStatus.Completed;
            }
            // 用户主动停止与取消类事件全部是 Cancelled。
            if (string.Equals(stopEvent, "user", StringComparison.Ordinal) ||
                string.Equals(stopEvent, "cancelled", StringComparison.Ordinal) ||
                string.Equals(stopEvent, "mod-disabled", StringComparison.Ordinal) ||
                string.Equals(stopEvent, "left-editor", StringComparison.Ordinal) ||
                string.Equals(stopEvent, "native-playback-stopped", StringComparison.Ordinal))
            {
                return SchedulerStatus.Cancelled;
            }
            return SchedulerStatus.Failed;
        }

        private static string ClassifyTermination(string stopReason, SchedulerStatus status)
        {
            if (status == SchedulerStatus.Completed)
                return "canonical-completion";

            string reason = stopReason ?? string.Empty;
            if (reason == "user-stop" || reason == "cancelled" ||
                reason == "mod-disabled" || reason == "left-editor" ||
                reason == "native-playback-stopped")
                return "user-cancel";
            if (reason.IndexOf("safety-frame-limit", StringComparison.Ordinal) >= 0 ||
                reason.IndexOf("safety-limit", StringComparison.Ordinal) >= 0)
                return "safety-limit";
            if (reason.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("watchdog", StringComparison.OrdinalIgnoreCase) >= 0)
                return "watchdog";
            if (reason.IndexOf("capture", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("png", StringComparison.OrdinalIgnoreCase) >= 0 ||
                // log-only 的帧末事务失败与 PNG 写盘失败属于同一类终止原因；
                // 它绝不能被误报成 write-png-failed，但 terminationKind 保持一致。
                reason.IndexOf("frame-transaction", StringComparison.OrdinalIgnoreCase) >= 0)
                return "capture-failure";
            return "lifecycle-failure";
        }

        private static void SaveState()
        {
            _savedCaptureFramerate = Time.captureFramerate;
            _savedTargetFrameRate = Application.targetFrameRate;
            _savedVSyncCount = QualitySettings.vSyncCount;
            _savedRdcAuto = EditorGameReflection.ReadRdcAuto();
            _savedSelectedFloorSeqs.Clear();
            EditorGameReflection.ReadSelectedFloorSeqs(_savedSelectedFloorSeqs);
            _ownsUnityTiming = true;
        }

        private static bool RestoreRdcAuto()
        {
            if (!_savedRdcAuto.HasValue) return true;
            if (!EditorGameReflection.TryWriteRdcAuto(_savedRdcAuto.Value))
            {
                Log.Warn("DeterministicFrameScheduler: RDC.auto 恢复失败。");
                return false;
            }
            _savedRdcAuto = null;
            return true;
        }

        private static bool RestorePlayback()
        {
            if (!_ownsPlayback) return true;

            object editor = EditorGameReflection.Editor();
            if (editor == null) return false;

            bool? playMode = EditorGameReflection.ReadEditorPlayMode();
            if (playMode != true) return playMode.HasValue;

            MethodInfo mSwitch = EditorGameReflection.EditorSwitchToEditModeMethod;
            if (mSwitch == null)
            {
                Log.Warn("DeterministicFrameScheduler: SwitchToEditMode 方法缺失，无法回 Editor。");
                return false;
            }

            try
            {
                mSwitch.Invoke(editor, new object[] { false });
            }
            catch (Exception ex)
            {
                Log.Exception("DeterministicFrameScheduler: SwitchToEditMode 调用失败", ex);
                return false;
            }

            bool? restoredPlayMode = EditorGameReflection.ReadEditorPlayMode();
            return restoredPlayMode.HasValue && !restoredPlayMode.Value;
        }

        private static bool RestoreSelectedFloorSeqs()
        {
            if (!_ownsPlayback) return true;
            try
            {
                if (_savedSelectedFloorSeqs.Count == 0) return true;

                object editor = EditorGameReflection.Editor();
                Type editorType = EditorGameReflection.EditorType;
                Type floorType = EditorGameReflection.FloorType;
                if (editor == null || editorType == null || floorType == null) return false;

                System.Collections.IList floors = EditorGameReflection.ReadFloorsList();
                if (floors == null || floors.Count == 0)
                {
                    Log.Warn("DeterministicFrameScheduler: 恢复选择时 floors 不可用，跳过。");
                    return false;
                }

                var targets = new List<object>();
                foreach (int want in _savedSelectedFloorSeqs)
                {
                    object found = null;
                    foreach (object floor in floors)
                    {
                        if (EditorGameReflection.FloorSeq(floor) == want)
                        {
                            found = floor;
                            break;
                        }
                    }
                    if (found == null)
                    {
                        Log.Warn("DeterministicFrameScheduler: 恢复选择时找不到 seqID=" + want + "，跳过。");
                        return false;
                    }
                    targets.Add(found);
                }

                if (targets.Count == 1)
                {
                    MethodInfo select = EditorGameReflection.EditorSelectFloorMethod;
                    if (select == null) return false;
                    select.Invoke(editor, new[] { targets[0], (object)false });
                    return true;
                }

                var ordered = new List<Tuple<int, object>>();
                for (int i = 0; i < targets.Count; i++)
                {
                    ordered.Add(Tuple.Create(EditorGameReflection.FloorSeq(targets[i]), targets[i]));
                }
                ordered.Sort((a, b) => a.Item1.CompareTo(b.Item1));

                for (int i = 1; i < ordered.Count; i++)
                {
                    if (ordered[i].Item1 != ordered[i - 1].Item1 + 1)
                    {
                        Log.Warn("DeterministicFrameScheduler: 非连续多选，跳过恢复。");
                        return false;
                    }
                }

                MethodInfo multi = EditorGameReflection.EditorMultiSelectFloorsMethod;
                if (multi == null)
                {
                    Log.Warn("DeterministicFrameScheduler: MultiSelectFloors 方法缺失，跳过恢复。");
                    return false;
                }
                multi.Invoke(editor, new[] { ordered[0].Item2, ordered[ordered.Count - 1].Item2, (object)true });
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("DeterministicFrameScheduler: 恢复选择失败", ex);
                return false;
            }
        }

        // ================================================================
        // 启动前校验 / 官方 playback 生命周期
        // ================================================================

        private static string ValidateStartConditions(bool allowExpectedTerminalControllerFail = false)
        {
            if (!EditorGameReflection.SchedulerApiAvailable)
            {
                return "game-api-unavailable";
            }
            if (!EditorGameReflection.ForcedClockApiAvailable)
            {
                return "forced-clock-api-unavailable";
            }
            if (!EditorGameReflection.IsProbablyEditorNow())
            {
                return "not-in-editor";
            }

            if (!allowExpectedTerminalControllerFail &&
                EditorGameReflection.IsFailureState(EditorGameReflection.ReadControllerState()))
            {
                return "controller-fail-state";
            }

            object editor = EditorGameReflection.Editor();
            if (editor == null)
            {
                return "editor-null";
            }
            if (!EditorGameReflection.IsLevelLoaded())
            {
                return "level-not-loaded";
            }
            if (EditorGameReflection.ReadEditorPlayMode() == true)
            {
                return "editor-already-playing";
            }

            // 这里**刻意不检查** controller.paused。paused 是 runtime precondition，不是启动前
            // 前提：静态 IL 已确认 `scrController.Awake` 执行 `paused = ADOBase.isLevelEditor`，
            // 因此编辑器 idle（以及 Esc 返回编辑模式后）`paused == true` 是**正常状态**；
            // 唯一会把它清零的是 Renderist 自己调用的官方 editor.Play() → scnGame.Play()
            // （IL: `ldc.i4.0; call set_paused`），即 Play 之后才可能满足。
            // 启动前检查它会必然误杀正常导出（0.3.6.0 的 `controller-paused` 启动拒绝即此原因），
            // 而且与紧随其后的 editor-already-playing（编辑器内 playMode == !paused）互相矛盾。
            // 正确的判定点是 playback readiness（生命周期真正到达 PlayerControl 之后），
            // 见 TickInitializationHold 的 TryDetectAbnormalPlaybackPause。
            // Renderist 在任何阶段都不写 paused、不调用 TogglePauseGame。

            if (EditorGameReflection.ReadRdcAuto() == null)
            {
                return "rdc-auto-unavailable";
            }
            return null;
        }

        private static bool TrySelectFloor0(out string error)
        {
            error = null;
            try
            {
                object editor = EditorGameReflection.Editor();
                if (editor == null)
                {
                    error = "editor-null";
                    return false;
                }

                System.Collections.IList floors = EditorGameReflection.ReadFloorsList();
                if (floors == null || floors.Count < 1)
                {
                    error = "floors-empty";
                    return false;
                }
                object floor0 = floors[0];

                MethodInfo mSelect = EditorGameReflection.EditorSelectFloorMethod;
                if (mSelect == null)
                {
                    error = "SelectFloor-missing";
                    return false;
                }

                Log.Info(UiText.LogSchedulerPlaybackRequested);

                // 从这一步起我们拥有该编辑器选择 / 播放状态：失败也必须恢复。
                _ownsPlayback = true;

                mSelect.Invoke(editor, new object[] { floor0, false });
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Exception("DeterministicFrameScheduler: SelectFloor(floor0) 失败", ex);
                return false;
            }
        }

        private static bool TryPlay(out string error)
        {
            error = null;
            try
            {
                object editor = EditorGameReflection.Editor();
                if (editor == null)
                {
                    error = "editor-null";
                    return false;
                }

                MethodInfo mPlay = EditorGameReflection.EditorPlayMethod;
                if (mPlay == null)
                {
                    error = "Play-missing";
                    return false;
                }

                mPlay.Invoke(editor, null);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Exception("DeterministicFrameScheduler: 启动官方 Editor Play 失败", ex);
                return false;
            }
        }

        private static bool IsPlaybackReady(object state)
        {
            if (_handoff == null) return false;

            object controller = EditorGameReflection.Controller();
            object player = controller == null ? null : ReadInstanceMember(controller, "playerOne");
            bool playerAlive = ReadBool(ReadInstanceMember(player, "alive"));
            // paused 读取失败不得当作 false 放行（否则会在无法证明未暂停的情况下开始导出）。
            bool? pausedState = EditorGameReflection.ReadControllerPaused();
            if (!pausedState.HasValue) return false;

            return _handoff.IsReady(state, playerAlive, pausedState.Value);
        }

        /// <summary>
        /// 判定"播放生命周期已到达 PlayerControl，但 paused 状态异常"。
        ///
        /// 判定阶段必须正确：editor idle（以及 Esc 返回编辑模式后）`controller.paused == true`
        /// 是正常状态，只有官方 editor.Play() → scnGame.Play() 才会把它清零；因此启动前
        /// 不能据此拒绝（0.3.6.0 的 `controller-paused` 启动拒绝就是阶段错误）。
        /// 到达 PlayerControl + playerAlive 之后仍未清零（或不可读）才是真正的异常。
        /// Renderist 不写 paused、不调用 TogglePauseGame。
        /// </summary>
        private static bool TryDetectAbnormalPlaybackPause(object state, out string reason)
        {
            reason = null;
            if (_handoff == null) return false;

            object controller = EditorGameReflection.Controller();
            object player = controller == null ? null : ReadInstanceMember(controller, "playerOne");
            bool playerAlive = ReadBool(ReadInstanceMember(player, "alive"));
            if (!_handoff.IsReadyExceptPaused(state, playerAlive))
                return false;   // 生命周期尚未到达可判定阶段：交给 readiness / watchdog 处理

            bool? paused = EditorGameReflection.ReadControllerPaused();
            if (!paused.HasValue)
            {
                reason = "controller-paused-state-unavailable-during-playback";
                return true;
            }
            if (paused.Value)
            {
                reason = "controller-paused-during-playback";
                return true;
            }
            return false;
        }

        private static string CheckEarlyTermination()
        {
            if (!EditorGameReflection.IsProbablyEditorNow())
            {
                return "left-editor";
            }
            bool? playMode = EditorGameReflection.ReadEditorPlayMode();
            if (_ownsPlayback && playMode.HasValue && !playMode.Value)
            {
                object controller = EditorGameReflection.Controller();
                object player = ReadInstanceMember(controller, "playerOne");
                bool playerAlive = ReadBool(ReadInstanceMember(player, "alive"));
                object state = EditorGameReflection.ReadControllerState();
                if (playerAlive && !EditorGameReflection.IsFailureState(state))
                    return "native-playback-stopped";
                return "playback-stopped-unexpectedly";
            }
            if (_ownsPlayback && !playMode.HasValue)
                return "playback-stopped-unexpectedly";
            return null;
        }

        // ================================================================
        // Harmony（Frame Begin；Forced Clock 归 EditorVisualClock）
        // ================================================================

        /// <summary>
        /// 观察当前 ADOFAI 版本的原生 completion entry point。该 Patch 只记录
        /// native OnLandOnPortal 已经执行，不改变其参数、返回值或执行顺序。
        /// </summary>
        private static bool RegisterCanonicalCompletionHook()
        {
            if (!UnregisterCanonicalCompletionHook())
                return false;

            try
            {
                Harmony harmony = ModEntry.Harmony;
                MethodInfo onLandOnPortal = EditorGameReflection.ControllerOnLandOnPortalMethod;
                if (harmony == null || onLandOnPortal == null)
                {
                    Log.Warn("MasterTimeline canonical completion hook unavailable: OnLandOnPortal signature not found");
                    return false;
                }

                _patchedControllerOnLandOnPortal = onLandOnPortal;
                _controllerOnLandOnPortalPatched = true;
                harmony.Patch(onLandOnPortal,
                    postfix: new HarmonyMethod(typeof(DeterministicFrameScheduler), nameof(OnLandOnPortalPostfix)));
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("DeterministicFrameScheduler: 注册 canonical completion hook 失败", ex);
                UnregisterCanonicalCompletionHook();
                return false;
            }
        }

        private static bool UnregisterCanonicalCompletionHook()
        {
            Harmony harmony = ModEntry.Harmony;
            if (_patchedControllerOnLandOnPortal == null)
                return true;
            if (harmony == null)
                return false;
            if (!_controllerOnLandOnPortalPatched)
            {
                _patchedControllerOnLandOnPortal = null;
                return true;
            }

            try
            {
                MethodInfo postfix = AccessTools.Method(typeof(DeterministicFrameScheduler), nameof(OnLandOnPortalPostfix));
                if (postfix == null)
                    return false;
                harmony.Unpatch(_patchedControllerOnLandOnPortal, postfix);
                _controllerOnLandOnPortalPatched = false;
                _patchedControllerOnLandOnPortal = null;
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("DeterministicFrameScheduler: 撤销 canonical completion hook 失败", ex);
                return false;
            }
        }

        private static void OnLandOnPortalPostfix(object __instance)
        {
            OnCanonicalCompletionRequested(__instance);
        }

        // ================================================================
        // PreEntry lifecycle boundary hooks（0.3.6.3）。
        // ================================================================
        //
        // 目的：让 Countdown_Update **单独**看到满足进入 PlayerControl 的 beat 条件，而
        // visual clock 仍停在 previousBoundaryTime，从而避免任何 stateful visual 组件
        // （TrailRenderer 等）看到“未来”chart time。
        //
        // patch 在 Start（playback 之前）安装，覆盖整个 session；但行为在 boundary phase
        // 之外完全惰性（Prefix/Postfix 首行即返回），不改变正式渲染流程。

        private static bool RegisterPreEntryLifecycleBridgeHook()
        {
            if (!UnregisterPreEntryLifecycleBridgeHook())
                return false;

            try
            {
                Harmony harmony = ModEntry.Harmony;
                MethodInfo countdownUpdate = EditorGameReflection.ControllerCountdownUpdateMethod;
                if (harmony == null || countdownUpdate == null)
                {
                    Log.Warn("PreEntry lifecycle bridge unavailable: countdownUpdate=" +
                             (countdownUpdate == null ? "null" : "ok"));
                    return false;
                }

                MethodInfo countdownPrefix = AccessTools.Method(typeof(DeterministicFrameScheduler),
                    nameof(CountdownUpdatePrefix));
                MethodInfo countdownPostfix = AccessTools.Method(typeof(DeterministicFrameScheduler),
                    nameof(CountdownUpdatePostfix));
                MethodInfo countdownFinalizer = AccessTools.Method(typeof(DeterministicFrameScheduler),
                    nameof(CountdownUpdateFinalizer));
                if (countdownPrefix == null || countdownPostfix == null || countdownFinalizer == null)
                {
                    Log.Warn("PreEntry lifecycle bridge hook bodies missing");
                    return false;
                }

                _patchedControllerCountdownUpdate = countdownUpdate;
                _countdownUpdatePrefixPatched = true;
                harmony.Patch(countdownUpdate, prefix: new HarmonyMethod(countdownPrefix));

                _countdownUpdatePostfixPatched = true;
                harmony.Patch(countdownUpdate, postfix: new HarmonyMethod(countdownPostfix));

                _countdownUpdateFinalizerPatched = true;
                harmony.Patch(countdownUpdate, finalizer: new HarmonyMethod(countdownFinalizer));

                Log.Info("PreEntry lifecycle bridge installed: " +
                         "scrController.Countdown_Update(prefix+postfix+finalizer)");
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("PreEntry: 注册 lifecycle boundary hooks 失败", ex);
                UnregisterPreEntryLifecycleBridgeHook();
                return false;
            }
        }

        private static bool UnregisterPreEntryLifecycleBridgeHook()
        {
            Harmony harmony = ModEntry.Harmony;
            bool success = true;

            if (_patchedControllerCountdownUpdate != null && harmony != null)
            {
                try
                {
                    if (_countdownUpdatePrefixPatched)
                    {
                        MethodInfo prefix = AccessTools.Method(typeof(DeterministicFrameScheduler),
                            nameof(CountdownUpdatePrefix));
                        if (prefix != null) harmony.Unpatch(_patchedControllerCountdownUpdate, prefix);
                        _countdownUpdatePrefixPatched = false;
                    }
                    if (_countdownUpdatePostfixPatched)
                    {
                        MethodInfo postfix = AccessTools.Method(typeof(DeterministicFrameScheduler),
                            nameof(CountdownUpdatePostfix));
                        if (postfix != null) harmony.Unpatch(_patchedControllerCountdownUpdate, postfix);
                        _countdownUpdatePostfixPatched = false;
                    }
                    if (_countdownUpdateFinalizerPatched)
                    {
                        MethodInfo finalizer = AccessTools.Method(typeof(DeterministicFrameScheduler),
                            nameof(CountdownUpdateFinalizer));
                        if (finalizer != null) harmony.Unpatch(_patchedControllerCountdownUpdate, finalizer);
                        _countdownUpdateFinalizerPatched = false;
                    }
                    if (!_countdownUpdatePrefixPatched && !_countdownUpdatePostfixPatched &&
                        !_countdownUpdateFinalizerPatched)
                        _patchedControllerCountdownUpdate = null;
                }
                catch (Exception ex)
                {
                    Log.Exception("PreEntry: 撤销 Countdown_Update hook 失败", ex);
                    success = false;
                }
            }

            return success && _patchedControllerCountdownUpdate == null;
        }

        // ================================================================
        // native editor playback teardown observer
        // ================================================================
        //
        // 当前 DLL 的 Esc 路径是：
        //   scnEditor.Update
        //     → if (Input.GetKeyDown(Escape) && playMode) { SwitchToEditMode(false); return; }
        // 而 scnEditor.playMode 是**只读派生属性**，语义为
        //   pausedInPlayMode ? true : !controller.paused
        // 也就是说它并不表示「是否在播放」。旧实现只用它轮询判断 native playback 是否
        // 停止；一旦暂停侧效应不再发生（例如 scrController.TogglePauseGame 被 Input Guard
        // 抑制，而 scnGame.ResetScene 正是通过它把编辑器切回 paused），playMode 就恒为
        // true，轮询再也命中不了，只能等 30 秒 frame-progress watchdog。
        //
        // 因此改为观察 exact native lifecycle 入口 scnEditor.SwitchToEditMode(bool)。
        // 该 Postfix 只做观察与 RequestStop，不执行 cleanup、不改参数、不阻断 native 方法。

        private static bool RegisterNativePlaybackStopObserver()
        {
            if (!UnregisterNativePlaybackStopObserver())
                return false;

            try
            {
                Harmony harmony = ModEntry.Harmony;
                MethodInfo switchToEditMode = EditorGameReflection.EditorSwitchToEditModeMethod;
                if (harmony == null || switchToEditMode == null)
                {
                    Log.Warn("MasterTimeline native playback stop observer unavailable: " +
                             "SwitchToEditMode(bool) signature not found");
                    return false;
                }

                _patchedEditorSwitchToEditMode = switchToEditMode;
                _editorSwitchToEditModePatched = true;
                harmony.Patch(switchToEditMode,
                    postfix: new HarmonyMethod(typeof(DeterministicFrameScheduler),
                        nameof(OnEditorSwitchToEditModePostfix)));
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("DeterministicFrameScheduler: 注册 native playback stop observer 失败", ex);
                UnregisterNativePlaybackStopObserver();
                return false;
            }
        }

        private static bool UnregisterNativePlaybackStopObserver()
        {
            Harmony harmony = ModEntry.Harmony;
            if (_patchedEditorSwitchToEditMode == null)
                return true;
            if (harmony == null)
                return false;
            if (!_editorSwitchToEditModePatched)
            {
                _patchedEditorSwitchToEditMode = null;
                return true;
            }

            try
            {
                MethodInfo postfix = AccessTools.Method(typeof(DeterministicFrameScheduler),
                    nameof(OnEditorSwitchToEditModePostfix));
                if (postfix == null)
                    return false;
                harmony.Unpatch(_patchedEditorSwitchToEditMode, postfix);
                _editorSwitchToEditModePatched = false;
                _patchedEditorSwitchToEditMode = null;
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("DeterministicFrameScheduler: 撤销 native playback stop observer 失败", ex);
                return false;
            }
        }

        /// <summary>
        /// scnEditor.SwitchToEditMode(bool) 的 Postfix。只作为 native lifecycle observer：
        /// 记录一次现场日志并请求统一停止；真正 cleanup 由下一次 Tick 的 ProcessStop 执行。
        ///
        /// scope gate 必须能排除 Renderist 自己的 cleanup 调用（RestorePlayback →
        /// SwitchToEditMode(false)）：ProcessStop 在 RestoreAll 之前已置 _running = false，
        /// 且 RestoreAll 全程 _cleanupInProgress = true；两者任一都足以在此立即 return。
        /// 这里不使用任何「忽略下一次调用」式的脆弱全局 flag。
        /// </summary>
        private static void OnEditorSwitchToEditModePostfix()
        {
            if (!_running || !_ownsPlayback || _cleanupInProgress ||
                _pendingStopReason != null)
                return;

            // 只在本次 session 已经进入 native playback 生命周期之后才视为停止信号。
            if (_status != SchedulerStatus.InitializationHold &&
                _status != SchedulerStatus.Capturing)
                return;

            Log.Info("MasterTimeline native editor SwitchToEditMode observed: status=" +
                     _status.ToString() +
                     " playMode=" + FormatFlag(EditorGameReflection.ReadEditorPlayMode()) +
                     " inStrictlyEditingMode=" +
                     FormatFlag(EditorGameReflection.ReadEditorInStrictlyEditingMode()) +
                     " controllerState=" + ToState(EditorGameReflection.ReadControllerState()) +
                     " conductorActive=" +
                     FormatFlag(EditorGameReflection.ReadConductorActiveInHierarchy()) +
                     " paused=" + FormatFlag(EditorGameReflection.ReadControllerPaused()) +
                     " outputFrameIndex=" + _outputFrameIndex.ToString(CultureInfo.InvariantCulture));

            RequestStop("native-playback-stopped", "native-playback-stopped");
        }

        private static bool RegisterConductorUpdateHook()
        {
            if (!UnregisterConductorUpdateHook())
                return false;

            try
            {
                Harmony harmony = ModEntry.Harmony;
                MethodInfo update = EditorGameReflection.ConductorUpdateMethod;
                if (harmony == null || update == null) return false;

                _patchedConductorUpdate = update;
                _conductorPrefixPatched = true;
                harmony.Patch(update,
                    prefix: new HarmonyMethod(typeof(DeterministicFrameScheduler), nameof(ConductorUpdatePrefix)));

                try
                {
                    _conductorPostfixPatched = true;
                    harmony.Patch(update,
                        postfix: new HarmonyMethod(typeof(DeterministicFrameScheduler), nameof(ConductorUpdatePostfix)));
                }
                catch (Exception ex)
                {
                    Log.Exception("DeterministicFrameScheduler: 注册 conductor.Update Postfix 失败，撤销已注册的 Prefix", ex);
                    UnregisterConductorUpdateHook();
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("DeterministicFrameScheduler: 注册 conductor.Update Patch 失败", ex);
                UnregisterConductorUpdateHook();
                return false;
            }
        }

        private static bool UnregisterConductorUpdateHook()
        {
            Harmony harmony = ModEntry.Harmony;
            if (_patchedConductorUpdate == null)
                return true;
            if (harmony == null)
                return false;

            bool success = true;
            if (_conductorPrefixPatched)
            {
                try
                {
                    MethodInfo prefix = AccessTools.Method(typeof(DeterministicFrameScheduler), nameof(ConductorUpdatePrefix));
                    if (prefix == null) success = false;
                    else
                    {
                        harmony.Unpatch(_patchedConductorUpdate, prefix);
                        _conductorPrefixPatched = false;
                    }
                }
                catch (Exception ex)
                {
                    success = false;
                    Log.Exception("DeterministicFrameScheduler: 撤销 conductor.Update Prefix 失败", ex);
                }
            }
            if (_conductorPostfixPatched)
            {
                try
                {
                    MethodInfo postfix = AccessTools.Method(typeof(DeterministicFrameScheduler), nameof(ConductorUpdatePostfix));
                    if (postfix == null) success = false;
                    else
                    {
                        harmony.Unpatch(_patchedConductorUpdate, postfix);
                        _conductorPostfixPatched = false;
                    }
                }
                catch (Exception ex)
                {
                    success = false;
                    Log.Exception("DeterministicFrameScheduler: 撤销 conductor.Update Postfix 失败", ex);
                }
            }
            if (!_conductorPrefixPatched && !_conductorPostfixPatched)
                _patchedConductorUpdate = null;
            return success && _patchedConductorUpdate == null;
        }

        /// <summary>
        /// 确定性帧期间阻止 ADOFAI 的异步 tick 角度刷新覆盖 native Conductor
        /// 已经根据 forced songposition 写入的 Planet 角度。生命周期等待期间
        /// 不注册为 active，故官方 Countdown 仍完全使用原生路径。
        /// </summary>
        private static bool RegisterAsyncInputAngleHook()
        {
            if (!UnregisterAsyncInputAngleHook())
                return false;

            try
            {
                Harmony harmony = ModEntry.Harmony;
                MethodInfo adjustAngle = EditorGameReflection.AsyncInputAdjustAngleMethod;
                if (harmony == null || adjustAngle == null)
                {
                    Log.Warn("MasterTimeline AsyncInput angle hook unavailable: AdjustAngle signature not found");
                    return false;
                }

                _patchedAsyncInputAdjustAngle = adjustAngle;
                _asyncInputAdjustAnglePatched = true;
                harmony.Patch(adjustAngle,
                    prefix: new HarmonyMethod(typeof(DeterministicFrameScheduler), nameof(AsyncInputAdjustAnglePrefix)));
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("DeterministicFrameScheduler: 注册 AsyncInput angle hook 失败", ex);
                UnregisterAsyncInputAngleHook();
                return false;
            }
        }

        private static bool UnregisterAsyncInputAngleHook()
        {
            Harmony harmony = ModEntry.Harmony;
            if (_patchedAsyncInputAdjustAngle == null)
                return true;
            if (harmony == null)
                return false;
            if (!_asyncInputAdjustAnglePatched)
            {
                _patchedAsyncInputAdjustAngle = null;
                return true;
            }
            try
            {
                MethodInfo prefix = AccessTools.Method(typeof(DeterministicFrameScheduler), nameof(AsyncInputAdjustAnglePrefix));
                if (prefix == null)
                    return false;
                harmony.Unpatch(_patchedAsyncInputAdjustAngle, prefix);
                _asyncInputAdjustAnglePatched = false;
                _patchedAsyncInputAdjustAngle = null;
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("DeterministicFrameScheduler: 撤销 AsyncInput angle hook 失败", ex);
                return false;
            }
        }

        private static bool AsyncInputAdjustAnglePrefix(object __0, ulong __1)
        {
            if (!_running || _pendingStopReason != null || _status != SchedulerStatus.Capturing || !_clockActive)
                return true;

            object controller = EditorGameReflection.Controller();
            object currentPlayer = ReadInstanceMember(controller, "playerOne");
            return currentPlayer == null || !ReferenceEquals(__0, currentPlayer);
        }

        // ================================================================
        // helpers
        // ================================================================

        private static object ReadInstanceMember(object instance, string name)
        {
            if (instance == null) return null;
            try
            {
                Type type = instance.GetType();
                PropertyInfo property = type.GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null) return property.GetValue(instance, null);
                FieldInfo field = type.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return field?.GetValue(instance);
            }
            catch { return null; }
        }

        private static object ReadStaticMember(Type type, string name)
        {
            if (type == null) return null;
            try
            {
                PropertyInfo property = type.GetProperty(name,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null) return property.GetValue(null, null);
                FieldInfo field = type.GetField(name,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                return field?.GetValue(null);
            }
            catch { return null; }
        }

        private static bool ReadBool(object value)
        {
            if (value == null) return false;
            try { return Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
            catch { return false; }
        }

        // ================================================================
        // PreEntry helpers
        // ================================================================

        /// <summary>
        /// 将原生 songposition 公式在 dspTime == dspTimeSong 的原点固定下来：
        /// -(calibration_i * pitch) - addoffset。这个值不依赖本次 callback 到达的
        /// wall time，且与 scrConductor.Update 的当前公式一致。若 Countdown 已跨越第
        /// 一个 beat，则拒绝锁定，避免把已推进的 native state 倒回去。
        /// </summary>
        private static bool TryLatchPreEntryClock(out string detail)
        {
            detail = "lifecycle-incomplete";
            if (_handoff == null || !_handoff.PlayRequested || !_handoff.PlayReturned ||
                !_handoff.SawStart || !_handoff.SawMusicScheduled || !_handoff.SawCountdown)
                return false;

            if (_outputFps <= 0 || _pitchUnavailable || _pitch < MinPitch)
            {
                detail = "output-fps-or-pitch-unavailable";
                return false;
            }

            object conductor = EditorGameReflection.Conductor();
            if (conductor == null)
            {
                detail = "conductor-unavailable";
                return false;
            }

            double? calibrationI = ToDouble(ReadStaticMember(conductor.GetType(), "calibration_i"));
            double? addOffset = ToDouble(ReadInstanceMember(conductor, "addoffset"));
            double? beatNumber = ToDouble(ReadInstanceMember(conductor, "beatNumber"));
            object rawSongPosition = ReadUnforcedSongPositionValue();
            double? rawSongPositionNumber = ToDouble(rawSongPosition);
            if (!calibrationI.HasValue || !addOffset.HasValue || !beatNumber.HasValue ||
                !rawSongPositionNumber.HasValue)
            {
                detail = "native-clock-fields-unavailable";
                return false;
            }

            if (beatNumber.Value > 0.0 || rawSongPositionNumber.Value >= 0.0)
            {
                detail = "countdown-latch-too-late:beatNumber=" +
                         beatNumber.Value.ToString("0.######", CultureInfo.InvariantCulture) +
                         ",rawSongposition=" +
                         rawSongPositionNumber.Value.ToString("0.######", CultureInfo.InvariantCulture);
                return false;
            }

            double anchor = -(calibrationI.Value * _pitch) - addOffset.Value;
            double step = _pitch / _outputFps;
            if (double.IsNaN(anchor) || double.IsInfinity(anchor) ||
                Math.Abs(anchor) > AnchorInvalidThresholdSeconds ||
                double.IsNaN(step) || double.IsInfinity(step) || step <= 0.0)
            {
                detail = "preentry-anchor-or-step-invalid";
                return false;
            }

            // deterministic 边界：B = 第一个 forced time >= canonicalStart 的绝对 output frame。
            // frame [0, B-1] 是 deterministic pre-entry；frame B 起必须交给 GameplayCapture，
            // chart time 才会正好落在 canonicalStart（floor entry time 与 autoplay 的对齐基准）。
            if (!EditorGameReflection.TryReadGameplayStartOffset(
                    out double boundaryOffset, out string boundaryError))
            {
                detail = "preentry-boundary-unavailable:" + (boundaryError ?? "unknown");
                return false;
            }
            double boundaryStart = _floor0EntryTime + boundaryOffset;
            double boundaryFramesExact = (boundaryStart - anchor) / step;
            if (double.IsNaN(boundaryStart) || double.IsInfinity(boundaryStart) ||
                Math.Abs(boundaryStart) > AnchorInvalidThresholdSeconds ||
                double.IsNaN(boundaryFramesExact) || double.IsInfinity(boundaryFramesExact) ||
                boundaryFramesExact > 9.0e15 || boundaryFramesExact < -9.0e15)
            {
                detail = "preentry-boundary-invalid";
                return false;
            }
            // near-integer 数值稳定化：x 在数学上恰好落在 grid point 时，FP 可能算出 x = m ± few ULP；
            // 落在 m 上方会让 ceil 多出一帧，而该帧 chart time 与 gameplay frame 0 相同。这里只在
            // 「half-ULP 量级」与「一帧的千分之一」的较小者内吸附，数学语义仍是 B = ceil(x)；
            // 吸附后果由下面的 grid bracket invariant 兜底（吸错 → bracket 不成立 → fail-closed）。
            double snapTolerance = Math.Min(
                PreEntryBoundaryHalfUlp(boundaryFramesExact) * 8.0,
                PreEntryBoundaryToleranceStepFraction);
            double nearestGridFrames = Math.Round(boundaryFramesExact);
            double stableFramesExact = Math.Abs(boundaryFramesExact - nearestGridFrames) <= snapTolerance
                ? nearestGridFrames
                : boundaryFramesExact;
            long boundaryFrameIndex = stableFramesExact <= 0.0
                ? 0L
                : (long)Math.Ceiling(stableFramesExact);

            // deterministic grid bracket invariant（TEMP probe）：
            // previousBoundaryTime < canonicalStart <= boundaryForcedTime。
            // 只吸收 floating-point 表示/运算误差（ULP based，并封顶在 step 的千分之一），
            // 不使用固定秒数窗口：step = pitch / OutputFps 在合法参数下可以从远大于 0.05 s
            // （1 FPS → step = 1 s）一直小到远小于 1e-9 s，任何绝对窗口或绝对下限都会在
            // 某一端失去正确性，并对 Output FPS / pitch 形成隐式限制。
            double boundaryForcedTime = anchor + boundaryFrameIndex * step;
            double previousBoundaryTime = anchor + (boundaryFrameIndex - 1) * step;
            double bracketTolerance = PreEntryBoundaryNumericalTolerance(boundaryStart, boundaryForcedTime, step);
            if (!(previousBoundaryTime < boundaryStart - bracketTolerance) ||
                !(boundaryStart <= boundaryForcedTime + bracketTolerance))
            {
                detail = "preentry-grid-bracket-invalid:previousBoundaryTime=" +
                         previousBoundaryTime.ToString("0.######", CultureInfo.InvariantCulture) +
                         ",canonicalStart=" + boundaryStart.ToString("0.######", CultureInfo.InvariantCulture) +
                         ",boundaryForcedTime=" +
                         boundaryForcedTime.ToString("0.######", CultureInfo.InvariantCulture) +
                         ",step=" + step.ToString("0.######", CultureInfo.InvariantCulture);
                return false;
            }

            // lifecycle-only boundary probe 需要的注入值：满足 native
            // `beatNumber >= adjustedCountdownTicks` 的**最小**整数，不取更大经验值。
            double? adjustedCountdownTicks = ToDouble(ReadInstanceMember(conductor, "adjustedCountdownTicks"));
            if (!adjustedCountdownTicks.HasValue ||
                double.IsNaN(adjustedCountdownTicks.Value) || double.IsInfinity(adjustedCountdownTicks.Value) ||
                adjustedCountdownTicks.Value > int.MaxValue - 1.0)
            {
                detail = "countdown-threshold-unavailable";
                return false;
            }
            int injectedBeatNumber = (int)Math.Ceiling(adjustedCountdownTicks.Value);

            _preEntryAnchor = anchor;
            _preEntryStep = step;
            _preEntryBoundaryOutputFrameIndex = boundaryFrameIndex;
            _preEntryBoundaryForcedTime = boundaryForcedTime;
            _preEntryBoundaryPreviousTime = previousBoundaryTime;
            _preEntryBoundaryCanonicalStart = boundaryStart;
            _preEntryInjectedBeatNumber = injectedBeatNumber;
            _preEntryFrameIndex = 0L;
            _forcedSongPosition = anchor;
            EditorVisualClock.SetForcedSongPosition(anchor);
            EditorVisualClock.SetActive(true);
            _preEntryClockLatched = true;

            detail = null;
            Log.Info("PreEntry clock latched: " +
                     "rawSongpositionBeforeForce=" + ToValue(rawSongPosition) +
                     " beatNumberBeforeForce=" + ToValue(beatNumber) +
                     " calibrationI=" + ToValue(calibrationI) +
                     " addoffset=" + ToValue(addOffset) +
                     " anchor=" + anchor.ToString("0.######", CultureInfo.InvariantCulture) +
                     " step=" + step.ToString("0.######", CultureInfo.InvariantCulture) +
                     " boundaryOutputFrameIndex=" + boundaryFrameIndex.ToString(CultureInfo.InvariantCulture) +
                     " previousBoundaryTime=" +
                     previousBoundaryTime.ToString("0.######", CultureInfo.InvariantCulture) +
                     " boundaryForcedTime=" +
                     boundaryForcedTime.ToString("0.######", CultureInfo.InvariantCulture) +
                     " canonicalStart=" + boundaryStart.ToString("0.######", CultureInfo.InvariantCulture) +
                     " adjustedCountdownTicks=" + ToValue(adjustedCountdownTicks) +
                     " injectedBeatNumber=" + injectedBeatNumber.ToString(CultureInfo.InvariantCulture) +
                     " sourceWaiting=true ");
            return true;
        }

        // ================================================================
        // PreEntry lifecycle boundary：Countdown_Update beat 注入 + 顺序取证
        // ================================================================

        /// <summary>
        /// scrController.Countdown_Update 的 Harmony Prefix。
        ///
        /// 只在 lifecycle-only boundary phase 生效：保存 conductor.beatNumber 原值，然后在
        /// 本方法作用域内临时注入满足 native `beatNumber >= adjustedCountdownTicks` 的最小值，
        /// 让 Countdown_Update 自己判定“该进入 PlayerControl”。visual clock、songposition、
        /// nextBeatTime、Planet angle、Trail、currentFloor 与 outputFrameIndex 都不修改。
        ///
        /// 资源生命周期采用静态保存（Unity 主线程单线程调用同一次 Prefix/Postfix 配对），
        /// 不为异常恢复引入额外基础设施；beatNumber 每帧都会被 native conductor 重算，
        /// 即使恢复失败也不会残留到下一帧。
        /// </summary>
        private static void CountdownUpdatePrefix()
        {
            if (!ShouldRunLifecycleBeatInjection()) return;

            object conductor = EditorGameReflection.Conductor();
            if (conductor == null)
            {
                RequestStop("preentry-beat-injection-failed", "conductor-unavailable");
                return;
            }

            FieldInfo beatField = conductor.GetType().GetField("beatNumber",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (beatField == null || beatField.FieldType != typeof(int))
            {
                RequestStop("preentry-beat-injection-failed", "beat-number-field-unavailable");
                return;
            }

            int originalBeat;
            try { originalBeat = Convert.ToInt32(beatField.GetValue(conductor), CultureInfo.InvariantCulture); }
            catch (Exception ex)
            {
                RequestStop("preentry-beat-injection-failed", "beat-number-read-failed:" + ex.Message);
                return;
            }

            // 注入值现场读取并校验（finite / > 0 / ceil 后落在 Int32 范围），
            // 不复用 latch 时的规划值。
            double? ticks = ToDouble(ReadInstanceMember(conductor, "adjustedCountdownTicks"));
            if (!ticks.HasValue || double.IsNaN(ticks.Value) || double.IsInfinity(ticks.Value) ||
                ticks.Value <= 0.0 || ticks.Value > int.MaxValue - 1.0)
            {
                RequestStop("preentry-beat-injection-failed",
                    "adjusted-countdown-ticks-invalid:" + ToValue(ticks));
                return;
            }

            int injectedBeat = (int)Math.Ceiling(ticks.Value);
            if (injectedBeat <= originalBeat)
                return; // 已经满足 native 阈值，无需注入

            // 建立 ownership：保存 exact conductor instance / exact FieldInfo / 原始 beat。
            _preEntryBeatOverrideConductor = conductor;
            _preEntryBeatOverrideField = beatField;
            _preEntryBeatOverrideOriginalBeat = originalBeat;
            _preEntryBeatOverrideOwned = true;
            _preEntryInjectedBeatNumber = injectedBeat;

            try { beatField.SetValue(conductor, injectedBeat); }
            catch (Exception ex)
            {
                Log.Exception("PreEntry lifecycle bridge: beat 注入失败", ex);
                RequestStop("preentry-beat-injection-failed", "countdown-beat-injection-failed");
            }
        }

        /// <summary>
        /// scrController.Countdown_Update 的 Harmony Postfix。使用 Prefix 保存的**同一个**
        /// conductor instance 与**同一个** FieldInfo 立即恢复 original beat；绝不重新全局查找。
        /// </summary>
        private static void CountdownUpdatePostfix()
        {
            if (!_preEntryBeatOverrideOwned) return;
            if (!TryRestorePreEntryBeatOverride(out string restoreError))
                RequestStop("preentry-beat-restore-failed", restoreError ?? "beat-override-restore-failed");
        }

        /// <summary>
        /// Harmony Finalizer：original method 抛异常时 Postfix 不会执行（Harmony 语义），
        /// 因此这里只调用与 Postfix 完全相同的恢复 helper，保证 scoped beat override
        /// 绝不逃出本次 Countdown_Update 调用。已恢复时 no-op；不吞掉原始异常
        /// （不声明 Exception 参数，异常照常向外传播）。
        /// </summary>
        private static void CountdownUpdateFinalizer()
        {
            if (!_preEntryBeatOverrideOwned) return;
            if (!TryRestorePreEntryBeatOverride(out string restoreError))
            {
                Log.Warn("PreEntry lifecycle bridge: beat override 异常路径恢复失败: " +
                         (restoreError ?? "unknown"));
                RequestStop("preentry-beat-restore-failed", restoreError ?? "beat-override-restore-failed");
            }
        }

        /// <summary>
        /// beat override 的唯一共享恢复入口（Postfix / Finalizer / cleanup 共用）。
        /// 只有写入并读回验证成功后才清空 ownership 状态；失败保留 ownership，
        /// 由 cleanup failure 与 residual ownership gate 处理。
        /// </summary>
        private static bool TryRestorePreEntryBeatOverride(out string error)
        {
            error = null;
            if (!_preEntryBeatOverrideOwned) return true;

            object conductor = _preEntryBeatOverrideConductor;
            FieldInfo beatField = _preEntryBeatOverrideField;
            if (conductor == null || beatField == null)
            {
                error = "beat-override-ownership-incomplete";
                return false;
            }

            int originalBeat = _preEntryBeatOverrideOriginalBeat;
            try { beatField.SetValue(conductor, originalBeat); }
            catch (Exception ex)
            {
                error = "beat-override-restore-write-failed:" + ex.Message;
                return false;
            }

            try
            {
                int observed = Convert.ToInt32(beatField.GetValue(conductor), CultureInfo.InvariantCulture);
                if (observed != originalBeat)
                {
                    error = "beat-override-restore-readback-mismatch:expected=" +
                            originalBeat.ToString(CultureInfo.InvariantCulture) +
                            ",observed=" + observed.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = "beat-override-restore-verify-failed:" + ex.Message;
                return false;
            }

            _preEntryBeatOverrideOwned = false;
            _preEntryBeatOverrideConductor = null;
            _preEntryBeatOverrideField = null;
            _preEntryBeatOverrideOriginalBeat = 0;
            return true;
        }

        /// <summary>
        /// 注入条件（严格最小）：scheduler 运行中、PreEntry 已 latch、处于 lifecycle-only
        /// boundary phase、command output index 仍是 B、当前 controller state 仍是 Countdown、
        /// 且 lifecycle 尚未观测到 PlayerControl。
        /// </summary>
        private static bool ShouldRunLifecycleBeatInjection()
        {
            if (!_running || _pendingStopReason != null)
                return false;
            if (!_preEntryClockLatched || !_preEntryLifecycleInjectionArmed) return false;
            if (_preEntryBoundaryOutputFrameIndex < 0) return false;
            if (_outputFrameIndex != _preEntryBoundaryOutputFrameIndex) return false;
            if (_handoff != null && _handoff.SawPlayerControl) return false;

            string state = ToState(EditorGameReflection.ReadControllerState());
            return string.Equals(state, "Countdown", StringComparison.Ordinal);
        }

        /// <summary>
        /// 明确不是“Camera != null”即 ready：候选至少要求本次 editor.Play 已返回、
        /// Start / OnMusicScheduled / Countdown 全部被 handoff 观测，当前提交状态仍是
        /// Countdown，原生三相机皆 active/enabled、有有效 viewport，且起始 player /
        /// planet / floor 已生成。真正接管仍由 FrameCaptureDriver 的 ownership transaction
        /// 决定，任一失败立即 fail-closed。
        /// </summary>
        private static bool IsPreEntryCaptureCandidate(object state, out string detail)
        {
            detail = null;
            if (_handoff == null)
            {
                detail = "lifecycle-handoff-unavailable";
                return false;
            }
            if (!_handoff.PlayRequested || !_handoff.PlayReturned || !_handoff.SawStart ||
                !_handoff.SawMusicScheduled || !_handoff.SawCountdown)
            {
                detail = "native-countdown-lifecycle-incomplete:" + _handoff.MarkerStatus;
                return false;
            }
            if (!string.Equals(ToState(state), "Countdown", StringComparison.Ordinal))
            {
                detail = "controller-not-countdown:" + ToState(state);
                return false;
            }

            if (!EditorGameReflection.TryReadChartCameraChain(
                    out Camera bgStaticCamera, out Camera bgCamera, out Camera mainCamera,
                    out string chainError))
            {
                detail = "camera-chain-unavailable:" + (chainError ?? "unknown");
                return false;
            }
            bool bgStaticReady = IsPreEntryCameraReady(
                bgStaticCamera, "Bgcamstatic", out string bgStaticError);
            bool bgReady = IsPreEntryCameraReady(bgCamera, "BGcam", out string bgError);
            bool mainReady = IsPreEntryCameraReady(mainCamera, "camobj", out string mainError);
            if (!bgStaticReady || !bgReady || !mainReady)
            {
                detail = "camera-not-visually-ready:" + (bgStaticError ?? bgError ?? mainError ?? "unknown");
                return false;
            }

            object controller = EditorGameReflection.Controller();
            object player = ReadInstanceMember(controller, "playerOne");
            object currentFloor = ReadInstanceMember(player, "currFloor");
            object planetarySystem = ReadInstanceMember(player, "planetarySystem");
            object chosenPlanet = ReadInstanceMember(planetarySystem, "chosenPlanet");
            if (player == null || currentFloor == null || chosenPlanet == null)
            {
                detail = "native-player-visuals-incomplete:" +
                         "player=" + (player != null) +
                         ",currentFloor=" + (currentFloor != null) +
                         ",chosenPlanet=" + (chosenPlanet != null);
                return false;
            }

            detail = "state=Countdown,cameraChain=active-enabled-viewport-valid," +
                     "playerPlanetFloor=ready";
            return true;
        }

        private static bool IsPreEntryCameraReady(Camera camera, string label, out string error)
        {
            error = null;
            try
            {
                if (camera == null)
                {
                    error = label + "-destroyed";
                    return false;
                }
                if (!camera.isActiveAndEnabled || !camera.gameObject.activeInHierarchy)
                {
                    error = label + "-inactive";
                    return false;
                }
                if (camera.pixelWidth <= 0 || camera.pixelHeight <= 0)
                {
                    error = label + "-viewport-invalid:" +
                            camera.pixelWidth.ToString(CultureInfo.InvariantCulture) + "x" +
                            camera.pixelHeight.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = label + "-inspection-failed:" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// PreEntry 的数值容差：只吸收 double 表示误差与少量乘加误差（ULP based），
        /// 并硬性封顶在 step 的极小比例内。
        ///
        /// 关键点（避免隐式参数限制）：**不使用固定绝对秒数下限**。旧的 `max(1e-9, halfUlp*4)`
        /// 在 step 很小时（高 Output FPS / 小 pitch 下 step &lt; 1e-9 是合法可达的）会变成
        /// 比一帧还大的隐式时间窗口。这里容差与 step 同阶缩小：tolerance &lt;= step * 1e-3，
        /// 即永远不足一帧的千分之一，因此不可能等价于一帧 / 半帧 / 0.05 s / 0.1 s 这类
        /// 产品级时间窗口，也不会对任何 Output FPS / pitch 形成隐式合法区间。
        /// </summary>
        private static double PreEntryBoundaryNumericalTolerance(double first, double second, double step)
        {
            if (double.IsNaN(step) || double.IsInfinity(step) || step <= 0.0)
                return 0.0;

            double magnitude = Math.Max(Math.Abs(first), Math.Abs(second));
            if (double.IsNaN(magnitude) || double.IsInfinity(magnitude))
                return 0.0;

            // 纯表示/算术误差量级：anchor + B * step 这类乘加链路的累计误差远小于 8 ULP。
            double representationSlack = PreEntryBoundaryHalfUlp(magnitude) * 8.0;
            double stepCap = step * PreEntryBoundaryToleranceStepFraction;
            return Math.Min(representationSlack, stepCap);
        }

        /// <summary>相邻 double 间距的一半（表示误差量级）；非有限或 0 时返回 0。</summary>
        private static double PreEntryBoundaryHalfUlp(double value)
        {
            double magnitude = Math.Abs(value);
            if (double.IsNaN(magnitude) || double.IsInfinity(magnitude) || magnitude == 0.0)
                return 0.0;

            long bits = BitConverter.DoubleToInt64Bits(magnitude);
            double next = BitConverter.Int64BitsToDouble(bits + 1);
            double spacing = next - magnitude;
            return (!double.IsNaN(spacing) && !double.IsInfinity(spacing) && spacing > 0.0)
                ? spacing * 0.5
                : 0.0;
        }

        private static long GetGameplayFrameIndex(long outputFrameIndex)
        {
            if (_preEntryGameplayStartOutputFrameIndex >= 0)
            {
                return outputFrameIndex - _preEntryGameplayStartOutputFrameIndex;
            }
            return outputFrameIndex;
        }

        private static string DescribeFrameTimeline(long outputFrameIndex)
        {
            if (_preEntryGameplayStartOutputFrameIndex < 0 ||
                outputFrameIndex < _preEntryGameplayStartOutputFrameIndex)
            {
                return "native-preentry";
            }

            long gameplayFrameIndex = GetGameplayFrameIndex(outputFrameIndex);
            double chartTime = _canonicalStartTime +
                               gameplayFrameIndex / (double)_outputFps * _pitch;
            return "gameplayFrameIndex=" + gameplayFrameIndex.ToString(CultureInfo.InvariantCulture) +
                   ",expectedChartTime=" + chartTime.ToString("0.######", CultureInfo.InvariantCulture);
        }

        private static double? TryProjectNativeSongPosition(object conductorDspTime,
            object dspTimeSong, object calibrationI, object addOffset, double pitch)
        {
            double? currentDspTime = ToDouble(conductorDspTime);
            double? songDspTime = ToDouble(dspTimeSong);
            double? calibration = ToDouble(calibrationI);
            double? offset = ToDouble(addOffset);
            if (!currentDspTime.HasValue || !songDspTime.HasValue ||
                !calibration.HasValue || !offset.HasValue || pitch < MinPitch)
                return null;

            return ((currentDspTime.Value - songDspTime.Value - calibration.Value) * pitch) -
                   offset.Value;
        }

        // AudioModule 不是当前 csproj 的静态编译引用；probe 仅在运行时按需读取
        // UnityEngine.AudioSettings.dspTime，避免为了诊断扩大正式 DLL 依赖。
        private static object ReadRuntimeStaticMember(string typeFullName, string memberName)
        {
            try
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type type = assembly.GetType(typeFullName, false);
                    if (type == null) continue;
                    PropertyInfo property = type.GetProperty(memberName,
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (property != null) return property.GetValue(null, null);
                    FieldInfo field = type.GetField(memberName,
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    return field?.GetValue(null);
                }
            }
            catch { }
            return null;
        }

        private static void LogFrame0Stage(long frameIndex, string stage)
        {
            if (frameIndex != 0 || string.Equals(_lastFrame0Stage, stage, StringComparison.Ordinal)) return;
            _lastFrame0Stage = stage;
            Log.Info("MasterTimeline Stage=Frame0 " + stage + " frameIndex=0 " + BuildRuntimeSnapshot());
        }

        private static string ToState(object v)
        {
            if (v == null) return null;
            return v is Enum e ? e.ToString() : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static object ReadUnforcedSongPositionValue()
        {
            bool active = EditorVisualClock.IsActive;
            if (active) EditorVisualClock.SetActive(false);
            try { return EditorGameReflection.ReadConductorSongPosition(); }
            finally
            {
                if (active)
                {
                    EditorVisualClock.SetForcedSongPosition(_forcedSongPosition);
                    EditorVisualClock.SetActive(true);
                }
            }
        }

        private static double? ToDouble(object value)
        {
            if (value == null) return null;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static void LogFrameConductor(string phase)
        {
            if (_outputFrameIndex < 4)
            {
                Log.Info("MasterTimeline Conductor " + phase + ": frameIndex=" +
                         _outputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                         " expectedChartTime=" + _forcedSongPosition.ToString("0.######", CultureInfo.InvariantCulture) +
                         " songposition=" + (_prefixSongPosition ?? "null"));
            }
        }

        private static string BuildRuntimeSnapshot()
        {
            object controller = EditorGameReflection.Controller();
            object player = ReadInstanceMember(controller, "playerOne");
            object current = ReadInstanceMember(player, "currFloor");
            object next = ReadInstanceMember(current, "nextfloor");
            object system = ReadInstanceMember(player, "planetarySystem");
            object planet = ReadInstanceMember(system, "chosenPlanet");
            return "controllerState=" + ToState(EditorGameReflection.ReadControllerState()) +
                   " currentFloor=" + ToValue(ReadInstanceMember(current, "seqID")) +
                   " nextFloor=" + ToValue(ReadInstanceMember(next, "seqID")) +
                   " nextFloorEntryTime=" + ToValue(ReadInstanceMember(next, "entryTime")) +
                   " playerAlive=" + ToValue(ReadInstanceMember(player, "alive")) +
                   " paused=" + ToValue(ReadInstanceMember(controller, "paused")) +
                   " chosenPlanetAngle=" + ToValue(ReadInstanceMember(planet, "angle")) +
                   " cachedAngle=" + ToValue(ReadInstanceMember(planet, "cachedAngle")) +
                   " targetExitAngle=" + ToValue(ReadInstanceMember(planet, "targetExitAngle")) +
                   " currentSeqID=" + EditorGameReflection.ReadCurrentSeqId().ToString(CultureInfo.InvariantCulture) +
                   " rdcAuto=" + ToValue(EditorGameReflection.ReadRdcAuto());
        }

        private static string ToValue(object value)
        {
            if (value == null) return "null";
            if (value is bool b) return b ? "true" : "false";
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("0.######", CultureInfo.InvariantCulture); }
            catch { return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null"; }
        }

        private static string FormatAuto(bool? v)
        {
            if (!v.HasValue) return "unavailable";
            return v.Value ? "true" : "false";
        }

        /// <summary>nullable bool 的统一日志格式（unavailable / true / false）。</summary>
        private static string FormatFlag(bool? v)
        {
            if (!v.HasValue) return "unavailable";
            return v.Value ? "true" : "false";
        }
    }
}
