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
    /// Phase 3.2.0 diagnostics-only observer for a Renderist-owned editor.Play()
    /// startup scope. It observes official state commits and OnMusicScheduled;
    /// it does not invent an ADOFAI generation/session token or alter playback.
    /// </summary>
    internal static class PlaybackLifecyclePoC
    {
        private const string DiagnosticsSubdir = "diagnostics";
        private const string LogFileBase = "playback-lifecycle-poc";
        private const string HarmonyId = "com.adofai.renderist.phase3.2.playback-lifecycle-poc";
        private const string StateEngineFullName = "MonsterLove.StateMachine.StateEngine";
        private const string Unavailable = "unavailable";
        private const int MaxUpdates = 900;

        private static bool _running;
        private static bool _scopeActive;
        private static bool _playRequested;
        private static bool _playReturned;
        private static bool _stopRequested;
        private static bool _finalizing;
        private static bool _readyCandidateLogged;
        private static bool _sawStateCommit;
        private static bool _sawStart;
        private static bool _sawCountdown;
        private static bool _sawPlayerControl;
        private static bool _sawMusicScheduled;
        private static int _updates;
        private static int _eventSeq;
        private static int _run;
        private static int _nextRun;
        private static string _stopReason;
        private static string _lastReason;
        private static string _logPath;
        private static StreamWriter _writer;
        private static Harmony _harmony;
        private static object _observedController;
        private static object _observedStateMachine;
        private static EventInfo _changedEvent;
        private static Action<Enum> _stateChangedHandler;
        private static MethodInfo _onMusicScheduled;

        public static bool IsRunning => _running;
        public static string StatusText { get; private set; } = UiText.GuiLifecyclePocIdle;
        public static string LogPath => _logPath;
        public static string LastReason => _lastReason;
        public static int UpdateCount => _updates;

        public static bool Start()
        {
            if (_running)
                return Reject("already-running");

            if (EditorExportController.IsBusy || EditorVisualClockPoc.IsRunning ||
                OfflineAudioClockPoC.IsRunning)
                return Reject("other-task-running");

            try
            {
                if (!EditorGameReflection.EnsureTypes() || !EditorGameReflection.IsProbablyEditorNow())
                    return Reject("editor-unavailable");
                if (EditorGameReflection.ReadEditorPlayMode() == true)
                    return Reject("already-in-play-mode");
                if (EditorGameReflection.Editor() == null || !EditorGameReflection.IsLevelLoaded())
                    return Reject("editor-or-level-unavailable");
                if (EditorGameReflection.ReadRdcAuto() != false)
                    return Reject("lifecycle-poc-requires-auto-off");

                ResolveTargets();
                string root = ResolveDiagnosticsRoot();
                if (string.IsNullOrEmpty(root))
                    return Reject("diagnostics-root-unavailable");
                string dir = Path.Combine(root, DiagnosticsSubdir);
                Directory.CreateDirectory(dir);
                string path = ResolveUniqueLogPath(dir);
                if (string.IsNullOrEmpty(path))
                    return Reject("log-path-unavailable");

                _writer = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = false };
                _logPath = path;
                _run = ++_nextRun;
                _updates = 0;
                _eventSeq = 0;
                _lastReason = null;
                _stopReason = null;
                _stopRequested = false;
                _finalizing = false;
                _playRequested = false;
                _playReturned = false;
                _readyCandidateLogged = false;
                _sawStateCommit = false;
                _sawStart = false;
                _sawCountdown = false;
                _sawPlayerControl = false;
                _sawMusicScheduled = false;
                _running = true;
                _scopeActive = true;
                StatusText = UiText.GuiLifecyclePocRunning;

                WriteEvent("LifecyclePoCBegin", null, true);
                InstallObserver();
                WriteEvent("ObserverInstalled", null, true);
                WriteEvent("PrePlaySnapshot", "prePlayState=" + StateName(EditorGameReflection.ReadControllerState()) + "|baseline=stale-only", true);
                WriteEvent("PlayInvocationBegin", null, true);

                _playRequested = true;
                WriteEvent("OfficialEditorPlay", "api=scnEditor.Play", true);
                InvokeEditorPlay();
                _playReturned = true;
                WriteEvent("PlayInvocationReturned", null, true);
                TryEvaluateReady();

                Log.Info(UiText.LogLifecyclePocStarted);
                Log.Info(UiText.Format(UiText.LogLifecyclePocLogPathFormat, _logPath));
                return true;
            }
            catch (Exception ex)
            {
                try { WriteEvent("LifecyclePoCFail", "reason=start-exception", true); } catch { }
                Log.Exception(UiText.LogLifecyclePocStartFailed, ex);
                RequestStop("exception");
                FinalizeRun();
                return false;
            }
        }

        public static void Tick()
        {
            if (!_running) return;
            try
            {
                if (!ModEntry.Enabled)
                    RequestStop("mod-disabled");
                else if (_stopRequested)
                    FinalizeRun();
                else
                {
                    _updates++;
                    TryEvaluateReady();
                    if (_updates >= MaxUpdates)
                    {
                        WriteEvent("LifecyclePoCFail", "reason=watchdog", true);
                        RequestStop("watchdog");
                    }
                }

                if (_stopRequested)
                    FinalizeRun();
            }
            catch (Exception ex)
            {
                Log.Exception(UiText.LogLifecyclePocTickError, ex);
                RequestStop("tick-exception");
                FinalizeRun();
            }
        }

        public static void Stop(string reason)
        {
            if (!_running && _writer == null) return;
            RequestStop(string.IsNullOrEmpty(reason) ? "cancelled" : reason);
            FinalizeRun();
        }

        private static bool Reject(string reason)
        {
            _lastReason = reason;
            StatusText = UiText.GuiLifecyclePocRejected + reason;
            Log.Warn(UiText.Format(UiText.LogLifecyclePocStartRejectedFormat, reason));
            return false;
        }

        private static void RequestStop(string reason)
        {
            _stopRequested = true;
            if (string.IsNullOrEmpty(_stopReason))
                _stopReason = reason;
        }

        private static void InvokeEditorPlay()
        {
            MethodInfo play = EditorGameReflection.EditorPlayMethod;
            if (play == null) throw new MissingMethodException("scnEditor.Play");
            object editor = EditorGameReflection.Editor();
            if (editor == null) throw new MissingMemberException("ADOBase.editor");
            play.Invoke(editor, null);
        }

        private static void ResolveTargets()
        {
            _observedController = EditorGameReflection.Controller();
            if (_observedController == null)
                throw new MissingMemberException("ADOBase.controller");

            _observedStateMachine = ReadMember(_observedController, "stateMachine");
            if (_observedStateMachine == null ||
                !string.Equals(_observedStateMachine.GetType().FullName, StateEngineFullName, StringComparison.Ordinal))
                throw new MissingMemberException("scrController.stateMachine -> " + StateEngineFullName);

            _changedEvent = _observedStateMachine.GetType().GetEvent(
                "Changed", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (_changedEvent == null || _changedEvent.DeclaringType == null ||
                !string.Equals(_changedEvent.DeclaringType.FullName, StateEngineFullName, StringComparison.Ordinal) ||
                _changedEvent.EventHandlerType != typeof(Action<Enum>))
                throw new MissingMethodException("StateEngine.Changed event Action<Enum>");

            Type controllerType = _observedController.GetType();
            _onMusicScheduled = controllerType.GetMethod("OnMusicScheduled",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (_onMusicScheduled == null || _onMusicScheduled.ReturnType != typeof(void))
                throw new MissingMethodException("scrController.OnMusicScheduled()");
        }

        private static void InstallObserver()
        {
            _stateChangedHandler = committed => OnStateChanged(_observedStateMachine, committed);
            _changedEvent.AddEventHandler(_observedStateMachine, _stateChangedHandler);

            _harmony = new Harmony(HarmonyId);
            try
            {
                _harmony.Patch(_onMusicScheduled,
                    postfix: new HarmonyMethod(typeof(PlaybackLifecyclePoC), nameof(OnMusicScheduledPostfix)));
            }
            catch
            {
                try { _changedEvent.RemoveEventHandler(_observedStateMachine, _stateChangedHandler); }
                finally
                {
                    try { _harmony.UnpatchAll(HarmonyId); } catch { }
                    _harmony = null;
                    _stateChangedHandler = null;
                }
                throw;
            }
        }

        private static void OnStateChanged(object subscribedStateMachine, Enum committedState)
        {
            try
            {
                if (!_scopeActive || !_playRequested || !ReferenceEquals(subscribedStateMachine, CurrentStateMachine()))
                    return;

                string state = StateName(committedState);
                _sawStateCommit = true;
                _sawStart |= string.Equals(state, "Start", StringComparison.Ordinal);
                _sawCountdown |= string.Equals(state, "Countdown", StringComparison.Ordinal);
                _sawPlayerControl |= string.Equals(state, "PlayerControl", StringComparison.Ordinal);
                WriteEvent("StateCommitted", "committedState=" + state + "|observer=StateEngine.Changed", true);

                if (EditorGameReflection.IsFailureState(committedState))
                {
                    WriteEvent("LifecyclePoCFail", "reason=official-fail-state", true);
                    RequestStop("official-fail-state");
                    return;
                }
                TryEvaluateReady();
            }
            catch (Exception ex)
            {
                Log.Exception(UiText.LogLifecyclePocTickError, ex);
                RequestStop("state-observer-exception");
            }
        }

        private static void OnMusicScheduledPostfix(object __instance)
        {
            try
            {
                if (!_scopeActive || !_playRequested || !ReferenceEquals(__instance, EditorGameReflection.Controller()))
                    return;
                _sawMusicScheduled = true;
                WriteEvent("MusicScheduled", "observer=OnMusicScheduled.Postfix", true);
                TryEvaluateReady();
            }
            catch (Exception ex)
            {
                Log.Exception(UiText.LogLifecyclePocTickError, ex);
                RequestStop("music-observer-exception");
            }
        }

        private static void TryEvaluateReady()
        {
            if (!_scopeActive || !_playRequested || !_playReturned || _readyCandidateLogged ||
                !_sawStateCommit || !_sawStart || !_sawMusicScheduled || !_sawCountdown || !_sawPlayerControl)
                return;

            object controller = EditorGameReflection.Controller();
            object player = ReadMember(controller, "playerOne");
            bool playerAlive = ReadBool(ReadMember(player, "alive")) == true;
            bool paused = ReadBool(ReadMember(controller, "paused")) == true;
            string state = StateName(EditorGameReflection.ReadControllerState());
            if (!playerAlive || paused || EditorGameReflection.IsFailureState(EditorGameReflection.ReadControllerState()) ||
                !string.Equals(state, "PlayerControl", StringComparison.Ordinal))
                return;

            _readyCandidateLogged = true;
            WriteEvent("LifecycleReadyCandidate", "readiness=official-commit-chain|basis=StateEngine.Changed+OnMusicScheduled", true);
            WriteEvent("LifecyclePoCPass", "readiness=official-commit-chain", true);
            StatusText = UiText.GuiLifecyclePocPass;
            RequestStop("pass");
        }

        private static void FinalizeRun()
        {
            if (_finalizing) return;
            _finalizing = true;
            bool wasRunning = _running;
            _scopeActive = false;
            _running = false;

            try
            {
                if (_changedEvent != null && _observedStateMachine != null && _stateChangedHandler != null)
                    _changedEvent.RemoveEventHandler(_observedStateMachine, _stateChangedHandler);
            }
            catch (Exception ex) { Log.Exception(UiText.LogLifecyclePocCleanupError, ex); }

            try { _harmony?.UnpatchAll(HarmonyId); }
            catch (Exception ex) { Log.Exception(UiText.LogLifecyclePocCleanupError, ex); }
            _harmony = null;
            _stateChangedHandler = null;
            _changedEvent = null;
            _observedStateMachine = null;
            _observedController = null;
            _onMusicScheduled = null;

            try
            {
                if (_writer != null)
                    WriteEvent("LifecyclePoCEnd", "reason=" + Clean(_stopReason ?? "unknown"), true);
            }
            catch { }
            CloseWriter();

            if (wasRunning)
            {
                _lastReason = _stopReason;
                if (!string.Equals(_stopReason, "pass", StringComparison.Ordinal))
                    StatusText = UiText.GuiLifecyclePocStopped;
                Log.Info(UiText.Format(UiText.LogLifecyclePocStoppedFormat, _stopReason ?? Unavailable));
            }
            _stopRequested = false;
            _stopReason = null;
            _finalizing = false;
        }

        private static void WriteEvent(string name, string extra, bool flush)
        {
            if (_writer == null) return;
            var sb = new StringBuilder(512);
            sb.Append("LifecyclePoC");
            Add(sb, "run", _run);
            Add(sb, "eventSeq", ++_eventSeq);
            Add(sb, "unityFrame", Time.frameCount);
            Add(sb, "event", name);
            Add(sb, "controllerState", StateName(EditorGameReflection.ReadControllerState()));
            Add(sb, "committedState", name == "StateCommitted" ? ExtractCommittedState(extra) : Unavailable);
            Add(sb, "scopeActive", _scopeActive);
            Add(sb, "playRequested", _playRequested);
            Add(sb, "playReturned", _playReturned);
            Add(sb, "playerAlive", ReadMember(ReadMember(EditorGameReflection.Controller(), "playerOne"), "alive"));
            Add(sb, "paused", ReadMember(EditorGameReflection.Controller(), "paused"));
            Add(sb, "RDC.auto", EditorGameReflection.ReadRdcAuto());
            if (!string.IsNullOrEmpty(extra)) sb.Append('|').Append(Clean(extra));
            _writer.WriteLine(sb.ToString());
            if (flush) _writer.Flush();
        }

        private static string ExtractCommittedState(string extra)
        {
            if (string.IsNullOrEmpty(extra)) return Unavailable;
            const string prefix = "committedState=";
            int start = extra.IndexOf(prefix, StringComparison.Ordinal);
            if (start < 0) return Unavailable;
            start += prefix.Length;
            int end = extra.IndexOf('|', start);
            return Clean(end < 0 ? extra.Substring(start) : extra.Substring(start, end - start));
        }

        private static object ReadMember(object instance, string name)
        {
            if (instance == null) return null;
            try
            {
                Type type = instance.GetType();
                while (type != null)
                {
                    PropertyInfo p = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (p != null) return p.GetValue(instance, null);
                    FieldInfo f = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (f != null) return f.GetValue(instance);
                    type = type.BaseType;
                }
            }
            catch { }
            return null;
        }

        private static object CurrentStateMachine()
        {
            return ReadMember(EditorGameReflection.Controller(), "stateMachine");
        }

        private static bool? ReadBool(object value)
        {
            if (value == null) return null;
            try { return Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static string StateName(object value)
        {
            if (value == null) return Unavailable;
            return Clean(value is Enum e ? e.ToString() : Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static string ResolveDiagnosticsRoot()
        {
            Settings settings = ModEntry.Settings;
            string configured = settings == null ? string.Empty : settings.OutputDirectory;
            DirectoryValidationResult validation = OutputPath.ValidateDirectory(configured);
            if (validation.Outcome == DirectoryValidationOutcome.Accept && !string.IsNullOrEmpty(validation.NormalizedPath))
                return validation.NormalizedPath;
            try { return Path.Combine(Application.persistentDataPath, "ADOFAI.Renderist"); }
            catch { return null; }
        }

        private static string ResolveUniqueLogPath(string dir)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(dir, LogFileBase + "-" + stamp + ".log");
            int i = 0;
            while (File.Exists(path))
                path = Path.Combine(dir, LogFileBase + "-" + stamp + "-" + (++i).ToString(CultureInfo.InvariantCulture) + ".log");
            return path;
        }

        private static void CloseWriter()
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }

        private static void Add(StringBuilder sb, string key, object value)
        {
            sb.Append('|').Append(key).Append('=').Append(Clean(Format(value)));
        }

        private static string Format(object value)
        {
            if (value == null) return Unavailable;
            if (value is bool b) return b ? "true" : "false";
            if (value is float f) return f.ToString("0.000000000", CultureInfo.InvariantCulture);
            if (value is double d) return d.ToString("0.000000000", CultureInfo.InvariantCulture);
            if (value is Enum e) return e.ToString();
            if (value is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string Clean(string value)
        {
            return (value ?? Unavailable).Replace('\r', ' ').Replace('\n', ' ').Replace('|', ';');
        }
    }
}
