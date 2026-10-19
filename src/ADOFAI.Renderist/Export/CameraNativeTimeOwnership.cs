using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using ADOFAI.Renderist.Logging;
using HarmonyLib;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 本 session 的**相机原生视觉时间入口** ownership。
    ///
    /// 职责边界（严格单一）：
    ///   * 在本次 Renderist-owned <c>editor.Play()</c> 之前安装 <c>scrCamera</c> 的窄时间替换：
    ///       - <c>scrCamera.Update()</c>：Prefix/Postfix/Finalizer 管调用作用域 + 窄 Transpiler
    ///         替换 3 处 <c>Time.get_deltaTime()</c>（timer / rottimer / coltimer）；
    ///       - <c>scrCamera.UpdateSize()</c>：同一作用域机制 + 替换 1 处 <c>Time.get_deltaTime()</c>
    ///         （pulsetimer）；由 Update 嵌套调用时**共用**该次授权；
    ///       - <c>scrCamera.CheckUpdateFrameRateScreen()</c> / <c>UpdateCustomFrameRateScreen()</c>：
    ///         各替换 1 处 <c>Time.get_time()</c>，使比较与写入使用同一时钟；
    ///   * 从 scheduler 的视觉帧授权读取本帧的受控增量与局部累计视觉时间；
    ///   * cleanup 时先失效 generation，再逐项精确 Unpatch（只撤自己的方法，绝不用 UnpatchAll）。
    ///
    /// **绝不**做的事：不 Patch Unity 全局 <c>Time</c> getter、不临时改全局 <c>timeScale</c>、
    /// 不跳过整个 <c>Update</c>、不保存/恢复视觉字段、不新建 Camera / RenderTexture 管线、
    /// 不拥有 RT / aspect / capture target（那些仍归既有 capture driver）。
    ///
    /// 只替换**已确认的时间调用点**：原分支、Lerp、Ease、Transform 写入、RT 重建、
    /// custom-FPS 的 Render / Blit 与 lock 标志消费全部继续由原代码执行。
    ///
    /// 所有失败路径 fail-closed：不静默继续、不放宽 IL 形态校验，也不 throw 穿出 Unity Update。
    /// </summary>
    internal sealed class CameraNativeTimeOwnership
    {
        /// <summary>处于本 session 生命周期的单例；Harmony 回调从这里取 owner。</summary>
        private static CameraNativeTimeOwnership Active { get; set; }

        /// <summary>每个原生调用独立 push / pop；嵌套 UpdateSize 只共享主 Update 的视觉步。</summary>
        private static readonly List<NativeCallScope> NativeCallScopes = new List<NativeCallScope>();

        private static int _scopeIdCounter;

        /// <summary>
        /// 当前目标 DLL 的 exact 形态（见 PROJECT_UNDERSTANDING / docs）：
        /// Update 3 处 deltaTime、UpdateSize 1 处 deltaTime、
        /// CheckUpdateFrameRateScreen 1 处 time、UpdateCustomFrameRateScreen 1 处 time。
        /// 实际替换数量不等于该期望值即拒绝安装（绝不接受部分替换）。
        /// </summary>
        private static readonly Dictionary<string, int> ExpectedTimeReadCounts = new Dictionary<string, int>
        {
            { UpdateMethodName, 3 },
            { UpdateSizeMethodName, 1 },
            { CheckUpdateFrameRateScreenMethodName, 1 },
            { UpdateCustomFrameRateScreenMethodName, 1 },
        };

        private const string CameraTypeFullName = "scrCamera";

        private const string UpdateMethodName = "Update";
        private const string UpdateSizeMethodName = "UpdateSize";
        private const string CheckUpdateFrameRateScreenMethodName = "CheckUpdateFrameRateScreen";
        private const string UpdateCustomFrameRateScreenMethodName = "UpdateCustomFrameRateScreen";

        /// <summary>
        /// 每个目标方法一个安装记录：目标 MethodInfo、是否已 Patch、Transpiler 名称。
        /// 「安装了一半」意味着其中一部分为 true —— cleanup 必须能逐项撤销已装部分。
        /// </summary>
        private sealed class CameraHook
        {
            internal string MethodName;
            internal MethodInfo Target;
            internal string TranspilerName;
            internal bool Patched;
        }

        private readonly Harmony _harmony;
        private readonly Action<string> _requestSessionFailure;
        private readonly VisualFrameAuthorizer _authorizer;

        private Type _cameraType;
        private CameraHook[] _hooks = new CameraHook[0];
        private object _cameraInstance;
        private long _generation;
        private bool _installed;
        private bool _active;
        private bool _failed;
        private string _failureReason;
        private int _updateScopeIdCounter;

        private double _visualClockBase;
        private double _accumulatedScaledVisualSeconds;
        private double _accumulatedUnscaledVisualSeconds;
        private long _lastProjectedOutputFrameIndex = -1L;
        private bool _hasConsumedAnyFrame;

        /// <summary>
        /// 由 scheduler 在 Play 之前构造。authorizer 是本 session 的视觉帧授权合同；
        /// requestSessionFailure 把 fail-closed 原因转成当前 session 的失败请求。
        /// </summary>
        internal CameraNativeTimeOwnership(
            Harmony harmony, VisualFrameAuthorizer authorizer, Action<string> requestSessionFailure)
        {
            _harmony = harmony ?? throw new ArgumentNullException(nameof(harmony));
            _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
            _requestSessionFailure = requestSessionFailure;
        }

        private enum CameraHookKind
        {
            /// <summary>Prefix/Postfix/Finalizer 管作用域 + 窄 Transpiler。</summary>
            ScopedTranspiler,

            /// <summary>只有窄 Transpiler（纯时间读取，没有调用作用域语义）。</summary>
            TranspilerOnly,
            }

        // ================================================================
        // 安装 / 绑定
        // ================================================================

        /// <summary>
        /// 安装全部窄 Patch。必须在本次 <c>editor.Play()</c> 之前调用。
        /// 安装一半失败时撤销已装部分并返回 false（部分安装的 Patch 绝不允许存活）。
        /// </summary>
        internal bool Begin(out string error)
        {
            error = null;
            if (_installed)
            {
                error = "camera-native-time-already-installed";
                return false;
            }

            try
            {
                _cameraType = AccessTools.TypeByName(CameraTypeFullName);
                if (_cameraType == null)
                {
                    error = "camera-type-unavailable:" + CameraTypeFullName;
                    return false;
                }

                _hooks = new[]
                {
                    new CameraHook
                    {
                        MethodName = UpdateMethodName,
                        TranspilerName = nameof(UpdateTranspiler),
                    },
                    new CameraHook
                    {
                        MethodName = UpdateSizeMethodName,
                        TranspilerName = nameof(UpdateSizeTranspiler),
                    },
                    new CameraHook
                    {
                        MethodName = CheckUpdateFrameRateScreenMethodName,
                        TranspilerName = nameof(CheckUpdateFrameRateScreenTranspiler),
                    },
                    new CameraHook
                    {
                        MethodName = UpdateCustomFrameRateScreenMethodName,
                        TranspilerName = nameof(UpdateCustomFrameRateScreenTranspiler),
                    },
                };

                CameraHookKind[] kinds =
                {
                    CameraHookKind.ScopedTranspiler,
                    CameraHookKind.ScopedTranspiler,
                    CameraHookKind.TranspilerOnly,
                    CameraHookKind.TranspilerOnly,
                };

                // 先解析全部目标：任一缺失即整体拒绝，不进入 Patch 阶段。
                for (int i = 0; i < _hooks.Length; i++)
                {
                    _hooks[i].Target = _cameraType.GetMethod(_hooks[i].MethodName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, Type.EmptyTypes, null);
                    if (_hooks[i].Target == null)
                    {
                        error = "camera-method-unavailable:" + _hooks[i].MethodName;
                        return false;
                    }
                }

                // 逐个登记安装目标与具体 Patch MethodInfo；已装部分必须可撤销。
                for (int i = 0; i < _hooks.Length; i++)
                {
                    CameraHook hook = _hooks[i];
                    if (hook.Target.IsAbstract || hook.Target.ContainsGenericParameters)
                    {
                        error = "camera-method-not-patchable:" + hook.MethodName;
                        TryUnpatchAll(out _);
                        return false;
                    }

                    MethodInfo prefix = AccessTools.Method(
                        typeof(CameraNativeTimeOwnership), hook.MethodName + "Prefix");
                    MethodInfo postfix = AccessTools.Method(
                        typeof(CameraNativeTimeOwnership), hook.MethodName + "Postfix");
                    MethodInfo finalizer = AccessTools.Method(
                        typeof(CameraNativeTimeOwnership), hook.MethodName + "Finalizer");
                    MethodInfo transpiler = AccessTools.Method(
                        typeof(CameraNativeTimeOwnership), hook.TranspilerName);
                    if (transpiler == null)
                    {
                        error = "camera-transpiler-missing:" + hook.TranspilerName;
                        TryUnpatchAll(out _);
                        return false;
                    }

                    HarmonyMethod prefixMethod = null;
                    HarmonyMethod postfixMethod = null;
                    HarmonyMethod finalizerMethod = null;
                    if (kinds[i] == CameraHookKind.ScopedTranspiler)
                    {
                        if (prefix == null || postfix == null || finalizer == null)
                        {
                            error = "camera-hook-method-missing:" + hook.MethodName;
                            TryUnpatchAll(out _);
                            return false;
                        }
                        prefixMethod = new HarmonyMethod(prefix);
                        postfixMethod = new HarmonyMethod(postfix);
                        finalizerMethod = new HarmonyMethod(finalizer);
                    }

                    try
                    {
                        // Patch 抛异常时可能已经写入一部分；此时也标记为已装，交给统一 Unpatch 收敛。
                        hook.Patched = true;
                        if (prefixMethod == null)
                        {
                            _harmony.Patch(hook.Target, transpiler: new HarmonyMethod(transpiler));
                        }
                        else
                        {
                            _harmony.Patch(hook.Target,
                                prefix: prefixMethod,
                                postfix: postfixMethod,
                                transpiler: new HarmonyMethod(transpiler),
                                finalizer: finalizerMethod);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Exception("CameraNativeTimeOwnership: Patch 失败 " + hook.MethodName, ex);
                        error = "camera-patch-failed:" + hook.MethodName + ":" + ex.GetType().Name;
                        TryUnpatchAll(out _);
                        return false;
                    }
                }

                _installed = true;
                Active = this;
                Log.Info("CameraNativeTimeOwnership installed: type=" + CameraTypeFullName +
                         " methods=" + DescribeHooks());
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("CameraNativeTimeOwnership: 安装异常", ex);
                error = "camera-install-exception:" + ex.GetType().Name;
                TryUnpatchAll(out _);
                return false;
            }
        }

        /// <summary>
        /// 绑定本 session 的 generation 与当前 <c>scrCamera</c> 实例，并记录原生时间锚点。
        /// 必须在 <c>editor.Play()</c> 之前调用（与 Planet ownership 的 Arm 同一时机）。
        /// </summary>
        internal bool Arm(long generation, out string error)
        {
            error = null;
            if (!_installed)
            {
                error = "camera-native-time-not-installed";
                return false;
            }
            if (generation == 0)
            {
                error = "camera-native-time-generation-invalid";
                return false;
            }

            try
            {
                object instance = ReadCameraInstance();
                if (instance == null)
                {
                    error = "camera-instance-unavailable";
                    return false;
                }

                _cameraInstance = instance;
                _generation = generation;
                _failed = false;
                _failureReason = null;
                _updateScopeIdCounter = 0;
                NativeCallScopes.Clear();
                _accumulatedScaledVisualSeconds = 0.0;
                _accumulatedUnscaledVisualSeconds = 0.0;
                _lastProjectedOutputFrameIndex = -1L;
                _hasConsumedAnyFrame = false;
                // 原生时间锚点：局部绝对视觉时间从这里加 scheduler 授权累计值投影，
                // **不**在每个 Unity Update 累加。
                _visualClockBase = ReadNativeTime();
                _active = true;

                Log.Info("CameraNativeTimeOwnership armed: sessionGeneration=" +
                         generation.ToString(CultureInfo.InvariantCulture) +
                         " cameraInstance=" + instance.GetType().FullName +
                         " instanceId=" + DescribeInstanceId(instance) +
                         " nativeTimeAnchor=" + _visualClockBase.ToString("R", CultureInfo.InvariantCulture));
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("CameraNativeTimeOwnership: Arm 异常", ex);
                error = "camera-arm-exception:" + ex.GetType().Name;
                return false;
            }
        }

        /// <summary>是否仍持有需要收敛的 ownership（供 residual gate 判定）。</summary>
        internal bool HasResidualOwnership
        {
            get
            {
                if (!_installed) return false;
                for (int i = 0; i < _hooks.Length; i++)
                {
                    if (_hooks[i].Patched) return true;
                }
                return false;
            }
        }

        internal bool IsActive => _active && _installed;

        internal long Generation => _generation;

        internal string DescribeState()
        {
            return "installed=" + (_installed ? "true" : "false") +
                   ",active=" + (_active ? "true" : "false") +
                   ",generation=" + _generation.ToString(CultureInfo.InvariantCulture) +
                   ",patched=" + DescribeHooks() +
                   ",accumulatedScaled=" + _accumulatedScaledVisualSeconds.ToString("R", CultureInfo.InvariantCulture) +
                   ",accumulatedUnscaled=" + _accumulatedUnscaledVisualSeconds.ToString("R", CultureInfo.InvariantCulture) +
                   ",lastProjectedFrame=" + _lastProjectedOutputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                   ",consumedAnyFrame=" + (_hasConsumedAnyFrame ? "true" : "false") +
                   ",failed=" + (_failed ? "true" : "false") +
                   ",failureReason=" + (_failureReason ?? "null");
        }

        // ================================================================
        // 时间入口（由 Transpiler 注入的调用点调用）
        // ================================================================

        /// <summary>
        /// <c>Time.get_deltaTime()</c> 的替换实现。
        ///
        /// 语义（按 session 生命周期状态显式区分，不靠宽泛忽略错误）：
        ///   * 没有活动 owner → 读**原生** <c>Time.deltaTime</c>（非导出期间行为完全不变）；
        ///   * owner 已失效 / 已清理 → 同样读原生值；
        ///   * **合法等待窗口**（首帧之前的 bootstrap、EOF 之后的交付 / Finalizing 等待）→
        ///     返回 0 增量，不消费、不重复计时，**不把合法等待判成 session 失败**；
        ///   * 本帧已失败 → 返回 0（不得重试）；
        ///   * 活动 owner 且本帧授权有效 → 本帧受控 scaled 增量
        ///     （同一作用域内的嵌套调用复用同一次授权）。
        ///
        /// 真正越相位（有授权但实例不符、或作用域与授权帧不一致）仍然 fail-closed。
        /// </summary>
        internal static float GetCameraDeltaTime(object cameraInstance)
        {
            CameraNativeTimeOwnership owner = Active;
            if (owner == null || !owner._active || !owner._installed)
            {
                return UnityEngine.Time.deltaTime;
            }

            try
            {
                VisualFrameAuthorization authorization;
                VisualSessionVisualState state;
                owner._authorizer.TryPeekCurrentState(out authorization, out state);

                if (!owner.OwnsInstance(cameraInstance))
                {
                    owner.FailClosed("camera-delta-instance-mismatch:instance=" +
                                     DescribeInstanceId(cameraInstance) +
                                     ",sessionState=" + state);
                    return 0f;
                }

                if (owner._failed || state == VisualSessionVisualState.Inactive ||
                    state == VisualSessionVisualState.Failed)
                {
                    // 本帧已被其它路径判失败：不得重试消费。
                    return 0f;
                }

                NativeCallScope scope = CurrentScope();
                if (scope == null)
                {
                    if (state == VisualSessionVisualState.Waiting) return 0f;
                    // 活动授权下缺少 Prefix 作用域是接线错误；独立 UpdateSize 另有零步作用域。
                    owner.FailClosed("camera-delta-out-of-phase:no-native-call-scope:instance=" +
                                     DescribeInstanceId(cameraInstance));
                    return 0f;
                }

                if (!ReferenceEquals(scope.Owner, owner) || scope.Key.Generation != owner._generation)
                {
                    owner.FailClosed("camera-delta-stale-native-scope");
                    return 0f;
                }

                // 独立 UpdateSize 只做原生求值，不能领取 Camera 槽位或写入回执。
                // 嵌套 UpdateSize 有自己的调用 token，但时间授权属于其主 Update。
                NativeCallScope stepScope = scope.VisualStepScope;
                if (stepScope == null) return 0f;
                if (state == VisualSessionVisualState.Waiting &&
                    stepScope.StateAtOpen == VisualSessionVisualState.Waiting)
                    return 0f;
                if (state != VisualSessionVisualState.Authorized ||
                    stepScope.StateAtOpen != VisualSessionVisualState.Authorized)
                {
                    owner.FailClosed("camera-authorization-changed-within-scope");
                    return 0f;
                }

                double unscaled;
                double scaled;
                string error;
                VisualFrameConsumeOutcome outcome =
                    owner._authorizer.TryConsume(stepScope.Key, out unscaled, out scaled, out error);

                switch (outcome)
                {
                    case VisualFrameConsumeOutcome.Consumed:
                        stepScope.Claimed = true;
                        stepScope.DeltaScaled = (float)scaled;
                        owner.Accumulate(stepScope, scaled, unscaled);
                        return stepScope.DeltaScaled;

                    case VisualFrameConsumeOutcome.NestedInScope:
                        // 每次读取均校验完整 key；不使用可能已跨 EOF / generation 的缓存增量。
                        stepScope.Claimed = true;
                        stepScope.DeltaScaled = (float)scaled;
                        return stepScope.DeltaScaled;

                    case VisualFrameConsumeOutcome.NoVisualStep:
                        // bootstrap / sealed / frame 0：明确不授予视觉步，返回 0 且不改变累计。
                        stepScope.Claimed = true;
                        stepScope.DeltaScaled = 0f;
                        return 0f;

                    case VisualFrameConsumeOutcome.FrameFailed:
                        // 本帧已经失败：不重复报错，只是不再推进任何时间。
                        return 0f;

                    default:
                        owner.FailClosed("camera-delta-consume-" + outcome + ":" + (error ?? "unknown"));
                        return 0f;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("CameraNativeTimeOwnership: GetCameraDeltaTime 异常", ex);
                owner.FailClosed("camera-delta-exception:" + ex.GetType().Name);
                return 0f;
            }
        }

        /// <summary>
        /// <c>Time.get_time()</c> 的替换实现，用于 native custom-FPS 检查。
        ///
        /// 返回类型必须与 Unity 的 <c>Time.time</c> **完全一致**（<c>float</c>）：替换后的
        /// 指令流里仍然存在 <c>conv.r8</c> 等转换，类型一旦不同就会改变语义。内部用 double
        /// 累计，只在返回时投影成 float。
        ///
        /// 活动 owner 下返回**同一时钟**的局部绝对视觉时间（原生锚点 + scheduler 授权累计
        /// scaled 视觉步），使 <c>UpdateCustomFrameRateScreen</c> 的写入与
        /// <c>CheckUpdateFrameRateScreen</c> 的比较使用一致的时间；否则读原生 <c>Time.time</c>。
        ///
        /// **不消费**任何授权，因此它在等待窗口（hidden / EOF 后等待 / bootstrap）里
        /// 也安全可用：时钟保持在最近一次真实消费的投影值上，不会往前跳。
        /// </summary>
        internal static float GetCameraVisualTime(object cameraInstance)
        {
            CameraNativeTimeOwnership owner = Active;
            if (owner == null || !owner._active || !owner._installed)
            {
                return UnityEngine.Time.time;
            }

            if (!owner.OwnsInstance(cameraInstance))
            {
                // 不 fail-closed：这不是时间消费，只是时钟读取；不属于本 owner 的实例
                // 保持原生语义，绝不因为一次只读调用中断导出。
                return UnityEngine.Time.time;
            }

            VisualFrameAuthorization authorization;
            VisualSessionVisualState state;
            owner._authorizer.TryPeekCurrentState(out authorization, out state);
            if (state == VisualSessionVisualState.Inactive)
            {
                // 本 session 的授权已失效：不再声称拥有局部时钟。
                return UnityEngine.Time.time;
            }

            return (float)owner.ProjectedVisualTime;
        }

        private double ProjectedVisualTime => _visualClockBase + _accumulatedScaledVisualSeconds;

        private void Accumulate(NativeCallScope scope, double scaled, double unscaled)
        {
            if (scope.Accumulated) return;
            scope.Accumulated = true;
            _accumulatedScaledVisualSeconds += scaled;
            _accumulatedUnscaledVisualSeconds += unscaled;
            _lastProjectedOutputFrameIndex = scope.AbsoluteOutputFrameIndex;
            _hasConsumedAnyFrame = true;
        }

        // ================================================================
        // 原生调用作用域（Harmony __state）
        // ================================================================

        /// <summary>
        /// 一次原生调用的作用域记录。它由 Prefix 建立、Postfix 只在原方法正常返回时关闭、
        /// Finalizer 无条件幂等关闭。
        /// </summary>
        internal sealed class NativeCallScope
        {
            internal CameraNativeTimeOwnership Owner;
            internal bool IsUpdate;
            internal VisualSessionVisualState StateAtOpen;
            internal NativeCallScope VisualStepScope;
            /// <summary>本作用域的消费 key；由 owner 在开启时用当前授权帧与当前原生帧构成。</summary>
            internal VisualFrameConsumptionKey Key;
            internal bool Claimed;
            internal bool Accumulated;
            internal float DeltaScaled;
            internal bool Closed;

            internal int ScopeId => Key.ScopeId;
            internal long NativeFrame => Key.NativeFrame;
            internal long AbsoluteOutputFrameIndex => Key.AbsoluteOutputFrameIndex;
        }

        private static NativeCallScope CurrentScope()
        {
            if (NativeCallScopes.Count == 0) return null;
            NativeCallScope scope = NativeCallScopes[NativeCallScopes.Count - 1];
            return scope != null && !scope.Closed ? scope : null;
        }

        private static void PushScope(NativeCallScope scope)
        {
            NativeCallScopes.Add(scope);
        }

        private static void RemoveScope(NativeCallScope scope)
        {
            for (int i = NativeCallScopes.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(NativeCallScopes[i], scope))
                {
                    NativeCallScopes.RemoveAt(i);
                    return;
                }
            }
        }

        internal bool TryOpenScope(long nativeFrame, bool isUpdate, out NativeCallScope scope, out string error)
        {
            error = null;
            scope = null;

            if (!_active)
            {
                error = "camera-owner-inactive";
                return false;
            }

            NativeCallScope current = CurrentScope();
            if (current != null)
            {
                if (!ReferenceEquals(current.Owner, this) || current.NativeFrame != nativeFrame)
                {
                    error = "camera-native-frame-changed-within-scope:open=" +
                            current.NativeFrame.ToString(CultureInfo.InvariantCulture) + ",observed=" +
                            nativeFrame.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                if (isUpdate)
                {
                    error = "camera-update-reentered";
                    return false;
                }
            }

            _updateScopeIdCounter++;
            if (_updateScopeIdCounter <= 0) _updateScopeIdCounter = 1;
            _scopeIdCounter++;
            if (_scopeIdCounter <= 0) _scopeIdCounter = 1;

            _authorizer.TryPeekCurrentState(out VisualFrameAuthorization authorization,
                out VisualSessionVisualState state);
            scope = new NativeCallScope
            {
                Owner = this,
                IsUpdate = isUpdate,
                StateAtOpen = state,
                // 本帧授权对应的 absolute output frame index 由 authorizer 提供：
                // owner **不**自行发明 frame authority，也不读 scheduler 内部计数。
                // 授权尚未发布或无匹配帧时为 -1，消费端会给出具体的 out-of-phase 错误。
                Key = new VisualFrameConsumptionKey(
                    VisualConsumerSlot.Camera, _generation,
                    state == VisualSessionVisualState.Authorized ? authorization.AbsoluteOutputFrameIndex : -1L,
                    nativeFrame, _scopeIdCounter),
            };
            scope.VisualStepScope = isUpdate ? scope : current?.VisualStepScope;
            PushScope(scope);
            return true;
        }

        private bool IsCurrentSessionScope(NativeCallScope scope)
        {
            return scope != null && ReferenceEquals(scope.Owner, this) && _active &&
                   scope.Key.Generation == _generation && _authorizer.IsArmed &&
                   _authorizer.Generation == _generation;
        }

        /// <summary>
        /// Postfix 路径：原方法正常返回，若本作用域确实领取过授权则记回执。
        /// 返回 false 只表示"回执无法登记"，调用方据此 fail-closed。
        /// </summary>
        internal bool TryCompleteScope(NativeCallScope scope, out string error)
        {
            error = null;
            if (scope == null) return true;
            if (!IsCurrentSessionScope(scope)) return true; // 迟到的旧 session 回调仅负责清理自己的 token。
            if (!scope.IsUpdate) return true;

            _authorizer.TryPeekCurrentState(out _, out VisualSessionVisualState state);
            if (scope.StateAtOpen == VisualSessionVisualState.Waiting &&
                state == VisualSessionVisualState.Waiting && !_failed)
                return true; // 合法 Waiting：没有消费，也没有授权回执需要完成。

            if (!scope.Claimed)
            {
                // 原方法正常返回但从未读取 deltaTime：本帧没有 Camera 时间消费。
                // Authorized 下仍严格要求消费，即便 frame 0 / lifecycle-only 的增量为 0。
                error = "camera-scope-closed-without-consumption:nativeFrame=" +
                        scope.NativeFrame.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            return _authorizer.TryCompleteScope(scope.Key, out error);
        }

        /// <summary>Finalizer 路径：无条件幂等关闭作用域；原方法抛异常时把本帧标记为失败。</summary>
        internal void CloseScopeAfterException(NativeCallScope scope, Exception exception)
        {
            if (!IsCurrentSessionScope(scope)) return;

            string reason = "camera-native-update-exception:" +
                            (exception == null ? "unknown" : exception.GetType().Name);
            // 包括 Waiting / 独立或嵌套 UpdateSize 的异常：不能依赖未必会到来的 EOF 才停止。
            FailClosed(reason);
            // 保留原始异常：本方法不返回 null，也不吞掉游戏错误。
        }

        /// <summary>pop 出栈（Postfix 与 Finalizer 共用，幂等）。在**任何**返回路径上都要调用。</summary>
        internal static void CloseScope(NativeCallScope scope)
        {
            if (scope == null || scope.Closed) return;
            scope.Closed = true;
            RemoveScope(scope);
        }

        // ================================================================
        // fail-closed / cleanup
        // ================================================================

        /// <summary>
        /// 记录稳定失败原因并转成当前 session 的失败请求；幂等。
        /// 只登记停止请求，不在这里 cleanup（统一由 scheduler 的 ProcessStop / RestoreAll 收敛）。
        /// </summary>
        internal void FailClosed(string reason)
        {
            if (_failed) return;
            _failed = true;
            _failureReason = reason;
            if (_active && _authorizer.IsArmed && _authorizer.Generation == _generation)
                _authorizer.FailFrame(VisualConsumerSlot.Camera, reason);
            Log.Error("CameraNativeTimeOwnership fail-closed: " + reason + " state=" + DescribeState());
            _requestSessionFailure?.Invoke(reason);
        }

        /// <summary>
        /// 终止路径（normal / cancel / failure / mod disable 共用）：
        ///   1) 先失效（_active=false）并清空作用域栈，阻止 in-flight 回调继续消费；
        ///   2) 逐项精确 Unpatch（只撤自己注册的方法）；
        ///   3) 释放实例引用。
        /// 返回 false 表示仍有 residual ownership，由调用方保留并交给 residual gate。
        /// </summary>
        internal bool TryDispose(out string error)
        {
            error = null;
            bool success = true;

            _active = false;
            _generation = 0;
            NativeCallScopes.Clear();
            if (ReferenceEquals(Active, this)) Active = null;

            if (!TryUnpatchAll(out string unpatchError))
            {
                success = false;
                error = AppendError(error, unpatchError);
            }

            if (!HasResidualOwnership)
            {
                _installed = false;
                _cameraInstance = null;
                _hooks = new CameraHook[0];
                _cameraType = null;
            }
            else
            {
                success = false;
                error = AppendError(error, "camera-hooks-still-patched");
            }

            if (success)
            {
                Log.Info("CameraNativeTimeOwnership disposed: failureReason=" + (_failureReason ?? "null"));
            }

            return success && !HasResidualOwnership;
        }

        private bool TryUnpatchAll(out string error)
        {
            error = null;
            if (_hooks == null || _hooks.Length == 0) return true;

            bool success = true;
            // 逆序撤销，与安装顺序对称。
            for (int i = _hooks.Length - 1; i >= 0; i--)
            {
                CameraHook hook = _hooks[i];
                if (!hook.Patched) continue;

                try
                {
                    if (_harmony == null || hook.Target == null)
                    {
                        success = false;
                        error = AppendError(error, "camera-unpatch-target-unavailable:" + hook.MethodName);
                        continue;
                    }

                    MethodInfo prefix = AccessTools.Method(
                        typeof(CameraNativeTimeOwnership), hook.MethodName + "Prefix");
                    MethodInfo postfix = AccessTools.Method(
                        typeof(CameraNativeTimeOwnership), hook.MethodName + "Postfix");
                    MethodInfo finalizer = AccessTools.Method(
                        typeof(CameraNativeTimeOwnership), hook.MethodName + "Finalizer");
                    MethodInfo transpiler = AccessTools.Method(
                        typeof(CameraNativeTimeOwnership), hook.TranspilerName);

                    // 只撤自己的方法；任何一项缺失都视为未收敛（不掩盖）。
                    bool complete = transpiler != null;
                    if (complete) _harmony.Unpatch(hook.Target, transpiler);
                    if (prefix != null)
                    {
                        _harmony.Unpatch(hook.Target, prefix);
                    }
                    if (postfix != null)
                    {
                        _harmony.Unpatch(hook.Target, postfix);
                    }
                    if (finalizer != null)
                    {
                        _harmony.Unpatch(hook.Target, finalizer);
                    }

                    hook.Patched = false;
                }
                catch (Exception ex)
                {
                    success = false;
                    error = AppendError(error, "camera-unpatch-failed:" + hook.MethodName + ":" + ex.GetType().Name);
                    Log.Exception("CameraNativeTimeOwnership: Unpatch 失败 " + hook.MethodName, ex);
                }
            }

            return success;
        }

        private static string DescribeInstanceId(object instance)
        {
            if (instance == null) return "null";
            try
            {
                UnityEngine.Object unityObject = instance as UnityEngine.Object;
                if (unityObject != null)
                {
                    return unityObject.GetInstanceID().ToString(CultureInfo.InvariantCulture);
                }
            }
            catch
            {
            }
            return "n/a";
        }

        private bool OwnsInstance(object instance)
        {
            if (instance == null || _cameraInstance == null) return false;
            return ReferenceEquals(instance, _cameraInstance);
        }

        private static object ReadCameraInstance()
        {
            try
            {
                Type cameraType = AccessTools.TypeByName(CameraTypeFullName);
                if (cameraType == null) return null;
                PropertyInfo property = cameraType.GetProperty("instance",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null) return property.GetValue(null, null);
                FieldInfo field = cameraType.GetField("_instance",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                return field?.GetValue(null);
            }
            catch (Exception ex)
            {
                Log.Exception("CameraNativeTimeOwnership: scrCamera.instance 读取失败", ex);
                return null;
            }
        }

        /// <summary>仅用于锚点的一次性原生读取；不在每帧被调用。</summary>
        private static double ReadNativeTime()
        {
            return UnityEngine.Time.time;
        }

        private string DescribeHooks()
        {
            if (_hooks == null || _hooks.Length == 0) return "none";
            string text = "";
            for (int i = 0; i < _hooks.Length; i++)
            {
                text += (text.Length == 0 ? "" : ",") + _hooks[i].MethodName + "=" +
                        (_hooks[i].Patched ? "patched" : "pending");
            }
            return text;
        }

        private static string AppendError(string current, string addition)
        {
            if (string.IsNullOrEmpty(addition)) return current;
            return string.IsNullOrEmpty(current) ? addition : current + "," + addition;
        }

        // ================================================================
        // Harmony：Prefix / Postfix / Finalizer
        // ================================================================

        private static void UpdatePrefix(object __instance, out NativeCallScope __state)
        {
            OpenScope(__instance, true, out __state);
        }

        private static void UpdatePostfix(NativeCallScope __state)
        {
            CompleteScope(__state);
        }

        private static void UpdateFinalizer(Exception __exception, NativeCallScope __state)
        {
            FinalizeScope(__state, __exception);
        }

        private static void UpdateSizePrefix(object __instance, out NativeCallScope __state)
        {
            OpenScope(__instance, false, out __state);
        }

        private static void UpdateSizePostfix(NativeCallScope __state)
        {
            CompleteScope(__state);
        }

        private static void UpdateSizeFinalizer(Exception __exception, NativeCallScope __state)
        {
            FinalizeScope(__state, __exception);
        }

        private static void OpenScope(object instance, bool isUpdate, out NativeCallScope __state)
        {
            __state = null;
            CameraNativeTimeOwnership owner = Active;
            if (owner == null || !owner._active || !owner._installed) return;

            try
            {
                NativeCallScope scope;
                string error;
                if (!owner.OwnsInstance(instance))
                {
                    owner.FailClosed("camera-scope-instance-mismatch");
                    return;
                }
                if (!owner.TryOpenScope(UnityEngine.Time.frameCount, isUpdate, out scope, out error))
                {
                    // 作用域无法建立：不注册 __state，调用点会读到 0 增量并 fail-closed。
                    owner.FailClosed(error ?? "camera-scope-open-failed");
                    return;
                }

                __state = scope;
            }
            catch (Exception ex)
            {
                Log.Exception("CameraNativeTimeOwnership: Prefix 异常", ex);
                owner.FailClosed("camera-scope-prefix-exception:" + ex.GetType().Name);
                __state = null;
            }
        }

        private static void CompleteScope(NativeCallScope __state)
        {
            CameraNativeTimeOwnership owner = __state?.Owner;

            try
            {
                if (owner != null && owner.IsCurrentSessionScope(__state))
                {
                    string error;
                    if (!owner.TryCompleteScope(__state, out error))
                    {
                        owner.FailClosed("camera-scope-complete-failed:" + (error ?? "unknown"));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("CameraNativeTimeOwnership: Postfix 异常", ex);
                owner.FailClosed("camera-scope-postfix-exception:" + ex.GetType().Name);
            }
            finally
            {
                CloseScope(__state);
            }
        }

        private static void FinalizeScope(NativeCallScope __state, Exception __exception)
        {
            CameraNativeTimeOwnership owner = __state?.Owner;
            try
            {
                if (__state == null) return;
                if (__exception != null && owner != null)
                {
                    owner.CloseScopeAfterException(__state, __exception);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("CameraNativeTimeOwnership: Finalizer 异常", ex);
            }
            finally
            {
                // 无论原方法还是其它 Patch 抛错，都幂等关闭作用域；绝不返回 null 吞掉原始异常。
                CloseScope(__state);
            }
        }

        // ================================================================
        // Harmony：窄 Transpiler
        // ================================================================

        /// <summary>
        /// <c>scrCamera.Update</c>：只替换 3 处 <c>Time.get_deltaTime()</c>（timer / rottimer / coltimer），
        /// 且每一处都必须处于已确认的形态中：
        /// <code>
        ///   ldarg.0 ; ldarg.0 ; ldfld scrCamera::&lt;timer&gt; ; call Time::get_deltaTime ; add ; stfld scrCamera::&lt;timer&gt;
        /// </code>
        /// 任何一处不匹配即整体拒绝安装（绝不接受"部分替换"）。
        /// </summary>
        internal static IEnumerable<CodeInstruction> UpdateTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceTimeReads(instructions, UpdateMethodName, TimeCallKind.DeltaTime,
                new[] { "timer", "rottimer", "coltimer" });
        }

        /// <summary>
        /// <c>scrCamera.UpdateSize</c>：只替换 1 处 <c>Time.get_deltaTime()</c>（pulsetimer）。
        /// </summary>
        internal static IEnumerable<CodeInstruction> UpdateSizeTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceTimeReads(instructions, UpdateSizeMethodName, TimeCallKind.DeltaTime,
                new[] { "pulsetimer" });
        }

        /// <summary>
        /// <c>scrCamera.CheckUpdateFrameRateScreen</c>：只替换 1 处 <c>Time.get_time()</c>，
        /// 它必须紧跟在 <c>ldfld scrCamera::lastCustomFPSUpdate</c> 之前被读取的形态中。
        /// lock 标志的消费与原有分支完全保留。
        /// </summary>
        internal static IEnumerable<CodeInstruction> CheckUpdateFrameRateScreenTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceTimeReads(instructions, CheckUpdateFrameRateScreenMethodName, TimeCallKind.Time,
                new[] { "lastCustomFPSUpdate", "enableCustomFPS", "lockCustomFrameUpdate" });
        }

        /// <summary>
        /// <c>scrCamera.UpdateCustomFrameRateScreen</c>：只替换 1 处 <c>Time.get_time()</c>，
        /// 使 <c>lastCustomFPSUpdate</c> 的写入与比较使用同一时钟。
        /// </summary>
        internal static IEnumerable<CodeInstruction> UpdateCustomFrameRateScreenTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceTimeReads(instructions, UpdateCustomFrameRateScreenMethodName, TimeCallKind.Time,
                new[] { "lastCustomFPSUpdate" });
        }

        private enum TimeCallKind
        {
            DeltaTime,
            Time,
        }

        /// <summary>
        /// 窄时间替换的唯一实现。返回 null 表示 **IL 形态不符**：调用方（Harmony 的
        /// Transpiler 包装）会把它变成异常，安装被拒绝，绝不安装一个部分替换的 Patch。
        /// </summary>
        private static IEnumerable<CodeInstruction> ReplaceTimeReads(
            IEnumerable<CodeInstruction> instructions, string targetMethodName, TimeCallKind kind,
            string[] allowedFieldNames)
        {
            var source = new List<CodeInstruction>(instructions);
            MethodInfo timeGetter = kind == TimeCallKind.DeltaTime
                ? AccessTools.Method(typeof(UnityEngine.Time), "get_deltaTime")
                : AccessTools.Method(typeof(UnityEngine.Time), "get_time");
            MethodInfo helper = kind == TimeCallKind.DeltaTime
                ? AccessTools.Method(typeof(CameraNativeTimeOwnership), nameof(GetCameraDeltaTime))
                : AccessTools.Method(typeof(CameraNativeTimeOwnership), nameof(GetCameraVisualTime));

            if (timeGetter == null || helper == null)
            {
                throw new InvalidOperationException(
                    "camera-transpiler-target-unavailable:" + targetMethodName);
            }

            var result = new List<CodeInstruction>(source.Count + 4);
            int replaced = 0;
            // 逐点记录本次替换的求值栈论据，作为安装日志的一部分（可被实机日志核对）。
            var siteEvidence = new List<string>();

            for (int i = 0; i < source.Count; i++)
            {
                CodeInstruction current = source[i];
                if (!IsCallTo(current, timeGetter))
                {
                    result.Add(current);
                    continue;
                }

                // 形态校验：时间读取两侧必须仍是已确认的相邻指令。任何不匹配都 fail-closed，
                // 绝不接受部分替换。刻意**不**要求"紧邻的操作数就是某个具体字段名"：
                // 当前 DLL 的 CheckUpdateFrameRateScreen 在字段读取与时间读取之间还有一条
                // brfalse.s；但也不放宽到"方法里出现过字段名即可"（见 IsExpectedIncident）。
                CodeInstruction previous = result.Count > 0 ? result[result.Count - 1] : null;
                CodeInstruction next = i + 1 < source.Count ? source[i + 1] : null;
                if (!IsExpectedIncident(previous, allowedFieldNames) ||
                    !IsExpectedIncident(next, allowedFieldNames))
                {
                    throw new InvalidOperationException(
                        "camera-transpiler-il-shape-mismatch:" + targetMethodName +
                        ":site=" + replaced.ToString(CultureInfo.InvariantCulture) +
                        ":before=" + Describe(previous) + ":after=" + Describe(next));
                }

                // **求值栈审计（逐点，不是字符串检查）**
                //
                // `Time.get_deltaTime` / `Time.get_time` 是 Unity 的 **static** 属性 getter：
                // 当前 DLL 的方法签名是 `static float get_X()`，即它们**消费 0 个操作数、
                // 产出 1 个值**。原生 `this` 只是最近邻的 ldarg.0 留下的栈内容，并不是 getter
                // 的接收者。
                //
                // 替换 helper 是 `static float Helper(object)`：消费 1 个、产出 1 个。
                // 因此**必须显式压入实例**，否则 helper 会消费栈顶那个不相干的字段值，
                // 既拿不到相机实例，也会让后面的 add/stfld 少一个操作数（栈失衡）。
                //
                // 正确替换 = `ldarg.0; call Helper`：净效果由 (0→1) 变成 (-1→+1)+1 = 1，
                // 与 getter 相同，因此原字段写入 / 算术 / conv 的操作数全部不变。
                if (!TryAuditReceiver(source, i, allowedFieldNames, out string auditDetail))
                {
                    throw new InvalidOperationException(
                        "camera-transpiler-stack-audit-failed:" + targetMethodName +
                        ":site=" + replaced.ToString(CultureInfo.InvariantCulture) + ":" + auditDetail);
                }

                var loadInstance = new CodeInstruction(OpCodes.Ldarg_0);
                var replacement = new CodeInstruction(OpCodes.Call, helper);
                // 原 call 上的**分支标签必须落在压栈之前的第一条指令上**：
                // 若落在 helper 调用上，跳转会跳过 ldarg.0，helper 就读到错误的栈顶。
                loadInstance.labels.AddRange(current.labels);
                result.Add(loadInstance);
                result.Add(replacement);

                siteEvidence.Add("site" + replaced.ToString(CultureInfo.InvariantCulture) +
                                 "@il" + i.ToString(CultureInfo.InvariantCulture) +
                                 "(" + auditDetail + ")");
                replaced++;
            }

            int expected = ExpectedTimeReadCounts.TryGetValue(targetMethodName, out int count) ? count : -1;
            if (expected < 0 || replaced != expected)
            {
                throw new InvalidOperationException(
                    "camera-transpiler-time-read-count-mismatch:" + targetMethodName +
                    ":expected=" + expected.ToString(CultureInfo.InvariantCulture) +
                    ":replaced=" + replaced.ToString(CultureInfo.InvariantCulture));
            }

            Log.Info("CameraNativeTimeOwnership transpiler applied: " + targetMethodName +
                     " kind=" + kind +
                     " replaced=" + replaced.ToString(CultureInfo.InvariantCulture) +
                     " allowedFields=" + string.Join(",", allowedFieldNames) +
                     " stackAudit=[" + string.Join(";", siteEvidence) + "]");
            return result;
        }

        private static bool IsCallTo(CodeInstruction instruction, MethodInfo method)
        {
            return instruction != null &&
                   (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
                   Equals(instruction.operand, method);
        }

        private static bool IsLdarg0(CodeInstruction instruction)
        {
            return instruction != null &&
                   (instruction.opcode == OpCodes.Ldarg_0 ||
                    (instruction.opcode == OpCodes.Ldarg && instruction.operand is int index && index == 0));
        }

        /// <summary>
        /// 逐点求值栈审计。
        ///
        /// `Time.get_deltaTime` / `Time.get_time` 在当前 DLL 里是 **static** getter
        /// （`static float get_X()`），因此它们消费 0 个操作数、产出 1 个值。替换 helper 消费 1 个
        /// `object` 参数，所以必须显式压入 `this`。审计因此要求：
        ///   * 目标方法是**实例**方法（`ldarg.0` 才是 `this`）；
        ///   * 时间读取原点的前一条指令处于已确认的形态（字段读取 / 实例方法入口等），
        ///     证明它不在一个"栈上已有未知内容"的位置；
        ///   * 通过后续的 `IsExpectedIncident` 检查确认该点仍属于已确认的累计 / 比较链。
        ///
        /// 这里不做"栈上是否已有 this"的假设 —— 恰恰因为 getter 是 static，栈上有没有 `this`
        /// 与它的正确性无关；我们**主动压入**，把语义从"隐式依赖"变成"显式构造"。
        /// </summary>
        private static bool TryAuditReceiver(
            List<CodeInstruction> source, int callIndex, string[] allowedFieldNames, out string detail)
        {
            if (callIndex <= 0 || callIndex >= source.Count)
            {
                detail = "call-site-out-of-range:" + callIndex.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            CodeInstruction previous = source[callIndex - 1];
            if (previous == null)
            {
                detail = "no-preceding-instruction";
                return false;
            }

            FieldInfo field = previous.operand as FieldInfo;
            if (field != null && previous.opcode == OpCodes.Ldfld)
            {
                bool allowed = false;
                for (int i = 0; i < allowedFieldNames.Length; i++)
                {
                    if (string.Equals(field.Name, allowedFieldNames[i], StringComparison.Ordinal))
                    {
                        allowed = true;
                        break;
                    }
                }
                if (!allowed)
                {
                    detail = "preceding-field-not-allowlisted:" + field.Name;
                    return false;
                }
                detail = "preceding=ldfld " + field.Name + ",getter=static0,helper=static1,pushThis=true";
                return true;
            }

            // custom-FPS 形态：读取紧跟 `ldarg.0`（写入链）或 `brfalse.s`（比较链）。
            if (IsLdarg0(previous) ||
                previous.opcode == OpCodes.Brfalse_S || previous.opcode == OpCodes.Brtrue_S ||
                previous.opcode == OpCodes.Nop)
            {
                detail = "preceding=" + previous.opcode +
                         ",getter=static0,helper=static1,pushThis=true";
                return true;
            }

            detail = "unexpected-preceding-instruction:" + Describe(previous);
            return false;
        }

        /// <summary>
        /// 指令的操作数栈效果。只覆盖目标方法里实际出现 / 形态校验放行的指令；
        /// 任何未知形态都返回 false（审计失败 ⇒ 拒绝安装），绝不做乐观假设。
        /// </summary>
        /// <summary>
        /// 指令的操作数栈效果（仅在诊断 / 未来扩展时使用）。
        /// <see cref="TryAuditReceiver"/> 不依赖它：静态 getter 的语义已由签名确定，
        /// 替换的正确性来自"显式压入 this"这一构造，而不是对周边指令栈效果的推断。
        /// </summary>
        private static bool TryStackEffect(CodeInstruction instruction, out int consumed, out int produced)
        {
            consumed = 0;
            produced = 0;
            if (instruction == null) return false;

            OpCode opcode = instruction.opcode;
            FieldInfo field = instruction.operand as FieldInfo;
            if (field != null)
            {
                if (opcode == OpCodes.Ldfld) { consumed = 1; produced = 1; return true; }
                if (opcode == OpCodes.Ldflda) { consumed = 1; produced = 1; return true; }
                if (opcode == OpCodes.Stfld) { consumed = 2; produced = 0; return true; }
                if (opcode == OpCodes.Ldsfld) { consumed = 0; produced = 1; return true; }
                if (opcode == OpCodes.Stsfld) { consumed = 1; produced = 0; return true; }
                return false;
            }

            if (opcode == OpCodes.Ldarg_0 || opcode == OpCodes.Ldarg_1 ||
                opcode == OpCodes.Ldarg_2 || opcode == OpCodes.Ldarg_3) { produced = 1; return true; }
            if (opcode == OpCodes.Ldloc_0 || opcode == OpCodes.Ldloc_1 ||
                opcode == OpCodes.Ldloc_2 || opcode == OpCodes.Ldloc_3) { produced = 1; return true; }
            if (opcode == OpCodes.Ldc_I4_0 || opcode == OpCodes.Ldc_I4_1 ||
                opcode == OpCodes.Ldc_R4 || opcode == OpCodes.Ldc_R8 ||
                opcode == OpCodes.Ldnull || opcode == OpCodes.Dup) { produced = 1; return true; }
            if (opcode == OpCodes.Add || opcode == OpCodes.Sub || opcode == OpCodes.Mul ||
                opcode == OpCodes.Div) { consumed = 2; produced = 1; return true; }
            if (opcode == OpCodes.Conv_R4 || opcode == OpCodes.Conv_R8 ||
                opcode == OpCodes.Neg || opcode == OpCodes.Not) { consumed = 1; produced = 1; return true; }
            if (opcode == OpCodes.Nop) return true;
            if (opcode == OpCodes.Br_S || opcode == OpCodes.Brfalse_S || opcode == OpCodes.Brtrue_S ||
                opcode == OpCodes.Beq_S || opcode == OpCodes.Bne_Un_S ||
                opcode == OpCodes.Bge_S || opcode == OpCodes.Bgt_S ||
                opcode == OpCodes.Ble_S || opcode == OpCodes.Blt_S ||
                opcode == OpCodes.Bge_Un_S || opcode == OpCodes.Bgt_Un_S ||
                opcode == OpCodes.Ble_Un_S || opcode == OpCodes.Blt_Un_S)
            {
                // 分支不会出现在接收者区间里（形态校验已排除）；这里保守地拒绝。
                return false;
            }

            return false;
        }

        /// <summary>
        /// 相邻指令是否属于已确认的时间读取形态（与 tools/ilprobe verify-camera 的规则一致）。
        ///
        /// 只允许加载 / 存储 / 常量 / 转换 / 加减 / 短分支贴着时间读取；任何 call / callvirt /
        /// ret / 未知操作数形态都算不符，安装整体被拒绝。加上精确的替换数量断言，这足以防止
        /// 在一个"看起来像时间累计"的陌生形态上误替换。
        /// </summary>
        private static bool IsExpectedIncident(CodeInstruction instruction, string[] allowedFieldNames)
        {
            if (instruction == null) return false;

            FieldInfo field = instruction.operand as FieldInfo;
            if (field != null)
            {
                if (field.DeclaringType == null ||
                    !string.Equals(field.DeclaringType.FullName, CameraTypeFullName, StringComparison.Ordinal))
                {
                    return false;
                }
                if (instruction.opcode != OpCodes.Ldfld && instruction.opcode != OpCodes.Stfld) return false;

                for (int i = 0; i < allowedFieldNames.Length; i++)
                {
                    if (string.Equals(field.Name, allowedFieldNames[i], StringComparison.Ordinal)) return true;
                }
                return false;
            }

            OpCode opcode = instruction.opcode;
            return opcode == OpCodes.Ldarg_0 || opcode == OpCodes.Ldarg_1 ||
                   opcode == OpCodes.Ldarg_2 || opcode == OpCodes.Ldarg_3 ||
                   opcode == OpCodes.Ldloc_0 || opcode == OpCodes.Ldloc_1 ||
                   opcode == OpCodes.Ldloc_2 || opcode == OpCodes.Ldloc_3 ||
                   opcode == OpCodes.Ldc_I4_0 || opcode == OpCodes.Ldc_I4_1 ||
                   opcode == OpCodes.Ldc_R4 || opcode == OpCodes.Ldc_R8 ||
                   opcode == OpCodes.Ldnull || opcode == OpCodes.Dup ||
                   opcode == OpCodes.Add || opcode == OpCodes.Sub ||
                   opcode == OpCodes.Mul || opcode == OpCodes.Div ||
                   opcode == OpCodes.Conv_R4 || opcode == OpCodes.Conv_R8 ||
                   opcode == OpCodes.Nop ||
                   // 短分支：CheckUpdateFrameRateScreen 在字段读取与时间读取之间有一条 brfalse.s。
                   opcode == OpCodes.Br_S || opcode == OpCodes.Brfalse_S || opcode == OpCodes.Brtrue_S ||
                   opcode == OpCodes.Beq_S || opcode == OpCodes.Bne_Un_S ||
                   opcode == OpCodes.Bge_S || opcode == OpCodes.Bgt_S ||
                   opcode == OpCodes.Ble_S || opcode == OpCodes.Blt_S ||
                   opcode == OpCodes.Bge_Un_S || opcode == OpCodes.Bgt_Un_S ||
                   opcode == OpCodes.Ble_Un_S || opcode == OpCodes.Blt_Un_S;
        }

        private static string Describe(CodeInstruction instruction)
        {
            if (instruction == null) return "none";
            return instruction.opcode + (instruction.operand == null ? "" : " " + instruction.operand);
        }
    }
}
