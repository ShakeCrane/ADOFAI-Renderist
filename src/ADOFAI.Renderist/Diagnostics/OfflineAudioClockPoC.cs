using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;
using ADOFAI.Renderist.Export;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Diagnostics
{
    /// <summary>
    /// Route B architecture validation only. No PNG/audio file and no formal scheduler dependency.
    /// </summary>
    internal static class OfflineAudioClockPoC
    {
        private const int Fps = 60;
        private const int FirstFrames = 16;
        private const int ExtendedFrames = 64;
        private const int MaxProbeFrames = 1024;
        private const int MaxHitsPerFrame = 16;
        private const string HarmonyId = "com.adofai.renderist.routeb-poc";

        private enum State { Idle, Starting, Preflight, InitializingAudioRenderer, Waiting, Active, Stopping, Failed }
        private sealed class Snapshot
        {
            internal int Frame;
            internal long Samples;
            internal double Expected;
            internal double AudioSeconds;
            internal double Forced;
            internal int Floor;
            internal int Seq;
            internal int Hits;
            internal string Angle;
            internal string CachedAngle;
        }
        private sealed class EndOfFrameHost : MonoBehaviour
        {
            private Coroutine _routine;
            internal void Begin() { _routine = StartCoroutine(Observe()); }
            private IEnumerator Observe()
            {
                while (true) { yield return new WaitForEndOfFrame(); OnFrameBoundary(); }
            }
        }
        private sealed class OfflineAudioClock : IDisposable
        {
            private NativeArray<float> _buffer;
            private int _sampleRate;
            private int _channels;
            private bool _started;
            internal long Samples { get; private set; }
            internal int LastSampleCount { get; private set; }
            internal double Seconds { get { return _sampleRate <= 0 ? 0 : Samples / (double)_sampleRate; } }

            internal OfflineAudioClock(int captureFps)
            {
                if (captureFps <= 0) throw new ArgumentOutOfRangeException("captureFps");
            }

            internal bool Start(out string error)
            {
                error = null;
                try
                {
                    _sampleRate = AudioSettings.outputSampleRate;
                    _channels = ChannelCount(AudioSettings.speakerMode);
                    if (_sampleRate <= 0 || _channels <= 0) { error = "audio-format-unavailable"; return false; }
                    if (!AudioRenderer.Start()) { error = "AudioRenderer.Start=false"; return false; }
                    Samples = 0;
                    LastSampleCount = 0;
                    _started = true;
                    WriteEvent("AudioRendererStarted", "sampleRate=" + _sampleRate + "|channels=" + _channels);
                    return true;
                }
                catch (Exception ex) { error = ex.Message; Stop(); return false; }
            }

            internal bool Capture(out string error)
            {
                error = null;
                if (!_started) { error = "audio-clock-not-started"; return false; }
                try
                {
                    LastSampleCount = Math.Max(0, AudioRenderer.GetSampleCountForCaptureFrame());
                    WriteEvent("AudioRendererSampleCount", "sampleCount=" + LastSampleCount);
                    if (LastSampleCount == 0) { error = "zero-samples"; return false; }
                    int count = checked(LastSampleCount * _channels);
                    if (!_buffer.IsCreated || _buffer.Length != count)
                    {
                        if (_buffer.IsCreated) _buffer.Dispose();
                        _buffer = new NativeArray<float>(count, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                    }
                    if (!AudioRenderer.Render(_buffer)) { error = "AudioRenderer.Render=false"; return false; }
                    Samples += LastSampleCount;
                    WriteEvent("AudioRendererCaptured", "sampleCount=" + LastSampleCount
                        + "|capturedSampleFrames=" + Samples + "|capturedSeconds=" + Fmt(Seconds));
                    return true;
                }
                catch (Exception ex) { error = ex.Message; return false; }
            }

            internal void Stop()
            {
                if (!_started) return;
                try { AudioRenderer.Stop(); } catch { }
                _started = false;
                WriteEvent("AudioRendererStopped", "capturedSampleFrames=" + Samples + "|capturedSeconds=" + Fmt(Seconds));
            }

            public void Dispose()
            {
                Stop();
                if (_buffer.IsCreated) { try { _buffer.Dispose(); } catch { } }
            }

            private static int ChannelCount(AudioSpeakerMode mode)
            {
                switch (mode.ToString())
                {
                    case "Mono": return 1;
                    case "Stereo": return 2;
                    case "Quad": return 4;
                    case "Surround": return 5;
                    case "Mode5point1": return 6;
                    case "Mode7point1": return 8;
                    default: return 2;
                }
            }
        }

        private static State _state;
        private static bool _running;
        private static bool _forcedActive;
        private static bool _ownsPlayback;
        private static bool _hooksRegistered;
        private static bool _stopRequested;
        private static bool _startPending;
        private static string _failureReason;
        private static string _stopReason;
        private static int _frame;
        private static int _targetFrames = FirstFrames;
        private static int _waitFrames;
        private static int _lastPreparedUnityFrame = -1;
        private static int _hitsThisFrame;
        private static int _totalHits;
        private static int _probeSequence;
        private static float _wallClockStart;
        private static int _floorAtPlayerControlPrefix = -1;
        private static int _runNumber;
        private static double _expected;
        private static double _forced;
        private static double _pitch = 1;
        private static string _logPath;
        private static string _comparisonPath;
        private static StreamWriter _writer;
        private static GameObject _host;
        private static Harmony _harmony;
        private static OfflineAudioClock _audio;
        private static readonly List<Snapshot> _current = new List<Snapshot>();
        private static List<Snapshot> _previous;
        private static int _savedCaptureFramerate;
        private static int _savedTargetFrameRate;
        private static int _savedVSync;
        private static bool? _savedAuto;

        private static Assembly _gameAssembly;
        private static Type _adoBase;
        private static Type _conductor;
        private static Type _controller;
        private static Type _player;
        private static Type _planet;
        private static Type _rdc;
        private static Type _asyncInputUtils;
        private static PropertyInfo _songPosition;
        private static PropertyInfo _calibrationI;
        private static MethodInfo _conductorUpdate;
        private static MethodInfo _songGetter;
        private static MethodInfo _songSetter;
        private static MethodInfo _calibrationGetter;
        private static MethodInfo _playerControlUpdate;
        private static MethodInfo _controllerLateUpdate;
        private static MethodInfo _playerHit;
        private static MethodInfo _planetRefresh;
        private static MethodInfo _adjustAngle;

        public static bool IsRunning { get { return _running; } }
        public static string StateName
        {
            get
            {
                switch (_state)
                {
                    case State.Starting: return "Starting";
                    case State.Preflight: return "Preflight";
                    case State.InitializingAudioRenderer: return "Initializing AudioRenderer";
                    case State.Waiting: return "Waiting for playback";
                    case State.Active: return "Running";
                    case State.Failed: return "Failed: " + (_failureReason ?? "unknown");
                    default: return _state.ToString();
                }
            }
        }

        public static string LogPath { get { return _logPath; } }
        public static string ComparisonPath { get { return _comparisonPath; } }
        public static int LogicalFrameIndex { get { return _frame; } }
        public static int TargetLogicalFrameCount { get { return _targetFrames; } }
        public static int TotalHitCount { get { return _totalHits; } }

        public static bool Start()
        {
            Log.Info("POC_BUTTON_CLICK");
            if (_running)
            {
                FailImmediate("already-running");
                return false;
            }
            _failureReason = null;
            _stopRequested = false;
            _stopReason = null;
            _startPending = true;
            _running = true;
            _state = State.Starting;
            Log.Info("POC_STARTING_ENTERED");
            return true;
        }

        private static void StartCore()
        {
            try
            {
                _state = State.Preflight;
                Log.Info("POC_PREFLIGHT_BEGIN");
                if (EditorExportController.IsBusy)
                {
                    Log.Warn("POC_PREFLIGHT_FAILED reason=editor-export-busy");
                    Fail("preflight: editor export is busy");
                    return;
                }
                if (EditorVisualClockPoc.IsRunning)
                {
                    Log.Warn("POC_PREFLIGHT_FAILED reason=visual-clock-poc-running");
                    Fail("preflight: another Visual Clock PoC is running");
                    return;
                }
                EnsureTypes();
                string reject = Validate();
                if (reject != null) { Log.Warn("POC_PREFLIGHT_FAILED reason=" + reject); Fail("preflight: " + reject); return; }
                Log.Info("POC_PREFLIGHT_OK");
                _runNumber++;
                _logPath = ResolveLogPath(_runNumber);
                _writer = new StreamWriter(_logPath, false, new UTF8Encoding(false));
                _writer.AutoFlush = false;
                _current.Clear();
                _comparisonPath = null;
                _frame = 0;
                _targetFrames = FirstFrames;
                _waitFrames = 0;
                _lastPreparedUnityFrame = -1;
                _hitsThisFrame = 0;
                _totalHits = 0;
                _probeSequence = 0;
                _expected = 0;
                _forced = 0;
                _wallClockStart = Time.realtimeSinceStartup;
                _forcedActive = false;
                _state = State.Waiting;
                SaveState();
                Time.captureFramerate = Fps;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = Math.Max(1000, Fps * 4);
                _pitch = ReadPitch();
                WriteHeader();
                if (!RegisterHooks(out string hookError)) { Fail("hook-failed: " + hookError); return; }
                if (!StartEditorPlayback(out string playError)) { Fail("play-failed: " + playError); return; }
                _host = new GameObject("ADOFAI.Renderist.OfflineAudioClockPoC");
                _host.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(_host);
                _host.AddComponent<EndOfFrameHost>().Begin();
                WriteEvent("PoCStarted", "fps=" + Fps + "|targetFrames=" + _targetFrames + "|rdcAuto=" + Fmt(ReadStatic(_rdc, "auto")));
                Log.Info("OfflineAudioClockPoC 已启动：" + _logPath);
            }
            catch (Exception ex)
            {
                Log.Exception("OfflineAudioClockPoC: 启动失败", ex);
                Fail("start-exception: " + ex.Message);
            }
        }
        public static void Tick()
        {
            if (!_running) return;
            try
            {
                if (_stopRequested) { SafeStop(_stopReason ?? "stopped"); return; }
                if (_startPending)
                {
                    Log.Info("POC_TICK status=Starting");
                    _startPending = false;
                    _state = State.Preflight;
                    Log.Info("POC_STARTING_TO_PREFLIGHT");
                    return;
                }
                if (_state == State.Preflight)
                {
                    StartCore();
                    if (!_running || _state == State.Failed) return;
                }
                if (!ModEntry.Enabled) { RequestStop("mod-disabled"); return; }
                if (_state != State.Waiting) return;
                _waitFrames++;
                object state = ReadInstance(ReadStatic(_adoBase, "controller"), "state");
                if (_waitFrames == 1 || IsReady(state)) WriteEvent("PlaybackReadiness", "state=" + Fmt(state));
                if (IsFailure(state)) { RequestStop("controller-failure"); return; }
                if (IsReady(state)) BeginDeterministicPhase();
                else if (_waitFrames > Fps * 30) RequestStop("playback-ready-timeout");
            }
            catch (Exception ex) { Log.Exception("OfflineAudioClockPoC: Tick 失败", ex); RequestStop("tick-exception:" + ex.Message); }
        }

        public static void Stop(string reason) { if (_running) RequestStop(reason ?? "user"); }

        private static void BeginDeterministicPhase()
        {
            if (_state != State.Waiting) return;
            if (!CanonicalizeInitialPlanetState(out string planetError))
            {
                Fail("planet-initial-state-failed: " + planetError);
                return;
            }
            _audio = new OfflineAudioClock(Fps);
            _state = State.InitializingAudioRenderer;
            if (!_audio.Start(out string error)) { Fail("audio-start-failed: " + error); return; }
            _expected = 0;
            _forced = 0;
            _forcedActive = true;
            _state = State.Active;
            WriteEvent("DeterministicPhaseStarted", "expectedFrameTime=0|forcedSongPosition=0|audioCapturedSeconds=0|calibration_i=" + Fmt(ReadCalibrationI()));
        }

        private static void ConductorPrefix()
        {
            if (!_running || !_forcedActive || _state != State.Active || Time.frameCount == _lastPreparedUnityFrame) return;
            _lastPreparedUnityFrame = Time.frameCount;
            _hitsThisFrame = 0;
            _expected = _frame / (double)Fps;
            _forced = _expected * _pitch;
            WriteEvent("ForcedTimePrepared", "expectedFrameTime=" + Fmt(_expected) + "|forcedSongPosition=" + Fmt(_forced) + "|unityFrame=" + Time.frameCount);
        }

        private static void ConductorPostfix()
        {
            if (!_running || !_forcedActive || _state != State.Active) return;
            WriteEvent("ConductorPostfix", string.Empty);
            RenderistAutoPlay.CatchUp();
        }

        private static void PlayerControlPrefix()
        {
            _floorAtPlayerControlPrefix = ReadPlayerFloor();
            WriteEvent("PlayerControlPrefix", "floorBefore=" + _floorAtPlayerControlPrefix);
        }

        private static void PlayerControlPostfix()
        {
            int after = ReadPlayerFloor();
            WriteEvent("PlayerControlPostfix", "floorAfter=" + after);
            if (_floorAtPlayerControlPrefix >= 0 && after != _floorAtPlayerControlPrefix)
                WriteEvent("OfficialAutoplayFloorChanged", "beforeFloor=" + _floorAtPlayerControlPrefix + "|afterFloor=" + after + "|note=RDC.auto-disabled");
        }

        private static void ControllerLatePrefix() { WriteEvent("ControllerLateUpdatePrefix", string.Empty); }
        private static void ControllerLatePostfix() { WriteEvent("ControllerLateUpdatePostfix", string.Empty); }

        private static void HitPrefix(object __instance, object[] __args)
        {
            WriteEvent("HitPrefix", "isAuto=" + Fmt(__args != null && __args.Length > 0 ? __args[0] : null)
                + "|floorBefore=" + ReadFloorSeq(ReadInstance(__instance, "currFloor")));
        }

        private static void HitPostfix(object __instance, object[] __args, object __result)
        {
            _hitsThisFrame++;
            _totalHits++;
            WriteEvent("HitPostfix", "isAuto=" + Fmt(__args != null && __args.Length > 0 ? __args[0] : null)
                + "|success=" + Fmt(__result) + "|floorAfter=" + ReadPlayerFloor());
        }

        private static void PlanetPrefix(object __instance) { WriteEvent("PlanetRefreshAnglesPrefix", PlanetState(__instance)); }
        private static void PlanetPostfix(object __instance) { WriteEvent("PlanetRefreshAnglesPostfix", PlanetState(__instance)); }

        private static bool CalibrationPrefix(ref float __result)
        {
            if (!_forcedActive) return true;
            __result = 0f;
            return false;
        }

        private static bool AdjustAnglePrefix()
        {
            if (!_forcedActive) return true;
            WriteEvent("AdjustAngleSuppressed", "suppressed=true");
            return false;
        }

        private static void SongGetterPostfix(ref double __result) { if (_forcedActive) __result = _forced; }
        private static void SongSetterPrefix(ref double value) { if (_forcedActive) value = _forced; }

        private static void OnFrameBoundary()
        {
            if (!_running || !_forcedActive || _state != State.Active) return;
            int frame = _frame;
            WriteEvent("FrameBoundary", "frame=" + frame + "|expectedFrameTime=" + Fmt(_expected) + "|forcedSongPosition=" + Fmt(_forced));
            string error = null;
            if (_audio == null || !_audio.Capture(out error))
            {
                WriteEvent("FrameBoundaryError", "frame=" + frame + "|reason=" + Sanitize(error));
                RequestStop("audio-capture-failed:" + error);
                return;
            }
            Snapshot snapshot = MakeSnapshot(frame);
            _current.Add(snapshot);
            WriteEvent("FrameBoundaryComplete", "frame=" + frame + "|audioCapturedSeconds=" + Fmt(snapshot.AudioSeconds)
                + "|audioErrorToNextFrame=" + Fmt(snapshot.AudioSeconds - ((frame + 1) / (double)Fps))
                + "|playerFloor=" + snapshot.Floor + "|hitCount=" + snapshot.Hits);
            _frame++;
            if (_frame < _targetFrames) return;
            if (_targetFrames == FirstFrames && _totalHits == 0)
            {
                _targetFrames = ExtendedFrames;
                WriteEvent("ProbeExtended", "reason=no-hit-in-first-16|targetFrames=" + _targetFrames);
            }
            else if (_targetFrames < MaxProbeFrames && _totalHits == 0)
            {
                object current = ReadInstance(CurrentPlayer(), "currFloor");
                object next = ReadInstance(current, "nextfloor");
                double nextEntry = ReadDouble(next, "entryTime");
                int requiredFrames = double.IsNaN(nextEntry)
                    ? MaxProbeFrames
                    : Math.Max(_targetFrames + 1, (int)Math.Ceiling((nextEntry + 1.0 / Fps) * Fps) + 4);
                _targetFrames = Math.Min(MaxProbeFrames, requiredFrames);
                WriteEvent("ProbeExtended", "reason=no-hit-in-current-window|nextFloorEntryTime="
                    + Fmt(nextEntry) + "|targetFrames=" + _targetFrames);
            }
            else RequestStop(_totalHits > 0 ? "target-frame-count-reached" : "target-frame-count-reached-without-hit");
        }

        private static bool CanonicalizeInitialPlanetState(out string error)
        {
            error = null;
            object player = CurrentPlayer();
            object current = ReadInstance(player, "currFloor");
            object planet = CurrentPlanet(player);
            if (player == null || current == null || planet == null)
            {
                error = "chosen-planet-or-floor-unavailable";
                return false;
            }
            if (ReadFloorSeq(current) != 0)
            {
                error = "current-floor-is-not-zero";
                return false;
            }
            object canonical = ReadInstance(planet, "targetExitAngle");
            double value;
            try { value = Convert.ToDouble(canonical, CultureInfo.InvariantCulture); }
            catch { value = double.NaN; }
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                error = "target-exit-angle-unavailable";
                return false;
            }
            WriteEvent("PlanetInitialStateBeforeCanonicalization", PlanetState(planet));
            if (!SetMember(planet, "cachedAngle", canonical))
            {
                error = "cached-angle-write-failed";
                return false;
            }
            WriteEvent("PlanetInitialStateCanonicalized", "source=chosenPlanet.targetExitAngle|canonicalCachedAngle="
                + Fmt(canonical) + "|" + PlanetState(planet));
            return true;
        }
        private static Snapshot MakeSnapshot(int frame)
        {
            object planet = CurrentPlanet(CurrentPlayer());
            return new Snapshot
            {
                Frame = frame,
                Samples = _audio.Samples,
                Expected = _expected,
                AudioSeconds = _audio.Seconds,
                Forced = _forced,
                Floor = ReadPlayerFloor(),
                Seq = ReadInt(ReadStatic(_adoBase, "controller"), "currentSeqID"),
                Hits = _hitsThisFrame,
                Angle = Fmt(ReadInstance(planet, "angle")),
                CachedAngle = Fmt(ReadInstance(planet, "cachedAngle"))
            };
        }

        private static class RenderistAutoPlay
        {
            internal static void CatchUp()
            {
                if (_hitsThisFrame >= MaxHitsPerFrame) return;
                object player = CurrentPlayer();
                object controller = ReadStatic(_adoBase, "controller");
                if (player == null || controller == null || ReadBool(controller, "paused") == true
                    || Fmt(ReadInstance(controller, "state")) != "PlayerControl" || ReadBool(player, "alive") == false) return;
                while (_hitsThisFrame < MaxHitsPerFrame)
                {
                    object current = ReadInstance(player, "currFloor");
                    object next = ReadInstance(current, "nextfloor");
                    if (current == null || next == null) return;
                    double nextEntry = ReadDouble(next, "entryTime");
                    if (double.IsNaN(nextEntry))
                    {
                        WriteEvent("RenderistAutoPlayError", "reason=next-entry-time-unavailable");
                        RequestStop("next-entry-time-unavailable");
                        return;
                    }
                    WriteEvent("RenderistAutoPlayDueCheck", "currentFloor=" + ReadFloorSeq(current) + "|nextFloor=" + ReadFloorSeq(next)
                        + "|nextFloorEntryTime=" + Fmt(nextEntry) + "|forcedSongPosition=" + Fmt(_forced));
                    if (_forced + 0.0001 < nextEntry) return;
                    object planet = CurrentPlanet(player);
                    Invoke(_planetRefresh, planet);
                    AlignPlanet(current, planet);
                    bool? oldAuto = ReadBoolStatic("auto");
                    try
                    {
                        SetStatic(_rdc, "auto", true);
                        PrepareHitState(player, controller);
                        object result = Invoke(_playerHit, player, true);
                        WriteEvent("RenderistAutoPlayHit", "isAuto=true|success=" + Fmt(result) + "|floorAfter=" + ReadPlayerFloor());
                        if (result is bool && !(bool)result) return;
                    }
                    finally { if (oldAuto.HasValue) SetStatic(_rdc, "auto", oldAuto.Value); }
                    if (ReadFloorSeq(ReadInstance(player, "currFloor")) == ReadFloorSeq(current))
                    {
                        WriteEvent("RenderistAutoPlayError", "reason=hit-did-not-advance");
                        return;
                    }
                }
                WriteEvent("RenderistAutoPlayGuard", "maxHitsPerFrame=" + MaxHitsPerFrame);
            }

            private static void PrepareHitState(object player, object controller)
            {
                object manager = ReadStatic(_adoBase, "playerManager");
                bool responsive = false;
                bool paused = SetMember(controller, "paused", false);
                bool penalty = SetMember(controller, "multipressPenalty", false);
                bool firstPress = SetMember(controller, "multipressAndHasPressedFirstPress", false);
                bool counter = SetMember(player, "consecMultipressCounter", 0);
                bool keyTimes = false;
                object times = ReadInstance(player, "keyTimes");
                if (times != null)
                {
                    try { InvokeNamed(times, "Clear"); keyTimes = true; } catch { }
                }
                if (manager != null)
                {
                    try { InvokeNamed(manager, "SetAllPlayerResponsive", true); responsive = true; } catch { }
                }
                WriteEvent("RenderistAutoPlayPrepareHit", "responsive=" + responsive
                    + "|paused=" + paused + "|multipressPenalty=" + penalty
                    + "|multipressFirstPress=" + firstPress + "|consecMultipressCounter=" + counter
                    + "|keyTimesCleared=" + keyTimes);
            }
            private static void AlignPlanet(object current, object planet)
            {
                if (current == null || planet == null || ReadBool(current, "midSpin") == true) return;
                object target = ReadInstance(planet, "targetExitAngle");
                if (target == null) return;
                SetMember(planet, "angle", target);
                SetMember(planet, "cachedAngle", target);
                WriteEvent("RenderistAutoPlayPlanetAlign", PlanetState(planet));
            }
        }

        private static string Validate()
        {
            if (ModEntry.Settings == null) return "settings-unavailable";
            if (ReadStatic(_adoBase, "editor") == null) return "editor-unavailable";
            if (_conductorUpdate == null || _songGetter == null || _songSetter == null) return "conductor-api-unavailable";
            if (_playerControlUpdate == null || _playerHit == null) return "player-api-unavailable";
            if (_planetRefresh == null) return "planet-refresh-api-unavailable";
            if (_calibrationGetter == null) { Log.Warn("CALIBRATION_API_FAILED reason=property-or-getter-not-found"); return "calibration-api-unavailable"; }
            if (_calibrationGetter.ReturnType != typeof(float)) { Log.Warn("CALIBRATION_API_FAILED reason=return-type-" + _calibrationGetter.ReturnType.FullName); return "calibration-api-unavailable"; }
            if (_adjustAngle == null) return "adjust-angle-api-unavailable";
            return null;
        }

        private static bool StartEditorPlayback(out string error)
        {
            error = null;
            try
            {
                object editor = ReadStatic(_adoBase, "editor");
                object floor0 = Index(ReadInstance(editor, "floors"), 0);
                InvokeNamed(editor, "SelectFloor", floor0, false);
                _savedAuto = ReadBoolStatic("auto");
                SetStatic(_rdc, "auto", false);
                InvokeNamed(editor, "Play");
                SetStatic(_rdc, "auto", false);
                _ownsPlayback = true;
                WriteEvent("OfficialEditorPlayRequested", "floor=0|rdcAuto=false");
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static bool RegisterHooks(out string error)
        {
            error = null;
            try
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(_conductorUpdate, prefix: new HarmonyMethod(typeof(OfflineAudioClockPoC), nameof(ConductorPrefix)), postfix: new HarmonyMethod(typeof(OfflineAudioClockPoC), nameof(ConductorPostfix)));
                _harmony.Patch(_songGetter, postfix: new HarmonyMethod(typeof(OfflineAudioClockPoC), nameof(SongGetterPostfix)));
                _harmony.Patch(_songSetter, prefix: new HarmonyMethod(typeof(OfflineAudioClockPoC), nameof(SongSetterPrefix)));
                _harmony.Patch(_calibrationGetter, prefix: new HarmonyMethod(typeof(OfflineAudioClockPoC), nameof(CalibrationPrefix)));
                _harmony.Patch(_playerControlUpdate, prefix: new HarmonyMethod(typeof(OfflineAudioClockPoC), nameof(PlayerControlPrefix)), postfix: new HarmonyMethod(typeof(OfflineAudioClockPoC), nameof(PlayerControlPostfix)));
                _harmony.Patch(_playerHit, prefix: new HarmonyMethod(typeof(OfflineAudioClockPoC), nameof(HitPrefix)), postfix: new HarmonyMethod(typeof(OfflineAudioClockPoC), nameof(HitPostfix)));
                _harmony.Patch(_planetRefresh, prefix: new HarmonyMethod(typeof(OfflineAudioClockPoC), nameof(PlanetPrefix)), postfix: new HarmonyMethod(typeof(OfflineAudioClockPoC), nameof(PlanetPostfix)));
                if (_controllerLateUpdate != null)
                    _harmony.Patch(_controllerLateUpdate, prefix: new HarmonyMethod(typeof(OfflineAudioClockPoC), nameof(ControllerLatePrefix)), postfix: new HarmonyMethod(typeof(OfflineAudioClockPoC), nameof(ControllerLatePostfix)));
                _harmony.Patch(_adjustAngle, prefix: new HarmonyMethod(typeof(OfflineAudioClockPoC), nameof(AdjustAnglePrefix)));
                _hooksRegistered = true;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { _harmony?.UnpatchAll(HarmonyId); } catch { }
                _harmony = null;
                return false;
            }
        }

        private static void FailImmediate(string reason)
        {
            _failureReason = Sanitize(reason);
            _running = false;
            _startPending = false;
            _state = State.Failed;
            Log.Warn("OfflineAudioClockPoC: Failed: " + _failureReason);
        }

        private static void Fail(string reason)
        {
            _failureReason = Sanitize(reason);
            _startPending = false;
            Log.Warn("OfflineAudioClockPoC: Failed: " + _failureReason);
            if (_hooksRegistered || _writer != null || _ownsPlayback || _host != null || _audio != null)
            {
                RequestStop("failed: " + _failureReason);
                return;
            }
            _running = false;
            _state = State.Failed;
        }

        private static bool IsFailureReason(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return false;
            return reason.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("unavailable", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("without-hit", StringComparison.OrdinalIgnoreCase) >= 0
                || reason == "controller-failure";
        }

        private static void RequestStop(string reason)
        {
            if (_stopRequested) return;
            _stopRequested = true;
            _stopReason = reason ?? "stopped";
            _state = State.Stopping;
        }

        private static void SafeStop(string reason)
        {
            if (!_running) return;
            bool failed = !string.IsNullOrEmpty(_failureReason) || IsFailureReason(reason);
            try
            {
                _forcedActive = false;
                _audio?.Dispose();
                _audio = null;
                GameObject host = _host;
                _host = null;
                if (host != null) { try { UnityEngine.Object.Destroy(host); } catch { } }
                if (_hooksRegistered)
                {
                    try { _harmony?.UnpatchAll(HarmonyId); } catch { }
                    _hooksRegistered = false;
                }
                RestoreState();
                WriteEvent("PoCStopped", "reason=" + Sanitize(reason) + "|logicalFrames=" + _current.Count + "|totalHitCount=" + _totalHits);
                List<Snapshot> previousRun = _previous;
                List<Snapshot> finishedRun = new List<Snapshot>(_current);
                CloseLog();
                BuildComparison(previousRun, finishedRun);
                _previous = finishedRun;
            }
            catch (Exception ex) { Log.Exception("OfflineAudioClockPoC: 停止失败", ex); CloseLog(); }
            finally { _running = false; _state = failed ? State.Failed : State.Idle; _stopRequested = false; _stopReason = null; _ownsPlayback = false; }
        }

        private static void SaveState()
        {
            _savedCaptureFramerate = Time.captureFramerate;
            _savedTargetFrameRate = Application.targetFrameRate;
            _savedVSync = QualitySettings.vSyncCount;
            _savedAuto = ReadBoolStatic("auto");
        }

        private static void RestoreState()
        {
            try
            {
                if (_savedAuto.HasValue) SetStatic(_rdc, "auto", _savedAuto.Value);
                Time.captureFramerate = _savedCaptureFramerate;
                Application.targetFrameRate = _savedTargetFrameRate;
                QualitySettings.vSyncCount = _savedVSync;
                if (_ownsPlayback)
                {
                    object editor = ReadStatic(_adoBase, "editor");
                    if (editor != null) InvokeNamed(editor, "SwitchToEditMode", false);
                }
            }
            catch (Exception ex) { Log.Exception("OfflineAudioClockPoC: 恢复失败", ex); }
        }

        private static void EnsureTypes()
        {
            if (_gameAssembly == null)
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if (assembly.GetName().Name == "Assembly-CSharp") { _gameAssembly = assembly; break; }
            if (_gameAssembly == null) throw new InvalidOperationException("Assembly-CSharp-unavailable");
            _adoBase = _adoBase ?? _gameAssembly.GetType("ADOBase");
            _conductor = _conductor ?? _gameAssembly.GetType("scrConductor");
            _controller = _controller ?? _gameAssembly.GetType("scrController");
            _player = _player ?? _gameAssembly.GetType("scrPlayer");
            _planet = _planet ?? _gameAssembly.GetType("scrPlanet");
            _rdc = _rdc ?? _gameAssembly.GetType("RDC");
            _asyncInputUtils = _asyncInputUtils ?? _gameAssembly.GetType("AsyncInputUtils");
            BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            BindingFlags statik = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            _songPosition = _conductor?.GetProperty("songposition_minusi", instance);
            _calibrationI = _conductor?.GetProperty("calibration_i", instance | statik);
            _conductorUpdate = _conductor?.GetMethod("Update", instance, null, Type.EmptyTypes, null);
            _songGetter = _songPosition?.GetGetMethod(true);
            _songSetter = _songPosition?.GetSetMethod(true);
            _calibrationGetter = _calibrationI?.GetGetMethod(true);
            if (_calibrationGetter != null)
                Log.Info("CALIBRATION_API_RESOLVED target=" + _calibrationGetter.DeclaringType.FullName + "." + _calibrationGetter.Name + "() return=" + _calibrationGetter.ReturnType.FullName + " static=" + _calibrationGetter.IsStatic);
            _playerControlUpdate = _controller?.GetMethod("PlayerControl_Update", instance, null, Type.EmptyTypes, null);
            _controllerLateUpdate = _controller?.GetMethod("LateUpdate", instance, null, Type.EmptyTypes, null);
            _playerHit = _player?.GetMethod("Hit", instance, null, new[] { typeof(bool) }, null);
            _planetRefresh = _planet?.GetMethod("Update_RefreshAngles", instance, null, Type.EmptyTypes, null);
            _adjustAngle = _asyncInputUtils?.GetMethod("AdjustAngle", statik, null, new[] { _player, typeof(ulong) }, null);
        }

        private static object CurrentPlayer() { return ReadInstance(ReadStatic(_adoBase, "controller"), "playerOne"); }
        private static object CurrentPlanet(object player) { return ReadInstance(ReadInstance(player, "planetarySystem"), "chosenPlanet"); }
        private static int ReadPlayerFloor() { return ReadFloorSeq(ReadInstance(CurrentPlayer(), "currFloor")); }
        private static int ReadFloorSeq(object floor) { return ReadInt(floor, "seqID"); }
        private static double ReadPitch()
        {
            double value = ReadDouble(ReadInstance(ReadStatic(_adoBase, "conductor"), "song"), "pitch");
            return value > 0.0001 ? value : 1.0;
        }
        private static float ReadCalibrationI()
        {
            object value = ReadStatic(_conductor, "calibration_i");
            try { return Convert.ToSingle(value, CultureInfo.InvariantCulture); } catch { return float.NaN; }
        }
        private static string PlanetState(object planet)
        {
            return "angle=" + Fmt(ReadInstance(planet, "angle")) + "|cachedAngle=" + Fmt(ReadInstance(planet, "cachedAngle"))
                + "|targetExitAngle=" + Fmt(ReadInstance(planet, "targetExitAngle"));
        }
        private static string ResolveLogPath(int run)
        {
            string root = ModEntry.Settings == null ? Application.persistentDataPath : ModEntry.Settings.OutputDirectory;
            if (string.IsNullOrEmpty(root)) root = Application.persistentDataPath;
            string dir = Path.Combine(root, "diagnostics");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "deterministic-core-poc-run-" + run + ".log");
        }
        private static bool IsReady(object state) { string s = Fmt(state); return s == "Countdown" || s == "PlayerControl"; }
        private static bool IsFailure(object state) { string s = Fmt(state); return s == "Fail" || s == "Fail2"; }
private static object ReadStatic(Type type, string name)
        {
            if (type == null) return null;
            try
            {
                PropertyInfo p = type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null) return p.GetValue(null, null);
                FieldInfo f = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                return f == null ? null : f.GetValue(null);
            }
            catch { return null; }
        }
        private static void SetStatic(Type type, string name, object value)
        {
            if (type == null) return;
            PropertyInfo p = type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.CanWrite) { p.SetValue(null, value, null); return; }
            FieldInfo f = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null) f.SetValue(null, value);
        }
        private static object ReadInstance(object instance, string name)
        {
            if (instance == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                Type type = instance.GetType();
                PropertyInfo p = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null) return p.GetValue(instance, null);
                FieldInfo f = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return f == null ? null : f.GetValue(instance);
            }
            catch { return null; }
        }
        private static bool SetMember(object instance, string name, object value)
        {
            if (instance == null) return false;
            try
            {
                Type type = instance.GetType();
                PropertyInfo p = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanWrite) { p.SetValue(instance, value, null); return true; }
                FieldInfo f = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) { f.SetValue(instance, value); return true; }
            }
            catch { }
            return false;
        }
        private static object Index(object collection, int index)
        {
            if (collection == null) return null;
            try { return collection.GetType().GetProperty("Item")?.GetValue(collection, new object[] { index }); } catch { return null; }
        }
        private static object Invoke(MethodInfo method, object instance, params object[] args) { return method == null ? null : method.Invoke(instance, args); }
        private static object InvokeNamed(object instance, string name, params object[] args)
        {
            if (instance == null) return null;
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (MethodInfo method in instance.GetType().GetMethods(flags))
                if (method.Name == name && method.GetParameters().Length == args.Length) return method.Invoke(instance, args);
            throw new MissingMethodException(instance.GetType().FullName, name);
        }
        private static int ReadInt(object instance, string name)
        {
            object value = ReadInstance(instance, name);
            try { return value == null ? -1 : Convert.ToInt32(value, CultureInfo.InvariantCulture); } catch { return -1; }
        }
        private static double ReadDouble(object instance, string name)
        {
            object value = ReadInstance(instance, name);
            try { double d = value == null ? double.NaN : Convert.ToDouble(value, CultureInfo.InvariantCulture); return double.IsInfinity(d) ? double.NaN : d; } catch { return double.NaN; }
        }
        private static bool? ReadBool(object instance, string name)
        {
            object value = ReadInstance(instance, name);
            if (value == null) return null;
            try { return Convert.ToBoolean(value, CultureInfo.InvariantCulture); } catch { return null; }
        }
        private static bool? ReadBoolStatic(string name)
        {
            object value = ReadStatic(_rdc, name);
            if (value == null) return null;
            try { return Convert.ToBoolean(value, CultureInfo.InvariantCulture); } catch { return null; }
        }
        private static string Fmt(object value)
        {
            if (value == null) return "null";
            if (value is float) return ((float)value).ToString("0.######", CultureInfo.InvariantCulture);
            if (value is double) return ((double)value).ToString("0.######", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
        }
        private static string Fmt(bool? value) { return value.HasValue ? (value.Value ? "true" : "false") : "null"; }
        private static string Sanitize(string value) { return (value ?? "null").Replace("\r", " ").Replace("\n", " ").Replace('|', '/'); }

        private static void WriteHeader()
        {
            if (_writer == null) return;
            _writer.WriteLine("# ADOFAI Renderist deterministic core PoC");
            _writer.WriteLine("# component=RenderistAutoPlay|audio=OfflineAudioClock|version=" + ModEntry.ModVersion);
            _writer.WriteLine("# officialAutoplayOnly=disabled|png=false|videoBridge=false");
            _writer.WriteLine("# expectedFrameTime=N/FPS; audioCapturedSeconds=sampleFrames/sampleRate");
        }
        private static void WriteEvent(string eventName, string extra)
        {
            if (_writer == null) return;
            var sb = new StringBuilder(900);
            _probeSequence++;
            Append(sb, "timestamp", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), true);
            Append(sb, "unityFrame", Time.frameCount.ToString(CultureInfo.InvariantCulture), false);
            Append(sb, "probeSequence", _probeSequence.ToString(CultureInfo.InvariantCulture), false);
            Append(sb, "wallClockElapsed", Fmt(Time.realtimeSinceStartup - _wallClockStart), false);
            Append(sb, "outputFrameIndex", _frame.ToString(CultureInfo.InvariantCulture), false);
            Append(sb, "expectedFrameTime", Fmt(_expected), false);
            Append(sb, "audioCapturedSampleFrames", _audio == null ? "0" : _audio.Samples.ToString(CultureInfo.InvariantCulture), false);
            Append(sb, "audioCapturedSeconds", _audio == null ? "0" : Fmt(_audio.Seconds), false);
            Append(sb, "forcedSongPosition", Fmt(_forced), false);
            Append(sb, "songposition_minusi", Fmt(ReadInstance(ReadStatic(_adoBase, "conductor"), "songposition_minusi")), false);
            Append(sb, "controllerState", Fmt(ReadInstance(ReadStatic(_adoBase, "controller"), "state")), false);
            Append(sb, "currentSeqID", ReadInt(ReadStatic(_adoBase, "controller"), "currentSeqID").ToString(CultureInfo.InvariantCulture), false);
            Append(sb, "playerFloor", ReadPlayerFloor().ToString(CultureInfo.InvariantCulture), false);
            object current = ReadInstance(CurrentPlayer(), "currFloor");
            object next = ReadInstance(current, "nextfloor");
            Append(sb, "currentFloorEntryTime", Fmt(ReadInstance(current, "entryTime")), false);
            Append(sb, "nextFloorEntryTime", Fmt(ReadInstance(next, "entryTime")), false);
            Append(sb, "hitCount", _hitsThisFrame.ToString(CultureInfo.InvariantCulture), false);
            Append(sb, "calibration_i", Fmt(ReadCalibrationI()), false);
            Append(sb, "calibration_v", Fmt(ReadInstance(ReadStatic(_adoBase, "conductor"), "calibration_v")), false);
            Append(sb, "songposition_minusv", Fmt(ReadInstance(ReadStatic(_adoBase, "conductor"), "songposition_minusv")), false);
            Append(sb, "rdcAuto", Fmt(ReadStatic(_rdc, "auto")), false);
            Append(sb, "event", eventName, false);
            if (!string.IsNullOrEmpty(extra)) Append(sb, "extra", extra, false);
            _writer.WriteLine(sb.ToString());
            if (eventName == "FrameBoundaryComplete" || eventName == "PoCStopped") _writer.Flush();
        }
        private static void Append(StringBuilder sb, string name, string value, bool first)
        {
            if (!first) sb.Append('|');
            sb.Append(name).Append('=').Append(Sanitize(value));
        }
        private static void CloseLog()
        {
            if (_writer == null) return;
            try { _writer.Flush(); _writer.Dispose(); } catch { }
            _writer = null;
            if (_current.Count > 0) _previous = new List<Snapshot>(_current);
        }
        private static void BuildComparison(List<Snapshot> runA, List<Snapshot> runB)
        {
            if (runA == null || runA.Count == 0 || runB == null || runB.Count == 0 || string.IsNullOrEmpty(_logPath)) return;
            _comparisonPath = Path.Combine(Path.GetDirectoryName(_logPath), "deterministic-core-poc-comparison-" + _runNumber + ".log");
            using (var writer = new StreamWriter(_comparisonPath, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("# RenderistAutoPlay deterministic core PoC comparison");
                writer.WriteLine("runAFrames=" + runA.Count + "|runBFrames=" + runB.Count);
                int count = Math.Min(runA.Count, runB.Count);
                int firstA = -1, firstB = -1;
                for (int i = 0; i < count; i++)
                {
                    Snapshot a = runA[i], b = runB[i];
                    if (firstA < 0 && a.Hits > 0) firstA = a.Frame;
                    if (firstB < 0 && b.Hits > 0) firstB = b.Frame;
                    bool same = a.Samples == b.Samples && Nearly(a.AudioSeconds, b.AudioSeconds)
                        && Nearly(a.Forced, b.Forced) && a.Floor == b.Floor && a.Seq == b.Seq
                        && a.Hits == b.Hits && a.Angle == b.Angle && a.CachedAngle == b.CachedAngle;
                    writer.WriteLine("frame=" + i + "|same=" + same + "|audioA=" + Fmt(a.AudioSeconds) + "|audioB=" + Fmt(b.AudioSeconds)
                        + "|floorA=" + a.Floor + "|floorB=" + b.Floor + "|hitsA=" + a.Hits + "|hitsB=" + b.Hits);
                }
                writer.WriteLine("firstHitRunA=" + firstA + "|firstHitRunB=" + firstB + "|firstHitSame=" + (firstA == firstB));
            }
        }
        private static bool Nearly(double a, double b) { return Math.Abs(a - b) <= 0.000001; }
    }
}
