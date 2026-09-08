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
    /// 与 Diagnostics.EditorVisualClockPoc 的反射块职责相同，但独立、只保留
    /// 正式路径需要的最小集合：类型 / 成员发现 + 只读状态读取 + RDC.auto 写入。
    ///
    /// 不引入 Assembly-CSharp.dll 编译引用；所有成员按名称在运行时解析，
    /// 解析失败时返回 null / false，调用方据此拒绝启动而不是抛异常。
    /// 本类不执行 Harmony Patch，不修改游戏状态（唯一的例外是 TryWriteRdcAuto）。
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

        private static PropertyInfo _pSongPosI;
        private static PropertyInfo _pSongPosMinusV;
        private static PropertyInfo _pState;
        private static PropertyInfo _pRdcAuto;
        private static FieldInfo _fCurrentSeq;

        private static MethodInfo _mConductorUpdate;
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

            if (_tConductor != null)
            {
                _pSongPosI = GetProperty(_tConductor, "songposition_minusi");
                _pSongPosMinusV = GetProperty(_tConductor, "songposition_minusv");
                _mConductorUpdate = _tConductor.GetMethod("Update",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (_tController != null)
            {
                _pState = GetProperty(_tController, "state");
                _fCurrentSeq = _tController.GetField("currentSeqID",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
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

        public static MethodInfo EditorPlayMethod
        {
            get
            {
                EnsureTypes();
                return _mEditorPlay;
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
        /// 读取 floors[0].entryTime，作为 canonical start time（chart-relative）。
        ///
        /// 与 songposition_minusi 使用同一 offset-adjusted 时间坐标系。
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
