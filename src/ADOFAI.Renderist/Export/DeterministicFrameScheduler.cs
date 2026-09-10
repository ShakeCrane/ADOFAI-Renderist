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
    ///     → FrameCaptureDriver 同步 PNG
    ///     → CommitFrame（成功后 outputFrameIndex++）
    ///
    /// 关键不变量：Frame N 未成功捕获，就不提交 N，也不开始 N+1。
    ///
    /// 时间只来自 MasterTimeline；不使用 wall clock、Unity Update 次数或 AudioRenderer。
    /// wall clock（Time.realtimeSinceStartupAsDouble）仅用于 watchdog 失败保护，绝不推进 timeline。
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

        private const int DefaultOutputFps = 60;
        /// <summary>End Tail 默认值；玩家可改为 Frames / Seconds / Beats。</summary>
        public const int DefaultTailFrameCount = 12;
        /// <summary>
        /// 默认 safety-only 上限：600 秒 @ 60 FPS。它只用于防止异常谱面无限运行，
        /// 不参与正常完成判断。
        /// </summary>
        public const int DefaultSafetyFrameLimit = 36000;
        private const int MaxSafetyFrameLimit = 1000000;
        private const string FramePrefix = "frame_";
        private const int ZeroPadWidth = 6;
        private const double AnchorInvalidThresholdSeconds = 3600.0;
        private const double GameplayAnchorConsistencyToleranceSeconds = 0.05;
        private const double PlaybackReadyTimeoutSeconds = 30.0;
        private const double CaptureTimeoutSeconds = 30.0;
        private const double FrameProgressWatchdogSeconds = 30.0;
        private const double MinPitch = 0.0001;

        private static SchedulerStatus _status = SchedulerStatus.Idle;
        private static bool _running;
        private static bool _restored;
        private static bool _cleanupInProgress;
        private static string _terminalStopReason;

        private static int _outputFps = DefaultOutputFps;
        private static int _safetyFrameLimit = DefaultSafetyFrameLimit;
        private static int _resolvedTailFrameCount = DefaultTailFrameCount;
        private static int _tailFramesCaptured;
        private static EndTailInput _endTailInput =
            new EndTailInput(EndTailPolicy.DefaultValue, EndTailPolicy.DefaultUnit);
        private static double _resolvedTailSeconds;
        private static double? _resolvedTailBeats;
        private static double? _completionBpm;
        private static int _outputFrameIndex;              // 已提交数量，同时也是“下一帧”编号
        private static int _captureRequestCount;
        private static int _capturedFrameCount;

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

        private static int _awaitFrameCount;
        private static int _activationUnityFrame = -1;
        private static int _lastPrepareUnityFrame = -1;
        private static bool _clockActive;
        private static bool _pendingCapture;
        private static int _pendingCaptureIndex = -1;
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
        private static int _canonicalCompletionFrameIndex = -1;
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
        public static int OutputFrameIndex => _outputFrameIndex;
        public static int OutputFps => _outputFps;
        public static int SafetyFrameLimit => _safetyFrameLimit;
        public static int ResolvedTailFrameCount => _resolvedTailFrameCount;
        public static int TailFramesCaptured => _tailFramesCaptured;
        public static double EndTailInputValue => _endTailInput.Value;
        public static EndTailUnit EndTailInputUnit => _endTailInput.Unit;
        public static double ResolvedTailSeconds => _resolvedTailSeconds;
        public static double? ResolvedTailBeats => _resolvedTailBeats;
        public static double? CompletionBpm => _completionBpm;
        public static int CaptureRequestCount => _captureRequestCount;
        public static int CapturedFrameCount => _capturedFrameCount;
        public static double OutputTime => _outputTime;
        public static double ForcedSongPosition => _forcedSongPosition;
        public static double CanonicalStartTime => _canonicalStartTime;
        public static double Pitch => _pitch;
        public static bool PitchUnavailable => _pitchUnavailable;
        public static string CaptureSource => _captureSource;
        public static int CaptureWidth => _captureWidth;
        public static int CaptureHeight => _captureHeight;
        public static bool CanonicalCompletionCallbackSeen => _canonicalCompletionCallbackSeen;
        public static bool CanonicalCompletionStateSeen => _canonicalCompletionStateSeen;
        public static int CanonicalCompletionFrameIndex => _canonicalCompletionFrameIndex;
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
        /// </summary>
        public static string TryStart(string outputDirectory, int outputFps, int safetyFrameLimit,
            EndTailInput endTailInput, bool allowExpectedTerminalControllerFail = false)
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

                _status = SchedulerStatus.Preparing;
                _running = true;
                _restored = false;
                _terminalStopReason = null;

                ResetRunStateForStart();

                _outputFps = outputFps > 0 ? outputFps : DefaultOutputFps;
                _safetyFrameLimit = NormalizeSafetyFrameLimit(safetyFrameLimit);
                _endTailInput = endTailInput;
                _initializationDeadlineRealtime = Time.realtimeSinceStartupAsDouble + PlaybackReadyTimeoutSeconds;

                SaveState();

                // ---- Unity 时间设置（复用 PoC 已验证 baseline）----
                Time.captureFramerate = _outputFps;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = Math.Max(1000, _outputFps * 4);

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

                // ---- 5) 捕获后端 ----
                if (!FrameCaptureDriver.Start(outputDirectory, FramePrefix, ZeroPadWidth,
                        OnCaptureResult, out long captureGeneration, out string captureError))
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
                    _safetyFrameLimit.ToString(CultureInfo.InvariantCulture),
                    outputDirectory));
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
                         " safetyFrameLimit=" +
                         _safetyFrameLimit.ToString(CultureInfo.InvariantCulture));
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
            _captureSource = null;
            _captureWidth = 0;
            _captureHeight = 0;
            _captureRequestCount = 0;
            _capturedFrameCount = 0;
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
        }

        private static bool HasResidualOwnership()
        {
            return _ownsPlayback || _ownsUnityTiming || _handoff != null ||
                   _captureGeneration != 0 || FrameCaptureDriver.IsRunning ||
                   FrameCaptureDriver.HasActiveCameraSource ||
                   _inputGuardHooks.Count > 0 ||
                   EditorVisualClock.HasTrackedHooks ||
                   _patchedConductorUpdate != null || _patchedAsyncInputAdjustAngle != null ||
                   _patchedControllerOnLandOnPortal != null ||
                   _patchedEditorSwitchToEditMode != null ||
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

            if (Time.realtimeSinceStartupAsDouble > _initializationDeadlineRealtime)
            {
                Log.Warn("MasterTimeline InitializationHold readiness timeout: state=" + ToState(state) + " handoff=" + (_handoff == null ? "null" : _handoff.MarkerStatus) + " " + BuildRuntimeSnapshot());
                RequestStop("playback-ready-timeout", "playback-ready-timeout");
                return;
            }

            string early = CheckEarlyTermination();
            if (early != null)
            {
                RequestStop(early, early);
                return;
            }

            if (!IsPlaybackReady(state)) return;

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
            if (!observedSongPosition.HasValue ||
                Math.Abs(observedSongPosition.Value - gameplayStart) > GameplayAnchorConsistencyToleranceSeconds)
            {
                Log.Warn("MasterTimeline lifecycle anchor mismatch: floor0EntryTime=" +
                         _floor0EntryTime.ToString("0.######", CultureInfo.InvariantCulture) +
                         " countdownOffset=" + gameplayStartOffset.ToString("0.######", CultureInfo.InvariantCulture) +
                         " deterministicGameplayStart=" + gameplayStart.ToString("0.######", CultureInfo.InvariantCulture) +
                         " lifecycleSongPosition=" + lifecycleSongPosition);
                RequestStop("canonical-start-mismatch", "native-gameplay-anchor-mismatch");
                return;
            }

            _canonicalStartTime = gameplayStart;
            _forcedSongPosition = gameplayStart;
            _timeline = new MasterTimeline(_outputFps, _canonicalStartTime, _pitch);
            Log.Info("MasterTimeline lifecycle-ready: lifecycleReadySongPosition=" + lifecycleSongPosition +
                     " floor0EntryTime=" + _floor0EntryTime.ToString("0.######", CultureInfo.InvariantCulture) +
                     " countdownOffset=" + gameplayStartOffset.ToString("0.######", CultureInfo.InvariantCulture) +
                     " gameplayStart=" + _canonicalStartTime.ToString("0.######", CultureInfo.InvariantCulture) +
                     " forcedSongPosition=" + _canonicalStartTime.ToString("0.######", CultureInfo.InvariantCulture) +
                     " frameIndex=0 " + BuildRuntimeSnapshot());

            EditorVisualClock.SetForcedSongPosition(_canonicalStartTime);
            EditorVisualClock.SetActive(true);

            // Render Source 激活点：playback ready 且 canonical visual clock ready 之后、
            // 进入 Capturing / 请求 frame 0 之前。Start 阶段不得提前接管摄像机。
            // 失败即 Fail，绝不回退到 Screen framebuffer。
            if (!FrameCaptureDriver.TryActivateCameraSource(
                    _captureGeneration, out string cameraSourceError))
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

            _activationUnityFrame = Time.frameCount + 1;
            _status = SchedulerStatus.Capturing;
            _clockActive = false;
            _lastPrepareUnityFrame = -1;
            _progressDeadlineRealtime = Time.realtimeSinceStartupAsDouble + FrameProgressWatchdogSeconds;

            Log.Info(UiText.Format(UiText.LogSchedulerInitHoldReleasedFormat,
                _canonicalStartTime.ToString("0.######", CultureInfo.InvariantCulture),
                _pitch.ToString("0.######", CultureInfo.InvariantCulture)));
        }

        private static void TickCapturing()
        {
            object state = EditorGameReflection.ReadControllerState();
            if (EditorGameReflection.IsFailureState(state))
            {
                RequestStop("unexpected-fail", "controller-" + ToState(state));
                return;
            }

            // capture transaction watchdog：EOF callback 未如约返回时失败收尾。
            if (_pendingCapture && Time.realtimeSinceStartupAsDouble > _captureDeadlineRealtime)
            {
                RequestStop("capture-timeout", "capture-timeout");
                return;
            }

            if (Time.realtimeSinceStartupAsDouble > _progressDeadlineRealtime)
            {
                RequestStop("watchdog-timeout", "frame-progress-watchdog-timeout");
                return;
            }

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

            if (_outputFrameIndex >= _safetyFrameLimit)
            {
                if (_canonicalCompletionStateSeen &&
                    _tailFramesCaptured >= _resolvedTailFrameCount)
                    RequestStop("completed", "canonical-completion-tail-drained");
                else
                    RequestStop("safety-limit", "safety-frame-limit");
                return;
            }

            int index = _outputFrameIndex;
            LogFrame0Stage(index, "BEGIN");
            MasterTimeline.FrameSample sample = _timeline.Prepare(index);
            _outputTime = sample.OutputTime;
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

            _captureRequestCount++;
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

        // ================================================================
        // Capture / Commit（WaitForEndOfFrame）
        // ================================================================

        private static void OnCaptureResult(long generation, int frameIndex, bool success, string filePath, string error)
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
                          " expectedChartTime=" + (_canonicalStartTime + frameIndex / (double)_outputFps * _pitch).ToString("0.######", CultureInfo.InvariantCulture) +
                          " hitsThisFrame=" + _hitsThisFrame.ToString(CultureInfo.InvariantCulture) +
                          " " + BuildRuntimeSnapshot());
            }

            if (!success)
            {
                Log.Error(UiText.Format(UiText.LogSchedulerCaptureFailedFormat,
                    frameIndex.ToString(CultureInfo.InvariantCulture),
                    error ?? "unknown"));
                RequestStop("capture-failed", "write-png-failed");
                return;
            }

            CommitFrame(frameIndex, filePath);
        }

        private static void CommitFrame(int frameIndex, string filePath)
        {
            if (frameIndex != _outputFrameIndex)
            {
                RequestStop("capture-failed", "frame-index-mismatch");
                return;
            }

            LogFrame0Stage(frameIndex, "COMMIT");
            _capturedFrameCount++;
            _outputFrameIndex++;

            _progressDeadlineRealtime = Time.realtimeSinceStartupAsDouble + FrameProgressWatchdogSeconds;

            if (_canonicalCompletionStateSeen && frameIndex > _canonicalCompletionFrameIndex)
                _tailFramesCaptured++;

            Log.Debug("DeterministicFrameScheduler: commit frame " + frameIndex + " -> " + filePath);

            if (_canonicalCompletionStateSeen &&
                _tailFramesCaptured >= _resolvedTailFrameCount)
            {
                RequestStop("completed", "canonical-completion-tail-drained");
                return;
            }

            if (_outputFrameIndex >= _safetyFrameLimit)
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
            // Completed 只能来自 canonical completion + 已捕获全部 tail。
            if (string.Equals(stopEvent, "completed", StringComparison.Ordinal) &&
                _canonicalCompletionStateSeen &&
                _tailFramesCaptured >= _resolvedTailFrameCount)
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
                reason.IndexOf("png", StringComparison.OrdinalIgnoreCase) >= 0)
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
            bool paused = ReadBool(ReadInstanceMember(controller, "paused"));

            return _handoff.IsReady(state, playerAlive, paused);
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
                    Log.Exception("DeterministicFrameScheduler: 注册 conductor.Update Postfix 失败，撤销已注册 Prefix", ex);
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

        internal static int NormalizeSafetyFrameLimit(int value)
        {
            if (value <= 0) return DefaultSafetyFrameLimit;
            return Math.Min(value, MaxSafetyFrameLimit);
        }

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

        private static bool ReadBool(object value)
        {
            if (value == null) return false;
            try { return Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
            catch { return false; }
        }

        private static void LogFrame0Stage(int frameIndex, string stage)
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
