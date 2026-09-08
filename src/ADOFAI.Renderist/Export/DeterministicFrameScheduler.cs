using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ADOFAI.Renderist.Capture;
using ADOFAI.Renderist.Diagnostics;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 保留的旧 deterministic scheduler 骨架，不是当前 Route B production authority。
    ///
    /// 数据流：
    ///   outputFrameIndex
    ///     → PrepareFrame（scrConductor.Update Prefix：设置本帧视觉时间）
    ///     → ADOFAI 原生 Update（旧版 Official Autoplay 路径）
    ///     → WaitForEndOfFrame
    ///     → FrameCaptureDriver 同步 PNG
    ///     → CommitFrame（成功后 outputFrameIndex++）
    ///
    /// 关键不变量：Frame N 未成功捕获，就不提交 N，也不开始 N+1。
    ///
    /// 正式路径不调用 scrPlayer.Hit、不做 catch-up、不迁移 DVA。
    /// Time.frameCount 仅用于启动去重与同帧防重复执行，不作为输出帧号。
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
        /// <summary>3.0 秒 @ 60 FPS（短序列验收基线）。</summary>
        public const int DefaultTargetFrameCount = 180;
        private const int PlaybackReadyTimeoutFrames = (int)(DefaultOutputFps * 30L);
        private const string FramePrefix = "frame_";
        private const int ZeroPadWidth = 6;
        private const double AnchorInvalidThresholdSeconds = 3600.0;

        private static SchedulerStatus _status = SchedulerStatus.Idle;
        private static bool _running;
        private static bool _restored;
        private static string _terminalStopReason;

        private static int _outputFps = DefaultOutputFps;
        private static int _targetFrameCount = DefaultTargetFrameCount;
        private static int _outputFrameIndex;              // 已提交数量，同时也是“下一帧”编号
        private static int _captureRequestCount;
        private static int _capturedFrameCount;

        private static double _outputTime;
        private static double _forcedSongPosition;
        private static double _canonicalStartTime;
        private static double _pitch = 1.0;
        private static bool _pitchUnavailable;

        private static int _awaitFrameCount;
        private static int _activationUnityFrame = -1;
        private static int _lastPrepareUnityFrame = -1;
        private static bool _clockActive;
        private static bool _pendingCapture;
        private static int _pendingCaptureIndex = -1;
        private static string _pendingStopEvent;
        private static string _pendingStopReason;

        // 保存需要恢复的状态
        private static int _savedCaptureFramerate;
        private static int _savedTargetFrameRate;
        private static int _savedVSyncCount;
        private static bool? _savedRdcAuto;
        private static readonly List<int> _savedSelectedFloorSeqs = new List<int>();
        private static bool _ownsPlayback;

        private static bool _hooksRegistered;

        public static bool IsRunning => _running;
        public static SchedulerStatus Status => _status;
        public static string StopReason => _terminalStopReason;
        public static int OutputFrameIndex => _outputFrameIndex;
        public static int OutputFps => _outputFps;
        public static int TargetFrameCount => _targetFrameCount;
        public static int CaptureRequestCount => _captureRequestCount;
        public static int CapturedFrameCount => _capturedFrameCount;
        public static double OutputTime => _outputTime;
        public static double ForcedSongPosition => _forcedSongPosition;
        public static double CanonicalStartTime => _canonicalStartTime;
        public static double Pitch => _pitch;
        public static bool PitchUnavailable => _pitchUnavailable;

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
        public static string TryStart(string outputDirectory, int outputFps, int targetFrameCount)
        {
            if (_running || !Terminal && _status != SchedulerStatus.Idle)
            {
                return "scheduler-already-running";
            }

            try
            {
                EditorGameReflection.EnsureTypes();

                string reject = ValidateStartConditions();
                if (reject != null)
                {
                    return reject;
                }

                if (string.IsNullOrEmpty(outputDirectory))
                {
                    return "output-directory-empty";
                }

                _status = SchedulerStatus.Preparing;
                _running = true;
                _restored = false;
                _terminalStopReason = null;

                _outputFps = outputFps > 0 ? outputFps : DefaultOutputFps;
                _targetFrameCount = targetFrameCount > 0 ? targetFrameCount : DefaultTargetFrameCount;
                _outputFrameIndex = 0;
                _outputTime = 0.0;
                _forcedSongPosition = 0.0;
                _canonicalStartTime = 0.0;
                _captureRequestCount = 0;
                _capturedFrameCount = 0;
                _awaitFrameCount = 0;
                _activationUnityFrame = -1;
                _lastPrepareUnityFrame = -1;
                _clockActive = false;
                _pendingCapture = false;
                _pendingCaptureIndex = -1;
                _pendingStopEvent = null;
                _pendingStopReason = null;

                SaveState();

                // ---- Unity 时间设置（复用 PoC 已验证 baseline）----
                Time.captureFramerate = _outputFps;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = Math.Max(1000, _outputFps * 4);

                // ---- pitch 只读一次；正式 anchor 不再依赖 playback-ready songposition ----
                _pitch = EditorGameReflection.ReadPitch(out _pitchUnavailable);

                // ---- 1) SelectFloor(floor0) ----
                if (!TrySelectFloor0(out string selectError))
                {
                    return FailStart("select-floor0-failed:" + selectError);
                }

                // ---- 2) CanonicalStartTime：读取 runtime floors[0].entryTime ----
                // 读取失败必须拒绝启动；禁止猜测 0 或回退 playback-ready songposition。
                if (!EditorGameReflection.TryReadFloor0EntryTime(out double canonical, out string entryError))
                {
                    return FailStart("floor0-entry-time-unavailable:" + entryError);
                }
                _canonicalStartTime = canonical;
                _forcedSongPosition = canonical;

                Log.Info(UiText.Format(UiText.LogSchedulerCanonicalAnchorFormat,
                    _canonicalStartTime.ToString("0.######", CultureInfo.InvariantCulture)));

                // ---- 3) 安装并激活 Forced Clock（必须在 editor.Play() 之前 hold canonical time）----
                RegisterConductorUpdateHook();
                if (!EditorVisualClock.RegisterForcedClockHooks())
                {
                    return FailStart("forced-clock-hooks-failed");
                }
                EditorVisualClock.SetForcedSongPosition(_canonicalStartTime);
                EditorVisualClock.SetActive(true);
                Log.Info(UiText.LogSchedulerForcedClockInstalled);
                Log.Info(UiText.LogSchedulerInitHoldStarted);

                // ---- 4) 启用官方 Auto（原值已由 SaveState 保存）----
                if (!EditorGameReflection.TryWriteRdcAuto(true))
                {
                    return FailStart("rdc-auto-write-failed");
                }

                // ---- 5) 捕获后端 ----
                if (!FrameCaptureDriver.Start(outputDirectory, FramePrefix, ZeroPadWidth,
                        OnCaptureResult, out string captureError))
                {
                    return FailStart("capture-driver-start-failed:" + captureError);
                }

                // ---- 6) 官方 editor.Play() ----
                if (!TryPlay(out string playError))
                {
                    return FailStart("start-playback-failed:" + playError);
                }
                Log.Info(UiText.LogSchedulerEditorPlayCalled);

                _status = SchedulerStatus.InitializationHold;
                Log.Info(UiText.Format(UiText.LogSchedulerStartedFormat,
                    _outputFps.ToString(CultureInfo.InvariantCulture),
                    _targetFrameCount.ToString(CultureInfo.InvariantCulture),
                    outputDirectory));
                return null;
            }
            catch (Exception ex)
            {
                Log.Exception("DeterministicFrameScheduler: 启动异常", ex);
                FailStart("start-exception:" + ex.Message);
                return "start-exception";
            }
        }

        private static string FailStart(string reason)
        {
            _terminalStopReason = reason;
            RestoreAll();
            _status = SchedulerStatus.Failed;
            _running = false;
            Log.Warn(UiText.Format(UiText.LogSchedulerStartRejectedFormat, reason));
            return reason;
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
            if (EditorGameReflection.IsFailureState(state))
            {
                RequestStop("unexpected-fail", "controller-" + ToState(state));
                return;
            }

            if (_awaitFrameCount > PlaybackReadyTimeoutFrames)
            {
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

            // 就绪：释放 initialization hold，下一 Unity Frame 从 frame 0 开始。
            // 不再读取 playback-ready 当前 songposition 作为 anchor。
            _activationUnityFrame = Time.frameCount + 1;
            _status = SchedulerStatus.Capturing;
            _clockActive = false;
            _lastPrepareUnityFrame = -1;

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

            string early = CheckEarlyTermination();
            if (early != null)
            {
                RequestStop(early, early);
                return;
            }

            // 捕获与提交由 EndOfFrame 回调完成；Tick 只负责环境检测与完成收尾。
        }

        // ================================================================
        // Frame Begin：scrConductor.Update Prefix
        // ================================================================

        private static void ConductorUpdatePrefix()
        {
            if (!_running || _pendingStopReason != null) return;

            if (_status == SchedulerStatus.InitializationHold)
            {
                // 初始化 Hold：不推进输出帧、不捕获，只把 clock 钉在 canonical start time。
                // 让 DSP / Audio 自然运行，但 chart-relative visual/game songposition 保持 hold。
                EditorVisualClock.SetForcedSongPosition(_canonicalStartTime);
                return;
            }

            if (_status != SchedulerStatus.Capturing) return;

            int frame = Time.frameCount;

            // 激活延迟一帧，避免在就绪检测所在的半帧内强制推进。
            if (!_clockActive)
            {
                if (frame < _activationUnityFrame) return;
                _clockActive = true;
                _lastPrepareUnityFrame = frame;
                PrepareFrame();
                return;
            }

            // 同帧去重：仅每个 Unity Frame 推进一次。
            if (frame != _lastPrepareUnityFrame)
            {
                _lastPrepareUnityFrame = frame;
                PrepareFrame();
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

            if (_outputFrameIndex >= _targetFrameCount)
            {
                RequestStop("completed", "target-frame-count-reached");
                return;
            }

            int index = _outputFrameIndex;
            _outputTime = (double)index / _outputFps;
            _forcedSongPosition = _canonicalStartTime + _outputTime * _pitch;

            EditorVisualClock.SetForcedSongPosition(_forcedSongPosition);

            _captureRequestCount++;
            _pendingCapture = true;
            _pendingCaptureIndex = index;
            FrameCaptureDriver.RequestCapture(index);

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

        private static void OnCaptureResult(int frameIndex, bool success, string filePath, string error)
        {
            if (!_running) return;

            if (_pendingCaptureIndex != frameIndex)
            {
                Log.Debug("DeterministicFrameScheduler: stale capture result " + frameIndex);
                return;
            }

            _pendingCapture = false;
            _pendingCaptureIndex = -1;

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
                // 序号错位 = 不变量被破坏；不得提交，直接失败。
                RequestStop("capture-failed", "frame-index-mismatch");
                return;
            }

            _capturedFrameCount++;
            _outputFrameIndex++;

            Log.Debug("DeterministicFrameScheduler: commit frame " + frameIndex + " -> " + filePath);

            if (_outputFrameIndex >= _targetFrameCount)
            {
                RequestStop("completed", "target-frame-count-reached");
            }
        }

        // ================================================================
        // 恢复
        // ================================================================

        private static void ProcessStop()
        {
            if (_restored) return;
            _restored = true;

            string stopEvent = _pendingStopEvent ?? "cancelled";
            string stopReason = _pendingStopReason ?? "cancelled";
            _pendingStopEvent = null;
            _pendingStopReason = null;
            _terminalStopReason = stopReason;

            _running = false;
            _clockActive = false;

            try
            {
                RestoreAll();
            }
            catch (Exception ex)
            {
                Log.Exception("DeterministicFrameScheduler: 恢复异常", ex);
            }

            _status = MapTerminal(stopEvent);
            Log.Info(UiText.Format(UiText.LogSchedulerStoppedFormat, stopReason ?? "?", _status.ToString()));
        }

        private static void RestoreAll()
        {
            // 顺序：先撤 Hook 与捕获后端，再恢复 RDC.auto 与 Editor 播放状态，最后恢复 Unity 时间。
            FrameCaptureDriver.Stop();
            EditorVisualClock.SetActive(false);
            EditorVisualClock.UnregisterForcedClockHooks();
            UnregisterConductorUpdateHook();

            RestoreRdcAuto();
            RestorePlayback();
            RestoreSelectedFloorSeqs();

            Time.captureFramerate = _savedCaptureFramerate;
            Application.targetFrameRate = _savedTargetFrameRate;
            QualitySettings.vSyncCount = _savedVSyncCount;

            Log.Debug("DeterministicFrameScheduler: restored captureFramerate=" + _savedCaptureFramerate +
                      " targetFrameRate=" + _savedTargetFrameRate +
                      " vSyncCount=" + _savedVSyncCount +
                      " rdcAuto=" + FormatAuto(EditorGameReflection.ReadRdcAuto()));
        }

        private static SchedulerStatus MapTerminal(string stopEvent)
        {
            if (string.Equals(stopEvent, "completed", StringComparison.Ordinal) ||
                string.Equals(stopEvent, "user", StringComparison.Ordinal))
            {
                return SchedulerStatus.Completed;
            }
            if (string.Equals(stopEvent, "cancelled", StringComparison.Ordinal) ||
                string.Equals(stopEvent, "mod-disabled", StringComparison.Ordinal) ||
                string.Equals(stopEvent, "left-editor", StringComparison.Ordinal))
            {
                return SchedulerStatus.Cancelled;
            }
            return SchedulerStatus.Failed;
        }

        private static void SaveState()
        {
            _savedCaptureFramerate = Time.captureFramerate;
            _savedTargetFrameRate = Application.targetFrameRate;
            _savedVSyncCount = QualitySettings.vSyncCount;
            _savedRdcAuto = EditorGameReflection.ReadRdcAuto();
            _savedSelectedFloorSeqs.Clear();
            EditorGameReflection.ReadSelectedFloorSeqs(_savedSelectedFloorSeqs);
        }

        private static void RestoreRdcAuto()
        {
            if (!_savedRdcAuto.HasValue) return;
            if (!EditorGameReflection.TryWriteRdcAuto(_savedRdcAuto.Value))
            {
                Log.Warn("DeterministicFrameScheduler: RDC.auto 恢复失败。");
            }
        }

        private static void RestorePlayback()
        {
            if (!_ownsPlayback) return;

            object editor = EditorGameReflection.Editor();
            if (editor == null) return;

            bool? playMode = EditorGameReflection.ReadEditorPlayMode();
            if (playMode != true) return;

            MethodInfo mSwitch = EditorGameReflection.EditorSwitchToEditModeMethod;
            if (mSwitch == null)
            {
                Log.Warn("DeterministicFrameScheduler: SwitchToEditMode 方法缺失，无法回 Editor。");
                return;
            }

            try
            {
                mSwitch.Invoke(editor, new object[] { false });
            }
            catch (Exception ex)
            {
                Log.Exception("DeterministicFrameScheduler: SwitchToEditMode 调用失败", ex);
            }
        }

        private static void RestoreSelectedFloorSeqs()
        {
            // 只有真正尝试过官方播放（会改编辑器选择）时才需要回填选择。
            if (!_ownsPlayback) return;
            try
            {
                if (_savedSelectedFloorSeqs.Count == 0) return;

                object editor = EditorGameReflection.Editor();
                Type editorType = EditorGameReflection.EditorType;
                Type floorType = EditorGameReflection.FloorType;
                if (editor == null || editorType == null || floorType == null) return;

                IList floors = EditorGameReflection.ReadFloorsList();
                if (floors == null || floors.Count == 0)
                {
                    Log.Warn("DeterministicFrameScheduler: 恢复选择时 floors 不可用，跳过。");
                    return;
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
                        return;
                    }
                    targets.Add(found);
                }

                if (targets.Count == 1)
                {
                    MethodInfo select = EditorGameReflection.EditorSelectFloorMethod;
                    if (select == null) return;
                    select.Invoke(editor, new[] { targets[0], (object)false });
                    return;
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
                        return;
                    }
                }

                MethodInfo multi = EditorGameReflection.EditorMultiSelectFloorsMethod;
                if (multi == null)
                {
                    Log.Warn("DeterministicFrameScheduler: MultiSelectFloors 方法缺失，跳过恢复。");
                    return;
                }
                multi.Invoke(editor, new[] { ordered[0].Item2, ordered[ordered.Count - 1].Item2, (object)true });
            }
            catch (Exception ex)
            {
                Log.Debug("DeterministicFrameScheduler: 恢复选择失败: " + ex.Message);
            }
        }

        // ================================================================
        // 启动前校验 / 官方 playback 生命周期
        // ================================================================

        private static string ValidateStartConditions()
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
            if (CaptureService.IsRecording)
            {
                return "capture-recording";
            }
            if (EditorVisualClockPoc.IsRunning)
            {
                return "visual-clock-poc-running";
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

                IList floors = EditorGameReflection.ReadFloorsList();
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

                // SelectFloor(floor0, cameraJump:false)
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

                // 官方 editor.Play()
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
            string s = ToState(state);
            return string.Equals(s, "PlayerControl", StringComparison.Ordinal) ||
                   string.Equals(s, "Countdown", StringComparison.Ordinal);
        }

        private static string CheckEarlyTermination()
        {
            if (!EditorGameReflection.IsProbablyEditorNow())
            {
                return "left-editor";
            }
            bool? playMode = EditorGameReflection.ReadEditorPlayMode();
            if (_ownsPlayback && playMode != true)
            {
                return "playback-stopped-unexpectedly";
            }
            return null;
        }

        // ================================================================
        // Harmony（Frame Begin only；Forced Clock 归 EditorVisualClock）
        // ================================================================

        private static void RegisterConductorUpdateHook()
        {
            UnregisterConductorUpdateHook();

            try
            {
                Harmony harmony = ModEntry.Harmony;
                MethodInfo update = EditorGameReflection.ConductorUpdateMethod;
                if (harmony == null || update == null) return;

                harmony.Patch(update,
                    prefix: new HarmonyMethod(typeof(DeterministicFrameScheduler), nameof(ConductorUpdatePrefix)));
                _hooksRegistered = true;
            }
            catch (Exception ex)
            {
                Log.Exception("DeterministicFrameScheduler: 注册 conductor.Update Prefix 失败", ex);
                UnregisterConductorUpdateHook();
            }
        }

        private static void UnregisterConductorUpdateHook()
        {
            if (!_hooksRegistered) return;
            Harmony harmony = ModEntry.Harmony;
            if (harmony != null)
            {
                try
                {
                    MethodInfo update = EditorGameReflection.ConductorUpdateMethod;
                    if (update != null) harmony.Unpatch(update, HarmonyPatchType.All, ModEntry.HarmonyId);
                }
                catch (Exception ex)
                {
                    Log.Exception("DeterministicFrameScheduler: 撤销 conductor.Update Prefix 失败", ex);
                }
            }
            _hooksRegistered = false;
        }

        // ================================================================
        // helpers
        // ================================================================

        private static string ToState(object v)
        {
            if (v == null) return null;
            return v is Enum e ? e.ToString() : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static string FormatAuto(bool? v)
        {
            if (!v.HasValue) return "unavailable";
            return v.Value ? "true" : "false";
        }
    }
}
