using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using ADOFAI.Renderist.Logging;
using DG.Tweening;
using DG.Tweening.Core;
using DG.Tweening.Plugins.Options;
using HarmonyLib;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 本 session 唯一目标 Planet <c>cosmeticRadius</c> Tween 的**局部视觉时间 ownership**。
    ///
    /// 职责边界（严格单一）：
    ///   * 在本次 Renderist-owned <c>editor.Play()</c> 之前安装 / 之后精确卸载
    ///     <c>scrController.OnMusicScheduled</c> 的局部 Prefix/Postfix ownership observer；
    ///   * 用 <c>DOTween.PlayingTweens()</c> 引用 identity 差分取得该次 schedule 新建的
    ///     唯一目标实例，并做严格实例契约 / 闭包 / planet+controller ownership / callback 审计；
    ///   * 取得 ownership 时立即 Pause，并保存原始 active / playing / position / duration；
    ///   * Prepare frame N 时按 **(4 + N) / frozenOutputFps** 定位原生 Tween position；
    ///   * duration 终值的 fail-safe 定位与后续每帧契约校验；
    ///   * cleanup 时先失效 generation，再精确撤销 Hook，并在契约允许时恢复原 playing 状态。
    ///
    /// **本模块不是新的 clock**：它不推进 <c>MasterTimeline</c>、不读 wall clock / Time.frameCount /
    /// chart songposition，也不重置 position。它只消费 scheduler 已经准备的 **absolute output frame
    /// index** 与 <c>TryStart</c> 时冻结一次的 OutputFps。所有缓冲 / EOF / capture / FFmpeg 逻辑
    /// 仍留在各自既有模块中。
    ///
    /// 插值仍由游戏原生 Tween 定义：本模块不写 Ease、不改 startValue/endValue、不改 target/id、
    /// 不重建 Tween，也不硬编码任何 endValue（真实 endValue 由 tileSize × radiusScale 决定）。
    /// 它只拥有 position。
    ///
    /// 所有失败路径都 fail-closed：不静默继续、不放宽过滤，也不 throw 穿出 Unity Update；
    /// 失败以稳定 reason 转成当前 session 的失败请求，由 scheduler / controller 既有终止与
    /// residual ownership 收敛路径处理。
    /// </summary>
    internal sealed class PlanetVisualTimeOwnership
    {
        // ---- 当前 Assembly-CSharp.dll FileVersion 0.4.3.0 已实测的静态形状 ----
        //
        // scrController : ADOBase
        //   void OnMusicScheduled()                                  // 无参；DOTween.To 返回值被丢弃
        //   scrController+<>c__DisplayClass171_0                      // 该次调用的闭包
        //     scrPlanet planet                                        // 唯一捕获字段
        //     float <OnMusicScheduled>b__0()                          // DOTween getter
        //     void  <OnMusicScheduled>b__1(float)                     // DOTween setter
        // scrController.playerOne : scrPlayer
        //   PlanetarySystem planetarySystem
        //     List<scrPlanet> planetList / allPlanets / availablePlanets
        // scrPlanet.planetarySystem : PlanetarySystem
        private const string PlanetTypeFullName = "scrPlanet";
        private const string PlayerOneMemberName = "playerOne";
        private const string PlanetarySystemMemberName = "planetarySystem";
        private const string OnMusicScheduledName = "OnMusicScheduled";
        private const string OnMusicScheduledLambdaPrefix = "<" + OnMusicScheduledName + ">";

        /// <summary>
        /// OnMusicScheduled 创建到 pre-entry output frame 0 EOF 之间的原生更新次数。
        /// 当前目标版本、指定谱面第一砖、30 / 60 FPS 实测均为 4（见 PROJECT_UNDERSTANDING §2.2.2）；
        /// 不是所有 Tween 的不变量，只用于这一条已识别的 Tween。
        /// </summary>
        private const long CreationLeadSteps = 4L;

        /// <summary>float position 目标位置的容差（秒）。Goto 直接写入 position，只需吸收 float 表示误差。</summary>
        private const float PositionTolerance = 1e-4f;

        /// <summary>
        /// 目标 Tween 必须为空的 9 个 callback 字段（前 8 个是 DOTween public 字段，
        /// onStart 是 ABSSequentiable 上的 internal 字段，必须按成员反射读取）。
        /// 生产代码绝不为兼容未知 callback 而 suppress / 清空 / 替换游戏 callback。
        /// </summary>
        private static readonly string[] CallbackFieldNames =
        {
            "onStart", "onPlay", "onPause", "onUpdate", "onStepComplete",
            "onComplete", "onRewind", "onKill", "onWaypointChange"
        };

        private static PlanetVisualTimeOwnership Active { get; set; }

        private readonly Harmony _harmony;
        private readonly Action _onMusicScheduledOwned;
        private readonly Action<string> _requestSessionFailure;

        private object _controller;
        private MethodInfo _method;
        private bool _patched;
        private bool _playRequested;
        private long _generation;
        private bool _failed;
        private string _failureReason;

        private readonly List<Tween> _snapshot = new List<Tween>();
        private bool _snapshotValid;

        private TweenerCore<float, float, FloatOptions> _instance;
        private bool _originalPlaying;
        private float _originalPosition;
        private double _duration;
        private bool _terminalPositioned;
        private float _terminalPosition;
        /// <summary>terminal-hold 只记录一次（降噪）；每帧契约验证不受影响。</summary>
        private bool _terminalHoldLogged;

        /// <summary>
        /// 本 session 的 ownership observer。controller 必须是本次 Renderist-owned
        /// <c>editor.Play()</c> 所对应的那个引用（由 PlaybackLifecycleHandoff 在 Play 之前取得）。
        /// onMusicScheduledOwned 只在 acquisition 完全成功后回调（用于记录既有 lifecycle marker）；
        /// requestSessionFailure 把 fail-closed 原因转成当前 session 的失败请求。
        /// </summary>
        internal PlanetVisualTimeOwnership(
            Harmony harmony, object controller, Action onMusicScheduledOwned, Action<string> requestSessionFailure)
        {
            _harmony = harmony ?? throw new ArgumentNullException(nameof(harmony));
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _onMusicScheduledOwned = onMusicScheduledOwned;
            _requestSessionFailure = requestSessionFailure;
        }

        /// <summary>
        /// 安装本次 OnMusicScheduled 的 Prefix/Postfix。必须在本次 editor.Play() 之前调用；
        /// 安装失败即拒绝启动（不允许“Play 之后才装”的窗口）。
        /// </summary>
        internal bool Begin(out string error)
        {
            error = null;
            try
            {
                _method = _controller.GetType().GetMethod(OnMusicScheduledName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (_method == null)
                {
                    error = "on-music-scheduled-unavailable";
                    return false;
                }

                // 先标记 ownership 再 Patch：部分应用 / Patch 抛异常时仍可被 cleanup 精确撤销。
                _patched = true;
                _harmony.Patch(_method,
                    prefix: new HarmonyMethod(typeof(PlanetVisualTimeOwnership), nameof(OnMusicScheduledPrefix)),
                    postfix: new HarmonyMethod(typeof(PlanetVisualTimeOwnership), nameof(OnMusicScheduledPostfix)));
                Active = this;
                Log.Info("PlanetVisualTimeOwnership observer installed: method=" +
                         _controller.GetType().Name + "." + OnMusicScheduledName);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Exception("PlanetVisualTimeOwnership: observer 安装失败", ex);
                TryDispose(out _);
                return false;
            }
        }

        /// <summary>
        /// 绑定本 session 的 generation authority（复用 FrameCaptureDriver 每次成功 Start
        /// 分配的单调 generation；不新建第二套 generation 机制）。必须在 editor.Play() 之前调用。
        /// </summary>
        internal void Arm(long generation)
        {
            _generation = generation;
            _playRequested = false;
            _snapshotValid = false;
            _terminalPositioned = false;
            _terminalPosition = 0f;
            _terminalHoldLogged = false;
            Log.Info("PlanetVisualTimeOwnership armed: sessionGeneration=" +
                     generation.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>本 session 已请求官方 editor.Play()。acquisition 只在该状态之后允许。</summary>
        internal void MarkPlayRequested()
        {
            _playRequested = true;
        }

        /// <summary>是否仍持有需要收敛的 ownership（供 residual gate 判定）。</summary>
        internal bool HasResidualOwnership => _patched || _instance != null || _method != null;

        internal string DescribeState()
        {
            return "generation=" + _generation.ToString(CultureInfo.InvariantCulture) +
                   ",playRequested=" + (_playRequested ? "true" : "false") +
                   ",patched=" + (_patched ? "true" : "false") +
                   ",instanceOwned=" + (_instance != null ? "true" : "false") +
                   ",terminalPositioned=" + (_terminalPositioned ? "true" : "false") +
                   ",failed=" + (_failed ? "true" : "false") +
                   ",failureReason=" + (_failureReason ?? "null");
        }

        // ================================================================
        // Harmony：OnMusicScheduled Prefix / Postfix
        // ================================================================

        private static void OnMusicScheduledPrefix(object __instance)
        {
            PlanetVisualTimeOwnership owner = Active;
            if (owner == null) return;
            try
            {
                owner.ObserveSchedulePrefix(__instance);
            }
            catch (Exception ex)
            {
                Log.Exception("PlanetVisualTimeOwnership: OnMusicScheduled Prefix 异常", ex);
                owner.FailClosed("planet-visual-time-schedule-prefix-exception:" + ex.GetType().Name);
            }
        }

        private static void OnMusicScheduledPostfix(object __instance)
        {
            PlanetVisualTimeOwnership owner = Active;
            if (owner == null) return;
            try
            {
                owner.ObserveSchedulePostfix(__instance);
            }
            catch (Exception ex)
            {
                Log.Exception("PlanetVisualTimeOwnership: OnMusicScheduled Postfix 异常", ex);
                owner.FailClosed("planet-visual-time-schedule-postfix-exception:" + ex.GetType().Name);
            }
        }

        /// <summary>
        /// 本 session 的 schedule 是否属于我们：generation 已绑定、editor.Play 已请求、
        /// 且触发实例就是本次 Renderist-owned controller。其它 controller 的 schedule
        /// 不属于本次 playback，直接忽略（不参与差分，也不触发失败）。
        /// </summary>
        private bool IsOwnedSchedule(object instance)
        {
            if (_failed) return false;
            if (_generation == 0) return false;
            if (!_playRequested) return false;
            return ReferenceEquals(instance, _controller);
        }

        /// <summary>Prefix：对 PlayingTweens() 做引用 identity 快照。</summary>
        private void ObserveSchedulePrefix(object instance)
        {
            _snapshotValid = false;
            if (!IsOwnedSchedule(instance)) return;

            _snapshot.Clear();
            List<Tween> playing = DOTween.PlayingTweens(new List<Tween>());
            if (playing != null)
            {
                for (int i = 0; i < playing.Count; i++)
                {
                    Tween tween = playing[i];
                    if (tween != null) _snapshot.Add(tween);
                }
            }
            _snapshotValid = true;
        }

        /// <summary>
        /// Postfix：再次取 PlayingTweens() 求新增实例，唯一且通过全部严格检查后才取得 ownership。
        /// 任何数量 / 身份 / 契约 / callback 不满足都 fail-closed。
        /// </summary>
        private void ObserveSchedulePostfix(object instance)
        {
            if (!IsOwnedSchedule(instance)) return;

            // 本 session 已经拥有目标实例后再次收到同一 controller 的 schedule：
            // 当前没有经过验证的合法 repeated schedule 语义，默认 fail-closed，
            // 绝不静默覆盖旧 instance。
            if (_instance != null)
            {
                FailClosed("planet-visual-time-repeated-music-schedule");
                return;
            }

            if (!_snapshotValid)
            {
                FailClosed("planet-visual-time-schedule-prefix-snapshot-missing");
                return;
            }

            List<Tween> playingAfter = DOTween.PlayingTweens(new List<Tween>());
            var added = new List<Tween>();
            if (playingAfter != null)
            {
                for (int i = 0; i < playingAfter.Count; i++)
                {
                    Tween tween = playingAfter[i];
                    if (tween == null) continue;
                    if (!ContainsReference(_snapshot, tween)) added.Add(tween);
                }
            }

            int before = _snapshot.Count;
            int after = playingAfter == null ? 0 : playingAfter.Count;

            if (added.Count == 0)
            {
                FailClosed("planet-visual-time-schedule-created-no-tween:before=" +
                           before.ToString(CultureInfo.InvariantCulture) + ",after=" +
                           after.ToString(CultureInfo.InvariantCulture));
                return;
            }

            // 新增多个：不假定循环只创建一个实例，不猜测哪一个才是目标。
            if (added.Count > 1)
            {
                FailClosed("planet-visual-time-schedule-instance-count-unexpected:before=" +
                           before.ToString(CultureInfo.InvariantCulture) + ",after=" +
                           after.ToString(CultureInfo.InvariantCulture) + ",new=" +
                           added.Count.ToString(CultureInfo.InvariantCulture));
                return;
            }

            Tween candidate = added[0];
            if (!TryAcceptTarget(candidate, out string identityReason))
            {
                FailClosed("planet-visual-time-schedule-identity-mismatch:" + identityReason);
                return;
            }

            var tweener = (TweenerCore<float, float, FloatOptions>)candidate;

            float duration = tweener.Duration();
            if (!IsFinitePositive(duration))
            {
                FailClosed("planet-visual-time-instance-duration-invalid:" + FormatFloat(duration));
                return;
            }

            bool wasPlaying = tweener.IsPlaying();
            if (!tweener.active)
            {
                FailClosed("planet-visual-time-instance-not-active");
                return;
            }
            if (!wasPlaying)
            {
                FailClosed("planet-visual-time-instance-not-playing");
                return;
            }

            float position = tweener.position;
            float fullPosition = tweener.fullPosition;
            if (!IsFinite(position) || position < 0f || !IsFinite(fullPosition) || fullPosition < 0f)
            {
                FailClosed("planet-visual-time-instance-position-invalid:position=" + FormatFloat(position) +
                           ",fullPosition=" + FormatFloat(fullPosition));
                return;
            }

            // 记录原始状态后立即 Pause（不 Kill / 不 Rewind / 不 Resume / 不改 Ease /
            // 不改 startValue·endValue / 不改 target·id / 不重建 Tween）。
            _instance = tweener;
            _originalPlaying = wasPlaying;
            _originalPosition = position;
            _duration = duration;

            try
            {
                tweener.Pause();
            }
            catch (Exception ex)
            {
                Log.Exception("PlanetVisualTimeOwnership: 目标 Tween Pause 失败", ex);
                FailClosed("planet-visual-time-pause-failed:" + ex.GetType().Name);
                return;
            }

            if (!tweener.active)
            {
                FailClosed("planet-visual-time-pause-deactivated-instance");
                return;
            }
            if (tweener.IsPlaying())
            {
                FailClosed("planet-visual-time-pause-not-effective");
                return;
            }

            Log.Info("PlanetVisualTimeOwnership acquired: sessionGeneration=" +
                     _generation.ToString(CultureInfo.InvariantCulture) +
                     " controller=" + _controller.GetType().Name +
                     " before=" + before.ToString(CultureInfo.InvariantCulture) +
                     " after=" + after.ToString(CultureInfo.InvariantCulture) +
                     " new=1 accepted=1" +
                     " instance=" + candidate.GetType().Name +
                     " closure=" + DescribeClosureTarget(candidate) +
                     " originalPlaying=" + (wasPlaying ? "true" : "false") +
                     " originalPosition=" + FormatFloat(position) +
                     " originalFullPosition=" + FormatFloat(fullPosition) +
                     " duration=" + FormatFloat(duration) +
                     " " + DescribeTweenRuntimeContract(tweener));

            // acquisition 完全成功后，才记录既有 lifecycle marker（SawMusicScheduled）。
            _onMusicScheduledOwned?.Invoke();
        }

        // ================================================================
        // 严格实例过滤
        // ================================================================

        /// <summary>
        /// 严格过滤：float TweenerCore + 本次 OnMusicScheduled 闭包的 getter/setter + 闭包持有目标
        /// scrPlanet + planet 属于本 controller / 本次 playback + 9 个 callback 全空。
        /// 任何一项不成立都返回 false 与稳定 reason（调用方 fail-closed）。
        /// </summary>
        private bool TryAcceptTarget(Tween tween, out string reason)
        {
            reason = null;

            if (!(tween is TweenerCore<float, float, FloatOptions> tweener))
            {
                reason = "not-float-tweener-core:" + tween.GetType().FullName;
                return false;
            }

            if (tweener.getter == null || tweener.setter == null)
            {
                reason = "getter-setter-missing";
                return false;
            }

            object getterClosure = tweener.getter.Target;
            object setterClosure = tweener.setter.Target;
            if (getterClosure == null || setterClosure == null)
            {
                reason = "closure-target-missing";
                return false;
            }
            if (!ReferenceEquals(getterClosure, setterClosure))
            {
                reason = "getter-setter-closure-mismatch";
                return false;
            }

            Type closureType = getterClosure.GetType();
            if (_controller == null || closureType.DeclaringType == null ||
                !ReferenceEquals(closureType.DeclaringType, _controller.GetType()))
            {
                reason = "closure-declaring-type-not-current-controller:" + closureType.FullName;
                return false;
            }

            if (!IsOnMusicScheduledLambda(tweener.getter.Method) ||
                !IsOnMusicScheduledLambda(tweener.setter.Method))
            {
                reason = "getter-setter-not-onmusicscheduled-closure:" +
                         DescribeMethod(tweener.getter.Method) + "/" + DescribeMethod(tweener.setter.Method);
                return false;
            }

            object planet = ReadClosurePlanet(getterClosure, out string planetReason);
            if (planet == null)
            {
                reason = planetReason;
                return false;
            }

            if (!TryVerifyPlanetOwnership(planet, out string ownershipReason))
            {
                reason = ownershipReason;
                return false;
            }

            if (!TryAuditCallbacks(tweener, out string callbackReason))
            {
                reason = callbackReason;
                return false;
            }

            return true;
        }

        private static bool IsOnMusicScheduledLambda(MethodInfo method)
        {
            return method != null &&
                   method.Name != null &&
                   method.Name.StartsWith(OnMusicScheduledLambdaPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// 闭包必须恰好持有一个 scrPlanet 类型字段，且该字段值非空。
        /// 这里只按类型识别目标 planet，不依赖字段名，也不要求闭包的其它状态形状。
        /// </summary>
        private static object ReadClosurePlanet(object closure, out string reason)
        {
            reason = null;
            object planet = null;
            int planetFieldCount = 0;

            FieldInfo[] fields = closure.GetType().GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (field.FieldType == null ||
                    !string.Equals(field.FieldType.FullName, PlanetTypeFullName, StringComparison.Ordinal))
                {
                    continue;
                }

                planetFieldCount++;
                object value = field.GetValue(closure);
                if (value != null && planet == null) planet = value;
            }

            if (planetFieldCount != 1)
            {
                reason = "closure-planet-field-count-unexpected:" +
                         planetFieldCount.ToString(CultureInfo.InvariantCulture);
                return null;
            }
            if (planet == null)
            {
                reason = "closure-planet-null";
                return null;
            }
            return planet;
        }

        /// <summary>
        /// planet 必须属于本 controller / 本次 playback：
        ///   1) planet.planetarySystem 与 controller.playerOne.planetarySystem 是同一对象；
        ///   2) planet 出现在该 system 的 planet 列表字段中（不硬编码列表字段名）。
        /// </summary>
        private bool TryVerifyPlanetOwnership(object planet, out string reason)
        {
            reason = null;

            object player = ReadMember(_controller, PlayerOneMemberName);
            if (player == null)
            {
                reason = "controller-player-unavailable";
                return false;
            }

            object system = ReadMember(player, PlanetarySystemMemberName);
            if (system == null)
            {
                reason = "player-planetary-system-unavailable";
                return false;
            }

            object planetSystem = ReadMember(planet, PlanetarySystemMemberName);
            if (planetSystem == null || !ReferenceEquals(planetSystem, system))
            {
                reason = "planet-not-owned-by-current-controller";
                return false;
            }

            int listsChecked = 0;
            FieldInfo[] fields = system.GetType().GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (!IsPlanetListField(field.FieldType)) continue;
                listsChecked++;

                if (!(field.GetValue(system) is IEnumerable list)) continue;
                foreach (object element in list)
                {
                    if (element != null && ReferenceEquals(element, planet)) return true;
                }
            }

            if (listsChecked == 0)
            {
                reason = "planetary-system-planet-list-unavailable";
                return false;
            }

            reason = "planet-not-in-current-controller-planet-list";
            return false;
        }

        private static bool IsPlanetListField(Type type)
        {
            if (type == null || !type.IsGenericType) return false;
            if (type.GetGenericTypeDefinition() != typeof(List<>)) return false;

            Type[] arguments = type.GetGenericArguments();
            return arguments.Length == 1 && arguments[0].FullName != null &&
                   string.Equals(arguments[0].FullName, PlanetTypeFullName, StringComparison.Ordinal);
        }

        /// <summary>9 个 callback 字段必须全部存在且为空。任一非空即 fail-closed。</summary>
        private static bool TryAuditCallbacks(Tween tween, out string reason)
        {
            reason = null;
            List<string> nonEmpty = null;
            Type type = tween.GetType();

            for (int i = 0; i < CallbackFieldNames.Length; i++)
            {
                string name = CallbackFieldNames[i];
                FieldInfo field = FindInstanceFieldInHierarchy(type, name);
                if (field == null)
                {
                    reason = "callback-field-unavailable:" + name;
                    return false;
                }

                if (field.GetValue(tween) == null) continue;
                if (nonEmpty == null) nonEmpty = new List<string>();
                nonEmpty.Add(name);
            }

            if (nonEmpty != null)
            {
                reason = "callback-not-empty:" + string.Join(",", nonEmpty.ToArray());
                return false;
            }
            return true;
        }

        private static FieldInfo FindInstanceFieldInHierarchy(Type type, string name)
        {
            Type current = type;
            while (current != null)
            {
                FieldInfo field = current.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field;
                current = current.BaseType;
            }
            return null;
        }

        // ================================================================
        // Prepare frame N：唯一的时间消费入口
        // ================================================================

        /// <summary>
        /// 在 scheduler 已准备 outcome frame N 的既有 Prepare 边界调用（pre-entry 与 gameplay 共用）。
        ///
        /// 唯一公式（double 计算后再安全转换给 DOTween 的 float API）：
        ///   targetPosition = (CreationLeadSteps + absoluteOutputFrameIndex) / frozenOutputFps
        ///   targetPosition = clamp(targetPosition, 0, actualDuration)
        ///
        /// 本方法不推进任何时间，也不在 EOF / Commit / wall-clock 回调中被调用。
        /// </summary>
        internal bool TryPrepareFrame(
            long generation, long absoluteOutputFrameIndex, int frozenOutputFps, out string error)
        {
            error = null;
            try
            {
                if (_failed)
                {
                    error = _failureReason ?? "planet-visual-time-ownership-failed";
                    return false;
                }
                if (generation == 0 || generation != _generation)
                {
                    error = "planet-visual-time-generation-mismatch:expected=" +
                            _generation.ToString(CultureInfo.InvariantCulture) + ",observed=" +
                            generation.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                if (absoluteOutputFrameIndex < 0)
                {
                    error = "planet-visual-time-frame-index-invalid";
                    return false;
                }
                if (frozenOutputFps <= 0)
                {
                    error = "planet-visual-time-output-fps-invalid";
                    return false;
                }
                if (_instance == null)
                {
                    // 任何 Prepare 都必须已经拥有实例：lifecycle readiness / pre-entry capture
                    // candidate 都以 SawMusicScheduled 为前提，因此这里没有合法“稍后再取得”的语义。
                    error = "planet-visual-time-instance-unavailable";
                    return false;
                }
                if (!IsControllerCurrent())
                {
                    error = "planet-visual-time-controller-changed";
                    return false;
                }

                double target = (CreationLeadSteps + (double)absoluteOutputFrameIndex) / frozenOutputFps;
                if (double.IsNaN(target) || double.IsInfinity(target) || target < 0.0)
                {
                    error = "planet-visual-time-target-position-invalid:" +
                            target.ToString("R", CultureInfo.InvariantCulture);
                    return false;
                }

                // 公式后 clamp 到真实 duration；绝不硬编码 endValue（真实 endValue 由游戏决定）。
                if (target < _duration)
                {
                    float expected = (float)target;
                    _instance.Goto(expected, false);
                    if (!TryVerifyOwnedRuntimeState(absoluteOutputFrameIndex, expected, false, out error))
                        return false;

                    LogFrame(absoluteOutputFrameIndex, "positioned",
                        "targetPosition=" + FormatFloat(expected) +
                        " position=" + FormatFloat(_instance.position) +
                        " fullPosition=" + FormatFloat(_instance.fullPosition) +
                        " duration=" + FormatFloat((float)_duration));
                    return true;
                }

                float terminal = (float)_duration;
                if (!_terminalPositioned)
                {
                    // 第一次 targetPosition >= duration：一次性 Goto(duration,false)，
                    // 随后验证当前 runtime 契约（不假定未来版本行为一致）。
                    _instance.Goto(terminal, false);
                    if (!TryVerifyOwnedRuntimeState(absoluteOutputFrameIndex, terminal, true, out error))
                        return false;

                    _terminalPositioned = true;
                    _terminalPosition = terminal;
                    Log.Info("PlanetVisualTimeOwnership terminal-positioned: absoluteOutputFrameIndex=" +
                             absoluteOutputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                             " duration=" + FormatFloat(terminal) +
                             " position=" + FormatFloat(_instance.position) +
                             " fullPosition=" + FormatFloat(_instance.fullPosition) +
                             " target=" + FormatFloat((float)target) +
                             " active=" + (_instance.active ? "true" : "false") +
                             " playing=" + (_instance.IsPlaying() ? "true" : "false") +
                             " sessionGeneration=" + _generation.ToString(CultureInfo.InvariantCulture));
                    return true;
                }

                // 已 terminal-positioned：不重复 Goto，但每帧仍验证实例契约。
                if (!TryVerifyOwnedRuntimeState(absoluteOutputFrameIndex, _terminalPosition, true, out error))
                    return false;

                // 降噪：terminal 段可能跨越大量帧（并跨 pre-entry → gameplay）。这里只在第一次
                // 进入 hold 时记录一行；每帧的契约验证完全保留，违反即 fail-closed。
                if (!_terminalHoldLogged)
                {
                    _terminalHoldLogged = true;
                    Log.Info("PlanetVisualTimeOwnership terminal-hold entered: absoluteOutputFrameIndex=" +
                             absoluteOutputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                             " duration=" + FormatFloat(_terminalPosition) +
                             " position=" + FormatFloat(_instance.position) +
                             " fullPosition=" + FormatFloat(_instance.fullPosition) +
                             " sessionGeneration=" + _generation.ToString(CultureInfo.InvariantCulture) +
                             " (per-frame contract checks continue silently)");
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("PlanetVisualTimeOwnership: Prepare frame 异常", ex);
                error = "planet-visual-time-prepare-exception:" + ex.GetType().Name;
                return false;
            }
        }

        /// <summary>
        /// 每帧 ownership 契约：instance 仍 active、未意外恢复 playing、position 符合本帧期望、
        /// controller 未改变。
        ///
        /// terminal 状态按**已实测**契约校验：`position` 与 `fullPosition` **两者**都必须在统一
        /// tolerance 内等于 duration。当前目标版本、`loops=1` 的这条 Planet Tween 在 30 FPS
        /// （N=11）与 60 FPS（N=26）正式 runtime 上均为 `position == fullPosition == duration == 0.5`；
        /// 该结论**不**推广为 DOTween 通用规则，也不随其它 Tween 形态放宽。
        /// </summary>
        private bool TryVerifyOwnedRuntimeState(
            long absoluteOutputFrameIndex, float expectedPosition, bool terminal, out string error)
        {
            error = null;

            if (_instance == null)
            {
                error = "planet-visual-time-instance-released";
                return false;
            }
            if (!_instance.active)
            {
                error = terminal
                    ? "planet-visual-time-instance-inactive-after-terminal"
                    : "planet-visual-time-instance-inactive";
                return false;
            }
            if (_instance.IsPlaying())
            {
                error = "planet-visual-time-instance-unexpectedly-playing:absoluteOutputFrameIndex=" +
                        absoluteOutputFrameIndex.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            float position = _instance.position;
            float fullPosition = _instance.fullPosition;
            if (!IsFinite(position) || !IsFinite(fullPosition))
            {
                error = "planet-visual-time-instance-position-not-finite:position=" +
                        FormatFloat(position) + ",fullPosition=" + FormatFloat(fullPosition);
                return false;
            }

            if (terminal)
            {
                if (IsAtPosition(position, expectedPosition) && IsAtPosition(fullPosition, expectedPosition))
                    return true;

                error = "planet-visual-time-instance-not-at-duration:expected=" + FormatFloat(expectedPosition) +
                        ",position=" + FormatFloat(position) + ",fullPosition=" + FormatFloat(fullPosition) +
                        ",tolerance=" + FormatFloat(PositionTolerance);
                return false;
            }

            if (!IsAtPosition(position, expectedPosition))
            {
                error = "planet-visual-time-instance-position-mismatch:expected=" + FormatFloat(expectedPosition) +
                        ",position=" + FormatFloat(position) + ",fullPosition=" + FormatFloat(fullPosition);
                return false;
            }
            return true;
        }

        private bool IsControllerCurrent()
        {
            if (_controller == null) return false;
            try { return ReferenceEquals(EditorGameReflection.Controller(), _controller); }
            catch { return false; }
        }

        private void LogFrame(long absoluteOutputFrameIndex, string phase, string detail)
        {
            string message = "PlanetVisualTimeOwnership " + phase + ": absoluteOutputFrameIndex=" +
                             absoluteOutputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                             " sessionGeneration=" + _generation.ToString(CultureInfo.InvariantCulture) +
                             " " + detail;
            if (absoluteOutputFrameIndex <= 1) Log.Info(message);
            else Log.Debug(message);
        }

        // ================================================================
        // fail-closed
        // ================================================================

        /// <summary>
        /// 记录稳定失败原因并转成当前 session 的失败请求。不 throw、不吞异常，
        /// 由 scheduler / controller 的既有终止与 cleanup 收敛路径处理。
        /// </summary>
        private void FailClosed(string reason)
        {
            if (_failed) return;
            _failed = true;
            _failureReason = reason;
            Log.Error("PlanetVisualTimeOwnership fail-closed: " + reason + " state=" + DescribeState());
            _requestSessionFailure?.Invoke(reason);
        }

        // ================================================================
        // cleanup / restore
        // ================================================================

        /// <summary>
        /// 终止路径（normal / cancel / failure / mod disable 共用）：
        ///   1) 先使当前 ownership generation 失效，阻止后续 Prefix/Postfix/Prepare 继续操作实例；
        ///   2) 精确撤销 Prefix/Postfix；
        ///   3) 仅在契约允许时恢复原 playing 状态（绝不 Kill 游戏 Tween）；
        ///   4) 释放 snapshot / controller / Tween 引用。
        /// 返回 false 表示仍有 residual ownership，由调用方（handoff）保留并交给 residual gate。
        /// </summary>
        internal bool TryDispose(out string error)
        {
            error = null;
            bool success = true;

            // 1) 先失效 generation：in-flight 的 Postfix / Prepare 观测到的 generation 已不匹配。
            _generation = 0;
            _playRequested = false;
            _snapshotValid = false;
            if (ReferenceEquals(Active, this)) Active = null;

            // 2) 精确撤销：只撤销本模块注册的 Prefix / Postfix。
            if (_patched)
            {
                try
                {
                    MethodInfo prefix = AccessTools.Method(
                        typeof(PlanetVisualTimeOwnership), nameof(OnMusicScheduledPrefix));
                    MethodInfo postfix = AccessTools.Method(
                        typeof(PlanetVisualTimeOwnership), nameof(OnMusicScheduledPostfix));
                    if (_harmony == null || _method == null || prefix == null || postfix == null)
                    {
                        success = false;
                        error = AppendError(error, "planet-visual-time-unpatch-target-unavailable");
                    }
                    else
                    {
                        _harmony.Unpatch(_method, prefix);
                        _harmony.Unpatch(_method, postfix);
                        _patched = false;
                    }
                }
                catch (Exception ex)
                {
                    success = false;
                    error = AppendError(error, "planet-visual-time-unpatch-failed:" + ex.Message);
                    Log.Exception("PlanetVisualTimeOwnership: Prefix/Postfix 撤销失败", ex);
                }
            }

            // 3) 恢复原 playing 状态（条件全部满足时）。
            if (_instance != null)
            {
                if (!TryRestoreOriginalPlaying(out string restoreError))
                {
                    success = false;
                    error = AppendError(error, restoreError);
                }
                else
                {
                    _instance = null;
                }
            }

            _snapshot.Clear();

            if (_patched)
            {
                // 撤销失败：保留 method 引用，下一次 TryDispose 继续收敛。
                success = false;
                error = AppendError(error, "planet-visual-time-observer-still-patched");
            }
            else
            {
                _method = null;
            }

            if (success)
            {
                _controller = null;
                Log.Info("PlanetVisualTimeOwnership disposed: restoredOriginalPlaying=" +
                         (_originalPlaying ? "true" : "false") +
                         " terminalPositioned=" + (_terminalPositioned ? "true" : "false") +
                         " failureReason=" + (_failureReason ?? "null"));
            }

            return success && !HasResidualOwnership;
        }

        /// <summary>
        /// 只在下列条件全部成立时恢复原始 playing 状态：
        /// 原状态为 playing、仍是同一个 owned instance、instance active、尚未进入
        /// terminal-positioned / completed、controller 仍属于该 session。
        /// 使用最小操作 <c>Play()</c>（不改 position、不 Restart、不 Rewind、不 Kill）。
        /// </summary>
        private bool TryRestoreOriginalPlaying(out string error)
        {
            error = null;

            if (!_originalPlaying)
            {
                Log.Info("PlanetVisualTimeOwnership: 原始状态非 playing，cleanup 不恢复");
                return true;
            }
            if (_terminalPositioned)
            {
                Log.Info("PlanetVisualTimeOwnership: instance 已 terminal-positioned/completed，cleanup 不强行 Play");
                return true;
            }
            if (!_instance.active)
            {
                Log.Info("PlanetVisualTimeOwnership: instance 已非 active（游戏已销毁），cleanup 不强行 Play");
                return true;
            }
            if (!IsControllerCurrent())
            {
                Log.Info("PlanetVisualTimeOwnership: controller 已不属于本 session，cleanup 不恢复");
                return true;
            }

            try
            {
                _instance.Play();
                if (!_instance.IsPlaying())
                {
                    error = "planet-visual-time-restore-play-not-effective";
                    return false;
                }

                Log.Info("PlanetVisualTimeOwnership restored original playing state: position=" +
                         FormatFloat(_instance.position) +
                         " fullPosition=" + FormatFloat(_instance.fullPosition) +
                         " duration=" + FormatFloat((float)_duration) +
                         " terminalPositioned=" + (_terminalPositioned ? "true" : "false"));
                return true;
            }
            catch (Exception ex)
            {
                error = "planet-visual-time-restore-play-failed:" + ex.GetType().Name;
                Log.Exception("PlanetVisualTimeOwnership: 恢复原 playing 状态失败", ex);
                return false;
            }
        }

        // ================================================================
        // 只读工具
        // ================================================================

        private static bool ContainsReference(List<Tween> list, Tween candidate)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], candidate)) return true;
            }
            return false;
        }

        private static object ReadMember(object instance, string name)
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

        private static object ReadFieldInHierarchy(object instance, string name)
        {
            if (instance == null) return null;
            FieldInfo field = FindInstanceFieldInHierarchy(instance.GetType(), name);
            if (field == null) return null;
            try { return field.GetValue(instance); }
            catch { return null; }
        }

        private static string DescribeClosureTarget(Tween tween)
        {
            try
            {
                if (!(tween is TweenerCore<float, float, FloatOptions> tweener) || tweener.getter == null)
                    return "unavailable";
                object closure = tweener.getter.Target;
                return closure == null ? "null" : closure.GetType().FullName;
            }
            catch { return "unavailable"; }
        }

        private static string DescribeMethod(MethodInfo method)
        {
            return method == null ? "null" : method.Name;
        }

        /// <summary>
        /// 只读记录游戏原生 Tween 的运行期配置（不修改任何值）。这些字段多为 DOTween internal，
        /// 只用于验收日志，任何读取失败都退化为 unavailable，不影响 ownership 判定。
        /// </summary>
        private static string DescribeTweenRuntimeContract(Tween tween)
        {
            return "updateType=" + FormatValue(ReadFieldInHierarchy(tween, "updateType")) +
                   " isIndependentUpdate=" + FormatValue(ReadFieldInHierarchy(tween, "isIndependentUpdate")) +
                   " easeType=" + FormatValue(ReadFieldInHierarchy(tween, "easeType")) +
                   " delay=" + FormatValue(ReadFieldInHierarchy(tween, "delay")) +
                   " loops=" + FormatValue(ReadFieldInHierarchy(tween, "loops")) +
                   " autoKill=" + FormatValue(ReadFieldInHierarchy(tween, "autoKill")) +
                   " target=" + FormatValue(tween.target) +
                   " id=" + FormatValue(tween.id);
        }

        private static string FormatValue(object value)
        {
            if (value == null) return "null";
            if (value is float f) return FormatFloat(f);
            if (value is IFormattable formattable)
            {
                try { return formattable.ToString(null, CultureInfo.InvariantCulture); }
                catch { return value.ToString(); }
            }
            return value.ToString();
        }

        private static string FormatFloat(float value)
        {
            if (float.IsNaN(value)) return "NaN";
            if (float.IsInfinity(value)) return value > 0f ? "Infinity" : "-Infinity";
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinitePositive(float value) => IsFinite(value) && value > 0f;

        private static bool IsAtPosition(float observed, float expected)
        {
            return Math.Abs(observed - expected) <= PositionTolerance;
        }

        private static string AppendError(string current, string addition)
        {
            if (string.IsNullOrEmpty(current)) return addition;
            return current + "," + addition;
        }
    }
}
