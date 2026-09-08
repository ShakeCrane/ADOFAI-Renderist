using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using ADOFAI.Renderist.Capture;
using ADOFAI.Renderist.Export;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Diagnostics
{
    /// <summary>
    /// Editor Forced Visual Clock / DVA Runtime Probe（Phase 3.x 诊断工具）。
    ///
    /// 目的（本轮唯一目标）：验证
    ///   官方 editor.Play()
    ///   + Time.captureFramerate
    ///   + Forced songposition
    /// 能否让 ADOFAI Editor 在正常 Unity 生命周期中按确定性的逻辑时间前进。
    ///
    /// DVA 模式仅验证简单谱面的 deterministic hit advancement：
    ///   * scrConductor.Update Postfix catch-up
    ///   * scrPlayer.Hit(isAuto:true)
    ///   * RDC.auto Preserve / TemporaryTrueDuringHit 对照
    ///
    /// 本轮<b>不实现</b>：
    ///   * get_calibration_i / AsyncInputUtils / scrPlayer.Hit Patch
    ///   * 截图 / RenderTexture / AudioRenderer / FFmpeg / replay
    ///
    /// Visual Clock 做三个必要的观察/控制点：
    ///   A. scrConductor.get_songposition_minusi Postfix：Active 时返回 forcedSongPosition
    ///   B. scrConductor.set_songposition_minusi Prefix：Active 时把 value 替换为 forcedSongPosition
    ///   C. scrConductor.Update Prefix：每个新 Unity Frame 开始时确定本帧 forcedSongPosition（不阻止/不手动调用 Update）
    /// DVA 模式再在同一 Update 的 Postfix 完成 catch-up，并用 scrController.Update Prefix/Postfix 记录执行顺序。
    ///
    /// 只观察、不主动 Seek；只读反射 + Harmony runtime type lookup，不引入 Assembly-CSharp。
    /// </summary>
    internal static class EditorVisualClockPoc
    {
        private const int OutputFps = 60;
        private const int TargetLogicalFrames = 180;          // 3.0 秒 @ 60 FPS
        private const int FrameOrderProbeLogicalFrameCount = 64;
        private const int PlaybackReadyTimeoutFrames = OutputFps * 30;   // 30 秒上限，防死等
        private const int MaxHitsPerFrame = 16;
        private const double HitDueToleranceSeconds = 0.000001;
        private const double PitchOneTolerance = 0.0001;
        private const string DiagnosticsSubdir = "diagnostics";
        private const string LogFileBase = "visual-clock-poc";
        private const string FrameOrderProbeLogFileBase = "frame-order-probe";
        private const string Unavailable = "unavailable";
        private const double AnchorInvalidThresholdSeconds = 3600.0; // 明显异常锚点判据（仅供终止测试，不做复杂修复）

        private enum PocState
        {
            Idle,
            AwaitPlaybackReady,
            Active,          // Forced Visual Clock 已启用
            Restoring,
            Completed,
            Canceled,
            Failed,
        }

        internal enum RdcAutoExperimentMode
        {
            Preserve,
            TemporaryTrueDuringHit,
        }

        // ---------------- 顶层状态 ----------------

        private static PocState _state = PocState.Idle;
        private static bool _running;                    // 生命周期内（Preparing..Restoring）为 true
        private static bool _forcedActive;               // Forced Clock 是否已真正启用（_state==Active 时才 true）
        private static bool _dvaProbeEnabled;
        private static bool _frameOrderProbeEnabled;
        private static bool _stopAfterCaptureBoundary;
        private static string _pendingStopEvent;
        private static string _pendingStopReason;

        private static double _startSongPosition;
        private static int _logicalFrameIndex;           // logicalFrame 0..179
        private static double _forcedSongPosition;
        private static int _lastUnityFrame = -1;
        private static int _activationUnityFrame = -1;
        private static int _awaitFrameCount;
        private static long _probeSequence;
        private static int _playerFloorAtPlayerControlPrefix = -1;

        private static double _pitch = 1.0;
        private static bool _pitchUnavailable;

        private static StreamWriter _writer;
        private static string _logPath;

        // ---------------- 状态保存（结束恢复用） ----------------
        private static int _savedCaptureFramerate;
        private static int _savedTargetFrameRate;
        private static int _savedVSyncCount;
        private static readonly List<int> _savedSelectedFloorSeqs = new List<int>();
        private static bool _ownsPlayback;               // 本 PoC 调用了 editor.Play()
        private static bool? _savedRdcAuto;
        private static RdcAutoExperimentMode _selectedRdcAutoMode = RdcAutoExperimentMode.Preserve;
        private static int _hitCountThisFrame;
        private static int _totalHitCount;

        // ---------------- 反射句柄 ----------------
        private static Assembly _gameAssembly;
        private static Type _tAdoBase;
        private static Type _tConductor;
        private static Type _tController;
        private static Type _tEditor;
        private static Type _tPlayer;
        private static Type _tFloor;
        private static Type _tPlanet;
        private static Type _tRdc;

        private static PropertyInfo _pSongPosI;
        private static PropertyInfo _pSongPosMinusV;
        private static PropertyInfo _pBeatNumber;
        private static PropertyInfo _pBarNumber;
        private static PropertyInfo _pDspTime;
        private static PropertyInfo _pDspTimeSong;
        private static PropertyInfo _pHasSongStarted;
        private static PropertyInfo _pState;
        private static PropertyInfo _pPaused;
        private static PropertyInfo _pRdcAuto;
        private static FieldInfo _fCurrentSeq;
        private static MethodInfo _mConductorUpdate;
        private static MethodInfo _mControllerUpdate;
        private static MethodInfo _mPlayerControlUpdate;
        private static MethodInfo _mControllerLateUpdate;
        private static MethodInfo _mPlayerHit;
        private static MethodInfo _mPlanetRefreshAngles;
        private static bool _hooksRegistered;
        private static GameObject _frameOrderProbeHost;

        private sealed class EndOfFrameProbeBehaviour : MonoBehaviour
        {
            private Coroutine _routine;

            internal void Begin()
            {
                if (_routine == null) _routine = StartCoroutine(Observe());
            }

            private IEnumerator Observe()
            {
                while (true)
                {
                    yield return new WaitForEndOfFrame();
                    OnEndOfFrameCaptureBoundary();
                }
            }
        }

        // ---------------- 上一帧参考（事件检测） ----------------
        private static int _prevPlayerFloor = -1;
        private static object _prevControllerState;

        public static bool IsRunning => _running;
        public static bool IsDvaProbe => _running && _dvaProbeEnabled;
        public static bool IsFrameOrderProbe => _running && _frameOrderProbeEnabled;
        public static string LogPath => _logPath;
        public static int LogicalFrameIndex => _logicalFrameIndex;
        public static int TargetLogicalFrameCount =>
            _frameOrderProbeEnabled ? FrameOrderProbeLogicalFrameCount : TargetLogicalFrames;
        public static string StateName => _state.ToString();
        public static int CurrentPlayerFloor => ReadPlayerFloor();
        public static int TotalHitCount => _totalHitCount;
        public static RdcAutoExperimentMode SelectedRdcAutoMode
        {
            get => _selectedRdcAutoMode;
            set
            {
                if (!_running) _selectedRdcAutoMode = value;
            }
        }

        // ================================================================
        // 生命周期
        // ================================================================

        public static bool Start()
        {
            return StartCore(false, false);
        }

        public static bool StartDvaProbe()
        {
            return StartCore(true, false);
        }

        /// <summary>
        /// 仅记录当前 DLL 的 Conductor / PlayerControl / LateUpdate / EndOfFrame 顺序。
        /// 不写 PNG，不调用 DVA，不自行 Hit。
        /// </summary>
        public static bool StartFrameOrderProbe()
        {
            return StartCore(false, true);
        }

        private static bool StartCore(bool enableDva, bool enableFrameOrderProbe)
        {
            if (_running)
            {
                Log.Warn(UiText.LogVcPocAlreadyRunning);
                return false;
            }

            try
            {
                EnsureTypes();

                // ---- 启动条件（section 6）----
                string reject = ValidateStartConditions(enableDva, enableFrameOrderProbe);
                if (reject != null)
                {
                    Log.Warn(UiText.Format(UiText.LogVcPocStartRejectedFormat, reject));
                    return false;
                }

                string root = ResolveDiagnosticsRoot();
                if (string.IsNullOrEmpty(root))
                {
                    Log.Error(UiText.LogVcPocNoOutputDir);
                    return false;
                }

                _dvaProbeEnabled = enableDva;
                _frameOrderProbeEnabled = enableFrameOrderProbe;

                string dir = Path.Combine(root, DiagnosticsSubdir);
                Directory.CreateDirectory(dir);
                string baseName = _dvaProbeEnabled
                    ? "visual-advancement-probe"
                    : (_frameOrderProbeEnabled ? FrameOrderProbeLogFileBase : LogFileBase);
                string path = ResolveUniqueLogPath(dir, baseName);
                if (string.IsNullOrEmpty(path))
                {
                    Log.Error(UiText.LogVcPocNoOutputDir);
                    return false;
                }

                var writer = new StreamWriter(path, false, new UTF8Encoding(false));
                writer.AutoFlush = false;
                _writer = writer;
                _logPath = path;

                _hitCountThisFrame = 0;
                _totalHitCount = 0;
                _probeSequence = 0;
                _playerFloorAtPlayerControlPrefix = -1;
                _stopAfterCaptureBoundary = false;
                _pendingStopEvent = null;
                _pendingStopReason = null;
                WriteHeader();

                // ---- 生命周期进入 ----
                _state = PocState.AwaitPlaybackReady;
                _awaitFrameCount = 0;
                _forcedActive = false;
                _ownsPlayback = false;
                _running = true;

                // ---- 保存需要恢复的状态 ----
                SaveState();

                // ---- Unity 时间设置（section 8）----
                Time.captureFramerate = OutputFps;
                QualitySettings.vSyncCount = 0;
                // 实验性设置：避免系统帧率限制阻碍离线处理；不声称其精确内部机制已确认。
                Application.targetFrameRate = Math.Max(1000, OutputFps * 4);

                // ---- 启动官方 Editor Play（section 7 / 9）----
                if (!StartEditorPlayback(out string playError))
                {
                    WriteLine(BuildEventLine("PocError", "reason=start-playback-failed|detail=" + Sanitize(playError)), flush: true);
                    FailRestore("start-playback-failed", playError);
                    return false;
                }
                _ownsPlayback = true;

                // ---- 注册三个观察/控制点（section 12）----
                RegisterHooks();

                if (_frameOrderProbeEnabled && !StartEndOfFrameObserver())
                {
                    WriteLine(BuildEventLine("FrameOrderProbeError", "reason=end-of-frame-observer-unavailable"), flush: true);
                    FailRestore("frame-order-probe-error", "end-of-frame-observer-unavailable");
                    return false;
                }

                string startEvent = _dvaProbeEnabled ? "DvaStarted" :
                    (_frameOrderProbeEnabled ? "FrameOrderProbeStarted" : "PocStarted");
                WriteLine(BuildEventLine(startEvent, "outputFPS=" + OutputFps + "|targetLogicalFrames=" + TargetLogicalFrameCount +
                    "|captureFramerate=" + Time.captureFramerate + "|targetFrameRate=" + Application.targetFrameRate +
                    "|vSyncCount=" + QualitySettings.vSyncCount + "|rdcAutoMode=" + _selectedRdcAutoMode +
                    "|rdcAuto=" + Fmt(ReadRdcAuto())), flush: true);

                Log.Info(_dvaProbeEnabled ? UiText.LogDvaProbeStarted :
                    (_frameOrderProbeEnabled ? UiText.LogFrameOrderProbeStarted : UiText.LogVcPocStarted));
                Log.Info(UiText.Format(UiText.LogVcPocLogPathFormat, path));
                return true;
            }
            catch (Exception ex)
            {
                _running = false;
                Log.Exception(UiText.LogVcPocStartFailed, ex);
                FailSafeClose();
                return false;
            }
        }

        /// <summary>每 ModEntry.OnUpdate 调用：推进状态机、检测事件、检测终止条件。不主动 Seek/Play/Hit。</summary>
        public static void Tick()
        {
            if (!_running) return;

            try
            {
                if (_pendingStopReason != null)
                {
                    string pendingEvent = _pendingStopEvent;
                    string pendingReason = _pendingStopReason;
                    _pendingStopEvent = null;
                    _pendingStopReason = null;
                    SafeStop(pendingEvent, pendingReason);
                    return;
                }

                if (!ModEntry.Enabled)
                {
                    SafeStop("mod-disabled", "mod-disabled");
                    return;
                }

                switch (_state)
                {
                    case PocState.AwaitPlaybackReady:
                        TickAwaitPlaybackReady();
                        break;
                    case PocState.Active:
                        TickActive();
                        break;
                    case PocState.Restoring:
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Exception(UiText.LogVcPocTickError, ex);
                SafeStop("tick-exception", "tick-exception");
            }
        }

        public static void Stop(string reason)
        {
            SafeStop(reason ?? "user", reason ?? "user");
        }

        // ================================================================
        // 状态机推进
        // ================================================================

        private static void TickAwaitPlaybackReady()
        {
            if (_state != PocState.AwaitPlaybackReady) return;

            _awaitFrameCount++;

            object state = ReadControllerState();
            if (IsFailureState(state))
            {
                WriteLine(BuildEventLine("UnexpectedFail", BuildDvaStateFields("phase=await-playback-ready")), flush: true);
                SafeStop("unexpected-fail", "controller-" + ToState(state));
                return;
            }

            // 超时保护（section 9）
            if (_awaitFrameCount > PlaybackReadyTimeoutFrames)
            {
                SafeStop("playback-ready-timeout", "playback-ready-timeout");
                return;
            }

            // 提前终止：离开编辑器 / playMode 结束等
            string earlyStop = CheckEarlyTermination();
            if (earlyStop != null)
            {
                SafeStop(earlyStop, earlyStop);
                return;
            }

            // 检测播放就绪（section 9 / 10）
            if (!IsPlaybackReady()) return;

            // ---- 记录就绪 + 捕获锚点 ----
            WriteLine(BuildEventLine("PlaybackReady",
                "frame=" + Time.frameCount + "|hasSongStarted=" + Fmt(ReadProperty(_tConductor, _pHasSongStarted)) +
                "|controllerState=" + Fmt(ReadProperty(_tController, _pState)) +
                "|editorPlayMode=" + Fmt(ReadEditorPlayMode())), flush: true);

            if (!CaptureAnchor())
            {
                WriteLine(BuildEventLine("PocError", "reason=anchor-invalid"), flush: true);
                SafeStop("anchor-invalid", "anchor-invalid");
                return;
            }

            // ---- 从下一 Unity Frame 启用 Forced Clock（section 14：避免半帧激活）----
            _activationUnityFrame = Time.frameCount + 1;
            _state = PocState.Active;
            _forcedActive = false;   // 真正启用交给 conductor.Update Prefix（下一帧）
            _lastUnityFrame = -1;
            _logicalFrameIndex = 0;
        }

        private static void TickActive()
        {
            if (_state != PocState.Active) return;

            object state = ReadControllerState();
            if (IsFailureState(state))
            {
                WriteLine(BuildEventLine("UnexpectedFail", BuildDvaStateFields("phase=active")), flush: true);
                SafeStop("unexpected-fail", "controller-" + ToState(state));
                return;
            }
            if (_dvaProbeEnabled && string.Equals(ToState(state), "Checkpoint", StringComparison.Ordinal))
            {
                WriteLine(BuildEventLine("UnsupportedTile", BuildDvaStateFields("reason=checkpoint-state")), flush: true);
                SafeStop("unsupported-tile", "checkpoint-state");
                return;
            }

            // 每 Unity Frame 检测 floor / controller state 变化
            DetectVisualEvents();

            string earlyStop = CheckEarlyTermination();
            if (earlyStop != null)
            {
                SafeStop(earlyStop, earlyStop);
                return;
            }

            // logicalFrame 达到上限 → 自动完成（section 15）
            int lastLogicalFrame = _frameOrderProbeEnabled
                ? FrameOrderProbeLogicalFrameCount - 1
                : TargetLogicalFrames;
            if (_logicalFrameIndex >= lastLogicalFrame)
            {
                if (_frameOrderProbeEnabled)
                {
                    _stopAfterCaptureBoundary = true;
                }
                else
                {
                    SafeStop("completed", null);
                }
            }
        }

        // ================================================================
        // Forced Visual Clock：conductor.Update Prefix（section 12C / 13 / 14）
        // ================================================================

        private static void OnConductorUpdatePrefix()
        {
            if (!_running) return;
            if (_pendingStopReason != null) return;
            WriteExecutionOrder("ConductorPrefix");
            WriteFrameOrderEvent("ConductorPrefix");

            // 尚未进入 Active：什么也不做，放行。
            if (_state != PocState.Active) return;

            int frame = Time.frameCount;

            // activation：从 activationUnityFrame 开始的第一个受控 Unity Frame，
            // logicalFrame 0 即对应强制时间 startSongPosition。
            if (!_forcedActive)
            {
                if (frame < _activationUnityFrame) return;
                _forcedActive = true;
                _lastUnityFrame = frame;
                _logicalFrameIndex = 0;
                _hitCountThisFrame = 0;
                _forcedSongPosition = _startSongPosition + ((double)_logicalFrameIndex / OutputFps) * _pitch;
                _prevPlayerFloor = ToInt(ReadPlayerFloor());
                _prevControllerState = ReadProperty(_tController, _pState);
                WriteLine(BuildEventLine(_dvaProbeEnabled ? "DvaActivated" : "VisualClockActivated",
                    "activationUnityFrame=" + frame + "|logicalFrame=0|forcedSongPosition=" + Fmt(_forcedSongPosition) +
                    "|startSongPosition=" + Fmt(_startSongPosition) + "|pitch=" + Fmt(_pitch) +
                    (_pitchUnavailable ? "|pitchUnavailable=true" : string.Empty)), flush: true);
                WriteLogicalFrameLine();
                WriteFrameOrderEvent("ForcedTimePrepared");
                return;
            }

            // 新 Unity Frame：一个受控帧内 forcedSongPosition 保持不变（section 13）
            if (frame != _lastUnityFrame)
            {
                _lastUnityFrame = frame;
                _logicalFrameIndex++;
                _hitCountThisFrame = 0;
                _forcedSongPosition = _startSongPosition + ((double)_logicalFrameIndex / OutputFps) * _pitch;

                // 每个 logical frame 记录一行（section 17）
                WriteLogicalFrameLine();
                WriteFrameOrderEvent("ForcedTimePrepared");
            }
        }

        private static void OnConductorUpdatePostfix()
        {
            if (!_running) return;
            if (_pendingStopReason != null) return;
            WriteExecutionOrder("ConductorPostfix");
            WriteFrameOrderEvent("ConductorPostfix");
            if (!_dvaProbeEnabled || !_forcedActive || _state != PocState.Active || _pendingStopReason != null) return;

            object state = ReadControllerState();
            if (IsFailureState(state))
            {
                WriteLine(BuildEventLine("UnexpectedFail", BuildDvaStateFields("phase=conductor-postfix")), flush: true);
                RequestSafeStop("unexpected-fail", "controller-" + ToState(state));
                return;
            }
            if (!string.Equals(ToState(state), "PlayerControl", StringComparison.Ordinal)) return;
            if (Math.Abs(_pitch - 1.0) > PitchOneTolerance)
            {
                WriteLine(BuildEventLine("DvaError", BuildDvaStateFields("reason=unsupported-pitch|pitch=" + Fmt(_pitch))), flush: true);
                RequestSafeStop("dva-error", "unsupported-pitch");
                return;
            }
            WriteExecutionOrder("DvaCatchUpBegin");
            try
            {
                CatchUpDueFloors();
            }
            catch (Exception ex)
            {
                WriteLine(BuildEventLine("DvaError", BuildDvaStateFields("reason=catchup-exception|detail=" + Sanitize(ex.Message))), flush: true);
                RequestSafeStop("dva-error", "catchup-exception");
            }
            finally
            {
                WriteExecutionOrder("DvaCatchUpEnd");
            }
        }

        private static void CatchUpDueFloors()
        {
            object player = CurrentPlayer();
            if (player == null)
            {
                WriteLine(BuildEventLine("DvaError", BuildDvaStateFields("reason=player-null")), flush: true);
                RequestSafeStop("dva-error", "player-null");
                return;
            }

            int hitCount = 0;
            while (hitCount < MaxHitsPerFrame)
            {
                object currentFloor = CurrentFloor(player);
                object nextFloor = NextFloor(currentFloor);
                if (nextFloor == null) break;

                double? entryTime = ToDouble(InstanceValue(nextFloor, nextFloor.GetType(), "entryTime"));
                if (!entryTime.HasValue || double.IsNaN(entryTime.Value) || double.IsInfinity(entryTime.Value))
                {
                    WriteLine(BuildEventLine("DvaError", BuildDvaStateFields("reason=next-entry-time-unavailable")), flush: true);
                    RequestSafeStop("dva-error", "next-entry-time-unavailable");
                    return;
                }
                if (entryTime.Value > _forcedSongPosition + HitDueToleranceSeconds) break;

                if (IsUnsupportedFloor(currentFloor) || IsUnsupportedFloor(nextFloor))
                {
                    WriteLine(BuildEventLine("UnsupportedTile", BuildDvaStateFields(
                        "reason=hold-or-midspin|currentFloor=" + Fmt(FloorSeq(currentFloor)) +
                        "|nextFloor=" + Fmt(FloorSeq(nextFloor)))), flush: true);
                    RequestSafeStop("unsupported-tile", "hold-or-midspin");
                    return;
                }

                int beforeFloor = FloorSeq(currentFloor);
                int beforeSeq = ReadCurrentSeqId();
                bool? rdcBefore = ReadRdcAuto();

                RefreshAndAlignChosenPlanet(player);
                ClearConfirmedMultipressState(player);

                bool? rdcDuring = rdcBefore;
                bool hitResult;
                if (_selectedRdcAutoMode == RdcAutoExperimentMode.TemporaryTrueDuringHit)
                {
                    bool? temporaryOld = ReadRdcAuto();
                    try
                    {
                        if (!TryWriteRdcAuto(true))
                        {
                            WriteLine(BuildEventLine("DvaError", BuildDvaStateFields("reason=rdc-auto-write-failed")), flush: true);
                            RequestSafeStop("dva-error", "rdc-auto-write-failed");
                            return;
                        }
                        rdcDuring = ReadRdcAuto();
                        hitResult = InvokeAutoHit(player);
                    }
                    finally
                    {
                        if (temporaryOld.HasValue && !TryWriteRdcAuto(temporaryOld.Value))
                        {
                            WriteLine(BuildEventLine("DvaError", BuildDvaStateFields(
                                "reason=rdc-auto-temporary-restore-failed")), flush: true);
                            RequestSafeStop("dva-error", "rdc-auto-temporary-restore-failed");
                        }
                    }
                }
                else
                {
                    rdcDuring = ReadRdcAuto();
                    hitResult = InvokeAutoHit(player);
                }

                object afterFloorObject = CurrentFloor(player);
                int afterFloor = FloorSeq(afterFloorObject);
                int afterSeq = ReadCurrentSeqId();
                hitCount++;
                _hitCountThisFrame = hitCount;
                _totalHitCount++;

                WriteLine(BuildEventLine("AutoHit", BuildDvaStateFields(
                    "mode=" + _selectedRdcAutoMode + "|hitResult=" + Fmt(hitResult) +
                    "|beforeFloor=" + beforeFloor + "|afterFloor=" + afterFloor +
                    "|beforeSeq=" + beforeSeq + "|afterSeq=" + afterSeq +
                    "|rdcAutoBefore=" + Fmt(rdcBefore) + "|rdcAutoDuring=" + Fmt(rdcDuring) +
                    "|rdcAutoAfter=" + Fmt(ReadRdcAuto()))), flush: false);

                if (!hitResult || afterFloor < 0 || afterFloor == beforeFloor)
                {
                    WriteLine(BuildEventLine("DvaError", BuildDvaStateFields("reason=hit-did-not-advance")), flush: true);
                    RequestSafeStop("dva-error", "hit-did-not-advance");
                    return;
                }

                WriteLine(BuildEventLine("FloorChanged", BuildDvaStateFields(
                    "beforeFloor=" + beforeFloor + "|afterFloor=" + afterFloor)), flush: false);
                _prevPlayerFloor = afterFloor;
            }

            if (hitCount >= MaxHitsPerFrame)
            {
                object next = NextFloor(CurrentFloor(player));
                double? nextEntry = next == null ? null : ToDouble(InstanceValue(next, next.GetType(), "entryTime"));
                if (nextEntry.HasValue && nextEntry.Value <= _forcedSongPosition + HitDueToleranceSeconds)
                {
                    WriteLine(BuildEventLine("GuardReached", BuildDvaStateFields(
                        "maxHitsPerFrame=" + MaxHitsPerFrame)), flush: true);
                    RequestSafeStop("guard-reached", "max-hits-per-frame");
                }
            }
        }

        private static bool InvokeAutoHit(object player)
        {
            object result = _mPlayerHit.Invoke(player, new object[] { true });
            return result is bool b && b;
        }

        private static bool IsUnsupportedFloor(object floor)
        {
            if (floor == null) return false;
            bool midSpin = ToBool(InstanceValue(floor, floor.GetType(), "midSpin")) == true;
            int holdLength = ToInt(InstanceValue(floor, floor.GetType(), "holdLength"));
            return midSpin || holdLength > 0;
        }

        private static void RefreshAndAlignChosenPlanet(object player)
        {
            object system = InstanceValue(player, player.GetType(), "planetarySystem");
            object chosen = system == null ? null : InstanceValue(system, system.GetType(), "chosenPlanet");
            if (chosen == null) throw new InvalidOperationException("chosenPlanet-unavailable");

            _mPlanetRefreshAngles.Invoke(chosen, null);
            double? target = ToDouble(InstanceValue(chosen, chosen.GetType(), "targetExitAngle"));
            if (!target.HasValue ||
                !TrySetInstanceValue(chosen, chosen.GetType(), "angle", target.Value) ||
                !TrySetInstanceValue(chosen, chosen.GetType(), "cachedAngle", target.Value))
            {
                throw new InvalidOperationException("planet-angle-alignment-failed");
            }
        }

        private static void ClearConfirmedMultipressState(object player)
        {
            object controller = Controller();
            TrySetInstanceValue(controller, _tController, "multipressPenalty", false);
            TrySetInstanceValue(controller, _tController, "multipressAndHasPressedFirstPress", false);
            TrySetInstanceValue(player, player.GetType(), "consecMultipressCounter", 0);
            object keyTimes = InstanceValue(player, player.GetType(), "keyTimes");
            if (keyTimes is System.Collections.IList list) list.Clear();
        }

        private static void RequestSafeStop(string eventName, string reason)
        {
            if (_pendingStopReason != null) return;
            _pendingStopEvent = eventName;
            _pendingStopReason = reason;
        }
        private static void WriteLogicalFrameLine()
        {
            if (_writer == null) return;
            var sb = new StringBuilder(768);
            AppendField(sb, "timestamp", NowStamp(), true);
            AppendField(sb, "realtimeSinceStartup", Fmt(Time.realtimeSinceStartup), false);
            AppendField(sb, "unityFrame", Time.frameCount.ToString(CultureInfo.InvariantCulture), false);
            AppendField(sb, "logicalFrame", _logicalFrameIndex.ToString(CultureInfo.InvariantCulture), false);
            AppendField(sb, "event", "LogicalFrame", false);
            AppendField(sb, "forcedSongPosition", Fmt(_forcedSongPosition), false);
            AppendField(sb, "songposition_minusi", Fmt(ReadProperty(_tConductor, _pSongPosI)), false);
            AppendField(sb, "songposition_minusv", Fmt(ReadProperty(_tConductor, _pSongPosMinusV)), false);
            AppendField(sb, "beatNumber", Fmt(ReadProperty(_tConductor, _pBeatNumber)), false);
            AppendField(sb, "barNumber", Fmt(ReadProperty(_tConductor, _pBarNumber)), false);
            AppendField(sb, "controllerState", Fmt(ReadProperty(_tController, _pState)), false);
            AppendField(sb, "currentSeqID", Fmt(ReadCurrentSeqId()), false);
            AppendField(sb, "playerFloor", Fmt(ReadPlayerFloor()), false);
            AppendField(sb, "nextFloorSeqID", Fmt(ReadNextFloorSeqId()), false);
            AppendField(sb, "nextFloorEntryTime", Fmt(ReadNextFloorEntryTime()), false);
            AppendField(sb, "hitCountThisFrame", _hitCountThisFrame.ToString(CultureInfo.InvariantCulture), false);
            AppendField(sb, "totalHitCount", _totalHitCount.ToString(CultureInfo.InvariantCulture), false);
            AppendField(sb, "rdcAuto", Fmt(ReadRdcAuto()), false);
            AppendField(sb, "editorPlayMode", Fmt(ReadEditorPlayMode()), false);
            AppendField(sb, "paused", Fmt(ReadProperty(_tController, _pPaused)), false);
            AppendField(sb, "pitch", Fmt(_pitch), false);
            WriteLine(sb.ToString(), flush: false);
        }

        // ================================================================
        // 事件检测（section 18）
        // ================================================================

        /// <summary>在 Active 期间检测并记录 floor / state 变化。在 TickActive 前调用。</summary>
        private static void DetectVisualEvents()
        {
            if (_writer == null) return;

            int floor = ToInt(ReadPlayerFloor());
            if (floor >= 0 && floor != _prevPlayerFloor)
            {
                WriteLine(BuildEventLine("PlayerFloorChanged", "playerFloor=" + floor), flush: false);
                _prevPlayerFloor = floor;
            }

            object state = ReadProperty(_tController, _pState);
            if (state != null && !Equals(_prevControllerState, state))
            {
                WriteLine(BuildEventLine("ControllerStateChanged",
                    "before=" + Fmt(_prevControllerState) + "|after=" + Fmt(state)), flush: true);
                _prevControllerState = state;
            }
        }

        private static string CheckEarlyTermination()
        {
            if (!IsProbablyEditorNow()) return "left-editor";

            object conductor = Conductor();
            if (conductor == null) return "conductor-lost";

            // playMode 结束（若本 PoC 启动了 Play，且 editor.playMode 已经不为 true）
            bool? playMode = ReadEditorPlayMode();
            if (_ownsPlayback && (playMode == false || playMode == null))
            {
                return "playback-stopped-unexpectedly";
            }

            return null;
        }

        // ================================================================
        // 停止 / 恢复
        // ================================================================

        private static void SafeStop(string eventOrReason, string stopReason)
        {
            if (!_running && _state == PocState.Idle) return;

            bool wasRunning = _running;
            _running = false;
            _forcedActive = false;
            _pendingStopEvent = null;
            _pendingStopReason = null;

            try
            {
                string eventName;
                if (string.Equals(eventOrReason, "completed", StringComparison.Ordinal))
                {
                    eventName = _dvaProbeEnabled ? "DvaCompleted" :
                        (_frameOrderProbeEnabled ? "FrameOrderProbeCompleted" : "PocCompleted");
                }
                else if (string.Equals(eventOrReason, "unexpected-fail", StringComparison.Ordinal))
                {
                    eventName = "UnexpectedFail";
                }
                else if (string.Equals(eventOrReason, "playback-stopped-unexpectedly", StringComparison.Ordinal))
                {
                    eventName = "UnexpectedPlaybackStop";
                }
                else if (string.Equals(eventOrReason, "guard-reached", StringComparison.Ordinal))
                {
                    eventName = "GuardReached";
                }
                else if (string.Equals(eventOrReason, "unsupported-tile", StringComparison.Ordinal))
                {
                    eventName = "UnsupportedTile";
                }
                else if (string.Equals(eventOrReason, "dva-error", StringComparison.Ordinal) ||
                         string.Equals(eventOrReason, "tick-exception", StringComparison.Ordinal) ||
                         string.Equals(eventOrReason, "restore-failed", StringComparison.Ordinal))
                {
                    eventName = _dvaProbeEnabled ? "DvaError" : "PocError";
                }
                else
                {
                    eventName = _dvaProbeEnabled ? "DvaCanceled" : "PocCanceled";
                }

                WriteLine(BuildEventLine(eventName, BuildDvaStateFields(
                    "reason=" + Sanitize(stopReason ?? "target-reached"))), flush: true);
            }
            catch { }

            RestoreRdcAuto();
            RestoreState();
            UnregisterHooks();
            StopEndOfFrameObserver();
            FailSafeClose();

            _state = PocState.Idle;
            _ownsPlayback = false;
            _lastUnityFrame = -1;
            _activationUnityFrame = -1;
            _dvaProbeEnabled = false;
            _frameOrderProbeEnabled = false;
            _stopAfterCaptureBoundary = false;

            if (wasRunning)
            {
                Log.Info(UiText.Format(UiText.LogVcPocStoppedFormat, stopReason ?? "?"));
            }
        }
        private static void FailRestore(string reason, string detail)
        {
            _running = false;
            _forcedActive = false;
            RestoreRdcAuto();
            RestoreState();
            UnregisterHooks();
            StopEndOfFrameObserver();
            FailSafeClose();
            _running = false;
            _state = PocState.Idle;
            _forcedActive = false;
            _ownsPlayback = false;
            _frameOrderProbeEnabled = false;
            _stopAfterCaptureBoundary = false;
            Log.Warn(UiText.Format(UiText.LogVcPocStartRejectedFormat, reason + " " + detail));
        }

        private static void SaveState()
        {
            _savedCaptureFramerate = Time.captureFramerate;
            _savedTargetFrameRate = Application.targetFrameRate;
            _savedVSyncCount = QualitySettings.vSyncCount;
            _savedRdcAuto = ReadRdcAuto();
            _savedSelectedFloorSeqs.Clear();
            ReadSelectedFloorSeqs(_savedSelectedFloorSeqs);
        }

        private static void RestoreRdcAuto()
        {
            if (!_savedRdcAuto.HasValue) return;
            if (!TryWriteRdcAuto(_savedRdcAuto.Value))
            {
                WriteLine(BuildEventLine("DvaError", "reason=rdc-auto-restore-failed"), flush: true);
            }
        }

        private static void RestoreState()
        {
            if (_state == PocState.Idle && _writer == null) return;

            try
            {
                if (_ownsPlayback)
                {
                    object editor = Editor();
                    if (editor != null && ReadEditorPlayMode() == true)
                    {
                        MethodInfo mSwitch = _tEditor?.GetMethod("SwitchToEditMode",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                            null, new[] { typeof(bool) }, null);
                        if (mSwitch == null)
                        {
                            WriteLine(BuildEventLine("SwitchToEditModeFailed",
                                "reason=method-missing"), flush: true);
                            throw new MissingMethodException("scnEditor.SwitchToEditMode(Boolean)");
                        }
                        try
                        {
                            mSwitch.Invoke(editor, new object[] { false });
                            WriteLine(BuildEventLine("SwitchToEditModeSucceeded",
                                "argument=false"), flush: true);
                        }
                        catch (Exception ex)
                        {
                            WriteLine(BuildEventLine("SwitchToEditModeFailed",
                                "reason=invoke-failed|detail=" + Sanitize(ex.Message)), flush: true);
                            throw;
                        }
                    }
                }

                RestoreSelectedFloors();

                Time.captureFramerate = _savedCaptureFramerate;
                Application.targetFrameRate = _savedTargetFrameRate;
                QualitySettings.vSyncCount = _savedVSyncCount;

                WriteLine(BuildEventLine("StateRestored",
                    "captureFramerate=" + _savedCaptureFramerate + "|targetFrameRate=" + _savedTargetFrameRate +
                    "|vSyncCount=" + _savedVSyncCount + "|selectedFloors=" + FmtSelected(_savedSelectedFloorSeqs) +
                    "|rdcAuto=" + Fmt(ReadRdcAuto())), flush: true);
            }
            catch (Exception ex)
            {
                Log.Exception(UiText.LogVcPocRestoreError, ex);
                try { WriteLine(BuildEventLine(_dvaProbeEnabled ? "DvaError" : "PocError", "reason=restore-failed|detail=" + Sanitize(ex.Message)), flush: true); } catch { }
            }
        }
        // ================================================================
        // 播放启动（section 7 / 9）
        // ================================================================

        private static bool StartEditorPlayback(out string error)
        {
            error = null;
            try
            {
                object editor = Editor();
                if (editor == null) { error = "editor-null"; return false; }

                object floorsObj = _tEditor?.GetProperty("floors",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(editor, null);
                if (floorsObj == null)
                {
                    var fField = _tEditor?.GetField("floors",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fField != null) floorsObj = fField.GetValue(editor);
                }
                if (floorsObj == null) { error = "floors-null"; return false; }

                // floors 是 List<scrFloor>；取第 0 格。
                System.Collections.IList list = floorsObj as System.Collections.IList;
                if (list == null || list.Count < 1) { error = "floors-empty"; return false; }
                object floor0 = list[0];

                MethodInfo mSelect = _tEditor?.GetMethod("SelectFloor",
                    BindingFlags.Public | BindingFlags.Instance);
                MethodInfo mPlay = _tEditor?.GetMethod("Play", BindingFlags.Public | BindingFlags.Instance);
                if (mSelect == null) { error = "SelectFloor-missing"; return false; }
                if (mPlay == null) { error = "Play-missing"; return false; }

                string floorName = floorsObj.GetType().Name + ".Count=" + list.Count;
                WriteLine(BuildEventLine("PlaybackRequested", "startFloor=0|floors=" + floorName + "|editor=" + Fmt(editor != null)), flush: true);
                Log.Info(UiText.LogVcPocPlaybackRequested);

                // SelectFloor(floor0, cameraJump:false)
                mSelect.Invoke(editor, new object[] { floor0, false });
                // 官方 editor.Play()
                mPlay.Invoke(editor, null);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Exception(UiText.LogVcPocStartPlaybackFailed, ex);
                return false;
            }
        }

        // ================================================================
        // 反射读取
        // ================================================================

        private static bool IsPlaybackReady()
        {
            object conductor = Conductor();
            if (conductor == null) return false;

            object state = ReadControllerState();
            if (IsFailureState(state)) return false;

            string s = ToState(state);
            // Do not treat editor.playMode or hasSongStarted alone as readiness:
            // both can be true while the controller is still entering play, and
            // the previous probe activated on a failed runtime in that window.
            return s == "PlayerControl" || s == "Countdown";
        }

        private static bool CaptureAnchor()
        {
            object conductor = Conductor();
            if (conductor == null) return false;

            try
            {
                object raw = ReadProperty(_tConductor, _pSongPosI);
                double? v = ToDouble(raw);
                if (!v.HasValue || double.IsNaN(v.Value) || double.IsInfinity(v.Value) ||
                    Math.Abs(v.Value) > AnchorInvalidThresholdSeconds)
                {
                    Log.Warn(UiText.LogVcPocAnchorAbnormal);
                    WriteLine(BuildEventLine("PocError", "reason=anchor-invalid|startSongPosition=" + Fmt(raw)), flush: true);
                    return false;
                }

                _startSongPosition = v.Value;

                // pitch：优先真实 conductor.song.pitch / AudioSource.pitch；失败则记录 unavailable 并回退 1。
                _pitch = TryReadPitch(out bool unavailable);
                _pitchUnavailable = unavailable;

                WriteLine(BuildEventLine("AnchorCaptured",
                    "startSongPosition=" + Fmt(_startSongPosition) +
                    "|songposition_minusv=" + Fmt(ReadProperty(_tConductor, _pSongPosMinusV)) +
                    "|dspTime=" + Fmt(ReadProperty(_tConductor, _pDspTime)) +
                    "|dspTimeSong=" + Fmt(ReadProperty(_tConductor, _pDspTimeSong)) +
                    "|beatNumber=" + Fmt(ReadProperty(_tConductor, _pBeatNumber)) +
                    "|currentSeqID=" + Fmt(ReadCurrentSeqId()) +
                    "|playerFloor=" + Fmt(ReadPlayerFloor()) +
                    "|pitch=" + Fmt(_pitch) +
                    (_pitchUnavailable ? "|pitchUnavailable=true|pitchUsedFallback=1" : string.Empty)), flush: true);

                _prevPlayerFloor = ToInt(ReadPlayerFloor());
                _prevControllerState = ReadProperty(_tController, _pState);
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception(UiText.LogVcPocAnchorFailed, ex);
                return false;
            }
        }

        private static double TryReadPitch(out bool unavailable)
        {
            unavailable = true;
            try
            {
                object conductor = Conductor();
                object song = _tConductor == null ? null :
                    _tConductor.GetProperty("song", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(conductor, null);
                if (song != null)
                {
                    PropertyInfo pPitch = song.GetType().GetProperty("pitch",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (pPitch != null)
                    {
                        object p = pPitch.GetValue(song, null);
                        double? d = ToDouble(p);
                        if (d.HasValue && !double.IsNaN(d.Value) && d.Value > 0.0001)
                        {
                            unavailable = false;
                            return d.Value;
                        }
                    }
                }

                // 回退：conductor 挂载的 AudioSource.pitch（只读反射，不引入 AudioModule 编译引用）
                object asrc = _tConductor == null ? null :
                    _tConductor.GetProperty("audioSource", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(conductor, null);
                if (asrc != null)
                {
                    PropertyInfo pPitch = asrc.GetType().GetProperty("pitch",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (pPitch != null)
                    {
                        object p = pPitch.GetValue(asrc, null);
                        double? d = ToDouble(p);
                        if (d.HasValue && !double.IsNaN(d.Value) && d.Value > 0.0001)
                        {
                            unavailable = false;
                            return d.Value;
                        }
                    }
                }
            }
            catch
            {
                // 保持 unavailable true
            }
            return 1.0;
        }

        private static void EnsureTypes()
        {
            try
            {
                if (_gameAssembly == null)
                {
                    foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "Assembly-CSharp")
                        {
                            _gameAssembly = asm;
                            break;
                        }
                    }
                }
            }
            catch { _gameAssembly = null; }

            if (_gameAssembly == null) return;
            try { _tAdoBase = _tAdoBase ?? _gameAssembly.GetType("ADOBase"); } catch { }
            try { _tConductor = _tConductor ?? _gameAssembly.GetType("scrConductor"); } catch { }
            try { _tController = _tController ?? _gameAssembly.GetType("scrController"); } catch { }
            try { _tEditor = _tEditor ?? _gameAssembly.GetType("scnEditor"); } catch { }
            try { _tPlayer = _tPlayer ?? _gameAssembly.GetType("scrPlayer"); } catch { }
            try { _tFloor = _tFloor ?? _gameAssembly.GetType("scrFloor"); } catch { }
            try { _tPlanet = _tPlanet ?? _gameAssembly.GetType("scrPlanet"); } catch { }
            try { _tRdc = _tRdc ?? _gameAssembly.GetType("RDC"); } catch { }

            if (_tConductor != null)
            {
                _pSongPosI = GetProperty(_tConductor, "songposition_minusi");
                _pSongPosMinusV = GetProperty(_tConductor, "songposition_minusv");
                _pBeatNumber = GetProperty(_tConductor, "beatNumber");
                _pBarNumber = GetProperty(_tConductor, "barNumber");
                _pDspTime = GetProperty(_tConductor, "dspTime");
                _pDspTimeSong = GetProperty(_tConductor, "dspTimeSong");
                _pHasSongStarted = GetProperty(_tConductor, "hasSongStarted");
                _mConductorUpdate = _tConductor.GetMethod("Update", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            if (_tController != null)
            {
                _pState = GetProperty(_tController, "state");
                _pPaused = GetProperty(_tController, "paused");
                _fCurrentSeq = _tController.GetField("currentSeqID",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _mControllerUpdate = _tController.GetMethod("Update",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _mPlayerControlUpdate = _tController.GetMethod("PlayerControl_Update",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _mControllerLateUpdate = _tController.GetMethod("LateUpdate",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            if (_tPlayer != null)
            {
                _mPlayerHit = _tPlayer.GetMethod("Hit",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(bool) }, null);
            }
            if (_tPlanet != null)
            {
                _mPlanetRefreshAngles = _tPlanet.GetMethod("Update_RefreshAngles",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            if (_tRdc != null)
            {
                _pRdcAuto = _tRdc.GetProperty("auto",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }
        }

        private static PropertyInfo GetProperty(Type type, string name)
        {
            if (type == null) return null;
            return type.GetProperty(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ??
                   type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        }

        private static object StaticValue(Type type, string member)
        {
            if (type == null) return null;
            try
            {
                PropertyInfo p = type.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (p != null) return p.GetValue(null, null);
                FieldInfo f = type.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f != null) return f.GetValue(null);
                return null;
            }
            catch { return null; }
        }

        private static object InstanceValue(object instance, Type type, string member)
        {
            if (instance == null || type == null) return null;
            try
            {
                PropertyInfo p = type.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null) return p.GetValue(instance, null);
                FieldInfo f = type.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) return f.GetValue(instance);
                return null;
            }
            catch { return null; }
        }

        private static object Conductor() => StaticValue(_tAdoBase, "conductor");
        private static object Controller() => StaticValue(_tAdoBase, "controller");
        private static object Editor() => StaticValue(_tAdoBase, "editor");

        private static object ReadProperty(Type owner, PropertyInfo p)
        {
            if (owner == null || p == null) return null;
            try { return p.GetValue(InstanceFor(owner), null); }
            catch { return null; }
        }

        private static object InstanceFor(Type owner)
        {
            if (owner == _tConductor) return Conductor();
            if (owner == _tController) return Controller();
            if (owner == _tEditor) return Editor();
            return null;
        }

        private static bool? ReadEditorPlayMode()
        {
            object editor = Editor();
            if (editor == null || _tEditor == null) return null;
            try
            {
                return ToBool(InstanceValue(editor, _tEditor, "playMode"));
            }
            catch { return null; }
        }

        private static int ReadPlayerFloor()
        {
            try
            {
                object controller = Controller();
                object playerOne = controller == null || _tController == null ? null :
                    InstanceValue(controller, _tController, "playerOne");
                if (playerOne == null) return -1;
                object currFloor = InstanceValue(playerOne, playerOne.GetType(), "currFloor");
                if (currFloor == null) return -1;
                object seq = InstanceValue(currFloor, currFloor.GetType(), "seqID");
                return ToInt(seq);
            }
            catch { return -1; }
        }

        private static object CurrentPlayer()
        {
            object controller = Controller();
            return controller == null ? null : InstanceValue(controller, _tController, "playerOne");
        }

        private static object CurrentFloor(object player)
        {
            return player == null ? null : InstanceValue(player, player.GetType(), "currFloor");
        }

        private static object NextFloor(object floor)
        {
            return floor == null ? null : InstanceValue(floor, floor.GetType(), "nextfloor");
        }

        private static int FloorSeq(object floor)
        {
            return floor == null ? -1 : ToInt(InstanceValue(floor, floor.GetType(), "seqID"));
        }

        private static int ReadCurrentSeqId()
        {
            try
            {
                object controller = Controller();
                return controller == null || _fCurrentSeq == null ? -1 : ToInt(_fCurrentSeq.GetValue(controller));
            }
            catch { return -1; }
        }

        private static object ReadControllerState()
        {
            return ReadProperty(_tController, _pState);
        }

        private static int ReadNextFloorSeqId()
        {
            return FloorSeq(NextFloor(CurrentFloor(CurrentPlayer())));
        }

        private static object ReadNextFloorEntryTime()
        {
            object next = NextFloor(CurrentFloor(CurrentPlayer()));
            return next == null ? null : InstanceValue(next, next.GetType(), "entryTime");
        }

        private static bool? ReadRdcAuto()
        {
            try
            {
                return _pRdcAuto == null ? null : ToBool(_pRdcAuto.GetValue(null, null));
            }
            catch { return null; }
        }

        private static bool TryWriteRdcAuto(bool value)
        {
            try
            {
                if (_pRdcAuto == null || !_pRdcAuto.CanWrite) return false;
                _pRdcAuto.SetValue(null, value, null);
                return true;
            }
            catch { return false; }
        }

        private static bool TrySetInstanceValue(object instance, Type type, string member, object value)
        {
            if (instance == null || type == null) return false;
            try
            {
                PropertyInfo p = type.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.CanWrite)
                {
                    p.SetValue(instance, ConvertValue(value, p.PropertyType), null);
                    return true;
                }
                FieldInfo f = type.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null) return false;
                f.SetValue(instance, ConvertValue(value, f.FieldType));
                return true;
            }
            catch { return false; }
        }

        private static object ConvertValue(object value, Type destination)
        {
            if (value == null || destination == null || destination.IsInstanceOfType(value)) return value;
            return Convert.ChangeType(value, destination, CultureInfo.InvariantCulture);
        }

        private static bool IsFailureState(object state)
        {
            string s = ToState(state);
            return string.Equals(s, "Fail", StringComparison.Ordinal) ||
                   string.Equals(s, "Fail2", StringComparison.Ordinal);
        }

        private static string BuildDvaStateFields(string prefix)
        {
            return (string.IsNullOrEmpty(prefix) ? string.Empty : prefix + "|") +
                "logicalFrame=" + _logicalFrameIndex +
                "|forcedSongPosition=" + Fmt(_forcedSongPosition) +
                "|controllerState=" + Fmt(ReadControllerState()) +
                "|playerFloor=" + Fmt(ReadPlayerFloor()) +
                "|currentSeqID=" + Fmt(ReadCurrentSeqId()) +
                "|nextFloorSeqID=" + Fmt(ReadNextFloorSeqId()) +
                "|nextFloorEntryTime=" + Fmt(ReadNextFloorEntryTime()) +
                "|hitCountThisFrame=" + _hitCountThisFrame +
                "|totalHitCount=" + _totalHitCount +
                "|rdcAuto=" + Fmt(ReadRdcAuto());
        }

        private static void WriteExecutionOrder(string eventName)
        {
            if (!_dvaProbeEnabled || _writer == null) return;
            WriteLine(BuildEventLine(eventName,
                "unityFrame=" + Time.frameCount +
                "|logicalFrame=" + _logicalFrameIndex +
                "|forcedSongPosition=" + Fmt(_forcedSongPosition)), flush: false);
        }

        private static void WriteFrameOrderEvent(string eventName, string extra = null)
        {
            if (!_frameOrderProbeEnabled || _writer == null) return;

            var sb = new StringBuilder(640);
            AppendField(sb, "timestamp", NowStamp(), true);
            AppendField(sb, "unityFrame", Time.frameCount.ToString(CultureInfo.InvariantCulture), false);
            AppendField(sb, "probeSequence", (++_probeSequence).ToString(CultureInfo.InvariantCulture), false);
            AppendField(sb, "logicalFrame", _logicalFrameIndex.ToString(CultureInfo.InvariantCulture), false);
            AppendField(sb, "event", eventName, false);
            AppendField(sb, "forcedSongPosition", Fmt(_forcedSongPosition), false);
            AppendField(sb, "songposition_minusi", Fmt(ReadProperty(_tConductor, _pSongPosI)), false);
            AppendField(sb, "controllerState", Fmt(ReadProperty(_tController, _pState)), false);
            AppendField(sb, "playerFloor", Fmt(ReadPlayerFloor()), false);
            AppendField(sb, "currentSeqID", Fmt(ReadCurrentSeqId()), false);
            AppendField(sb, "rdcAuto", Fmt(ReadRdcAuto()), false);
            if (!string.IsNullOrEmpty(extra)) AppendField(sb, "extra", extra, false);
            WriteLine(sb.ToString(), flush: string.Equals(eventName, "CaptureBoundaryEndOfFrame", StringComparison.Ordinal));
        }

        private static bool StartEndOfFrameObserver()
        {
            try
            {
                StopEndOfFrameObserver();
                _frameOrderProbeHost = new GameObject("ADOFAI.Renderist.FrameOrderProbe");
                _frameOrderProbeHost.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(_frameOrderProbeHost);
                EndOfFrameProbeBehaviour observer = _frameOrderProbeHost.AddComponent<EndOfFrameProbeBehaviour>();
                observer.Begin();
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("EditorVisualClockPoc: 创建 EndOfFrame 观察器失败", ex);
                StopEndOfFrameObserver();
                return false;
            }
        }

        private static void StopEndOfFrameObserver()
        {
            GameObject host = _frameOrderProbeHost;
            _frameOrderProbeHost = null;
            if (host == null) return;
            try { UnityEngine.Object.Destroy(host); } catch { }
        }

        private static void OnEndOfFrameCaptureBoundary()
        {
            if (!_running || !_frameOrderProbeEnabled || !_forcedActive ||
                _state != PocState.Active || _pendingStopReason != null)
            {
                return;
            }

            WriteFrameOrderEvent("CaptureBoundaryEndOfFrame");
            if (_stopAfterCaptureBoundary)
            {
                // 先记录最终画面的 EndOfFrame，再撤销 Forced Clock；下一次 ModEntry.OnUpdate 完成恢复。
                _forcedActive = false;
                RequestSafeStop("completed", "frame-order-probe-complete");
            }
        }
        private static void ReadSelectedFloorSeqs(List<int> into)
        {
            try
            {
                object editor = Editor();
                if (editor == null || _tEditor == null) return;
                object sel = InstanceValue(editor, _tEditor, "selectedFloors");
                if (sel is System.Collections.IEnumerable en)
                {
                    foreach (object floor in en)
                    {
                        if (floor == null) continue;
                        object seq = InstanceValue(floor, floor.GetType(), "seqID");
                        int v = ToInt(seq);
                        if (v >= 0) into.Add(v);
                    }
                }
            }
            catch { }
        }

        private static void RestoreSelectedFloors()
        {
            try
            {
                object editor = Editor();
                if (editor == null || _tEditor == null || _savedSelectedFloorSeqs.Count == 0) return;
                object floorsObj = InstanceValue(editor, _tEditor, "floors");
                if (!(floorsObj is System.Collections.IList list) || list.Count == 0)
                {
                    WriteLine(BuildEventLine("SelectionRestoreSkipped", "reason=floors-unavailable"), flush: true);
                    return;
                }

                var targets = new List<object>();
                foreach (int want in _savedSelectedFloorSeqs)
                {
                    object found = null;
                    foreach (object floor in list)
                    {
                        if (FloorSeq(floor) == want) { found = floor; break; }
                    }
                    if (found == null)
                    {
                        WriteLine(BuildEventLine("SelectionRestoreSkipped", "reason=floor-not-found|seqID=" + want), flush: true);
                        return;
                    }
                    targets.Add(found);
                }

                if (targets.Count == 1)
                {
                    MethodInfo select = _tEditor.GetMethod("SelectFloor",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, new[] { _tFloor, typeof(bool) }, null);
                    if (select == null) throw new MissingMethodException("scnEditor.SelectFloor");
                    select.Invoke(editor, new[] { targets[0], (object)false });
                    return;
                }

                var ordered = new List<Tuple<int, object>>();
                for (int i = 0; i < targets.Count; i++)
                {
                    ordered.Add(Tuple.Create(FloorSeq(targets[i]), targets[i]));
                }
                ordered.Sort((a, b) => a.Item1.CompareTo(b.Item1));

                var seqs = new List<int>();
                foreach (Tuple<int, object> item in ordered) seqs.Add(item.Item1);
                for (int i = 1; i < seqs.Count; i++)
                {
                    if (seqs[i] != seqs[i - 1] + 1)
                    {
                        WriteLine(BuildEventLine("SelectionRestoreSkipped", "reason=non-contiguous-selection"), flush: true);
                        return;
                    }
                }

                MethodInfo multi = _tEditor.GetMethod("MultiSelectFloors",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { _tFloor, _tFloor, typeof(bool) }, null);
                if (multi == null) throw new MissingMethodException("scnEditor.MultiSelectFloors");
                multi.Invoke(editor, new[] { ordered[0].Item2, ordered[ordered.Count - 1].Item2, (object)true });
            }
            catch (Exception ex)
            {
                Log.Debug("EditorVisualClockPoc: restore selected floors failed: " + ex.Message);
                WriteLine(BuildEventLine("SelectionRestoreSkipped", "reason=exception|detail=" + Sanitize(ex.Message)), flush: true);
            }
        }
        private static bool IsProbablyEditorNow()
        {
            // 与 EditorTimeProbe 一致：以实机验证场景名 scnEditor 为主，ADOBase.isLevelEditor 为辅助。
            if (string.Equals(SceneName(), "scnEditor", StringComparison.Ordinal)) return true;
            return ReadStaticNullableBool(_tAdoBase, "isLevelEditor") == true;
        }

        private static string SceneName()
        {
            try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; }
            catch { return Unavailable; }
        }

        private static bool? ReadStaticNullableBool(Type type, string member) => ToBool(StaticValue(type, member));

        /// <summary>启动条件校验，返回拒绝原因；null 表示可启动。</summary>
        private static string ValidateStartConditions(bool enableDva, bool enableFrameOrderProbe)
        {
            EnsureTypes();
            if (_tAdoBase == null || _tConductor == null || _tController == null || _tEditor == null)
            {
                return "game-types-unavailable";
            }

            if (enableDva && (_tPlayer == null || _tFloor == null || _tPlanet == null || _tRdc == null ||
                              _mConductorUpdate == null || _mControllerUpdate == null || _mPlayerHit == null ||
                              _mPlanetRefreshAngles == null || _pRdcAuto == null || _fCurrentSeq == null))
            {
                return "dva-api-unavailable";
            }

            if (enableFrameOrderProbe && (_mConductorUpdate == null || _mPlayerControlUpdate == null ||
                                          _mControllerLateUpdate == null || _mPlanetRefreshAngles == null ||
                                          _pRdcAuto == null))
            {
                return "frame-order-probe-api-unavailable";
            }

            if (enableFrameOrderProbe && ReadRdcAuto() != true)
            {
                return "official-autoplay-not-enabled";
            }

            if (ReadStaticNullableBool(_tAdoBase, "isLevelEditor") != true)
            {
                return "not-in-editor";
            }

            object editor = Editor();
            if (editor == null)
            {
                return "editor-null";
            }

            // Editor 已加载正常关卡
            object customLevel = _tEditor.GetProperty("customLevel",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(editor, null);
            object floorsObj = _tEditor.GetProperty("floors",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(editor, null);
            bool levelLoaded = customLevel != null || (floorsObj is System.Collections.ICollection fc && fc.Count > 1);
            if (!levelLoaded)
            {
                return "level-not-loaded";
            }

            // Editor 不在 Play
            if (ReadEditorPlayMode() == true)
            {
                return "editor-already-playing";
            }

            if (EditorExportController.IsBusy)
            {
                return "editor-export-busy";
            }

            if (EditorTimeProbe.IsRunning)
            {
                return "time-probe-running";
            }

            if (CaptureService.IsRecording)
            {
                return "capture-recording";
            }

            return null;
        }

        // ================================================================
        // Harmony（section 12）
        // ================================================================

        private static void RegisterHooks()
        {
            UnregisterHooks();
            if (_tConductor == null) return;

            try
            {
                Harmony harmony = ModEntry.Harmony;
                if (harmony == null) return;

                PropertyInfo songVec = _tConductor.GetProperty("songposition_minusi",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo getter = songVec?.GetGetMethod(true);
                MethodInfo setter = songVec?.GetSetMethod(true);

                if (getter != null)
                {
                    harmony.Patch(getter,
                        postfix: new HarmonyMethod(typeof(EditorVisualClockPoc), nameof(SongPosGetterPostfix)));
                }
                if (setter != null)
                {
                    harmony.Patch(setter,
                        prefix: new HarmonyMethod(typeof(EditorVisualClockPoc), nameof(SongPosSetterPrefix)));
                }
                if (_mConductorUpdate != null)
                {
                    harmony.Patch(_mConductorUpdate,
                        prefix: new HarmonyMethod(typeof(EditorVisualClockPoc), nameof(ConductorUpdatePrefix)),
                        postfix: new HarmonyMethod(typeof(EditorVisualClockPoc), nameof(ConductorUpdatePostfix)));
                }
                if (_dvaProbeEnabled && _mControllerUpdate != null)
                {
                    harmony.Patch(_mControllerUpdate,
                        prefix: new HarmonyMethod(typeof(EditorVisualClockPoc), nameof(ControllerUpdatePrefix)),
                        postfix: new HarmonyMethod(typeof(EditorVisualClockPoc), nameof(ControllerUpdatePostfix)));
                }
                if (_frameOrderProbeEnabled)
                {
                    harmony.Patch(_mPlayerControlUpdate,
                        prefix: new HarmonyMethod(typeof(EditorVisualClockPoc), nameof(PlayerControlUpdatePrefix)),
                        postfix: new HarmonyMethod(typeof(EditorVisualClockPoc), nameof(PlayerControlUpdatePostfix)));
                    harmony.Patch(_mPlayerHit,
                        prefix: new HarmonyMethod(typeof(EditorVisualClockPoc), nameof(PlayerHitPrefix)),
                        postfix: new HarmonyMethod(typeof(EditorVisualClockPoc), nameof(PlayerHitPostfix)));
                    harmony.Patch(_mControllerLateUpdate,
                        prefix: new HarmonyMethod(typeof(EditorVisualClockPoc), nameof(ControllerLateUpdatePrefix)),
                        postfix: new HarmonyMethod(typeof(EditorVisualClockPoc), nameof(ControllerLateUpdatePostfix)));
                    harmony.Patch(_mPlanetRefreshAngles,
                        prefix: new HarmonyMethod(typeof(EditorVisualClockPoc), nameof(PlanetRefreshAnglesPrefix)),
                        postfix: new HarmonyMethod(typeof(EditorVisualClockPoc), nameof(PlanetRefreshAnglesPostfix)));
                }

                _hooksRegistered = true;
            }
            catch (Exception ex)
            {
                Log.Exception("EditorVisualClockPoc: 注册 Harmony Hook 失败", ex);
                // A partial Patch() sequence must not leave a live hook behind
                // while _hooksRegistered is still false.
                try
                {
                    _hooksRegistered = true;
                    UnregisterHooks();
                }
                catch { }
            }
        }

        private static void UnregisterHooks()
        {
            if (!_hooksRegistered) return;
            Harmony harmony = ModEntry.Harmony;
            if (harmony != null)
            {
                try
                {
                    PropertyInfo songVec = _tConductor?.GetProperty("songposition_minusi",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    MethodInfo getter = songVec?.GetGetMethod(true);
                    MethodInfo setter = songVec?.GetSetMethod(true);
                    if (getter != null) harmony.Unpatch(getter, HarmonyPatchType.All, ModEntry.HarmonyId);
                    if (setter != null) harmony.Unpatch(setter, HarmonyPatchType.All, ModEntry.HarmonyId);
                    if (_mConductorUpdate != null) harmony.Unpatch(_mConductorUpdate, HarmonyPatchType.All, ModEntry.HarmonyId);
                    if (_mControllerUpdate != null) harmony.Unpatch(_mControllerUpdate, HarmonyPatchType.All, ModEntry.HarmonyId);
                    if (_mPlayerControlUpdate != null) harmony.Unpatch(_mPlayerControlUpdate, HarmonyPatchType.All, ModEntry.HarmonyId);
                    if (_mPlayerHit != null) harmony.Unpatch(_mPlayerHit, HarmonyPatchType.All, ModEntry.HarmonyId);
                    if (_mControllerLateUpdate != null) harmony.Unpatch(_mControllerLateUpdate, HarmonyPatchType.All, ModEntry.HarmonyId);
                    if (_mPlanetRefreshAngles != null) harmony.Unpatch(_mPlanetRefreshAngles, HarmonyPatchType.All, ModEntry.HarmonyId);
                }
                catch (Exception ex)
                {
                    Log.Exception("EditorVisualClockPoc: 撤销 Harmony Hook 失败", ex);
                }
            }
            _hooksRegistered = false;
        }

        // ---- A. Getter：Active 时返回 forcedSongPosition ----
        private static void SongPosGetterPostfix(ref double __result)
        {
            if (_running && _forcedActive)
            {
                __result = _forcedSongPosition;
            }
        }

        // ---- B. Setter：Active 时把传入值替换为 forcedSongPosition ----
        private static void SongPosSetterPrefix(ref double value)
        {
            if (_running && _forcedActive)
            {
                value = _forcedSongPosition;
            }
        }

        // ---- C. conductor.Update Prefix：每新 Unity Frame 确定本帧 forcedSongPosition ----
        private static void ConductorUpdatePrefix()
        {
            OnConductorUpdatePrefix();
        }

        private static void ConductorUpdatePostfix()
        {
            OnConductorUpdatePostfix();
        }

        private static void ControllerUpdatePrefix()
        {
            WriteExecutionOrder("ControllerUpdatePrefix");
        }

        private static void ControllerUpdatePostfix()
        {
            WriteExecutionOrder("ControllerUpdatePostfix");
        }

        private static void PlayerControlUpdatePrefix()
        {
            _playerFloorAtPlayerControlPrefix = ReadPlayerFloor();
            WriteFrameOrderEvent("PlayerControlPrefix");
        }

        private static void PlayerControlUpdatePostfix()
        {
            int afterFloor = ReadPlayerFloor();
            WriteFrameOrderEvent("PlayerControlPostfix");
            if (_playerFloorAtPlayerControlPrefix >= 0 && afterFloor >= 0 &&
                _playerFloorAtPlayerControlPrefix != afterFloor)
            {
                WriteFrameOrderEvent("OfficialAutoplayFloorAdvanced",
                    "beforeFloor=" + _playerFloorAtPlayerControlPrefix + "|afterFloor=" + afterFloor);
            }
        }

        private static void PlayerHitPrefix()
        {
            WriteFrameOrderEvent("PlayerHitPrefix");
        }

        private static void PlayerHitPostfix()
        {
            WriteFrameOrderEvent("PlayerHitPostfix");
        }

        private static void ControllerLateUpdatePrefix()
        {
            WriteFrameOrderEvent("ControllerLateUpdatePrefix");
        }

        private static void ControllerLateUpdatePostfix()
        {
            WriteFrameOrderEvent("ControllerLateUpdatePostfix");
        }

        private static void PlanetRefreshAnglesPrefix()
        {
            WriteFrameOrderEvent("PlanetRefreshAnglesPrefix");
        }

        private static void PlanetRefreshAnglesPostfix()
        {
            WriteFrameOrderEvent("PlanetRefreshAnglesPostfix");
        }

        // ================================================================
        // 日志
        // ================================================================

        private static string BuildEventLine(string eventName, string extra)
        {
            var sb = new StringBuilder(512);
            AppendField(sb, "timestamp", NowStamp(), true);
            AppendField(sb, "frame", Time.frameCount.ToString(CultureInfo.InvariantCulture), false);
            AppendField(sb, "event", eventName, false);
            if (!string.IsNullOrEmpty(extra)) AppendField(sb, "extra", extra, false);
            return sb.ToString();
        }

        private static void WriteLine(string line, bool flush)
        {
            StreamWriter w = _writer;
            if (w == null) return;
            w.WriteLine(line);
            if (flush)
            {
                try { w.Flush(); } catch { }
            }
        }

        private static void WriteHeader()
        {
            if (_writer == null) return;
            _writer.WriteLine(_dvaProbeEnabled
                ? "# ADOFAI Renderist DVA Runtime Probe v1"
                : (_frameOrderProbeEnabled
                    ? "# ADOFAI Renderist Frame Order Probe v1"
                    : "# ADOFAI Renderist Editor Forced Visual Clock PoC v1"));
            _writer.WriteLine("# version=" + ModEntry.ModVersion);
            _writer.WriteLine("# startedAt=" + NowStamp());
            _writer.WriteLine("# outputFPS=" + OutputFps);
            _writer.WriteLine("# targetLogicalFrames=" + TargetLogicalFrameCount);
            _writer.WriteLine("# autoHit=" + (_dvaProbeEnabled ? "runtime-probe" : "disabled"));
            _writer.WriteLine("# frameOrderProbe=" + _frameOrderProbeEnabled);
            _writer.WriteLine("# rdcAutoMode=" + _selectedRdcAutoMode);
            _writer.WriteLine("# maxHitsPerFrame=" + MaxHitsPerFrame);
            _writer.WriteLine("# unityVersion=" + Application.unityVersion);
            _writer.WriteLine("# holdSupport=disabled");
            _writer.WriteLine("# midspinSupport=disabled");
            _writer.WriteLine("# gimmickSupport=disabled");
            _writer.WriteLine("# checkpointSupport=disabled");
            _writer.Flush();
        }

        private static string ResolveDiagnosticsRoot()
        {
            Settings settings = ModEntry.Settings;
            string configured = settings != null ? settings.OutputDirectory : string.Empty;
            DirectoryValidationResult v = OutputPath.ValidateDirectory(configured);
            if (v.Outcome == DirectoryValidationOutcome.Accept && !string.IsNullOrEmpty(v.NormalizedPath))
            {
                return v.NormalizedPath;
            }
            try
            {
                return Path.Combine(Application.persistentDataPath, "ADOFAI.Renderist");
            }
            catch { return null; }
        }

        private static string ResolveUniqueLogPath(string dir, string baseName)
        {
            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string basePath = Path.Combine(dir, baseName + "-" + stamp + ".log");
                string path = basePath;
                int i = 0;
                while (File.Exists(path))
                {
                    i++;
                    path = Path.Combine(dir, baseName + "-" + stamp + "_" + i.ToString("000", CultureInfo.InvariantCulture) + ".log");
                }
                return path;
            }
            catch { return null; }
        }

        private static void FailSafeClose()
        {
            try
            {
                StreamWriter w = _writer;
                _writer = null;
                if (w != null) { w.Flush(); w.Close(); w.Dispose(); }
            }
            catch { }
            _logPath = null;
        }

        private static string NowStamp() =>
            DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

        private static string Fmt(object v)
        {
            if (v == null) return Unavailable;
            if (v is double d) return d.ToString("0.######", CultureInfo.InvariantCulture);
            if (v is float f) return f.ToString("0.######", CultureInfo.InvariantCulture);
            if (v is bool b) return b ? "true" : "false";
            if (v is Enum e) return e.ToString();
            if (v is int i) return i.ToString(CultureInfo.InvariantCulture);
            if (v is long l) return l.ToString(CultureInfo.InvariantCulture);
            if (v is byte by) return by.ToString(CultureInfo.InvariantCulture);
            if (v is short sh) return sh.ToString(CultureInfo.InvariantCulture);
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static string Sanitize(string s) => s == null ? string.Empty : s.Replace('\r', ' ').Replace('\n', ' ').Replace('|', ';');

        private static string FmtSelected(List<int> seqs)
        {
            if (seqs == null || seqs.Count == 0) return Unavailable;
            return string.Join(",", seqs.ConvertAll(x => x.ToString(CultureInfo.InvariantCulture)).ToArray());
        }

        private static void AppendField(StringBuilder sb, string key, string value, bool first)
        {
            if (!first) sb.Append(" | ");
            sb.Append(key).Append('=').Append(Sanitize(value ?? string.Empty));
        }

        private static bool? ToBool(object v)
        {
            if (v == null) return null;
            try { return Convert.ToBoolean(v, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static double? ToDouble(object v)
        {
            if (v == null) return null;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static int ToInt(object v)
        {
            if (v == null) return -1;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch { return -1; }
        }

        private static string ToState(object v)
        {
            if (v == null) return null;
            return v is Enum e ? e.ToString() : Convert.ToString(v, CultureInfo.InvariantCulture);
        }
    }
}
