using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 保留的旧 Editor Export / diagnostics 路径的只读 ADOFAI 运行时反射工具。
    ///
    /// 与已退役的 Diagnostics 反射块职责相同，但独立、只保留
    /// 正式路径需要的最小集合：类型 / 成员发现 + 只读状态读取 + RDC.auto 写入。
    ///
    /// 不引入 Assembly-CSharp.dll 编译引用；所有成员按名称在运行时解析，
    /// 解析失败时返回 null / false，调用方据此拒绝启动而不是抛异常。
    /// 本类不执行 Harmony Patch；除 TryWriteRdcAuto 外，只有明确限定的
    /// terminal restart 会调用 ADOFAI 官方 ChangeToStartState 状态迁移。
    /// </summary>
    internal static class EditorGameReflection
    {
        private const string Unavailable = "unavailable";

        private static bool _resolved;
        private static Assembly _gameAssembly;
        private static Type _tAdoBase;
        private static Type _tConductor;
        private static Type _tController;
        private static Type _tEditor;
        private static Type _tPlayer;
        private static Type _tFloor;
        private static Type _tRdc;
        private static Type _tAsyncInputUtils;

        private static PropertyInfo _pSongPosI;
        private static PropertyInfo _pSongPosMinusV;
        private static PropertyInfo _pAdjustedCountdownTicks;
        private static PropertyInfo _pState;
        private static PropertyInfo _pRdcAuto;
        private static FieldInfo _fCurrentSeq;
        private static FieldInfo _fCrotchetAtStart;

        private static MethodInfo _mConductorUpdate;
        private static MethodInfo _mAsyncInputAdjustAngle;
        private static MethodInfo _mControllerChangeToStartState;
        private static MethodInfo _mEditorPlay;
        private static MethodInfo _mEditorSelectFloor;
        private static MethodInfo _mEditorSwitchToEditMode;
        private static MethodInfo _mEditorMultiSelectFloors;

        // ================================================================
        // API 可用性（启动前校验用）
        // ================================================================

        /// <summary>解析 Assembly-CSharp 与所需类型 / 成员。幂等。</summary>
        public static bool EnsureTypes()
        {
            if (_resolved) return true;

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

            if (_gameAssembly == null) return false;

            try { _tAdoBase = _tAdoBase ?? _gameAssembly.GetType("ADOBase"); } catch { }
            try { _tConductor = _tConductor ?? _gameAssembly.GetType("scrConductor"); } catch { }
            try { _tController = _tController ?? _gameAssembly.GetType("scrController"); } catch { }
            try { _tEditor = _tEditor ?? _gameAssembly.GetType("scnEditor"); } catch { }
            try { _tPlayer = _tPlayer ?? _gameAssembly.GetType("scrPlayer"); } catch { }
            try { _tFloor = _tFloor ?? _gameAssembly.GetType("scrFloor"); } catch { }
            try { _tRdc = _tRdc ?? _gameAssembly.GetType("RDC"); } catch { }
            try { _tAsyncInputUtils = _tAsyncInputUtils ?? _gameAssembly.GetType("AsyncInputUtils"); } catch { }

            if (_tConductor != null)
            {
                _pSongPosI = GetProperty(_tConductor, "songposition_minusi");
                _pSongPosMinusV = GetProperty(_tConductor, "songposition_minusv");
                _pAdjustedCountdownTicks = GetProperty(_tConductor, "adjustedCountdownTicks");
                _fCrotchetAtStart = _tConductor.GetField("crotchetAtStart",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _mConductorUpdate = _tConductor.GetMethod("Update",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (_tController != null)
            {
                _pState = GetProperty(_tController, "state");
                _fCurrentSeq = _tController.GetField("currentSeqID",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _mControllerChangeToStartState = _tController.GetMethod("ChangeToStartState",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
            }

            if (_tEditor != null)
            {
                _mEditorPlay = _tEditor.GetMethod("Play",
                    BindingFlags.Public | BindingFlags.Instance);
                _mEditorSelectFloor = _tEditor.GetMethod("SelectFloor",
                    BindingFlags.Public | BindingFlags.Instance);
                _mEditorSwitchToEditMode = _tEditor.GetMethod("SwitchToEditMode",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(bool) }, null);
            }

            if (_tRdc != null)
            {
                _pRdcAuto = _tRdc.GetProperty("auto",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }

            if (_tAsyncInputUtils != null && _tPlayer != null)
            {
                _mAsyncInputAdjustAngle = _tAsyncInputUtils.GetMethod("AdjustAngle",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { _tPlayer, typeof(ulong) }, null);
            }

            _resolved = true;
            return true;
        }

        /// <summary>正式 Scheduler 所需 API 是否完整。</summary>
        public static bool SchedulerApiAvailable
        {
            get
            {
                EnsureTypes();
                return _tAdoBase != null && _tConductor != null && _tController != null &&
                       _tEditor != null && _tPlayer != null && _tFloor != null && _tRdc != null &&
                       _pState != null && _pRdcAuto != null && _pRdcAuto.CanWrite &&
                       _mConductorUpdate != null && _mEditorPlay != null &&
                       _mEditorSelectFloor != null && _mEditorSwitchToEditMode != null;
            }
        }

        /// <summary>
        /// Forced Visual Clock 所需 API 是否完整。
        /// songposition_minusi 的 getter / setter 与 scrConductor.Update 缺一不可。
        /// </summary>
        public static bool ForcedClockApiAvailable
        {
            get
            {
                EnsureTypes();
                return _pSongPosI != null && _pSongPosI.GetGetMethod(true) != null &&
                       _pSongPosI.GetSetMethod(true) != null && _mConductorUpdate != null;
            }
        }

        /// <summary>取 songposition_minusi 的 getter / setter（供 Harmony Patch）。</summary>
        public static bool TryGetSongPositionAccessors(out MethodInfo getter, out MethodInfo setter)
        {
            getter = null;
            setter = null;
            if (_pSongPosI == null) return false;
            getter = _pSongPosI.GetGetMethod(true);
            setter = _pSongPosI.GetSetMethod(true);
            return getter != null && setter != null;
        }

        public static MethodInfo ConductorUpdateMethod
        {
            get
            {
                EnsureTypes();
                return _mConductorUpdate;
            }
        }

        /// <summary>
        /// 当前游戏版本 AsyncInputUtils.AdjustAngle(scrPlayer, ulong) 的异步角度刷新入口。
        /// 确定性 forced clock 活跃时由 Scheduler 暂时抑制该入口，避免它用 tick 时钟
        /// 覆盖 native Conductor 已写入的本帧角度。
        /// </summary>
        public static MethodInfo AsyncInputAdjustAngleMethod
        {
            get
            {
                EnsureTypes();
                return _mAsyncInputAdjustAngle;
            }
        }

        public static MethodInfo EditorPlayMethod
        {
            get
            {
                EnsureTypes();
                return _mEditorPlay;
            }
        }

        /// <summary>
        /// 当前 ADOFAI 版本用于把 terminal controller state 重新置为 Start 的
        /// 官方公开入口。仅供 terminal restart re-arm 使用；不属于普通启动的
        /// scheduler API gate，也不直接写入 controller.state。
        /// </summary>
        public static MethodInfo ControllerChangeToStartStateMethod
        {
            get
            {
                EnsureTypes();
                return _mControllerChangeToStartState;
            }
        }

        public static MethodInfo EditorSelectFloorMethod
        {
            get
            {
                EnsureTypes();
                return _mEditorSelectFloor;
            }
        }

        public static MethodInfo EditorSwitchToEditModeMethod
        {
            get
            {
                EnsureTypes();
                return _mEditorSwitchToEditMode;
            }
        }

        public static MethodInfo EditorMultiSelectFloorsMethod
        {
            get
            {
                EnsureTypes();
                if (_mEditorMultiSelectFloors == null && _tEditor != null && _tFloor != null)
                {
                    _mEditorMultiSelectFloors = _tEditor.GetMethod("MultiSelectFloors",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, new[] { _tFloor, _tFloor, typeof(bool) }, null);
                }
                return _mEditorMultiSelectFloors;
            }
        }

        // ================================================================
        // 实例访问
        // ================================================================

        public static object Conductor() => StaticValue(_tAdoBase, "conductor");
        public static object Controller() => StaticValue(_tAdoBase, "controller");
        public static object Editor() => StaticValue(_tAdoBase, "editor");

        public static Type EditorType
        {
            get { EnsureTypes(); return _tEditor; }
        }

        public static Type FloorType
        {
            get { EnsureTypes(); return _tFloor; }
        }

        // ================================================================
        // 只读状态读取
        // ================================================================

        /// <summary>当前 songposition_minusi（未做确定性覆盖的原始值）。</summary>
        public static object ReadConductorSongPosition()
        {
            object conductor = Conductor();
            return conductor == null || _pSongPosI == null
                ? null
                : ReadPropertyValue(conductor, _pSongPosI);
        }

        /// <summary>songposition_minusv 原始值（锚点日志用）。</summary>
        public static object ReadConductorSongPositionMinusV()
        {
            object conductor = Conductor();
            return conductor == null || _pSongPosMinusV == null
                ? null
                : ReadPropertyValue(conductor, _pSongPosMinusV);
        }

        /// <summary>
        /// 根据当前 native Conductor 的 countdown 参数，读取 gameplay-start 的
        /// chart-time 偏移。该值不使用 wall clock；Countdown_Update 的 beatNumber
        /// 从 nextBeatTime=0 开始，在达到 adjusted ticks 前跨过 adjusted ticks-1
        /// 个 crotchet，因此阈值是 (adjusted ticks - 1) × crotchet。pitch 在 native
        /// countdown 调度与 songposition 计算中相互抵消。
        /// </summary>
        public static bool TryReadGameplayStartOffset(out double offset, out string rejectReason)
        {
            offset = 0.0;
            rejectReason = null;

            try
            {
                object conductor = Conductor();
                if (conductor == null || _pAdjustedCountdownTicks == null || _fCrotchetAtStart == null)
                {
                    rejectReason = "conductor-countdown-api-unavailable";
                    return false;
                }

                double? adjustedTicks = ToDouble(ReadPropertyValue(conductor, _pAdjustedCountdownTicks));
                double? crotchet = ToDouble(_fCrotchetAtStart.GetValue(conductor));
                if (!adjustedTicks.HasValue || !crotchet.HasValue ||
                    double.IsNaN(adjustedTicks.Value) || double.IsInfinity(adjustedTicks.Value) ||
                    double.IsNaN(crotchet.Value) || double.IsInfinity(crotchet.Value) ||
                    adjustedTicks.Value < 0.0 || crotchet.Value <= 0.0001)
                {
                    rejectReason = "conductor-countdown-values-invalid";
                    return false;
                }

                // scrConductor.Update starts nextBeatTime at zero. The first beat
                // raises beatNumber from 0 to 1, so Countdown_Update observes the
                // requested beat count after (ticks - 1) beat intervals.
                offset = Math.Max(0.0, adjustedTicks.Value - 1.0) * crotchet.Value;
                if (double.IsNaN(offset) || double.IsInfinity(offset) || Math.Abs(offset) > 3600.0)
                {
                    rejectReason = "gameplay-start-offset-invalid";
                    return false;
                }

                return true;
            }
            catch
            {
                rejectReason = "gameplay-start-offset-read-failed";
                return false;
            }
        }

        public static bool? ReadRdcAuto()
        {
            EnsureTypes();
            if (_pRdcAuto == null) return null;
            try { return ToBool(_pRdcAuto.GetValue(null, null)); }
            catch { return null; }
        }

        public static bool TryWriteRdcAuto(bool value)
        {
            EnsureTypes();
            try
            {
                if (_pRdcAuto == null || !_pRdcAuto.CanWrite) return false;
                _pRdcAuto.SetValue(null, value, null);
                return true;
            }
            catch { return false; }
        }

        public static object ReadControllerState()
        {
            object controller = Controller();
            return controller == null || _pState == null
                ? null
                : ReadPropertyValue(controller, _pState);
        }

        public static bool? ReadEditorPlayMode()
        {
            object editor = Editor();
            if (editor == null || _tEditor == null) return null;
            try { return ToBool(ReadInstanceMember(editor, _tEditor, "playMode")); }
            catch { return null; }
        }

        public static bool IsProbablyEditorNow()
        {
            EnsureTypes();
            // 与 Diagnostics 一致：以实机验证场景名 scnEditor 为主，isLevelEditor 为辅助。
            if (string.Equals(SceneName(), "scnEditor", StringComparison.Ordinal)) return true;
            return ToBool(StaticValue(_tAdoBase, "isLevelEditor")) == true;
        }

        public static int ReadPlayerFloor()
        {
            try
            {
                object controller = Controller();
                if (controller == null || _tController == null) return -1;
                object playerOne = ReadInstanceMember(controller, _tController, "playerOne");
                if (playerOne == null) return -1;
                object currFloor = ReadInstanceMember(playerOne, playerOne.GetType(), "currFloor");
                if (currFloor == null) return -1;
                return ToInt(ReadInstanceMember(currFloor, currFloor.GetType(), "seqID"));
            }
            catch { return -1; }
        }

        public static int ReadCurrentSeqId()
        {
            try
            {
                object controller = Controller();
                return controller == null || _fCurrentSeq == null
                    ? -1
                    : ToInt(_fCurrentSeq.GetValue(controller));
            }
            catch { return -1; }
        }

        public static string SceneName()
        {
            try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; }
            catch { return Unavailable; }
        }

        /// <summary>编辑器是否已加载可播放关卡（customLevel 非空或 floors 多于 1 格）。</summary>
        public static bool IsLevelLoaded()
        {
            try
            {
                object editor = Editor();
                if (editor == null || _tEditor == null) return false;
                object customLevel = ReadInstanceMember(editor, _tEditor, "customLevel");
                if (customLevel != null) return true;
                IList floors = ReadFloorsList();
                return floors != null && floors.Count > 1;
            }
            catch { return false; }
        }

        /// <summary>读取 scnEditor.floors（List&lt;scrFloor&gt;）。</summary>
        public static IList ReadFloorsList()
        {
            object editor = Editor();
            if (editor == null || _tEditor == null) return null;
            object floors = ReadInstanceMember(editor, _tEditor, "floors");
            return floors as IList;
        }

        public static void ReadSelectedFloorSeqs(List<int> into)
        {
            if (into == null) return;
            try
            {
                object editor = Editor();
                if (editor == null || _tEditor == null) return;
                object sel = ReadInstanceMember(editor, _tEditor, "selectedFloors");
                if (sel is IEnumerable en)
                {
                    foreach (object floor in en)
                    {
                        if (floor == null) continue;
                        int seq = FloorSeq(floor);
                        if (seq >= 0) into.Add(seq);
                    }
                }
            }
            catch { }
        }

        public static int FloorSeq(object floor)
        {
            return floor == null ? -1 : ToInt(ReadInstanceMember(floor, floor.GetType(), "seqID"));
        }

        /// <summary>
        /// 读取 floors[0].entryTime，作为 chart/floor 基准；最终 gameplay-start
        /// anchor 在 lifecycle-ready 时结合 native countdown threshold 计算。
        ///
        /// 该值仅作为 floor-time base，不直接作为 PlayerControl handoff time。
        /// 读取失败返回 false 并给出可诊断原因；调用方必须拒绝启动，不得猜测 0。
        /// </summary>
        public static bool TryReadFloor0EntryTime(out double entryTime, out string rejectReason)
        {
            entryTime = 0.0;
            rejectReason = null;

            try
            {
                IList floors = ReadFloorsList();
                if (floors == null || floors.Count < 1)
                {
                    rejectReason = "floors-empty";
                    return false;
                }

                object floor0 = floors[0];
                if (floor0 == null)
                {
                    rejectReason = "floor0-null";
                    return false;
                }

                object raw = ReadInstanceMember(floor0, floor0.GetType(), "entryTime");
                double? v = ToDouble(raw);
                if (!v.HasValue || double.IsNaN(v.Value) || double.IsInfinity(v.Value) ||
                    Math.Abs(v.Value) > 3600.0)
                {
                    rejectReason = "floor0-entry-time-invalid";
                    return false;
                }

                entryTime = v.Value;
                return true;
            }
            catch
            {
                rejectReason = "floor0-entry-time-read-failed";
                return false;
            }
        }

        /// <summary>读取当前播放音高；unavailable=true 表示无法读取并回退到 1。</summary>
        public static double ReadPitch(out bool unavailable)
        {
            unavailable = true;
            try
            {
                object conductor = Conductor();
                if (conductor == null || _tConductor == null) return 1.0;

                object song = ReadInstanceMember(conductor, _tConductor, "song");
                if (song != null)
                {
                    object p = ReadInstanceMember(song, song.GetType(), "pitch");
                    double? d = ToDouble(p);
                    if (d.HasValue && !double.IsNaN(d.Value) && d.Value > 0.0001)
                    {
                        unavailable = false;
                        return d.Value;
                    }
                }

                object asrc = ReadInstanceMember(conductor, _tConductor, "audioSource");
                if (asrc != null)
                {
                    object p = ReadInstanceMember(asrc, asrc.GetType(), "pitch");
                    double? d = ToDouble(p);
                    if (d.HasValue && !double.IsNaN(d.Value) && d.Value > 0.0001)
                    {
                        unavailable = false;
                        return d.Value;
                    }
                }
            }
            catch
            {
                unavailable = true;
            }
            return 1.0;
        }

        public static bool IsFailureState(object state)
        {
            string s = ToState(state);
            return string.Equals(s, "Fail", StringComparison.Ordinal) ||
                   string.Equals(s, "Fail2", StringComparison.Ordinal);
        }

        // ================================================================
        // 通用反射读取 / 类型转换
        // ================================================================

        private static PropertyInfo GetProperty(Type type, string name)
        {
            if (type == null) return null;
            return type.GetProperty(name,
                       BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ??
                   type.GetProperty(name,
                       BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
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
            catch { return null; }
        }

        private static object ReadInstanceMember(object instance, Type type, string member)
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
            catch { return null; }
        }

        private static object ReadPropertyValue(object instance, PropertyInfo p)
        {
            if (instance == null || p == null) return null;
            try { return p.GetValue(instance, null); }
            catch { return null; }
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
