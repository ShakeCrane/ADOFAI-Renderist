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
        private static Type _tPlanet;
        private static Type _tFloor;
        private static Type _tPortal;
        private static Type _tRdc;
        private static Type _tAsyncInputUtils;
        private static Type _tScrCamera;
        private static Type _tPlayerManager;

        // Render Source（scrCamera 原生摄像机链）成员元数据。
        // 只缓存成员描述，绝不缓存场景对象引用。
        private static MemberInfo _mCameraBgStatic;
        private static MemberInfo _mCameraBg;
        private static MemberInfo _mCameraMain;

        private static PropertyInfo _pSongPosI;
        private static PropertyInfo _pSongPosMinusV;
        private static PropertyInfo _pAdjustedCountdownTicks;
        private static PropertyInfo _pState;
        private static PropertyInfo _pRdcAuto;
        private static FieldInfo _fCurrentSeq;
        private static FieldInfo _fCrotchetAtStart;
        private static FieldInfo _fConductorBpm;
        private static FieldInfo _fControllerListBpm;

        private static MethodInfo _mConductorUpdate;
        private static MethodInfo _mAsyncInputAdjustAngle;
        private static MethodInfo _mControllerChangeToStartState;
        private static MethodInfo _mControllerOnLandOnPortal;
        private static MethodInfo _mEditorPlay;
        private static MethodInfo _mEditorSelectFloor;
        private static MethodInfo _mEditorSwitchToEditMode;
        private static MethodInfo _mEditorMultiSelectFloors;

        // Input Guard：Renderist session 活跃期间抑制玩家输入的精确目标方法。
        private static MethodInfo _mPlayerManagerAnyValidInput;
        private static MethodInfo _mPlayerValidInputTriggered;
        private static MethodInfo _mPlayerValidInputReleased;
        private static MethodInfo _mPlayerCountValidKeysPressed;
        private static MethodInfo _mEditorZoomCamera;
        private static MethodInfo _mControllerTogglePauseGame;

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
            try { _tPlanet = _tPlanet ?? _gameAssembly.GetType("scrPlanet"); } catch { }
            try { _tFloor = _tFloor ?? _gameAssembly.GetType("scrFloor"); } catch { }
            try { _tPortal = _tPortal ?? _gameAssembly.GetType("Portal"); } catch { }
            try { _tRdc = _tRdc ?? _gameAssembly.GetType("RDC"); } catch { }
            try { _tAsyncInputUtils = _tAsyncInputUtils ?? _gameAssembly.GetType("AsyncInputUtils"); } catch { }
            try { _tPlayerManager = _tPlayerManager ?? _gameAssembly.GetType("scrPlayerManager"); } catch { }

            if (_tConductor != null)
            {
                _pSongPosI = GetProperty(_tConductor, "songposition_minusi");
                _pSongPosMinusV = GetProperty(_tConductor, "songposition_minusv");
                _pAdjustedCountdownTicks = GetProperty(_tConductor, "adjustedCountdownTicks");
                _fCrotchetAtStart = _tConductor.GetField("crotchetAtStart",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fConductorBpm = _tConductor.GetField("bpm",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _mConductorUpdate = _tConductor.GetMethod("Update",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (_tController != null)
            {
                _pState = GetProperty(_tController, "state");
                _fCurrentSeq = _tController.GetField("currentSeqID",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fControllerListBpm = _tController.GetField("listBPM",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _mControllerChangeToStartState = _tController.GetMethod("ChangeToStartState",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                // Input Guard：Space 暂停入口。Esc cancellation 走的是 scnEditor.Update 中
                // 一个独立且更早的 KeyCode.Escape 分支（直接 SwitchToEditMode(false) 并 ret），
                // 不经过本方法；见 PROJECT_UNDERSTANDING.md 第 11 节。
                _mControllerTogglePauseGame = _tController.GetMethod("TogglePauseGame",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (_tPlanet != null && _tPortal != null)
                {
                    _mControllerOnLandOnPortal = _tController.GetMethod("OnLandOnPortal",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, new[] { _tPlanet, _tPortal, typeof(string) }, null);
                }
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
                // Input Guard：官方滚轮缩放入口（真实签名 ZoomCamera(float, bool, bool)）。
                _mEditorZoomCamera = _tEditor.GetMethod("ZoomCamera",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(float), typeof(bool), typeof(bool) }, null);
            }

            if (_tPlayerManager != null)
            {
                _mPlayerManagerAnyValidInput = _tPlayerManager.GetMethod("AnyValidInputWasTriggered",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
            }

            if (_tPlayer != null)
            {
                _mPlayerValidInputTriggered = _tPlayer.GetMethod("ValidInputWasTriggered",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                _mPlayerValidInputReleased = _tPlayer.GetMethod("ValidInputWasReleased",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                _mPlayerCountValidKeysPressed = _tPlayer.GetMethod("CountValidKeysPressed",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
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
                       _mEditorSelectFloor != null && _mEditorSwitchToEditMode != null &&
                       _mControllerOnLandOnPortal != null;
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

        /// <summary>
        /// 当前 ADOFAI 版本的原生关卡完成入口。Renderist 只观察该入口的
        /// Postfix，并同时等待 controller.state 提交为 Won；不调用此方法。
        /// </summary>
        public static MethodInfo ControllerOnLandOnPortalMethod
        {
            get
            {
                EnsureTypes();
                return _mControllerOnLandOnPortal;
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
        // Input Guard：session 期间的玩家输入 / 编辑器缩放 / 暂停入口
        // ================================================================

        /// <summary>
        /// Input Guard 所需的 6 个精确目标方法是否全部解析成功。
        /// 缺一即视为不可用：session 必须拒绝启动，而不是带着可被玩家干扰的
        /// gameplay 继续导出。
        /// </summary>
        public static bool InputGuardApiAvailable
        {
            get
            {
                EnsureTypes();
                return _mPlayerManagerAnyValidInput != null &&
                       _mPlayerValidInputTriggered != null &&
                       _mPlayerValidInputReleased != null &&
                       _mPlayerCountValidKeysPressed != null &&
                       _mEditorZoomCamera != null &&
                       _mControllerTogglePauseGame != null;
            }
        }

        /// <summary>不可用时列出缺失的目标，供启动拒绝原因诊断。</summary>
        public static string DescribeMissingInputGuardApi()
        {
            EnsureTypes();
            var missing = new List<string>();
            if (_mPlayerManagerAnyValidInput == null) missing.Add("scrPlayerManager.AnyValidInputWasTriggered");
            if (_mPlayerValidInputTriggered == null) missing.Add("scrPlayer.ValidInputWasTriggered");
            if (_mPlayerValidInputReleased == null) missing.Add("scrPlayer.ValidInputWasReleased");
            if (_mPlayerCountValidKeysPressed == null) missing.Add("scrPlayer.CountValidKeysPressed");
            if (_mEditorZoomCamera == null) missing.Add("scnEditor.ZoomCamera(float,bool,bool)");
            if (_mControllerTogglePauseGame == null) missing.Add("scrController.TogglePauseGame");
            return missing.Count == 0 ? null : string.Join(",", missing.ToArray());
        }

        public static MethodInfo PlayerManagerAnyValidInputWasTriggeredMethod
        {
            get { EnsureTypes(); return _mPlayerManagerAnyValidInput; }
        }

        public static MethodInfo PlayerValidInputWasTriggeredMethod
        {
            get { EnsureTypes(); return _mPlayerValidInputTriggered; }
        }

        public static MethodInfo PlayerValidInputWasReleasedMethod
        {
            get { EnsureTypes(); return _mPlayerValidInputReleased; }
        }

        public static MethodInfo PlayerCountValidKeysPressedMethod
        {
            get { EnsureTypes(); return _mPlayerCountValidKeysPressed; }
        }

        public static MethodInfo EditorZoomCameraMethod
        {
            get { EnsureTypes(); return _mEditorZoomCamera; }
        }

        public static MethodInfo ControllerTogglePauseGameMethod
        {
            get { EnsureTypes(); return _mControllerTogglePauseGame; }
        }

        /// <summary>当前 scrController.paused 原始值（供被抑制的 TogglePauseGame 返回兼容结果）。</summary>
        public static bool? ReadControllerPaused()
        {
            try
            {
                object controller = Controller();
                if (controller == null || _tController == null) return null;
                return ToBool(ReadInstanceMember(controller, _tController, "paused"));
            }
            catch { return null; }
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
        // Render Source：当前 DLL 的 scrCamera 原生摄像机链
        // ================================================================

        /// <summary>
        /// 解析当前 ADOFAI DLL 的 scrCamera 类型与三个谱面摄像机成员。
        /// 只缓存成员元数据；解析失败时保持 null，下次调用会重试。
        /// </summary>
        private static void EnsureCameraChainTypes()
        {
            if (_tScrCamera != null) return;

            EnsureTypes();
            if (_gameAssembly == null) return;

            try { _tScrCamera = _gameAssembly.GetType("scrCamera"); } catch { _tScrCamera = null; }
            if (_tScrCamera == null) return;

            _mCameraBgStatic = FindInstanceMember(_tScrCamera, "Bgcamstatic");
            _mCameraBg = FindInstanceMember(_tScrCamera, "BGcam");
            _mCameraMain = FindInstanceMember(_tScrCamera, "camobj");
        }

        /// <summary>
        /// 读取当前 session 的 ADOFAI 原生谱面摄像机链。
        ///
        /// 已在当前本机 DLL（Assembly-CSharp FileVersion 0.4.3.0）静态确认：
        ///   scrCamera : ADOBase（非 sealed）
        ///   static scrCamera instance { get; set; }
        ///   public UnityEngine.Camera Bgcamstatic / BGcam / camobj
        ///
        /// 每次调用都重新读取 scrCamera.instance 及其成员，因此每个新的 capture
        /// ownership 都取得当前 session 的对象，不跨 session 缓存场景引用。
        /// 本方法只读取；不修改 Camera 的任何状态。
        /// 任一环节失败返回 false 与 machine-readable error。
        /// </summary>
        public static bool TryReadChartCameraChain(
            out UnityEngine.Camera bgStaticCamera,
            out UnityEngine.Camera bgCamera,
            out UnityEngine.Camera mainCamera,
            out string error)
        {
            bgStaticCamera = null;
            bgCamera = null;
            mainCamera = null;
            error = null;

            EnsureCameraChainTypes();

            if (_tScrCamera == null)
            {
                error = "scr-camera-type-unavailable";
                return false;
            }

            object instance = StaticValue(_tScrCamera, "instance");
            if (instance == null)
            {
                error = "scr-camera-instance-unavailable";
                return false;
            }

            UnityEngine.Camera chainBgStatic;
            UnityEngine.Camera chainBg;
            UnityEngine.Camera chainMain;

            if (!TryReadCameraMember(instance, _mCameraBgStatic, "Bgcamstatic",
                    out chainBgStatic, out error)) return false;
            if (!TryReadCameraMember(instance, _mCameraBg, "BGcam",
                    out chainBg, out error)) return false;
            if (!TryReadCameraMember(instance, _mCameraMain, "camobj",
                    out chainMain, out error)) return false;

            bgStaticCamera = chainBgStatic;
            bgCamera = chainBg;
            mainCamera = chainMain;
            return true;
        }

        private static bool TryReadCameraMember(object instance, MemberInfo member, string memberName,
            out UnityEngine.Camera camera, out string error)
        {
            camera = null;
            error = null;

            if (member == null)
            {
                error = "scr-camera-member-missing:" + memberName;
                return false;
            }

            object raw = ReadMemberValue(instance, member);
            if (raw == null)
            {
                error = "scr-camera-member-unavailable:" + memberName;
                return false;
            }

            if (!(raw is UnityEngine.Camera typed))
            {
                error = "scr-camera-member-not-a-camera:" + memberName;
                return false;
            }

            // Unity 的 == 重载在这里区分“仍然是 Camera 但对象已销毁”。
            if (typed == null)
            {
                error = "scr-camera-member-destroyed:" + memberName;
                return false;
            }

            camera = typed;
            return true;
        }

        private static MemberInfo FindInstanceMember(Type type, string name)
        {
            if (type == null) return null;
            try
            {
                PropertyInfo property = type.GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property != null && property.GetGetMethod(true) != null) return property;
                return type.GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch { return null; }
        }

        private static object ReadMemberValue(object instance, MemberInfo member)
        {
            if (instance == null || member == null) return null;
            try
            {
                if (member is PropertyInfo property) return property.GetValue(instance, null);
                if (member is FieldInfo field) return field.GetValue(instance);
                return null;
            }
            catch { return null; }
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

        /// <summary>
        /// 读取 scnEditor.playMode。
        ///
        /// 重要：当前 DLL 的 playMode 是**只读派生属性**，不是「是否处于播放模式」的
        /// flag。其 IL 语义等价于 `pausedInPlayMode ? true : !controller.paused`。
        /// 因此它只反映暂停状态；是否存在编辑器 playback 必须靠 exact
        /// SwitchToEditMode observer（见项目理解文档第 11/12 节），不能只靠本属性。
        /// </summary>
        public static bool? ReadEditorPlayMode()
        {
            object editor = Editor();
            if (editor == null || _tEditor == null) return null;
            try { return ToBool(ReadInstanceMember(editor, _tEditor, "playMode")); }
            catch { return null; }
        }

        /// <summary>
        /// 只读诊断：scnEditor.inStrictlyEditingMode（当前 DLL 中是 private bool 字段）。
        ///
        /// 当前 DLL 中它只被写入、从不被游戏自身读取：`scnEditor.SwitchToEditMode(bool)`
        /// 总是写入 true，`scnGame.Play` 也会写入。编辑器场景只走 SwitchToEditMode，
        /// 因此编辑器内该值恒为 true（播放与编辑皆然），不能用作
        /// 「playback 已停止」的判据。
        /// </summary>
        public static bool? ReadEditorInStrictlyEditingMode()
        {
            object editor = Editor();
            if (editor == null || _tEditor == null) return null;
            try { return ToBool(ReadInstanceMember(editor, _tEditor, "inStrictlyEditingMode")); }
            catch { return null; }
        }

        /// <summary>只读诊断：scrConductor 所在 GameObject 是否 activeInHierarchy。</summary>
        public static bool? ReadConductorActiveInHierarchy()
        {
            try
            {
                object conductor = Conductor();
                if (conductor is UnityEngine.Component component && component != null)
                {
                    return component.gameObject != null && component.gameObject.activeInHierarchy;
                }
                return null;
            }
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

        /// <summary>
        /// 读取编辑器当前谱面的最终 effective BPM。当前 ADOFAI DLL 的
        /// CalculateFloorEntryTimes / Start_Rewind 都使用
        /// scrConductor.bpm × scrFloor.speed 表示该 floor 的 effective BPM。
        /// </summary>
        public static bool TryReadFinalEffectiveBpm(out double bpm, out string rejectReason)
        {
            bpm = 0.0;
            rejectReason = null;
            try
            {
                EnsureTypes();
                object conductor = Conductor();
                if (conductor == null || _fConductorBpm == null)
                {
                    rejectReason = "conductor-bpm-api-unavailable";
                    return false;
                }

                double? baseBpm = ToDouble(_fConductorBpm.GetValue(conductor));
                if (!IsPositiveFinite(baseBpm))
                {
                    rejectReason = "conductor-bpm-invalid";
                    return false;
                }

                IList floors = ReadFloorsList();
                if (floors == null || floors.Count == 0)
                {
                    rejectReason = "floors-empty";
                    return false;
                }

                for (int i = floors.Count - 1; i >= 0; i--)
                {
                    object floor = floors[i];
                    if (floor == null) continue;
                    double? speed = ToDouble(ReadInstanceMember(floor, floor.GetType(), "speed"));
                    if (!IsPositiveFinite(speed)) continue;

                    double effective = baseBpm.Value * speed.Value;
                    if (!IsPositiveFinite(effective))
                    {
                        rejectReason = "final-effective-bpm-invalid";
                        return false;
                    }

                    bpm = effective;
                    return true;
                }

                rejectReason = "final-floor-speed-unavailable";
                return false;
            }
            catch
            {
                rejectReason = "final-effective-bpm-read-failed";
                return false;
            }
        }

        /// <summary>
        /// completion 时优先读取 ADOFAI Start_Rewind 已生成的 listBPM 最后一项。
        /// 其 Item2 是 base BPM × floor speed；不可用时回退到同一已验证公式。
        /// </summary>
        public static bool TryReadCompletionEffectiveBpm(out double bpm, out string rejectReason)
        {
            bpm = 0.0;
            rejectReason = null;
            try
            {
                EnsureTypes();
                object controller = Controller();
                if (controller != null && _fControllerListBpm != null &&
                    _fControllerListBpm.GetValue(controller) is IList bpms)
                {
                    for (int i = bpms.Count - 1; i >= 0; i--)
                    {
                        object pair = bpms[i];
                        if (pair == null) continue;
                        double? effective = ToDouble(ReadInstanceMember(pair, pair.GetType(), "Item2"));
                        if (!IsPositiveFinite(effective)) continue;
                        bpm = effective.Value;
                        return true;
                    }
                }
            }
            catch
            {
                // Fall through to the chart-derived value below.
            }

            return TryReadFinalEffectiveBpm(out bpm, out rejectReason);
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

        public static bool IsWonState(object state)
        {
            return string.Equals(ToState(state), "Won", StringComparison.Ordinal);
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

        private static bool IsPositiveFinite(double? value)
        {
            return value.HasValue && !double.IsNaN(value.Value) &&
                   !double.IsInfinity(value.Value) && value.Value > 0.0001;
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
