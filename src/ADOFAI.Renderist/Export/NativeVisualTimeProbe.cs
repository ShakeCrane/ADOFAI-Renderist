using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using ADOFAI.Renderist.Logging;
using UnityEngine;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// ============================ TEMP DIAGNOSTIC (not ship-quality) ============================
    /// 原生视觉时间**只读**探针（Camera + Planet ParticleSystem），用于一次实机取证：
    /// 在 MasterTimeline 逻辑帧冻结 / FFmpeg 背压等待期间，原生 Camera 平滑跟随与行星拖尾
    /// 是否仍在按 Unity Update 推进。
    ///
    /// 实例发现（依据当前游戏 `Assembly-CSharp.dll` 0.4.3.0 的 IL 核对，不是猜测）：
    ///   * `scrController.chosenPlanet` 是**只读属性**（`get_chosenPlanet`），不是字段；
    ///     上一轮探针按字段名去取它，必然得到 null —— 那不是 "ParticleSystem 不存在"。
    ///   * 真实路径：`scrController.planetarySystem`（属性）→ `PlanetarySystem`；
    ///     其 `allPlanets` / `planetList` / `availablePlanets` 都是 `List&lt;scrPlanet&gt;`
    ///     （public 字段；`allPlanets` 只在 `Init()` 里被写过一次，因此它是全部 8 个行星的池子）。
    ///   * `scrPlanet.planetRenderer`（public 字段）→ `PlanetRenderer`；
    ///     其 `tailParticles` / `tailParticlesCoop` / `coreParticles` / `sparks` /
    ///     `deathExplosion` 都是 public `UnityEngine.ParticleSystem` 字段。
    ///   * `PlanetRenderer.Awake()` 在 coopMode 下把原拖尾 GameObject 改名为 `…old` 并停用，
    ///     然后把 `tailParticles` **重新指向** `tailParticlesCoop`；因此
    ///     "字段里没有拖尾" 不能证明 "场景里没有 ParticleSystem" —— 本探针额外做一次
    ///     有界的层级扫描来判定这种孤儿实例是否真的存在。
    ///
    /// 硬边界（由自动化契约测试固定）：
    ///   * **只读**：不写 `Time.timeScale` / `Time.captureFramerate` / `Application.targetFrameRate`，
    ///     不写 Camera Transform，不调用任何 Tween API，不调用任何 ParticleSystem 变更 API。
    ///     `GetParticles` 是只读复制，明确允许。
    ///   * 不参与任何导出判定：不推进/回退帧号，不影响 `MasterTimeline`、帧提交、
    ///     L3-A/L3-B 帧事务、L3-C Finalizing / 核验 / 发布。
    ///   * 绝不把异常抛回 scheduler：内部异常只记一次并自行禁用。
    ///   * 类型元数据可缓存；**运行时实例缺失绝不永久缓存为解析失败** —— 每次采样都重新
    ///     发现实例，只把原因写进当次记录。
    ///   * 有界日志：每逻辑帧最多数个采样点 + 全局记录预算 + 每个采样点内固定的
    ///     行星 / ParticleSystem 描述上限，绝不逐 Unity Update 全程刷日志。
    ///
    /// 清理：本文件与全部接线、csproj 临时引用在实机取证后整体删除/回滚。
    /// ============================================================================================
    /// </summary>
    internal static class NativeVisualTimeProbe
    {
        /// <summary>总开关。临时诊断构建为 true；清理时整文件删除。</summary>
        internal const bool Enabled = true;

        /// <summary>全局记录预算：达到后只再输出一行并停止采样（防止日志失控）。</summary>
        internal const int MaxRecords = 2500;

        /// <summary>等待交付窗口的节流参数（每窗口前 N tick + 之后每 M tick，且只覆盖有限窗口）。</summary>
        internal const int DeliveryWindowHeadTicks = 3;
        internal const int DeliveryWindowStrideTicks = 30;
        internal const int DeliveryWindowLimit = 6;

        /// <summary>hidden boundary phase 的节流参数（该阶段每个 Unity frame 都会被调用）。</summary>
        internal const int HiddenFrameHeadTicks = 3;
        internal const int HiddenFrameStrideTicks = 15;
        internal const int HiddenFrameLimit = 60;

        private const string Prefix = "NVT|";
        private const int ParticleSampleCount = 4;
        private const int MaxPlanets = 12;
        private const int MaxSystems = 6;
        private const int MaxFullSystems = 2;
        private const int MaxHierarchyEntries = 8;

        // 行星集合归属位掩码（同一实例可能同时出现在多个集合里，去重后按位记录来源）。
        private const int SrcAllPlanets = 1;
        private const int SrcPlanetList = 2;
        private const int SrcAvailablePlanets = 4;
        private const int SrcChosenPlanet = 8;
        private const int SrcNamedField = 16;

        // 只读粒子年龄缓冲：整 session 复用，样本内零托管分配（除最终字符串）。
        private static readonly ParticleSystem.Particle[] _particleSample =
            new ParticleSystem.Particle[ParticleSampleCount];

        private static readonly StringBuilder _record = new StringBuilder(4096);
        private static readonly StringBuilder _cameraText = new StringBuilder(2048);
        private static readonly StringBuilder _planetText = new StringBuilder(3072);
        private static readonly StringBuilder _systemText = new StringBuilder(1024);
        private static readonly StringBuilder _ageText = new StringBuilder(512);
        private static readonly StringBuilder _hierText = new StringBuilder(1024);
        private static readonly StringBuilder _scratch = new StringBuilder(256);

        private static bool _disabled;
        private static long _records;
        private static bool _budgetLogged;

        // ---- 静态释放句柄（类型元数据；按成员逐项解析，缺一项不影响其余项） ----
        private static bool _typesResolved;
        private static string _typesReason;
        private static string _typeNames;

        private static PropertyInfo _pControllerPlanetarySystem;
        private static PropertyInfo _pControllerChosenPlanet;
        private static PropertyInfo _pControllerPlayerOne;

        private static FieldInfo _fPlayerPlanetarySystem;
        private static FieldInfo _fPlanetPlanetarySystem;
        private static FieldInfo _fPlanetPlanetRenderer;
        private static FieldInfo _fPlanetAngle;
        private static FieldInfo _fPlanetCosmeticAngle;

        private static FieldInfo _fPsAllPlanets;
        private static FieldInfo _fPsPlanetList;
        private static FieldInfo _fPsAvailablePlanets;
        private static FieldInfo _fPsChosenPlanet;
        private static FieldInfo _fPsSpeed;
        private static readonly FieldInfo[] _fPsNamedPlanets = new FieldInfo[8];

        private static readonly string[] _namedPlanetFields =
        {
            "planetRed", "planetBlue", "planetGreen", "planetYellow",
            "planetPurple", "planetPink", "planetOrange", "planetCyan",
        };

        private static FieldInfo _fRendererTailParticles;
        private static FieldInfo _fRendererTailParticlesCoop;
        private static FieldInfo _fRendererCoreParticles;
        private static FieldInfo _fRendererSparks;
        private static FieldInfo _fRendererDeathExplosion;
        private static FieldInfo _fRendererOnlyRing;
        private static FieldInfo _fRendererScrSamurai;

        private static PropertyInfo _pCameraInstance;
        private static PropertyInfo _pCameraPositionState;
        private static bool _cameraFieldsResolved;
        private static readonly Dictionary<string, FieldInfo> _cameraFields =
            new Dictionary<string, FieldInfo>(StringComparer.Ordinal);

        // ---- 运行时发现缓冲（每次采样重填；实例缺失不缓存） ----
        private static readonly object[] _planets = new object[MaxPlanets];
        private static readonly int[] _planetIds = new int[MaxPlanets];
        private static readonly int[] _planetSources = new int[MaxPlanets];
        private static readonly bool[] _planetActive = new bool[MaxPlanets];
        private static int _planetCount;
        private static string _planetRoute;
        private static string _planetReason;
        private static object _chosenPlanet;
        private static string _collectionCounts;
        private static string _collectionTypes;
        private static string _propertyFailure;
        private static double _planetarySpeed;

        private static readonly ParticleSystem[] _systems = new ParticleSystem[MaxSystems];
        private static readonly string[] _systemRoles = new string[MaxSystems];
        private static readonly int[] _systemPlanet = new int[MaxSystems];
        private static readonly bool[] _systemReferenced = new bool[MaxSystems];
        private static int _systemCount;

        private static readonly int[] _hierIds = new int[MaxHierarchyEntries];
        private static readonly bool[] _hierActive = new bool[MaxHierarchyEntries];
        private static readonly string[] _hierNames = new string[MaxHierarchyEntries];
        private static readonly int[] _hierCounts = new int[MaxHierarchyEntries];
        private static int _hierCount;

        /// <summary>层级扫描按行星实例缓存 —— 只缓存**成功**结果，失败不缓存。</summary>
        private static readonly HashSet<int> _hierarchyScanned = new HashSet<int>();

        // ---- 等待交付窗口节流状态 ----
        private static long _deliveryWindowKey = long.MinValue;
        private static int _deliveryWindowIndex;
        private static int _deliveryTickInWindow;

        // ---- hidden boundary phase 节流状态 ----
        private static int _hiddenFrameKey = int.MinValue;
        private static int _hiddenIndex;
        private static int _hiddenTick;

        // ==================================================================== entry points

        /// <summary>统一采样入口。所有输入都是**值**：探针不持有 scheduler 状态、不回写任何东西。</summary>
        internal static void Sample(
            string phase, long generation, long outputFrameIndex, string schedulerStatus,
            int hitsThisFrame, string extra)
        {
            if (!Enabled || _disabled) return;
            if (_records >= MaxRecords)
            {
                if (!_budgetLogged)
                {
                    _budgetLogged = true;
                    Log.Warn(Prefix + "ph=budget-exhausted|records=" +
                             _records.ToString(CultureInfo.InvariantCulture) + "|sampling-disabled");
                }
                return;
            }

            try
            {
                _records++;
                Log.Info(BuildRecord(phase, generation, outputFrameIndex, schedulerStatus,
                    hitsThisFrame, extra, IsFullDetailPhase(phase)));
            }
            catch (Exception ex)
            {
                Disable(ex);
            }
        }

        /// <summary>
        /// 等待交付窗口的节流采样：只在该窗口前若干个 Tick 与之后每 N 个 Tick 记录一次，
        /// 且只覆盖有限个窗口。绝不逐 Unity Update 全程刷日志。
        /// </summary>
        internal static void SampleAwaitingDelivery(
            long generation, long outputFrameIndex, string schedulerStatus)
        {
            if (!Enabled || _disabled) return;

            long key = (generation << 32) ^ outputFrameIndex;
            if (key != _deliveryWindowKey)
            {
                _deliveryWindowKey = key;
                _deliveryTickInWindow = 0;
                _deliveryWindowIndex++;

                if (_deliveryWindowIndex > DeliveryWindowLimit)
                {
                    // 超出窗口上限：不再采样，但每进入一个新窗口只记录一行边界证据。
                    if (_deliveryWindowIndex != DeliveryWindowLimit + 1) return;
                    Sample("await-delivery-limit", generation, outputFrameIndex, schedulerStatus, -1,
                        "window=" + _deliveryWindowIndex.ToString(CultureInfo.InvariantCulture));
                    return;
                }
            }

            int tick = _deliveryTickInWindow++;
            bool take = tick < DeliveryWindowHeadTicks ||
                        (DeliveryWindowStrideTicks > 0 && tick % DeliveryWindowStrideTicks == 0);
            if (!take) return;

            Sample("await-delivery", generation, outputFrameIndex, schedulerStatus, -1,
                "tick=" + tick.ToString(CultureInfo.InvariantCulture) +
                "|window=" + _deliveryWindowIndex.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// hidden lifecycle-only boundary phase 的节流采样：该阶段每个 Unity frame 都会被
        /// 调用，因此必须节流；它记录 `timeScale = 0` 期间 Camera 与粒子**是否仍在老化**。
        /// </summary>
        internal static void SampleHiddenPhase(
            long generation, long outputFrameIndex, string schedulerStatus)
        {
            if (!Enabled || _disabled) return;

            int unityFrame = Time.frameCount;
            if (unityFrame != _hiddenFrameKey)
            {
                _hiddenFrameKey = unityFrame;
                _hiddenTick = 0;
                _hiddenIndex++;

                if (_hiddenIndex > HiddenFrameLimit)
                {
                    if (_hiddenIndex != HiddenFrameLimit + 1) return;
                    Sample("hidden-frame-limit", generation, outputFrameIndex, schedulerStatus, -1,
                        "frames=" + _hiddenIndex.ToString(CultureInfo.InvariantCulture));
                    return;
                }
            }

            int tick = _hiddenTick++;
            bool take = tick < HiddenFrameHeadTicks ||
                        (HiddenFrameStrideTicks > 0 && tick % HiddenFrameStrideTicks == 0);
            if (!take) return;

            Sample("hidden-frame", generation, outputFrameIndex, schedulerStatus, -1,
                "tick=" + tick.ToString(CultureInfo.InvariantCulture) +
                "|hf=" + _hiddenIndex.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// session 开始时一次性记录：类型元数据解析结果、实例身份、粒子**静态配置**快照
        /// （配置不随时间变化，因此整个 session 只记一次；皮肤/形态差异也在这里体现）。
        /// </summary>
        internal static void SampleSessionStart(long generation, long outputFrameIndex, string schedulerStatus)
        {
            if (!Enabled || _disabled) return;

            try
            {
                ResolveTypes();
                _records++;
                Log.Info(BuildRecord("session-start", generation, outputFrameIndex, schedulerStatus, -1,
                    "types=" + (_typesResolved ? "ok" : "pending") +
                    "|typesReason=" + (_typesReason ?? "-") +
                    "|typeNames=" + (_typeNames ?? "?") +
                    "|missing=" + MissingMembers() +
                    "|cameraFields=" + (_cameraFieldsResolved
                        ? _cameraFields.Count.ToString(CultureInfo.InvariantCulture)
                        : "pending"), true));
            }
            catch (Exception ex)
            {
                Disable(ex);
            }
        }

        private static void Disable(Exception ex)
        {
            _disabled = true;
            Log.Warn(Prefix + "ph=probe-disabled|reason=" + ex.GetType().Name + ": " + ex.Message);
        }

        /// <summary>
        /// 记录量级：只有 lifecycle / 帧事务锚点才输出完整配置（层级扫描 + 前若干个系统的
        /// 完整模块配置）；高频等待窗口只输出时间与年龄本身。
        /// </summary>
        private static bool IsFullDetailPhase(string phase)
        {
            switch (phase)
            {
                case "session-start":
                case "prepare-preentry":
                case "prepare":
                case "post-conductor":
                case "pre-hit":
                case "preentry-freeze":
                case "preentry-restore":
                case "g-plus-one":
                case "commit":
                case "eof":
                    return true;
                default:
                    return false;
            }
        }

        // ==================================================================== reflection (type metadata)

        /// <summary>
        /// 只解析并缓存**类型与成员元数据**。可重入且幂等：任何一步失败只记录原因，
        /// 绝不让"运行时实例当时不存在"变成永久的解析失败。
        /// </summary>
        private static void ResolveTypes()
        {
            if (_typesResolved) return;

            object controller = EditorGameReflection.Controller();
            Type controllerType = controller?.GetType();
            Assembly game = controllerType?.Assembly;
            if (game == null)
            {
                _typesReason = "game-assembly-unresolved";
                return;
            }

            if (_pControllerPlanetarySystem == null)
                _pControllerPlanetarySystem = controllerType.GetProperty("planetarySystem",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (_pControllerChosenPlanet == null)
                _pControllerChosenPlanet = controllerType.GetProperty("chosenPlanet",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (_pControllerPlayerOne == null)
                _pControllerPlayerOne = controllerType.GetProperty("playerOne",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Type playerType = game.GetType("scrPlayer", false);
            if (playerType != null && _fPlayerPlanetarySystem == null)
                _fPlayerPlanetarySystem = Field(playerType, "planetarySystem");

            Type planetType = game.GetType("scrPlanet", false);
            if (planetType != null)
            {
                if (_fPlanetPlanetarySystem == null)
                    _fPlanetPlanetarySystem = Field(planetType, "planetarySystem");
                if (_fPlanetPlanetRenderer == null)
                    _fPlanetPlanetRenderer = Field(planetType, "planetRenderer");
                if (_fPlanetAngle == null) _fPlanetAngle = Field(planetType, "angle");
                if (_fPlanetCosmeticAngle == null) _fPlanetCosmeticAngle = Field(planetType, "cosmeticAngle");
            }

            Type psType = game.GetType("PlanetarySystem", false);
            if (psType != null)
            {
                if (_fPsAllPlanets == null) _fPsAllPlanets = Field(psType, "allPlanets");
                if (_fPsPlanetList == null) _fPsPlanetList = Field(psType, "planetList");
                if (_fPsAvailablePlanets == null) _fPsAvailablePlanets = Field(psType, "availablePlanets");
                if (_fPsChosenPlanet == null) _fPsChosenPlanet = Field(psType, "chosenPlanet");
                if (_fPsSpeed == null) _fPsSpeed = Field(psType, "speed");
                for (int i = 0; i < _namedPlanetFields.Length; i++)
                {
                    if (_fPsNamedPlanets[i] == null)
                        _fPsNamedPlanets[i] = Field(psType, _namedPlanetFields[i]);
                }
            }

            Type rendererType = game.GetType("PlanetRenderer", false);
            if (rendererType != null)
            {
                if (_fRendererTailParticles == null) _fRendererTailParticles = Field(rendererType, "tailParticles");
                if (_fRendererTailParticlesCoop == null) _fRendererTailParticlesCoop = Field(rendererType, "tailParticlesCoop");
                if (_fRendererCoreParticles == null) _fRendererCoreParticles = Field(rendererType, "coreParticles");
                if (_fRendererSparks == null) _fRendererSparks = Field(rendererType, "sparks");
                if (_fRendererDeathExplosion == null) _fRendererDeathExplosion = Field(rendererType, "deathExplosion");
                if (_fRendererOnlyRing == null) _fRendererOnlyRing = Field(rendererType, "onlyRing");
                if (_fRendererScrSamurai == null) _fRendererScrSamurai = Field(rendererType, "scrSamurai");
            }

            Type cameraType = game.GetType("scrCamera", false);
            if (cameraType != null)
            {
                if (_pCameraInstance == null)
                    _pCameraInstance = cameraType.GetProperty("instance",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (_pCameraPositionState == null)
                    _pCameraPositionState = cameraType.GetProperty("positionState",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                ResolveCameraFields(cameraType);
            }

            _typeNames = "ctrl=" + (controllerType?.Name ?? "?") +
                         ",player=" + (playerType?.Name ?? "?") +
                         ",planet=" + (planetType?.Name ?? "?") +
                         ",ps=" + (psType?.Name ?? "?") +
                         ",renderer=" + (rendererType?.Name ?? "?") +
                         ",camera=" + (cameraType?.Name ?? "?");

            _typesResolved = _pControllerPlanetarySystem != null && _fPlanetPlanetRenderer != null &&
                             _fRendererTailParticles != null;
            _typesReason = _typesResolved ? null
                : planetType == null ? "scrPlanet-missing"
                : psType == null ? "PlanetarySystem-missing"
                : rendererType == null ? "PlanetRenderer-missing"
                : cameraType == null ? "scrCamera-missing"
                : "member-missing";
        }

        private static void ResolveCameraFields(Type cameraType)
        {
            if (_cameraFieldsResolved) return;
            foreach (FieldInfo field in cameraType.GetFields(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                _cameraFields[field.Name] = field;
            }
            _cameraFieldsResolved = _cameraFields.Count > 0;
        }

        private static FieldInfo Field(Type type, string name)
        {
            return type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        /// <summary>逐项列出缺失的成员句柄，绝不把"缺失"折叠成一个笼统的 None。</summary>
        private static string MissingMembers()
        {
            _scratch.Length = 0;
            AppendMissing(_scratch, "ctrl.planetarySystem", _pControllerPlanetarySystem);
            AppendMissing(_scratch, "ctrl.chosenPlanet", _pControllerChosenPlanet);
            AppendMissing(_scratch, "ctrl.playerOne", _pControllerPlayerOne);
            AppendMissing(_scratch, "scrPlayer.planetarySystem", _fPlayerPlanetarySystem);
            AppendMissing(_scratch, "scrPlanet.planetarySystem", _fPlanetPlanetarySystem);
            AppendMissing(_scratch, "scrPlanet.planetRenderer", _fPlanetPlanetRenderer);
            AppendMissing(_scratch, "PlanetarySystem.allPlanets", _fPsAllPlanets);
            AppendMissing(_scratch, "PlanetarySystem.planetList", _fPsPlanetList);
            AppendMissing(_scratch, "PlanetarySystem.availablePlanets", _fPsAvailablePlanets);
            AppendMissing(_scratch, "PlanetarySystem.chosenPlanet", _fPsChosenPlanet);
            AppendMissing(_scratch, "PlanetRenderer.tailParticles", _fRendererTailParticles);
            AppendMissing(_scratch, "PlanetRenderer.tailParticlesCoop", _fRendererTailParticlesCoop);
            AppendMissing(_scratch, "PlanetRenderer.coreParticles", _fRendererCoreParticles);
            AppendMissing(_scratch, "PlanetRenderer.sparks", _fRendererSparks);
            AppendMissing(_scratch, "scrCamera.instance", _pCameraInstance);
            AppendMissing(_scratch, "scrCamera.positionState", _pCameraPositionState);
            return _scratch.Length == 0 ? "none" : _scratch.ToString();
        }

        private static void AppendMissing(StringBuilder sb, string label, MemberInfo member)
        {
            if (member != null) return;
            if (sb.Length > 0) sb.Append(';');
            sb.Append(label);
        }

        /// <summary>
        /// 只读属性读取必须自己吞掉异常：`scrController.get_playerOne()` 的 IL 是
        /// `playerManager.players[0]`（`ldelem.ref`），players 尚未建立时索引会抛异常。
        /// 探针绝不能因为一次"现在还没准备好"就永久自禁用 —— 只记下原因，下一次采样再试。
        /// </summary>
        private static object TryProperty(PropertyInfo property, object target)
        {
            if (property == null || target == null) return null;
            try
            {
                return property.GetValue(target);
            }
            catch (Exception ex)
            {
                if (_propertyFailure == null)
                    _propertyFailure = property.Name + ":" + ex.GetType().Name;
                return null;
            }
        }

        // ==================================================================== runtime instance discovery

        /// <summary>
        /// 每次采样都重新定位运行时实例。失败原因写进当次记录，**不**形成永久缓存。
        /// </summary>
        private static void DiscoverInstances()
        {
            _planetCount = 0;
            _systemCount = 0;
            // 注意：`_hierCount` 与 `_hier*` 是**跨采样持久**的层级扫描结果缓存
            // （按行星实例 ID 只成功扫描一次），因此不在这里清空；否则第二次之后的
            // 记录会把"已缓存、本次没重扫"误报成 `hier=empty`。
            _planetRoute = null;
            _planetReason = null;
            _chosenPlanet = null;
            _collectionCounts = null;
            _collectionTypes = null;
            _planetarySpeed = 0d;
            _propertyFailure = null;

            object controller = EditorGameReflection.Controller();
            if (controller == null)
            {
                _planetReason = "controller-null";
                return;
            }

            _chosenPlanet = TryProperty(_pControllerChosenPlanet, controller);

            object planetary = TryProperty(_pControllerPlanetarySystem, controller);
            string route = "controller.planetarySystem";
            if (planetary == null)
            {
                object playerOne = TryProperty(_pControllerPlayerOne, controller);
                planetary = playerOne == null ? null : _fPlayerPlanetarySystem?.GetValue(playerOne);
                route = "controller.playerOne.planetarySystem";
            }
            if (planetary == null && _chosenPlanet != null)
            {
                planetary = _fPlanetPlanetarySystem?.GetValue(_chosenPlanet);
                route = "controller.chosenPlanet.planetarySystem";
            }

            if (planetary == null)
            {
                _planetReason = "planetarySystem-null:routes=[controller.planetarySystem," +
                                "controller.playerOne.planetarySystem," +
                                "controller.chosenPlanet.planetarySystem]" +
                                (_propertyFailure == null ? "" : ":propertyFailure=" + _propertyFailure);
                if (_chosenPlanet != null)
                {
                    // 行星系统拿不到，但选中行星本身可用：仍然尝试它的渲染器与粒子实例。
                    CollectPlanet(_chosenPlanet, SrcChosenPlanet);
                    for (int i = 0; i < _planetCount; i++) CollectSystemsFromPlanet(i);
                }
                return;
            }

            _planetRoute = route;
            _planetarySpeed = ReadNumber(_fPsSpeed, planetary);

            IList all = _fPsAllPlanets?.GetValue(planetary) as IList;
            IList list = _fPsPlanetList?.GetValue(planetary) as IList;
            IList avail = _fPsAvailablePlanets?.GetValue(planetary) as IList;
            object systemChosen = _fPsChosenPlanet?.GetValue(planetary);

            _collectionCounts = "all=" + Count(all) + "/list=" + Count(list) +
                                "/avail=" + Count(avail) +
                                "/named=" + NamedPlanetCount(planetary) +
                                "/chosen=" + (systemChosen == null ? "0" : "1");
            _collectionTypes = "all=" + ListTypeName(all) + "/list=" + ListTypeName(list) +
                               "/avail=" + ListTypeName(avail);

            CollectList(all, SrcAllPlanets);
            CollectList(list, SrcPlanetList);
            CollectList(avail, SrcAvailablePlanets);
            CollectPlanet(systemChosen, SrcChosenPlanet);
            for (int i = 0; i < _fPsNamedPlanets.Length; i++)
                CollectPlanet(_fPsNamedPlanets[i]?.GetValue(planetary), SrcNamedField);

            if (_planetCount == 0)
            {
                _planetReason = "no-planet-instances:collections=" + _collectionCounts;
                return;
            }

            for (int i = 0; i < _planetCount; i++) CollectSystemsFromPlanet(i);

            if (_systemCount == 0)
            {
                _planetReason = "no-particlesystem:planets=" +
                                _planetCount.ToString(CultureInfo.InvariantCulture) +
                                "/renderers=" + CountRenderers();
            }
        }

        private static void CollectList(IList list, int source)
        {
            if (list == null) return;
            int n;
            try { n = list.Count; }
            catch (Exception) { return; }

            for (int i = 0; i < n && i < MaxPlanets * 2; i++)
            {
                object item;
                try { item = list[i]; }
                catch (Exception) { break; }
                CollectPlanet(item, source);
            }
        }

        private static void CollectPlanet(object planet, int source)
        {
            if (planet == null) return;
            if (!(planet is UnityEngine.Object unity) || unity == null) return;

            int id = unity.GetInstanceID();
            for (int i = 0; i < _planetCount; i++)
            {
                if (_planetIds[i] == id)
                {
                    _planetSources[i] |= source;
                    return;
                }
            }
            if (_planetCount >= MaxPlanets) return;

            int slot = _planetCount++;
            _planets[slot] = planet;
            _planetIds[slot] = id;
            _planetSources[slot] = source;
            _planetActive[slot] = IsActive(unity);
        }

        private static void CollectSystemsFromPlanet(int slot)
        {
            object renderer = _fPlanetPlanetRenderer?.GetValue(_planets[slot]);
            if (renderer == null) return;

            int planetId = _planetIds[slot];
            AddSystem(_fRendererTailParticles?.GetValue(renderer) as ParticleSystem, "tail", planetId, true);
            AddSystem(_fRendererTailParticlesCoop?.GetValue(renderer) as ParticleSystem, "coop", planetId, true);
            AddSystem(_fRendererCoreParticles?.GetValue(renderer) as ParticleSystem, "core", planetId, true);
            AddSystem(_fRendererSparks?.GetValue(renderer) as ParticleSystem, "spark", planetId, true);
            AddSystem(_fRendererDeathExplosion?.GetValue(renderer) as ParticleSystem, "death", planetId, true);
        }

        /// <summary>按 Unity 实例身份去重：同一 ParticleSystem 被多个字段 / 多个行星引用时只保留一份。</summary>
        private static void AddSystem(ParticleSystem ps, string role, int planetId, bool referenced)
        {
            if (ps == null || _systemCount >= MaxSystems) return;

            int id = ps.GetInstanceID();
            for (int i = 0; i < _systemCount; i++)
            {
                if (_systems[i] != null && _systems[i].GetInstanceID() == id)
                {
                    if (_systemRoles[i].IndexOf(role, StringComparison.Ordinal) < 0)
                        _systemRoles[i] = _systemRoles[i] + "+" + role;
                    return;
                }
            }

            int slot = _systemCount++;
            _systems[slot] = ps;
            _systemRoles[slot] = role;
            _systemPlanet[slot] = planetId;
            _systemReferenced[slot] = referenced;
        }

        /// <summary>
        /// 有界层级扫描：判定"字段里没有拖尾"是否等价于"场景里没有 ParticleSystem"。
        /// 典型反例是 `PlanetRenderer.Awake()` 在 coopMode 下改名并停用的 `…old` 拖尾。
        /// 每个行星实例**只成功扫描一次**，失败不缓存。
        /// </summary>
        private static void ScanHierarchyFor(int slot)
        {
            if (_hierCount >= MaxHierarchyEntries) return;

            int planetId = _planetIds[slot];
            if (planetId == 0 || _hierarchyScanned.Contains(planetId)) return;
            if (!(_planets[slot] is Component component) || component == null) return;

            ParticleSystem[] found;
            try
            {
                found = component.GetComponentsInChildren<ParticleSystem>(true);
            }
            catch (Exception)
            {
                return;
            }
            if (found == null || found.Length == 0) return;

            _hierarchyScanned.Add(planetId);
            for (int i = 0; i < found.Length && _hierCount < MaxHierarchyEntries; i++)
            {
                ParticleSystem ps = found[i];
                if (ps == null) continue;
                int index = _hierCount++;
                _hierIds[index] = ps.GetInstanceID();
                _hierActive[index] = ps.gameObject.activeInHierarchy;
                _hierNames[index] = SafeName(ps);
                _hierCounts[index] = SafeCount(ps);
            }
        }

        private static string SafeName(UnityEngine.Object o)
        {
            try { return o.name; }
            catch (Exception) { return "?"; }
        }

        private static int SafeCount(ParticleSystem ps)
        {
            try { return ps.particleCount; }
            catch (Exception) { return -1; }
        }

        private static int CountRenderers()
        {
            int count = 0;
            for (int i = 0; i < _planetCount; i++)
            {
                if (_fPlanetPlanetRenderer?.GetValue(_planets[i]) != null) count++;
            }
            return count;
        }

        private static int NamedPlanetCount(object planetary)
        {
            int count = 0;
            for (int i = 0; i < _fPsNamedPlanets.Length; i++)
            {
                if (_fPsNamedPlanets[i]?.GetValue(planetary) != null) count++;
            }
            return count;
        }

        private static string Count(IList list)
        {
            if (list == null) return "null";
            try { return list.Count.ToString(CultureInfo.InvariantCulture); }
            catch (Exception) { return "?"; }
        }

        /// <summary>记录集合的**真实运行时类型**（证明它是 List&lt;scrPlanet&gt; 而不是别的容器）。</summary>
        private static string ListTypeName(object list)
        {
            if (list == null) return "null";
            Type type = list.GetType();
            if (!type.IsGenericType) return type.Name;
            Type[] args = type.GetGenericArguments();
            return type.Name + "<" + (args.Length > 0 ? args[0].Name : "?") + ">";
        }

        private static bool IsActive(UnityEngine.Object o)
        {
            if (o is Component component && component != null) return component.gameObject.activeInHierarchy;
            if (o is GameObject go && go != null) return go.activeInHierarchy;
            return false;
        }

        // ==================================================================== formatting

        private static string BuildRecord(
            string phase, long generation, long outputFrameIndex, string schedulerStatus,
            int hitsThisFrame, string extra, bool full)
        {
            DiscoverInstances();

            _record.Length = 0;
            _record.Append(Prefix);
            _record.Append("ph=").Append(phase);
            _record.Append("|gen=").Append(generation.ToString(CultureInfo.InvariantCulture));
            _record.Append("|f=").Append(outputFrameIndex.ToString(CultureInfo.InvariantCulture));
            _record.Append("|uf=").Append(Time.frameCount.ToString(CultureInfo.InvariantCulture));
            _record.Append("|sched=").Append(schedulerStatus ?? "?");
            if (hitsThisFrame >= 0)
                _record.Append("|hits=").Append(hitsThisFrame.ToString(CultureInfo.InvariantCulture));

            _record.Append("|ts=").Append(F(Time.timeScale, 6));
            _record.Append("|dt=").Append(F(Time.deltaTime, 8));
            _record.Append("|udt=").Append(F(Time.unscaledDeltaTime, 8));
            _record.Append("|t=").Append(F(Time.time, 6));
            _record.Append("|rt=").Append(F(Time.realtimeSinceStartup, 4));
            _record.Append("|cf=").Append(Time.captureFramerate.ToString(CultureInfo.InvariantCulture));

            if (_pCameraInstance != null)
                _record.Append("|cam=").Append(DescribeCamera());
            if (_typesResolved)
                _record.Append("|pl=").Append(DescribePlanets());
            else
                _record.Append("|pl=types-pending:reason=").Append(_typesReason ?? "unknown");

            if (full) AppendHierarchy();
            AppendSystems(full);
            AppendAges(full);

            if (!string.IsNullOrEmpty(extra))
                _record.Append("|x=").Append(extra);

            return _record.ToString();
        }

        private static string DescribePlanets()
        {
            _planetText.Length = 0;
            _planetText.Append("route=").Append(_planetRoute ?? "unresolved");
            _planetText.Append(",collections=").Append(_collectionCounts ?? "?");
            _planetText.Append(",types=").Append(_collectionTypes ?? "?");
            _planetText.Append(",n=").Append(_planetCount.ToString(CultureInfo.InvariantCulture));
            _planetText.Append(",systems=").Append(_systemCount.ToString(CultureInfo.InvariantCulture));
            _planetText.Append(",psSpeed=").Append(D(_planetarySpeed, 4));
            _planetText.Append(",chosen=").Append(Id(_chosenPlanet as UnityEngine.Object));
            _planetText.Append(",reason=").Append(_planetReason ?? "-");

            for (int i = 0; i < _planetCount; i++)
            {
                object planet = _planets[i];
                object renderer = _fPlanetPlanetRenderer?.GetValue(planet);

                // 行星退役路径（scrPlanet 的 bool 参数销毁重载 → PlanetRenderer 的同名重载）
                // 会把拖尾以 StopEmittingAndClear 语义停止，因此"旧行星仍有存活拖尾"
                // 必须实测，不能从集合成员关系直接推断。
                _planetText.Append("|p").Append(i.ToString(CultureInfo.InvariantCulture));
                _planetText.Append("=id:").Append(_planetIds[i].ToString(CultureInfo.InvariantCulture));
                _planetText.Append(",src:").Append(Sources(_planetSources[i]));
                _planetText.Append(",on:").Append(Bit(_planetActive[i]));
                _planetText.Append(",rend:").Append(Id(renderer as UnityEngine.Object));
                if (renderer != null)
                {
                    _planetText.Append(",tail:").Append(ReadId(_fRendererTailParticles, renderer));
                    _planetText.Append(",coop:").Append(ReadId(_fRendererTailParticlesCoop, renderer));
                    _planetText.Append(",core:").Append(ReadId(_fRendererCoreParticles, renderer));
                    _planetText.Append(",spark:").Append(ReadId(_fRendererSparks, renderer));
                    _planetText.Append(",death:").Append(ReadId(_fRendererDeathExplosion, renderer));
                    _planetText.Append(",onlyRing:").Append(Bit(ReadBool(_fRendererOnlyRing, renderer)));
                    _planetText.Append(",samurai:").Append(Bit(_fRendererScrSamurai?.GetValue(renderer) != null));
                }
                _planetText.Append(",angle:").Append(D(ReadNumber(_fPlanetAngle, planet), 4));
                _planetText.Append(",cosmetic:").Append(D(ReadNumber(_fPlanetCosmeticAngle, planet), 6));
            }

            return _planetText.ToString();
        }

        private static string Sources(int mask)
        {
            if (mask == 0) return "?";
            _scratch.Length = 0;
            if ((mask & SrcAllPlanets) != 0) _scratch.Append("all+");
            if ((mask & SrcPlanetList) != 0) _scratch.Append("list+");
            if ((mask & SrcAvailablePlanets) != 0) _scratch.Append("avail+");
            if ((mask & SrcChosenPlanet) != 0) _scratch.Append("chosen+");
            if ((mask & SrcNamedField) != 0) _scratch.Append("named+");
            if (_scratch.Length > 0) _scratch.Length--;
            return _scratch.ToString();
        }

        /// <summary>
        /// 每个有效实例都记录：实例身份、播放状态、`time`、粒子数、scaled/unscaled、
        /// simulationSpeed、startLifetime、simulationSpace、emission 与 trails 配置。
        /// </summary>
        private static void AppendSystems(bool full)
        {
            for (int i = 0; i < _systemCount; i++)
            {
                ParticleSystem ps = _systems[i];
                if (ps == null) continue;

                try
                {
                    DescribeSystem(i, ps, full && i < MaxFullSystems);
                    _record.Append(_systemText);
                }
                catch (Exception ex)
                {
                    // 单个实例的模块读取失败不得升级成"整个探针失败"：只丢这一个实例，
                    // 其余实例与后续采样继续。
                    _record.Append("|sy").Append(i.ToString(CultureInfo.InvariantCulture))
                           .Append("=read-failed:").Append(ex.GetType().Name);
                }
            }
        }

        private static void DescribeSystem(int i, ParticleSystem ps, bool detail)
        {
            _systemText.Length = 0;
            _systemText.Append("|sy").Append(i.ToString(CultureInfo.InvariantCulture));
            _systemText.Append("=id:").Append(ps.GetInstanceID().ToString(CultureInfo.InvariantCulture));
            _systemText.Append(",role:").Append(_systemRoles[i]);
            _systemText.Append(",planet:").Append(_systemPlanet[i].ToString(CultureInfo.InvariantCulture));
            _systemText.Append(",ref:").Append(Bit(_systemReferenced[i]));
            _systemText.Append(",on:").Append(Bit(IsActive(ps)));
            _systemText.Append(",tier:").Append(detail ? "1" : "2");

            _systemText.Append(",play:").Append(Bit(ps.isPlaying));
            _systemText.Append(",emit:").Append(Bit(ps.isEmitting));
            _systemText.Append(",pause:").Append(Bit(ps.isPaused));
            _systemText.Append(",stopped:").Append(Bit(ps.isStopped));
            _systemText.Append(",t:").Append(F(ps.time, 6));
            _systemText.Append(",cnt:").Append(ps.particleCount.ToString(CultureInfo.InvariantCulture));
            _systemText.Append(",name:").Append(SafeName(ps));

            ParticleSystem.MainModule main = ps.main;
            _systemText.Append(",unscaled:").Append(Bit(main.useUnscaledTime));
            _systemText.Append(",sim:").Append(F(main.simulationSpeed, 4));
            _systemText.Append(",life:").Append(Curve(main.startLifetime));
            _systemText.Append(",space:").Append(main.simulationSpace.ToString());
            _systemText.Append(",max:").Append(main.maxParticles.ToString(CultureInfo.InvariantCulture));

            if (detail)
            {
                _systemText.Append(",tot:").Append(F(ps.totalTime, 4));
                _systemText.Append(",dur:").Append(F(main.duration, 4));
                _systemText.Append(",loop:").Append(Bit(main.loop));
                _systemText.Append(",scaling:").Append(main.scalingMode.ToString());
                _systemText.Append(",prewarm:").Append(Bit(main.prewarm));
                _systemText.Append(",gravity:").Append(F(main.gravityModifier.constant, 4));

                ParticleSystem.EmissionModule emission = ps.emission;
                    _systemText.Append(",emitOn:").Append(Bit(emission.enabled));
                    _systemText.Append(",rate:").Append(Curve(emission.rateOverTime));
                    _systemText.Append(",rateD:").Append(Curve(emission.rateOverDistance));

                    ParticleSystem.TrailModule trails = ps.trails;
                    _systemText.Append(",tr:").Append(Bit(trails.enabled));
                    _systemText.Append(",trRatio:").Append(F(trails.ratio, 4));
                    _systemText.Append(",trLife:").Append(Curve(trails.lifetime));
                    _systemText.Append(",trMin:").Append(F(trails.minVertexDistance, 4));
                    _systemText.Append(",trWorld:").Append(Bit(trails.worldSpace));
                    _systemText.Append(",trRibbons:").Append(trails.ribbonCount.ToString(CultureInfo.InvariantCulture));
                }
        }

        /// <summary>
        /// 只读粒子年龄样本：每个采样点都取（含等待窗口），因为
        /// "同逻辑帧等待期间 remainingLifetime 是否减少" 正是本轮要回答的问题。
        /// </summary>
        private static void AppendAges(bool full)
        {
            int budget = full ? MaxFullSystems : 2;
            _ageText.Length = 0;
            int written = 0;
            for (int i = 0; i < _systemCount && written < budget; i++)
            {
                ParticleSystem ps = _systems[i];
                if (ps == null) continue;
                if (written > 0) _ageText.Append(';');
                _ageText.Append(ps.GetInstanceID().ToString(CultureInfo.InvariantCulture)).Append(':');
                AppendAgeSample(_ageText, ps);
                written++;
            }

            if (written == 0)
                _record.Append("|age=none:reason=").Append(_planetReason ?? "no-system");
            else
                _record.Append("|age=").Append(_ageText);
        }

        private static void AppendHierarchy()
        {
            // 扫描按行星实例缓存（只缓存成功结果），因此这里最多每个行星付一次代价；
            // 缓存内容跨采样重放，所以每条 full-detail 记录都能看到同一份层级证据。
            for (int i = 0; i < _planetCount; i++) ScanHierarchyFor(i);

            if (_hierCount == 0)
            {
                // 扫描尚未成功（或该行星子树里确实没有 ParticleSystem）：显式标注，绝不静默省略。
                _record.Append("|hier=empty:scannedPlanets=")
                       .Append(_hierarchyScanned.Count.ToString(CultureInfo.InvariantCulture))
                       .Append(",planets=")
                       .Append(_planetCount.ToString(CultureInfo.InvariantCulture));
                return;
            }

            _hierText.Length = 0;
            for (int i = 0; i < _hierCount; i++)
            {
                if (i > 0) _hierText.Append(' ');
                _hierText.Append('[')
                         .Append(_hierIds[i].ToString(CultureInfo.InvariantCulture))
                         .Append(' ').Append(_hierNames[i])
                         .Append(" on:").Append(Bit(_hierActive[i]))
                         .Append(" cnt:").Append(_hierCounts[i].ToString(CultureInfo.InvariantCulture))
                         .Append(']');
            }
            _record.Append("|hier=").Append(_hierText);
        }

        private static string DescribeCamera()
        {
            object cam = _pCameraInstance?.GetValue(null);
            if (cam == null) return "instance-missing";

            _cameraText.Length = 0;
            _cameraText.Append("id=").Append(Id(cam as UnityEngine.Object));
            _cameraText.Append(",active=").Append(Bit(IsActive(cam as UnityEngine.Object)));

            // scrCamera.Update() 的 IL 已确认：timer / rottimer / coltimer 全部直接累加
            // Time.deltaTime，pos = Lerp(frompos+offset+holdOffset, topos+…, min(timer*camspeed,1))；
            // UpdateSize() 里的 pulsetimer 同理。这些计时器是"每个 Unity Update 前进一次"，
            // 与逻辑帧号无关 —— 这正是等待期间视觉时间失配的机制。
            _cameraText.Append(",timer=").Append(F(Get<float>(cam, "timer"), 6));
            _cameraText.Append(",from=").Append(V2(Get<Vector2>(cam, "frompos")));
            _cameraText.Append(",to=").Append(V2(Get<Vector2>(cam, "topos")));
            _cameraText.Append(",pos=").Append(V2(Get<Vector2>(cam, "pos")));
            _cameraText.Append(",offset=").Append(V2(Get<Vector2>(cam, "offset")));
            _cameraText.Append(",holdOff=").Append(V2(Get<Vector2>(cam, "holdOffset")));
            _cameraText.Append(",shake=").Append(V2(Get<Vector2>(cam, "shake")));
            _cameraText.Append(",speed=").Append(F(Get<float>(cam, "camspeed"), 6));
            _cameraText.Append(",lockedSpd=").Append(Bit(Get<bool>(cam, "lockedSpeed")));
            _cameraText.Append(",bpmSpd=").Append(Bit(Get<bool>(cam, "speedAffectedByBPMChanges")));
            _cameraText.Append(",trialSpd=").Append(Bit(Get<bool>(cam, "speedAffectedBySpeedTrial")));
            _cameraText.Append(",rot=").Append(F(Get<float>(cam, "rot"), 6));
            _cameraText.Append(",rotTo=").Append(F(Get<float>(cam, "torot"), 6));
            _cameraText.Append(",rotDur=").Append(F(Get<float>(cam, "rotdur"), 6));
            _cameraText.Append(",rotTimer=").Append(F(Get<float>(cam, "rottimer"), 6));
            _cameraText.Append(",rotEditor=").Append(Bit(Get<bool>(cam, "editorRotation")));
            _cameraText.Append(",zNorm=").Append(F(Get<float>(cam, "camsizenormal"), 6));
            _cameraText.Append(",zFrom=").Append(F(Get<float>(cam, "fromsize"), 6));
            _cameraText.Append(",zTo=").Append(F(Get<float>(cam, "tosize"), 6));
            _cameraText.Append(",zTweenT=").Append(F(Get<float>(cam, "sizeTweenTime"), 6));
            _cameraText.Append(",pulseT=").Append(F(Get<float>(cam, "pulsetimer"), 6));
            _cameraText.Append(",pulseOn=").Append(Bit(Get<bool>(cam, "isPulsingOnHit")));
            _cameraText.Append(",zoomSize=").Append(F(Get<float>(cam, "zoomSize"), 6));
            _cameraText.Append(",userSizeMul=").Append(F(Get<float>(cam, "userSizeMultiplier"), 6));
            _cameraText.Append(",colT=").Append(F(Get<float>(cam, "coltimer"), 6));
            _cameraText.Append(",colDur=").Append(F(Get<float>(cam, "coldur"), 6));
            _cameraText.Append(",col=").Append(Colour(Get<Color>(cam, "col")));
            _cameraText.Append(",moving=").Append(Bit(Get<bool>(cam, "isMoveTweening")));
            _cameraText.Append(",sizing=").Append(Bit(Get<bool>(cam, "isSizeTweening")));
            _cameraText.Append(",zoomOut=").Append(Bit(Get<bool>(cam, "isZoomingOut")));
            _cameraText.Append(",follow=").Append(Bit(Get<bool>(cam, "followMode")));
            _cameraText.Append(",followMoving=").Append(Bit(Get<bool>(cam, "followMovingPlatforms")));
            _cameraText.Append(",skip=").Append(Bit(Get<bool>(cam, "skipUpdate")));
            _cameraText.Append(",movType=").Append(ReadEnumField(cam, "lastUsedMovementType"));
            _cameraText.Append(",lastTile=").Append(Get<int>(cam, "lastTileCamFloor").ToString(CultureInfo.InvariantCulture));
            _cameraText.Append(",furthFloor=").Append(Get<int>(cam, "furthestFloorID").ToString(CultureInfo.InvariantCulture));
            _cameraText.Append(",furthPlanet=").Append(Id(Get<object>(cam, "furthestPlanet") as UnityEngine.Object));
            _cameraText.Append(",customFps=").Append(Bit(Get<bool>(cam, "enableCustomFPS")));
            _cameraText.Append(",frameRate=").Append(F(Get<float>(cam, "frameRate"), 4));
            _cameraText.Append(",lockFpsUpd=").Append(Bit(Get<bool>(cam, "lockCustomFrameUpdate")));
            _cameraText.Append(",lastFpsUpd=").Append(D(Get<double>(cam, "lastCustomFPSUpdate"), 6));
            _cameraText.Append(",pState=").Append(ReadPositionState(cam));
            _cameraText.Append(",pStateInt=").Append(Get<int>(cam, "positionStateInt").ToString(CultureInfo.InvariantCulture));

            // Transform / 父 Transform：只读位置与旋转，不写任何值。
            // scrCamera.LateUpdate() 的 IL 是 CheckUpdateFrameRateScreen() + SetPosition(transform.position)，
            // 即每帧把世界 Z 重新钉回常量；camobj 的 Z 因此应逐帧保持不变。
            if (cam is Component behaviour)
            {
                Transform self = behaviour.transform;
                _cameraText.Append(",selfW=").Append(V3(self.position));
                _cameraText.Append(",selfL=").Append(V3(self.localPosition));
                _cameraText.Append(",selfParent=").Append(DescribeTransform(self.parent));
            }

            _cameraText.Append(",camobj=").Append(DescribeUnityCamera(cam, "camobj"));
            return _cameraText.ToString();
        }

        private static string Colour(Color c)
        {
            return "(" + F(c.r, 3) + "," + F(c.g, 3) + "," + F(c.b, 3) + "," + F(c.a, 3) + ")";
        }

        private static string ReadEnumField(object cam, string name)
        {
            if (!_cameraFields.TryGetValue(name, out FieldInfo field)) return "?";
            try
            {
                object value = field.GetValue(cam);
                return value?.ToString() ?? "?";
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string DescribeUnityCamera(object cam, string fieldName)
        {
            Camera unityCamera = Get<Camera>(cam, fieldName);
            if (unityCamera == null) return "null";

            Transform t = unityCamera.transform;
            return "id" + unityCamera.GetInstanceID().ToString(CultureInfo.InvariantCulture) +
                   "@" + V3(t.position) + "/p" + DescribeTransform(t.parent);
        }

        private static string DescribeTransform(Transform t)
        {
            return t == null ? "null" : t.name + "@" + V3(t.position) + "/e" + V3(t.eulerAngles);
        }

        private static string ReadPositionState(object cam)
        {
            if (_pCameraPositionState == null) return "?";
            try
            {
                object value = _pCameraPositionState.GetValue(cam);
                return value?.ToString() ?? "?";
            }
            catch (Exception)
            {
                return "?";
            }
        }

        /// <summary>
        /// 只读取出最多 <see cref="ParticleSampleCount"/> 个粒子的年龄样本。
        /// **不**调用任何变更 API；数组与缓冲复用，样本内零托管分配（除最终字符串）。
        /// </summary>
        private static void AppendAgeSample(StringBuilder sb, ParticleSystem ps)
        {
            int count;
            try
            {
                count = ps.GetParticles(_particleSample);
            }
            catch (Exception ex)
            {
                sb.Append("get-failed:").Append(ex.GetType().Name);
                return;
            }

            if (count <= 0)
            {
                sb.Append("none");
                return;
            }

            int limit = count < ParticleSampleCount ? count : ParticleSampleCount;
            for (int i = 0; i < limit; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(F(_particleSample[i].remainingLifetime, 6))
                  .Append('/')
                  .Append(F(_particleSample[i].startLifetime, 6));
            }
        }

        private static string Curve(ParticleSystem.MinMaxCurve curve)
        {
            return curve.mode + ":" + F(curve.constant, 6) + "x" + F(curve.curveMultiplier, 4);
        }

        private static bool ReadBool(FieldInfo field, object target)
        {
            if (field == null || target == null) return false;
            object value = field.GetValue(target);
            return value is bool typed && typed;
        }

        /// <summary>
        /// 读一个数值字段。`scrPlanet.angle` 与 `PlanetarySystem.speed` 在当前 DLL 里是
        /// **double**，而 `cosmeticAngle` 是 float —— 两者都必须支持，否则会静默记成 0。
        /// </summary>
        private static double ReadNumber(FieldInfo field, object target)
        {
            if (field == null || target == null) return 0d;
            object value = field.GetValue(target);
            if (value is float single) return single;
            if (value is double number) return number;
            return 0d;
        }

        private static object Get<TObject>(object target, FieldInfo field)
        {
            if (target == null || field == null) return null;
            return field.GetValue(target);
        }

        /// <summary>读取一个 Unity 对象字段并输出实例 ID；字段缺失或对象已销毁都输出 null。</summary>
        private static string ReadId(FieldInfo field, object target)
        {
            if (field == null || target == null) return "null";
            return Id(field.GetValue(target) as UnityEngine.Object);
        }

        private static T Get<T>(object target, string name)
        {
            if (target == null || !_cameraFields.TryGetValue(name, out FieldInfo field))
                return default(T);

            object value = field.GetValue(target);
            return value is T typed ? typed : default(T);
        }

        private static string Bit(bool value)
        {
            return value ? "1" : "0";
        }

        private static string F(float value, int digits)
        {
            return value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        private static string D(double value, int digits)
        {
            return value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        private static string Id(UnityEngine.Object o)
        {
            return o == null ? "null" : o.GetInstanceID().ToString(CultureInfo.InvariantCulture);
        }

        private static string V2(Vector2 v)
        {
            return "(" + F(v.x, 6) + "," + F(v.y, 6) + ")";
        }

        private static string V3(Vector3 v)
        {
            return "(" + F(v.x, 6) + "," + F(v.y, 6) + "," + F(v.z, 6) + ")";
        }
    }
}
