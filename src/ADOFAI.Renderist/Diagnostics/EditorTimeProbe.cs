using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using ADOFAI.Renderist.Capture;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Diagnostics
{
    /// <summary>
    /// 编辑器播放时间链 / Seek 行为实机探针（Phase 3.0）。
    ///
    /// 只观察、不修改：
    ///   * 不主动 Play / Pause / Resume / Stop；
    ///   * 不主动 Seek / Scrub；
    ///   * 不修改 conductor 时间字段 / AudioSource / DSP 时间 / 关卡数据。
    ///
    /// 通过 reflection 读取 scrConductor / scrController / scnEditor / ADOBase 的只读状态，
    /// 写入独立 .log 文件；Harmony 仅以“Prefix 采样 + Postfix 采样”方式记录
    /// scrConductor 的 seek 调用与 scnEditor.Play 调用，不改变原方法行为。
    /// </summary>
    internal static class EditorTimeProbe
    {
        private const string DiagnosticsSubdir = "diagnostics";
        private const string LogFileBase = "editor-time-probe";
        private const string Unavailable = "unavailable";
        private const float SampleIntervalSeconds = 0.1f;        // 普通采样约 10 Hz
        private const double SeekJumpThresholdSeconds = 0.5;     // 相邻采样间的时间跳变阈值（秒）

        private static bool _active;
        private static StreamWriter _writer;
        private static string _logPath;
        private static float _lastSampleRealtime = float.NegativeInfinity;
        private static float _lastErrorLogRealtime = float.NegativeInfinity;

        private static Assembly _gameAssembly;
        private static Type _tAdoBase;
        private static Type _tConductor;
        private static Type _tController;
        private static Type _tEditor;

        private static MethodInfo _mScrubToTime;
        private static MethodInfo _mScrubToTile;
        private static MethodInfo _mPlay;
        private static bool _hooksRegistered;

        private static ProbeSnapshot _prev;

        public static bool IsRunning => _active;

        public static string LogPath => _logPath;

        // ---------------- lifecycle ----------------

        public static bool Start()
        {
            if (_active)
            {
                Log.Warn(UiText.LogTimeProbeAlreadyRunning);
                return false;
            }

            try
            {
                EnsureTypes();

                // Editor-only：启动前确认当前处于编辑器。
                // 以已实机验证的场景名 "scnEditor" 为主，ADOBase.isLevelEditor 为辅助。
                if (!IsProbablyEditorNow())
                {
                    Log.Warn(UiText.LogTimeProbeRejectedNotEditor);
                    return false;
                }

                string root = ResolveDiagnosticsRoot();
                if (string.IsNullOrEmpty(root))
                {
                    Log.Error(UiText.LogTimeProbeNoOutputDir);
                    return false;
                }

                string dir = Path.Combine(root, DiagnosticsSubdir);
                Directory.CreateDirectory(dir);

                string path = ResolveUniqueLogPath(dir);
                if (string.IsNullOrEmpty(path))
                {
                    Log.Error(UiText.LogTimeProbeNoOutputDir);
                    return false;
                }

                var writer = new StreamWriter(path, false, new UTF8Encoding(false));
                writer.AutoFlush = false;

                _writer = writer;
                _logPath = path;
                _active = true;
                _lastSampleRealtime = float.NegativeInfinity;
                _prev = default(ProbeSnapshot);

                RegisterHooks();

                WriteHeader();
                WriteLine(BuildFullLine("ProbeStarted", null), flush: true);
                _prev = CaptureSnapshot();

                Log.Info(UiText.LogTimeProbeStarted);
                Log.Info(UiText.Format(UiText.LogTimeProbeLogPathFormat, path));
                return true;
            }
            catch (Exception ex)
            {
                _active = false;
                Log.Exception(UiText.Format(UiText.LogTimeProbeStartFailedFormat, ex.Message), ex);
                FailSafeClose();
                return false;
            }
        }

        public static void Stop(string reason)
        {
            bool wasActive = _active;
            _active = false;

            if (_writer != null)
            {
                try
                {
                    WriteLine(BuildFullLine("ProbeStopped", "reason=" + (reason ?? "?")), flush: true);
                }
                catch
                {
                    // Stop 期间 IO 异常不向玩家抛出；最终仍尽力关闭。
                }
            }

            UnregisterHooks();
            FailSafeClose();

            if (wasActive)
            {
                Log.Info(UiText.Format(UiText.LogTimeProbeStoppedFormat, reason ?? "?"));
            }
        }

        /// <summary>
        /// 每 OnUpdate 调用一次：普通采样（约 10 Hz）+ 状态跃迁检测。
        /// 不执行任何主动 Seek / Play / Pause。
        /// </summary>
        public static void Tick()
        {
            if (!_active) return;

            try
            {
                if (!ModEntry.Enabled)
                {
                    Stop("mod-disabled");
                    return;
                }

                ProbeSnapshot now = CaptureSnapshot();

                if (_prev.Valid)
                {
                    // 场景变化
                    if (!string.Equals(_prev.Scene, now.Scene, StringComparison.Ordinal))
                    {
                        WriteLine(BuildFullLine("SceneChanged", null), flush: false);
                    }

                    // 进入 / 离开编辑器
                    if (ChangeOf(_prev.InEditor, now.InEditor, out bool editorNow))
                    {
                        if (editorNow)
                        {
                            WriteLine(BuildFullLine("EditorEntered", null), flush: true);
                        }
                        else
                        {
                            WriteLine(BuildFullLine("EditorExited", null), flush: true);
                            Stop("left-editor");
                            return;
                        }
                    }

                    // 播放 / 停止
                    if (NullableBoolChanged(_prev.PlayMode, now.PlayMode, out bool playNow))
                    {
                        WriteLine(BuildFullLine(playNow ? "PlayStarted" : "PlayStopped", null), flush: true);
                    }

                    // 暂停 / 恢复（仅在播放态下有意义）
                    if (now.PlayMode == true &&
                        NullableBoolChanged(_prev.PausedInPlayMode, now.PausedInPlayMode, out bool pausedNow))
                    {
                        WriteLine(BuildFullLine(pausedNow ? "PauseDetected" : "ResumeDetected", null), flush: true);
                    }

                    // controller state 变化
                    if (!string.Equals(_prev.ControllerState, now.ControllerState, StringComparison.Ordinal))
                    {
                        WriteLine(BuildFullLine("ControllerStateChanged",
                            "before=" + (_prev.ControllerState ?? Unavailable) +
                            "|after=" + (now.ControllerState ?? Unavailable)),
                            flush: false);
                    }

                    // 未被 Hook 捕获的时间跳变（相邻普通采样间）
                    if (IsSeekJump(_prev.SongPosI, now.SongPosI))
                    {
                        WriteLine(BuildFullLine("ScrubDetected",
                            "beforeSongPosI=" + Fmt(_prev.SongPosI) +
                            "|afterSongPosI=" + Fmt(now.SongPosI)),
                            flush: true);
                    }
                }

                // 普通采样（约 10 Hz）
                float rt = Time.realtimeSinceStartup;
                if (_lastSampleRealtime == float.NegativeInfinity ||
                    (rt - _lastSampleRealtime) >= SampleIntervalSeconds)
                {
                    WriteLine(BuildFullLine("Sample", null), flush: false);
                    _lastSampleRealtime = rt;
                }

                _prev = now;
            }
            catch (Exception ex)
            {
                ReportErrorOnce(ex);
                Stop("tick-exception");
            }
        }

        // ---------------- reflection helpers ----------------

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
            catch
            {
                _gameAssembly = null;
            }

            if (_gameAssembly == null) return;

            try { _tAdoBase = _tAdoBase ?? _gameAssembly.GetType("ADOBase"); } catch { }
            try { _tConductor = _tConductor ?? _gameAssembly.GetType("scrConductor"); } catch { }
            try { _tController = _tController ?? _gameAssembly.GetType("scrController"); } catch { }
            try { _tEditor = _tEditor ?? _gameAssembly.GetType("scnEditor"); } catch { }
        }

        private static object StaticValue(Type type, string member)
        {
            if (type == null) return null;
            try
            {
                PropertyInfo p = type.GetProperty(member,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (p != null) return p.GetValue(null, null);

                FieldInfo f = type.GetField(member,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f != null) return f.GetValue(null);

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static object InstanceValue(object instance, Type type, string member)
        {
            if (instance == null || type == null) return null;
            try
            {
                PropertyInfo p = type.GetProperty(member,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null) return p.GetValue(instance, null);

                FieldInfo f = type.GetField(member,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) return f.GetValue(instance);

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static bool InstanceExists(Type owner, string staticMember)
        {
            return StaticValue(owner, staticMember) != null;
        }

        private static object Conductor()
        {
            return StaticValue(_tAdoBase, "conductor");
        }

        private static object Controller()
        {
            return StaticValue(_tAdoBase, "controller");
        }

        private static object Editor()
        {
            return StaticValue(_tAdoBase, "editor");
        }

        private static bool? ReadStaticNullableBool(Type type, string member)
        {
            return ToBool(StaticValue(type, member));
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

        private static string ToState(object v)
        {
            if (v == null) return null;
            return v is Enum e ? e.ToString() : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static string CurrentSceneName()
        {
            try { return SceneManager.GetActiveScene().name; }
            catch { return Unavailable; }
        }

        private static string NowStamp()
        {
            return DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

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

        private static string CleanValue(string v)
        {
            if (v == null) return Unavailable;
            return v.Replace('\r', ' ').Replace('\n', ' ').Replace('|', ';');
        }

        private static void AppendField(StringBuilder sb, string key, string value, bool first)
        {
            if (!first) sb.Append(" | ");
            sb.Append(key).Append('=').Append(CleanValue(value));
        }

        // ---------------- state capture ----------------

        private struct ProbeSnapshot
        {
            public bool Valid;
            public string Scene;
            public bool InEditor;
            public bool? PlayMode;
            public bool? PausedInPlayMode;
            public double? SongPosI;
            public string ControllerState;
        }

        private static bool IsProbablyEditorNow()
        {
            // 首选：Phase 2.2 实机验证的编辑器场景名。
            if (string.Equals(CurrentSceneName(), "scnEditor", StringComparison.Ordinal))
            {
                return true;
            }
            // 辅助：ADOBase.isLevelEditor。
            return ReadStaticNullableBool(_tAdoBase, "isLevelEditor") == true;
        }

        private static ProbeSnapshot CaptureSnapshot()
        {
            object conductor = Conductor();
            object controller = Controller();
            object editor = Editor();

            return new ProbeSnapshot
            {
                Valid = true,
                Scene = CurrentSceneName(),
                InEditor = IsProbablyEditorNow(),
                PlayMode = ToBool(InstanceValue(editor, _tEditor, "playMode")),
                PausedInPlayMode = ToBool(InstanceValue(editor, _tEditor, "pausedInPlayMode")),
                SongPosI = ToDouble(InstanceValue(conductor, _tConductor, "songposition_minusi")),
                ControllerState = ToState(InstanceValue(controller, _tController, "currentState")),
            };
        }

        private static bool ChangeOf(bool prev, bool now, out bool current)
        {
            current = now;
            return prev != now;
        }

        private static bool NullableBoolChanged(bool? prev, bool? now, out bool current)
        {
            current = now == true;
            if (now == null) return false; // 不把 unavailable 当跃迁
            return prev != now;
        }

        private static bool IsSeekJump(double? prev, double? now)
        {
            if (!prev.HasValue || !now.HasValue) return false;
            return Math.Abs(now.Value - prev.Value) > SeekJumpThresholdSeconds;
        }

        // ---------------- line builders ----------------

        private static string BuildFullLine(string eventName, string extra)
        {
            EnsureTypes();

            object conductor = Conductor();
            object controller = Controller();
            object editor = Editor();

            var sb = new StringBuilder(640);
            AppendField(sb, "timestamp", NowStamp(), true);
            AppendField(sb, "frame", Time.frameCount.ToString(CultureInfo.InvariantCulture), false);
            AppendField(sb, "event", eventName, false);
            AppendField(sb, "scene", CurrentSceneName(), false);
            AppendField(sb, "isLevelEditor", Fmt(StaticValue(_tAdoBase, "isLevelEditor")), false);
            AppendField(sb, "isScnGame", Fmt(StaticValue(_tAdoBase, "isScnGame")), false);
            AppendField(sb, "isCLS", Fmt(StaticValue(_tAdoBase, "isCLS")), false);
            AppendField(sb, "editorInstance", InstanceExists(_tAdoBase, "editor") ? "true" : "false", false);
            AppendField(sb, "controllerInstance", InstanceExists(_tAdoBase, "controller") ? "true" : "false", false);
            AppendField(sb, "conductorInstance", InstanceExists(_tAdoBase, "conductor") ? "true" : "false", false);
            AppendField(sb, "playMode", Fmt(InstanceValue(editor, _tEditor, "playMode")), false);
            AppendField(sb, "pausedInPlayMode", Fmt(InstanceValue(editor, _tEditor, "pausedInPlayMode")), false);
            AppendField(sb, "songposition_minusi", Fmt(InstanceValue(conductor, _tConductor, "songposition_minusi")), false);
            AppendField(sb, "songposition_minusv", Fmt(InstanceValue(conductor, _tConductor, "songposition_minusv")), false);
            AppendField(sb, "beatNumber", Fmt(InstanceValue(conductor, _tConductor, "beatNumber")), false);
            AppendField(sb, "barNumber", Fmt(InstanceValue(conductor, _tConductor, "barNumber")), false);
            AppendField(sb, "bpm", Fmt(InstanceValue(conductor, _tConductor, "bpm")), false);
            AppendField(sb, "hasSongStarted", Fmt(InstanceValue(conductor, _tConductor, "hasSongStarted")), false);
            AppendField(sb, "dspTime", Fmt(InstanceValue(conductor, _tConductor, "dspTime")), false);
            AppendField(sb, "dspTimeSong", Fmt(InstanceValue(conductor, _tConductor, "dspTimeSong")), false);
            AppendField(sb, "deltaSongPos", Fmt(InstanceValue(conductor, _tConductor, "deltaSongPos")), false);
            AppendField(sb, "previousFrameTime", Fmt(InstanceValue(conductor, _tConductor, "previousFrameTime")), false);
            AppendField(sb, "nextBeatTime", Fmt(InstanceValue(conductor, _tConductor, "nextBeatTime")), false);
            AppendField(sb, "nextBarTime", Fmt(InstanceValue(conductor, _tConductor, "nextBarTime")), false);
            AppendField(sb, "dspTimeSongPosZero", Fmt(InstanceValue(conductor, _tConductor, "dspTimeSongPosZero")), false);
            AppendField(sb, "controllerPaused", Fmt(InstanceValue(controller, _tController, "paused")), false);
            AppendField(sb, "audioPaused", Fmt(InstanceValue(controller, _tController, "audioPaused")), false);
            AppendField(sb, "controllerState", Fmt(InstanceValue(controller, _tController, "currentState")), false);
            if (!string.IsNullOrEmpty(extra))
            {
                AppendField(sb, "extra", extra, false);
            }
            return sb.ToString();
        }

        // ---------------- log file ----------------

        private static void WriteHeader()
        {
            if (_writer == null) return;
            _writer.WriteLine("# ADOFAI Renderist editor time probe");
            _writer.WriteLine("# version=" + ModEntry.ModVersion);
            _writer.WriteLine("# startedAt=" + NowStamp());
            _writer.WriteLine("# sampleIntervalMs=" +
                ((int)(SampleIntervalSeconds * 1000)).ToString(CultureInfo.InvariantCulture));
            _writer.Flush();
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

        private static string ResolveDiagnosticsRoot()
        {
            Settings settings = ModEntry.Settings;
            string configured = settings != null ? settings.OutputDirectory : string.Empty;

            DirectoryValidationResult v = OutputPath.ValidateDirectory(configured);
            if (v.Outcome == DirectoryValidationOutcome.Accept && !string.IsNullOrEmpty(v.NormalizedPath))
            {
                return v.NormalizedPath;
            }

            // 回退：Unity 用户数据目录（不写安装目录 / Managed / Mods / references）。
            try
            {
                return Path.Combine(Application.persistentDataPath, "ADOFAI.Renderist");
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveUniqueLogPath(string dir)
        {
            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string basePath = Path.Combine(dir, LogFileBase + "-" + stamp + ".log");
                string path = basePath;
                int i = 0;
                while (File.Exists(path))
                {
                    i++;
                    path = Path.Combine(dir,
                        LogFileBase + "-" + stamp + "_" + i.ToString("000", CultureInfo.InvariantCulture) + ".log");
                }
                return path;
            }
            catch
            {
                return null;
            }
        }

        private static void FailSafeClose()
        {
            try
            {
                StreamWriter w = _writer;
                _writer = null;
                if (w != null)
                {
                    w.Flush();
                    w.Close();
                    w.Dispose();
                }
            }
            catch
            {
            }
            _logPath = null;
        }

        private static void ReportErrorOnce(Exception ex)
        {
            float rt = Time.realtimeSinceStartup;
            if (_lastErrorLogRealtime != float.NegativeInfinity && (rt - _lastErrorLogRealtime) < 5f)
            {
                return;
            }
            _lastErrorLogRealtime = rt;
            Log.Exception(UiText.LogTimeProbeError, ex);
        }

        // ---------------- Harmony 只读诊断 Hook ----------------

        private sealed class SeekBefore
        {
            public double? SongPosI;
            public object Beat;
            public double? DspTimeSong;
            public bool? PlayMode;
            public bool? PausedInPlayMode;
        }

        private static SeekBefore CaptureSeekBefore(object instance)
        {
            object editor = Editor();
            return new SeekBefore
            {
                SongPosI = ToDouble(InstanceValue(instance, _tConductor, "songposition_minusi")),
                Beat = InstanceValue(instance, _tConductor, "beatNumber"),
                DspTimeSong = ToDouble(InstanceValue(instance, _tConductor, "dspTimeSong")),
                PlayMode = ToBool(InstanceValue(editor, _tEditor, "playMode")),
                PausedInPlayMode = ToBool(InstanceValue(editor, _tEditor, "pausedInPlayMode")),
            };
        }

        private static void EmitSeekLine(string eventName, string arg, SeekBefore before, object instance)
        {
            object editor = Editor();

            var sb = new StringBuilder(640);
            AppendField(sb, "timestamp", NowStamp(), true);
            AppendField(sb, "frame", Time.frameCount.ToString(CultureInfo.InvariantCulture), false);
            AppendField(sb, "event", eventName, false);
            AppendField(sb, "arg", arg, false);
            AppendField(sb, "before.songposition_minusi", before != null ? Fmt(before.SongPosI) : Unavailable, false);
            AppendField(sb, "after.songposition_minusi", Fmt(InstanceValue(instance, _tConductor, "songposition_minusi")), false);
            AppendField(sb, "before.beatNumber", before != null ? Fmt(before.Beat) : Unavailable, false);
            AppendField(sb, "after.beatNumber", Fmt(InstanceValue(instance, _tConductor, "beatNumber")), false);
            AppendField(sb, "before.dspTimeSong", before != null ? Fmt(before.DspTimeSong) : Unavailable, false);
            AppendField(sb, "after.dspTimeSong", Fmt(InstanceValue(instance, _tConductor, "dspTimeSong")), false);
            AppendField(sb, "playMode", Fmt(InstanceValue(editor, _tEditor, "playMode")), false);
            AppendField(sb, "pausedInPlayMode", Fmt(InstanceValue(editor, _tEditor, "pausedInPlayMode")), false);
            WriteLine(sb.ToString(), flush: true);
        }

        private static void ScrubMusicToTimePrefix(object __instance, double newTime, out object __state)
        {
            __state = CaptureSeekBefore(__instance);
        }

        private static void ScrubMusicToTimePostfix(object __instance, double newTime, object __state)
        {
            try
            {
                EmitSeekLine("ScrubMusicToTime",
                    newTime.ToString("0.######", CultureInfo.InvariantCulture),
                    __state as SeekBefore, __instance);
            }
            catch (Exception ex)
            {
                ReportErrorOnce(ex);
            }
        }

        private static void ScrubMusicToTilePrefix(object __instance, int tileID, out object __state)
        {
            __state = CaptureSeekBefore(__instance);
        }

        private static void ScrubMusicToTilePostfix(object __instance, int tileID, object __state)
        {
            try
            {
                EmitSeekLine("ScrubMusicToTile",
                    tileID.ToString(CultureInfo.InvariantCulture),
                    __state as SeekBefore, __instance);
            }
            catch (Exception ex)
            {
                ReportErrorOnce(ex);
            }
        }

        private static void PlayPostfix(object __instance)
        {
            try
            {
                WriteLine(BuildFullLine("scnEditorPlay", null), flush: true);
            }
            catch (Exception ex)
            {
                ReportErrorOnce(ex);
            }
        }

        private static void RegisterHooks()
        {
            UnregisterHooks();

            try
            {
                Harmony harmony = ModEntry.Harmony;
                if (harmony == null) return;

                if (_tConductor != null)
                {
                    _mScrubToTime = _tConductor.GetMethod("ScrubMusicToTime",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (_mScrubToTime != null)
                    {
                        harmony.Patch(_mScrubToTime,
                            prefix: new HarmonyMethod(typeof(EditorTimeProbe), nameof(ScrubMusicToTimePrefix)),
                            postfix: new HarmonyMethod(typeof(EditorTimeProbe), nameof(ScrubMusicToTimePostfix)));
                    }

                    _mScrubToTile = _tConductor.GetMethod("ScrubMusicToTile",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (_mScrubToTile != null)
                    {
                        harmony.Patch(_mScrubToTile,
                            prefix: new HarmonyMethod(typeof(EditorTimeProbe), nameof(ScrubMusicToTilePrefix)),
                            postfix: new HarmonyMethod(typeof(EditorTimeProbe), nameof(ScrubMusicToTilePostfix)));
                    }
                }

                if (_tEditor != null)
                {
                    _mPlay = _tEditor.GetMethod("Play", BindingFlags.Public | BindingFlags.Instance);
                    if (_mPlay != null)
                    {
                        harmony.Patch(_mPlay,
                            postfix: new HarmonyMethod(typeof(EditorTimeProbe), nameof(PlayPostfix)));
                    }
                }

                _hooksRegistered = true;
            }
            catch (Exception ex)
            {
                Log.Exception("EditorTimeProbe: 注册 Harmony Hook 失败", ex);
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
                    if (_mScrubToTime != null) harmony.Unpatch(_mScrubToTime, HarmonyPatchType.All, ModEntry.HarmonyId);
                    if (_mScrubToTile != null) harmony.Unpatch(_mScrubToTile, HarmonyPatchType.All, ModEntry.HarmonyId);
                    if (_mPlay != null) harmony.Unpatch(_mPlay, HarmonyPatchType.All, ModEntry.HarmonyId);
                }
                catch (Exception ex)
                {
                    Log.Exception("EditorTimeProbe: 撤销 Harmony Hook 失败", ex);
                }
            }

            _mScrubToTime = null;
            _mScrubToTile = null;
            _mPlay = null;
            _hooksRegistered = false;
        }
    }
}
