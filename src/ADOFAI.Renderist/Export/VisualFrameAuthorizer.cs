using System;
using System.Collections.Generic;
using System.Globalization;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 本 session 当前的视觉授权生命周期状态。owner 用它区分
    /// 「合法的等待窗口」与「真正的越相位 / 重复消费」：
    /// 前者只能得到 0 增量，后者必须 fail-closed。
    /// </summary>
    internal enum VisualSessionVisualState
    {
        /// <summary>generation 尚未绑定，或本 session 已失效：owner 不做任何消费。</summary>
        Inactive = 0,

        /// <summary>
        /// 合法等待窗口（owner 已安装但首帧尚未发布，或本帧授权已在 EOF 关闭、
        /// session 仍在等待交付 / Finalizing）。此时原生 Update 仍可能继续运行，
        /// 但**不授予任何视觉步**，也不构成失败。
        /// </summary>
        Waiting = 1,

        /// <summary>本帧授权已发布且尚未关闭：owner 可以在本帧消费一次。</summary>
        Authorized = 2,

        /// <summary>本帧已经被标记失败：不得重试、不得提交。</summary>
        Failed = 3,
    }

    /// <summary>
    /// 单个逻辑帧允许消费的视觉相位。
    ///
    /// 相位是**授权的一部分**，不是观察结果：owner 只能按 scheduler 已经发布的相位消费，
    /// 不能自行推断"现在大概是 gameplay"。
    /// </summary>
    internal enum VisualFramePhase
    {
        /// <summary>session 已建立但 frame 0 之前的 bootstrap 窗口；不得消费视觉时间。</summary>
        Bootstrap = 0,

        /// <summary>deterministic pre-entry 逻辑帧（含绝对 frame 0）。</summary>
        PreEntry = 1,

        /// <summary>gameplay 完整逻辑步。</summary>
        Gameplay = 2,

        /// <summary>
        /// gameplay 起始边界 G 的 partial 步：本帧只覆盖
        /// previousBoundaryTime → canonicalStart。
        /// </summary>
        GameplayPartial = 3,

        /// <summary>
        /// B−1 的视觉授权已一次性关闭、但 GameplayCapture 尚未开始的隐藏迁移窗口。
        /// 它只做生命周期：published 后不发任何新视觉步（两个增量恒为 0）。
        /// </summary>
        Sealed = 4,
    }

    /// <summary>
    /// 一个消费者槽位。每个槽位在 session 内最多由一个 owner 取得；
    /// 各槽位的回执互相独立（不是"第一个消费者读完就全局清空"）。
    /// </summary>
    internal enum VisualConsumerSlot
    {
        Camera = 0,
        TweenBatch = 1,
        ParticleBatch = 2,
    }

    /// <summary>
    /// owner 在<b>原生调用帧</b>内消费视觉授权时使用的 key。
    ///
    /// <see cref="NativeFrame"/> 就是 Unity <c>Time.frameCount</c>：授权只在 Prepare 所在的
    /// 那一个原生帧内有效，早于 Prepare、晚于本步消费相位或跨帧重试都必须得到具体的相位错误，
    /// 而不是把消费顺延到任意等待帧。
    /// </summary>
    internal readonly struct VisualFrameConsumptionKey : IEquatable<VisualFrameConsumptionKey>
    {
        internal VisualFrameConsumptionKey(VisualConsumerSlot slot, long generation, long absoluteOutputFrameIndex,
            long nativeFrame, int scopeId)
        {
            Slot = slot;
            Generation = generation;
            AbsoluteOutputFrameIndex = absoluteOutputFrameIndex;
            NativeFrame = nativeFrame;
            ScopeId = scopeId;
        }

        internal VisualConsumerSlot Slot { get; }
        internal long Generation { get; }
        internal long AbsoluteOutputFrameIndex { get; }
        internal long NativeFrame { get; }
        /// <summary>同一次 owner 调用作用域 id；0 = 未建立作用域（调用方错误）。</summary>
        internal int ScopeId { get; }

        public bool Equals(VisualFrameConsumptionKey other)
        {
            return Slot == other.Slot &&
                   Generation == other.Generation &&
                   AbsoluteOutputFrameIndex == other.AbsoluteOutputFrameIndex &&
                   NativeFrame == other.NativeFrame &&
                   ScopeId == other.ScopeId;
        }

        public override bool Equals(object obj)
        {
            return obj is VisualFrameConsumptionKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Slot;
                hash = (hash * 397) ^ Generation.GetHashCode();
                hash = (hash * 397) ^ AbsoluteOutputFrameIndex.GetHashCode();
                hash = (hash * 397) ^ NativeFrame.GetHashCode();
                hash = (hash * 397) ^ ScopeId;
                return hash;
            }
        }
    }

    /// <summary>
    /// owner 消费一次视觉步的结果分类。NestedInScope 共享本次消费；NoVisualStep
    /// 是合法的零步调用。其余错误必须让本帧失败，绝不补走剩余时间。
    /// </summary>
    internal enum VisualFrameConsumeOutcome
    {
        /// <summary>本次调用取得了授权，可以按返回的增量推进。</summary>
        Consumed = 0,

        /// <summary>同一作用域内的嵌套调用（例如 Update 内嵌 UpdateSize）：复用本次授权，不重复计时。</summary>
        NestedInScope = 1,

        /// <summary>
        /// 该授权不授予视觉步（bootstrap / sealed / frame 0）。增量恒为 0；
        /// 当前调用仍需配对完成回执，它不是失败或可推进的时间。
        /// </summary>
        NoVisualStep = 2,

        /// <summary>该消费者槽位在本帧已经被消费过（重复消费）。</summary>
        DuplicateConsumption = 3,

        /// <summary>授权已失效（session 已停止 / generation 已变更）。</summary>
        StaleAuthorization = 4,

        /// <summary>owner 没有对应原生帧的有效授权（早于 Prepare 或晚于本帧消费相位）。</summary>
        OutOfPhase = 5,

        /// <summary>该槽位注册了 owner 但从未登记（调用方接线错误）。</summary>
        NotRegistered = 6,

        /// <summary>调用方未建立作用域（scopeId &lt;= 0）。</summary>
        InvalidScope = 7,

        /// <summary>本帧已被标记失败（原生异常等）：不得重试。</summary>
        FrameFailed = 8,
    }

    /// <summary>
    /// 一次授权的**不可变**快照。它是值语义：owner 拿到的就是本帧允许消费的全部时间，
    /// 不持有任何可变引用，也不会因为后续帧的发布而变化。
    /// </summary>
    internal readonly struct VisualFrameAuthorization
    {
        internal VisualFrameAuthorization(long generation, long absoluteOutputFrameIndex, long nativeFrame,
            VisualFramePhase phase, double chartSampleSeconds, double chartSampleDeltaSeconds,
            double visualUnscaledDeltaSeconds, double visualScaledDeltaSeconds,
            double partialFraction, double timeScale, bool lifecycleOnly, bool zeroStep)
        {
            Generation = generation;
            AbsoluteOutputFrameIndex = absoluteOutputFrameIndex;
            NativeFrame = nativeFrame;
            Phase = phase;
            ChartSampleSeconds = chartSampleSeconds;
            ChartSampleDeltaSeconds = chartSampleDeltaSeconds;
            VisualUnscaledDeltaSeconds = visualUnscaledDeltaSeconds;
            VisualScaledDeltaSeconds = visualScaledDeltaSeconds;
            PartialFraction = partialFraction;
            TimeScale = timeScale;
            LifecycleOnly = lifecycleOnly;
            ZeroStep = zeroStep;
        }

        internal long Generation { get; }
        internal long AbsoluteOutputFrameIndex { get; }
        internal long NativeFrame { get; }
        internal VisualFramePhase Phase { get; }
        internal double ChartSampleSeconds { get; }
        internal double ChartSampleDeltaSeconds { get; }
        internal double VisualUnscaledDeltaSeconds { get; }
        internal double VisualScaledDeltaSeconds { get; }
        internal double PartialFraction { get; }
        internal double TimeScale { get; }

        /// <summary>
        /// true = 本授权只过生命周期，**不占用输出帧序列、不授予视觉步**：
        ///   * 它不推进 <c>_lastPublishedOutputFrameIndex</c>，因此后续 G 仍可合法发布同一 index；
        ///   * 它在整个相位持续期间都有效，每次独立调用都配对零步消费与完成；
        ///     上次完成后可以跨原生帧再进入，不占用真实输出帧的单次消费额度。
        /// bootstrap / hidden 迁移窗口使用它。
        /// </summary>
        internal bool LifecycleOnly { get; }

        /// <summary>
        /// true = 本授权是一帧真实的输出提交，但**首帧默认零视觉增量**
        /// （frame 0 的初始化 bootstrap 不由额外 Unity Update 补步）。
        /// </summary>
        internal bool ZeroStep { get; }

        /// <summary>该授权是否授予一个真实视觉步（bootstrap / sealed / frame 0 都不授予）。</summary>
        internal bool GrantsVisualStep =>
            !ZeroStep && !LifecycleOnly &&
            (Phase == VisualFramePhase.PreEntry ||
             Phase == VisualFramePhase.Gameplay ||
             Phase == VisualFramePhase.GameplayPartial);

        /// <summary>后续授权是否必须严格接在本授权之后（lifecycle-only 不占序列位置）。</summary>
        internal bool AdvancesOutputFrameSequence => !LifecycleOnly;

        internal bool Matches(long generation, long absoluteOutputFrameIndex)
        {
            return generation != 0 && generation == Generation && absoluteOutputFrameIndex == AbsoluteOutputFrameIndex;
        }

        internal string Describe()
        {
            return "generation=" + Generation.ToString(CultureInfo.InvariantCulture) +
                   ",frame=" + AbsoluteOutputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                   ",nativeFrame=" + NativeFrame.ToString(CultureInfo.InvariantCulture) +
                   ",phase=" + Phase +
                   ",lifecycleOnly=" + (LifecycleOnly ? "true" : "false") +
                   ",zeroStep=" + (ZeroStep ? "true" : "false") +
                   ",chartSample=" + ChartSampleSeconds.ToString("R", CultureInfo.InvariantCulture) +
                   ",chartDelta=" + ChartSampleDeltaSeconds.ToString("R", CultureInfo.InvariantCulture) +
                   ",visualUnscaled=" + VisualUnscaledDeltaSeconds.ToString("R", CultureInfo.InvariantCulture) +
                   ",visualScaled=" + VisualScaledDeltaSeconds.ToString("R", CultureInfo.InvariantCulture) +
                   ",partialFraction=" + PartialFraction.ToString("R", CultureInfo.InvariantCulture) +
                   ",timeScale=" + TimeScale.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 视觉帧授权合同：**每消费者独立回执 + 一次消费 + 失效 + EOF 前完整性校验**。
    ///
    /// 职责边界（严格单一，刻意保持 Unity-free）：
    ///   * 由 scheduler 在既有 Prepare 边界发布本帧的不可变授权（generation / absolute frame /
    ///     native frame / phase / chart sample / visual 增量）；
    ///   * 各 owner（Camera / Tween batch / Particle batch）按自己的原生调用相位消费，并留下回执；
    ///   * EOF 前由 scheduler 校验所有<b>已注册</b>消费者都在本帧完成回执，然后关闭授权。
    ///
    /// 它**不是**时钟，也**不是**全局视觉管理器：不推进 <c>MasterTimeline</c>、不读写 Unity、
    /// 不持有 owner 实例、不决定任何 L3-C 状态或计数 authority。
    ///
    /// 不变量：
    ///   1. 一次成功/开始过的消费绝不在同一逻辑帧内被重试（<see cref="VisualFrameConsumeOutcome.DuplicateConsumption"/>）；
    ///   2. 授权只在 Prepare 所在的原生帧内有效（<see cref="VisualFrameConsumeOutcome.OutOfPhase"/>）；
    ///   3. 未注册的槽位不会被要求回执；注册过的槽位缺失回执即帧失败；
    ///   4. 失效（<see cref="Invalidate"/>）后一切消费与校验都返回 stale，绝不假装已收敛。
    /// </summary>
    internal sealed class VisualFrameAuthorizer
    {
        /// <summary>
        /// partial fraction 的容差：只吸收 <c>partialStep / step</c> 的浮点表示误差。
        /// 与 PreEntry boundary 的 ULP 级容差同量级，不构成任何隐式时间窗口。
        /// </summary>
        private const double PartialFractionTolerance = 1e-9;

        private readonly object _gate = new object();
        private readonly HashSet<VisualConsumerSlot> _registered = new HashSet<VisualConsumerSlot>();
        private readonly Dictionary<VisualConsumerSlot, ConsumptionRecord> _consumed =
            new Dictionary<VisualConsumerSlot, ConsumptionRecord>();
        private readonly Dictionary<VisualConsumerSlot, string> _failures =
            new Dictionary<VisualConsumerSlot, string>();

        private long _generation;
        private long _lastPublishedOutputFrameIndex = -1L;
        private VisualFrameAuthorization _current;
        private bool _hasCurrent;
        private bool _currentFailed;
        private bool _invalidated;
        private long _publishedFrameCount;
        private long _consumedFrameCount;

        private struct ConsumptionRecord
        {
            internal VisualFrameConsumptionKey Key;
            internal int CompletedScopeId;
            internal bool Completed;
        }

        /// <summary>本 authorizer 绑定的 session generation（复用 capture driver 的单调 generation）。</summary>
        internal long Generation
        {
            get { lock (_gate) { return _generation; } }
        }

        /// <summary>false = 尚未绑定 generation：此时任何消费都是 stale。</summary>
        internal bool IsArmed
        {
            get { lock (_gate) { return _generation != 0 && !_invalidated; } }
        }

        internal bool HasCurrentAuthorization
        {
            get { lock (_gate) { return _hasCurrent; } }
        }

        /// <summary>本帧是否已被标记失败（原生异常等）：失败帧不得重试消费。</summary>
        internal bool CurrentFrameFailed
        {
            get { lock (_gate) { return _currentFailed; } }
        }

        internal long PublishedFrameCount
        {
            get { lock (_gate) { return _publishedFrameCount; } }
        }

        internal long ConsumedFrameCount
        {
            get { lock (_gate) { return _consumedFrameCount; } }
        }

        /// <summary>
        /// 绑定本 session 的 generation（必须在任何 owner 安装 / 任何 Prepare 之前调用）。
        /// 只接受非零 generation：0 不是合法 authority。
        ///
        /// 这是**每个 session 的完整复位**：上一次 session 的注册消费者集合与所有
        /// 帧 / 回执 / 失败状态一律清空。否则 Completed / Cancelled / Failed 之后重新
        /// Arm 时会因为残留的 <c>Camera</c> 注册而让本 session 的登记失败
        /// （<c>consumer-already-registered</c>），使 restart 永远无法开始。
        /// </summary>
        internal void Arm(long generation)
        {
            lock (_gate)
            {
                if (generation == 0)
                    throw new ArgumentOutOfRangeException(nameof(generation), "session generation must be non-zero");
                _generation = generation;
                _invalidated = false;
                _hasCurrent = false;
                _current = default(VisualFrameAuthorization);
                _currentFailed = false;
                _lastPublishedOutputFrameIndex = -1L;
                _consumed.Clear();
                _failures.Clear();
                // 注册集合属于 session：上一个 session 的登记绝不允许泄漏到下一次
                // （owner 每次 Begin 都会重新登记）。
                _registered.Clear();
            }
        }

        /// <summary>
        /// 登记一个本 session 必须回执的消费者槽位。必须在 playback 之前完成；
        /// 重复登记同一槽位是非法的（否则接线错误会被静默掩盖）。
        /// </summary>
        internal bool TryRegisterConsumer(VisualConsumerSlot slot, out string error)
        {
            lock (_gate)
            {
                if (_generation == 0)
                {
                    error = "authorizer-not-armed";
                    return false;
                }
                if (_registered.Contains(slot))
                {
                    error = "consumer-already-registered:" + slot;
                    return false;
                }
                _registered.Add(slot);
                error = null;
                return true;
            }
        }

        /// <summary>槽位是否登记为需要回执（诊断用；未登记的槽位不参与 EOF 完整性校验）。</summary>
        internal bool IsConsumerRegistered(VisualConsumerSlot slot)
        {
            lock (_gate) { return _registered.Contains(slot); }
        }

        /// <summary>已注册槽位的只读快照（诊断用）；调用方拿到的是副本，不暴露内部集合。</summary>
        internal IReadOnlyCollection<VisualConsumerSlot> RegisteredConsumers
        {
            get { lock (_gate) { return new List<VisualConsumerSlot>(_registered); } }
        }

        /// <summary>
        /// 在既有 Prepare 边界发布本帧授权。返回 false 时 <paramref name="error"/> 给出具体原因，
        /// 调用方必须让当前 session fail-closed（绝不发布一半的授权）。
        ///
        /// 发布顺序（frame index 只能前进 1）是刻意的：授权是 authority 的一部分，
        /// 回退、跳跃或重复发布都意味着 scheduler 已经不一致。
        /// </summary>
        internal bool TryPublishFrame(
            long generation,
            long absoluteOutputFrameIndex,
            long nativeFrame,
            VisualFramePhase phase,
            double chartSampleSeconds,
            double chartSampleDeltaSeconds,
            double outputFps,
            double baseTimeScale,
            double partialFraction,
            out VisualFrameAuthorization authorization,
            out string error)
        {
            lock (_gate)
            {
                authorization = default(VisualFrameAuthorization);

                if (generation == 0 || generation != _generation || _invalidated)
                {
                    error = "publish-stale-generation:expected=" + _generation.ToString(CultureInfo.InvariantCulture) +
                            ",observed=" + generation.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                if (absoluteOutputFrameIndex < 0)
                {
                    error = "publish-frame-index-invalid";
                    return false;
                }
                if (nativeFrame < 0)
                {
                    error = "publish-native-frame-invalid";
                    return false;
                }
                if (!IsFinite(chartSampleSeconds))
                {
                    error = "publish-chart-sample-not-finite";
                    return false;
                }
                if (!IsFinite(chartSampleDeltaSeconds))
                {
                    error = "publish-chart-delta-not-finite";
                    return false;
                }
                if (!(outputFps > 0.0) || !IsFinite(outputFps))
                {
                    error = "publish-output-fps-invalid";
                    return false;
                }
                if (!IsFinite(baseTimeScale) || baseTimeScale < 0.0)
                {
                    error = "publish-base-time-scale-invalid";
                    return false;
                }
                if (partialFraction < 0.0 || partialFraction > 1.0 + PartialFractionTolerance ||
                    !IsFinite(partialFraction))
                {
                    error = "publish-partial-fraction-out-of-range:" +
                            partialFraction.ToString("R", CultureInfo.InvariantCulture);
                    return false;
                }
                if (_lastPublishedOutputFrameIndex >= 0 &&
                    absoluteOutputFrameIndex != _lastPublishedOutputFrameIndex + 1)
                {
                    error = "publish-frame-sequence-broken:last=" +
                            _lastPublishedOutputFrameIndex.ToString(CultureInfo.InvariantCulture) + ",next=" +
                            absoluteOutputFrameIndex.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                bool lifecycleOnly = phase == VisualFramePhase.Bootstrap || phase == VisualFramePhase.Sealed;
                double step = 1.0 / outputFps;
                double fraction = partialFraction > 1.0 ? 1.0 : partialFraction;

                // 相位决定本帧实际授予的视觉增量。刻意不用任何"当前 timeScale"反推：
                // 授权先于原生消费发生，读运行时状态只会把等待期间的值混进来。
                double unscaled;
                double scaled;
                switch (phase)
                {
                    case VisualFramePhase.Bootstrap:
                    case VisualFramePhase.Sealed:
                        unscaled = 0.0;
                        scaled = 0.0;
                        break;
                    case VisualFramePhase.GameplayPartial:
                        // G partial：visual unscaled = partialFraction / FPS，scaled 再乘既有保存的基础 timeScale。
                        // 不能读滞后一帧的 Time.deltaTime，也不能把已经缩小的 timeScale 再乘一次 fraction。
                        unscaled = fraction * step;
                        scaled = unscaled * baseTimeScale;
                        break;
                    default:
                        // 普通 pre-entry / gameplay：visual unscaled 增量恒为 1/FPS（pitch 已含在 chart sample 里，
                        // 不再额外乘进视觉增量）。
                        unscaled = step;
                        scaled = step * baseTimeScale;
                        break;
                }

                if (!IsFinite(unscaled) || unscaled < 0.0 || !IsFinite(scaled) || scaled < 0.0)
                {
                    error = "publish-visual-delta-invalid";
                    return false;
                }
                // float 化必须仍然是一个真实的正步长（极端合法 FPS 下不得下溢成 0 后被当成"没有时间"）。
                if (unscaled > 0.0 && !((float)unscaled > 0f))
                {
                    error = "publish-visual-delta-underflow";
                    return false;
                }

                // frame 0 仍要求真实回执；不可变快照与首次/嵌套消费统一使用零步数值。
                bool zeroStep = !lifecycleOnly && absoluteOutputFrameIndex == 0L;
                if (zeroStep)
                {
                    unscaled = 0.0;
                    scaled = 0.0;
                }

                authorization = new VisualFrameAuthorization(
                    _generation, absoluteOutputFrameIndex, nativeFrame, phase,
                    chartSampleSeconds, chartSampleDeltaSeconds,
                    unscaled, scaled, fraction, baseTimeScale, lifecycleOnly, zeroStep);

                _current = authorization;
                _hasCurrent = true;
                _currentFailed = false;
                // lifecycle-only 不占用输出帧序列位置：hidden 迁移窗口结束后，G 仍然发布
                // 同一个 absolute output frame index，因此它**不能**推进序列。
                if (authorization.AdvancesOutputFrameSequence)
                    _lastPublishedOutputFrameIndex = absoluteOutputFrameIndex;
                _consumed.Clear();
                _failures.Clear();
                _publishedFrameCount++;
                error = null;
                return true;
            }
        }

        /// <summary>
        /// 发布一个不授予任何视觉步的相位（bootstrap / sealed）。等价于
        /// <see cref="TryPublishFrame"/> 且两个增量恒为 0；语义上单独命名是为了让调用点
        /// 明确表达"本帧只过生命周期"。
        /// </summary>
        internal bool TryPublishLifecycleOnly(
            long generation, long absoluteOutputFrameIndex, long nativeFrame, VisualFramePhase phase,
            double chartSampleSeconds, double outputFps, double baseTimeScale,
            out VisualFrameAuthorization authorization, out string error)
        {
            if (phase != VisualFramePhase.Bootstrap && phase != VisualFramePhase.Sealed)
            {
                authorization = default(VisualFrameAuthorization);
                error = "lifecycle-only-phase-invalid:" + phase;
                return false;
            }
            return TryPublishFrame(generation, absoluteOutputFrameIndex, nativeFrame, phase,
                chartSampleSeconds, 0.0, outputFps, baseTimeScale, 0.0, out authorization, out error);
        }

        /// <summary>
        /// 只读窥视当前授权（不消费、不改变任何状态）。owner 用它取得"本帧是哪一个
        /// absolute output frame"，而**不**自行发明 frame authority。
        /// </summary>
        internal bool TryPeekCurrent(out VisualFrameAuthorization authorization)
        {
            lock (_gate)
            {
                authorization = _current;
                return _hasCurrent && !_invalidated && _generation != 0;
            }
        }

        /// <summary>
        /// 只读窥视当前授权**与** session 生命周期状态（单次加锁，两者必然一致）。
        ///
        /// owner 必须用这个入口区分：
        ///   * <see cref="VisualSessionVisualState.Authorized"/>：可以消费一次；
        ///   * <see cref="VisualSessionVisualState.Waiting"/>：合法的等待窗口
        ///     （首帧之前的 bootstrap，或 EOF 后的交付 / Finalizing 等待）——只返回 0 增量，
        ///     **不消费、也不算失败**；
        ///   * <see cref="VisualSessionVisualState.Failed"/>：本帧已失败，不得重试。
        /// </summary>
        internal bool TryPeekCurrentState(
            out VisualFrameAuthorization authorization, out VisualSessionVisualState state)
        {
            lock (_gate)
            {
                authorization = _current;
                if (_generation == 0 || _invalidated)
                {
                    state = VisualSessionVisualState.Inactive;
                    return false;
                }
                if (!_hasCurrent)
                {
                    state = VisualSessionVisualState.Waiting;
                    return false;
                }
                state = _currentFailed ? VisualSessionVisualState.Failed : VisualSessionVisualState.Authorized;
                return true;
            }
        }

        /// <summary>
        /// 消费者消费本帧授权。<paramref name="key"/> 必须由 owner 用**当前原生帧**构成。
        ///
        /// 结果是三选一，绝不"部分成功"：
        ///   * <see cref="VisualFrameConsumeOutcome.Consumed"/>：唯一一次真实消费，调用方按增量推进；
        ///   * <see cref="VisualFrameConsumeOutcome.NestedInScope"/>：同一作用域内的嵌套读取，增量照给但不再计时；
        ///   * 其它：本帧失败的具体原因。
        /// </summary>
        internal VisualFrameConsumeOutcome TryConsume(
            VisualFrameConsumptionKey key, out double unscaledDeltaSeconds, out double scaledDeltaSeconds,
            out string error)
        {
            lock (_gate)
            {
                unscaledDeltaSeconds = 0.0;
                scaledDeltaSeconds = 0.0;
                error = null;

                if (key.ScopeId <= 0)
                {
                    error = "consume-invalid-scope";
                    return VisualFrameConsumeOutcome.InvalidScope;
                }
                if (_generation == 0 || _invalidated || key.Generation != _generation)
                {
                    error = "consume-stale-generation:expected=" +
                            _generation.ToString(CultureInfo.InvariantCulture) + ",observed=" +
                            key.Generation.ToString(CultureInfo.InvariantCulture);
                    return VisualFrameConsumeOutcome.StaleAuthorization;
                }
                if (!IsConsumerRegisteredLocked(key.Slot))
                {
                    error = "consume-consumer-not-registered:" + key.Slot;
                    return VisualFrameConsumeOutcome.NotRegistered;
                }
                if (!_hasCurrent)
                {
                    // 合法等待窗口（首帧之前 / EOF 之后）。owner 应当先用
                    // TryPeekCurrentState 看到 Waiting 并直接返回 0 增量；走到这里说明
                    // 调用方跳过了状态检查，按越相位拒绝（绝不静默补时间）。
                    error = "consume-no-current-authorization";
                    return VisualFrameConsumeOutcome.OutOfPhase;
                }
                if (_currentFailed)
                {
                    error = "consume-frame-already-failed";
                    return VisualFrameConsumeOutcome.FrameFailed;
                }

                VisualFrameAuthorization authorization = _current;
                // lifecycle-only（hidden / sealed）授权覆盖整个相位，跨越任意多个原生
                // Unity frame：它不绑定"Prepare 所在的那一个原生帧"，重复进入原生 Update
                // 只得到 0 增量（见下），不算越相位、也不算重复消费。它同样不占用输出帧序列。
                bool nativeFrameMatches =
                    authorization.LifecycleOnly ||
                    key.NativeFrame == authorization.NativeFrame;
                if (key.AbsoluteOutputFrameIndex != authorization.AbsoluteOutputFrameIndex ||
                    !nativeFrameMatches)
                {
                    error = "consume-out-of-phase:expectedFrame=" +
                            authorization.AbsoluteOutputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                            ",expectedNativeFrame=" + authorization.NativeFrame.ToString(CultureInfo.InvariantCulture) +
                            ",observedFrame=" + key.AbsoluteOutputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                            ",observedNativeFrame=" + key.NativeFrame.ToString(CultureInfo.InvariantCulture) +
                            ",phase=" + authorization.Phase +
                            ",lifecycleOnly=" + (authorization.LifecycleOnly ? "true" : "false");
                    return VisualFrameConsumeOutcome.OutOfPhase;
                }

                if (_consumed.TryGetValue(key.Slot, out ConsumptionRecord record))
                {
                    if (record.Key.Equals(key) && !record.Completed)
                    {
                        // 同一作用域内的嵌套调用：复用已取得的授权。
                        unscaledDeltaSeconds = authorization.VisualUnscaledDeltaSeconds;
                        scaledDeltaSeconds = authorization.VisualScaledDeltaSeconds;
                        return VisualFrameConsumeOutcome.NestedInScope;
                    }

                    if (authorization.LifecycleOnly && record.Completed && !record.Key.Equals(key))
                    {
                        // 上次调用已配对完成；为这一次 hidden 调用登记自己的 key。
                        // 不沿用首个调用的 ScopeId / NativeFrame，也不允许覆盖仍在途的回执。
                        _consumed[key.Slot] = new ConsumptionRecord { Key = key, Completed = false };
                        return VisualFrameConsumeOutcome.NoVisualStep;
                    }

                    error = "consume-duplicate:slot=" + key.Slot +
                            ",firstScope=" + record.Key.ScopeId.ToString(CultureInfo.InvariantCulture) +
                            ",observedScope=" + key.ScopeId.ToString(CultureInfo.InvariantCulture) +
                            ",completed=" + (record.Completed ? "true" : "false") +
                            ",frame=" + key.AbsoluteOutputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                            ",phase=" + authorization.Phase;
                    return VisualFrameConsumeOutcome.DuplicateConsumption;
                }

                if (!authorization.GrantsVisualStep)
                {
                    // bootstrap / sealed / **frame 0**：本帧不授予视觉步。登记"已消费"
                    // 以免同一帧反复试探，但增量恒为 0，且不推进任何时间。
                    _consumed[key.Slot] = new ConsumptionRecord { Key = key, Completed = false };
                    return VisualFrameConsumeOutcome.NoVisualStep;
                }

                _consumed[key.Slot] = new ConsumptionRecord { Key = key, Completed = false };
                unscaledDeltaSeconds = authorization.VisualUnscaledDeltaSeconds;
                scaledDeltaSeconds = authorization.VisualScaledDeltaSeconds;
                return VisualFrameConsumeOutcome.Consumed;
            }
        }

        /// <summary>
        /// 关闭一次 owner 调用作用域。只有原方法**正常返回**（Postfix）才把回执标记为完成；
        /// 带异常的 Finalizer 走 <see cref="FailFrame"/>。
        /// </summary>
        internal bool TryCompleteScope(VisualFrameConsumptionKey key, out string error)
        {
            lock (_gate)
            {
                error = null;
                if (_generation == 0 || _invalidated || key.Generation != _generation)
                {
                    error = "complete-stale-generation";
                    return false;
                }
                if (!_hasCurrent || key.AbsoluteOutputFrameIndex != _current.AbsoluteOutputFrameIndex)
                {
                    error = "complete-no-matching-authorization";
                    return false;
                }
                if (_currentFailed)
                {
                    error = "complete-frame-already-failed";
                    return false;
                }
                if (!_consumed.TryGetValue(key.Slot, out ConsumptionRecord record))
                {
                    error = "complete-without-consumption:slot=" + key.Slot;
                    return false;
                }
                if (!record.Key.Equals(key))
                {
                    error = "complete-scope-mismatch:recordScope=" +
                            record.Key.ScopeId.ToString(CultureInfo.InvariantCulture) + ",observedScope=" +
                            key.ScopeId.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                if (record.Completed)
                {
                    error = "complete-already-completed:slot=" + key.Slot;
                    return false;
                }

                record.Completed = true;
                record.CompletedScopeId = key.ScopeId;
                _consumed[key.Slot] = record;
                return true;
            }
        }

        /// <summary>
        /// 把一个消费者槽位标记为本帧失败，并记录只属于该槽位的原因。
        /// 失败帧绝不重试、绝不 commit；scheduler 由 <see cref="TryValidateEndOfFrame"/> 看到它。
        /// </summary>
        internal void FailFrame(VisualConsumerSlot slot, string reason)
        {
            lock (_gate)
            {
                _currentFailed = true;
                _failures[slot] = string.IsNullOrEmpty(reason) ? "consumer-failed" : reason;
            }
        }

        /// <summary>
        /// EOF 前（**读回之前**）的完整性校验：所有已注册消费者都必须在本帧留下完成回执，
        /// 且本帧没有被标记失败。只在 <c>OnCaptureResult</c> 检查会晚于 PNG 写盘 / RGB24 形成，
        /// 因此唯一关口必须在这里。
        /// </summary>
        internal bool TryValidateEndOfFrame(out string error)
        {
            lock (_gate)
            {
                error = null;
                if (_generation == 0 || _invalidated)
                {
                    error = "eof-validation-stale-authorization";
                    return false;
                }
                if (!_hasCurrent)
                {
                    error = "eof-validation-no-current-authorization";
                    return false;
                }

                string failure = DescribeFirstFailureLocked();
                if (failure != null)
                {
                    error = failure;
                    return false;
                }

                foreach (VisualConsumerSlot slot in _registered)
                {
                    if (!_consumed.TryGetValue(slot, out ConsumptionRecord record))
                    {
                        error = "eof-missing-receipt:slot=" + slot +
                                ",frame=" + _current.AbsoluteOutputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                                ",phase=" + _current.Phase;
                        return false;
                    }
                    if (!record.Completed)
                    {
                        error = "eof-incomplete-receipt:slot=" + slot +
                                ",scope=" + record.Key.ScopeId.ToString(CultureInfo.InvariantCulture) +
                                ",frame=" + _current.AbsoluteOutputFrameIndex.ToString(CultureInfo.InvariantCulture) +
                                ",phase=" + _current.Phase;
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// 关闭本帧授权（读回/交付前的最后一步）。关闭后本帧不可再消费，
        /// 直到下一次 Prepare 发布新授权。
        /// </summary>
        internal void CloseCurrentFrame()
        {
            lock (_gate)
            {
                if (_hasCurrent) _consumedFrameCount++;
                _hasCurrent = false;
                _current = default(VisualFrameAuthorization);
                _consumed.Clear();
                _failures.Clear();
                _currentFailed = false;
            }
        }

        /// <summary>
        /// 使当前授权失效（session 停止 / cleanup / panic）。失效后所有消费与校验都返回 stale，
        /// 且不会再发布任何帧，直到重新 <see cref="Arm"/>。
        /// </summary>
        internal void Invalidate()
        {
            lock (_gate)
            {
                _invalidated = true;
                _hasCurrent = false;
                _current = default(VisualFrameAuthorization);
                _consumed.Clear();
                _failures.Clear();
                _currentFailed = false;
            }
        }

        /// <summary>只读诊断快照；绝不改变状态。</summary>
        internal string Describe(long currentNativeFrame)
        {
            lock (_gate)
            {
                if (_generation == 0)
                    return "generation=unarmed,invalidated=" + (_invalidated ? "true" : "false");

                string receipts = "";
                foreach (VisualConsumerSlot slot in _registered)
                {
                    bool consumed = _consumed.TryGetValue(slot, out ConsumptionRecord record);
                    receipts += (receipts.Length == 0 ? "" : ";") + slot + "=" +
                                (!consumed ? "none" : (record.Completed ? "completed" : "in-flight"));
                }

                string failures = "";
                foreach (KeyValuePair<VisualConsumerSlot, string> pair in _failures)
                {
                    failures += (failures.Length == 0 ? "" : ";") + pair.Key + "=" + pair.Value;
                }

                return "generation=" + _generation.ToString(CultureInfo.InvariantCulture) +
                       ",invalidated=" + (_invalidated ? "true" : "false") +
                       ",hasCurrent=" + (_hasCurrent ? "true" : "false") +
                       ",frameFailed=" + (_currentFailed ? "true" : "false") +
                       ",published=" + _publishedFrameCount.ToString(CultureInfo.InvariantCulture) +
                       ",consumedFrames=" + _consumedFrameCount.ToString(CultureInfo.InvariantCulture) +
                       ",nativeFrame=" + currentNativeFrame.ToString(CultureInfo.InvariantCulture) +
                       (!_hasCurrent
                           ? ",current=none"
                           : ",current=[" + _current.Describe() + "]") +
                       ",receipts=" + (receipts.Length == 0 ? "none" : receipts) +
                       ",failures=" + (failures.Length == 0 ? "none" : failures);
            }
        }

        private bool IsConsumerRegisteredLocked(VisualConsumerSlot slot)
        {
            return _registered.Contains(slot);
        }

        private string DescribeFirstFailureLocked()
        {
            foreach (KeyValuePair<VisualConsumerSlot, string> pair in _failures)
            {
                return "eof-consumer-failed:slot=" + pair.Key + ",reason=" + pair.Value;
            }
            return null;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
