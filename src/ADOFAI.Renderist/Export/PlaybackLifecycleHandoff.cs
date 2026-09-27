using System;
using System.Reflection;
using HarmonyLib;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>只关联本次 Renderist-owned editor.Play() 的官方启动 commit 链。</summary>
    internal sealed class PlaybackLifecycleHandoff : IDisposable
    {
        private const string StateEngineFullName = "MonsterLove.StateMachine.StateEngine";

        private readonly Harmony _harmony;
        private readonly Action<string> _requestOwnershipFailure;
        private object _controller;
        private object _stateMachine;
        private EventInfo _changedEvent;
        private Action<Enum> _changedHandler;
        private bool _changedSubscribed;

        /// <summary>
        /// 本 session 的 Planet 视觉时间 ownership。由本 handoff 持有，因此两者的生命周期
        /// 完全一致：安装早于本次 editor.Play()，cleanup 精确撤销，清理失败计入同一 residual gate。
        /// </summary>
        private PlanetVisualTimeOwnership _planetVisualTime;

        internal bool PlayRequested { get; private set; }
        internal bool PlayReturned { get; private set; }
        internal bool SawStart { get; private set; }
        internal bool SawCountdown { get; private set; }
        internal bool SawPlayerControl { get; private set; }
        internal bool SawMusicScheduled { get; private set; }

        internal string MarkerStatus => "requested=" + PlayRequested + ",returned=" + PlayReturned + ",start=" + SawStart + ",music=" + SawMusicScheduled + ",countdown=" + SawCountdown + ",playerControl=" + SawPlayerControl;

        internal PlaybackLifecycleHandoff(Harmony harmony, Action<string> requestOwnershipFailure)
        {
            _harmony = harmony ?? throw new ArgumentNullException(nameof(harmony));
            _requestOwnershipFailure = requestOwnershipFailure;
        }

        internal bool Begin(out string error)
        {
            error = null;
            try
            {
                _controller = EditorGameReflection.Controller();
                if (_controller == null) { error = "controller-null"; return false; }

                _stateMachine = ReadMember(_controller, "stateMachine");
                if (_stateMachine == null || _stateMachine.GetType().FullName != StateEngineFullName)
                {
                    error = "state-machine-unavailable";
                    return false;
                }

                // The editor can already be in the committed Start state when this
                // handoff is established. In that case there is no second Start
                // transition for Changed to report after Play() is requested.
                string initialState = ToState(EditorGameReflection.ReadControllerState());
                SawStart = string.Equals(initialState, "Start", StringComparison.Ordinal);

                _changedEvent = _stateMachine.GetType().GetEvent("Changed",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_changedEvent == null || _changedEvent.EventHandlerType != typeof(Action<Enum>))
                {
                    error = "state-changed-event-unavailable";
                    return false;
                }

                _changedHandler = OnStateChanged;
                _changedEvent.AddEventHandler(_stateMachine, _changedHandler);
                _changedSubscribed = true;

                // Planet 视觉时间 ownership observer：必须在本次 editor.Play() 之前安装。
                // 它的 OnMusicScheduled Postfix 只有在完整 acquisition 成功后才回调
                // MarkMusicScheduled()，因此 SawMusicScheduled 仍然表达“本次 playback 的
                // OnMusicScheduled 已被本 session 观测并成功接管”。
                _planetVisualTime = new PlanetVisualTimeOwnership(
                    _harmony, _controller, MarkMusicScheduled, _requestOwnershipFailure);
                if (!_planetVisualTime.Begin(out string ownershipError))
                {
                    error = "planet-visual-time-ownership-unavailable:" + ownershipError;
                    TryDispose();
                    return false;
                }

                Log.Info("MasterTimeline lifecycle handoff established: state=" + initialState + " startSatisfied=" + SawStart);
                Active = this;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                TryDispose();
                return false;
            }
        }

        internal bool IsCurrentController(object instance)
        {
            return ReferenceEquals(_controller, instance);
        }

        internal void MarkPlayRequested()
        {
            PlayRequested = true;
            _planetVisualTime?.MarkPlayRequested();
        }

        internal void MarkPlayReturned() { PlayReturned = true; }

        /// <summary>
        /// 绑定本 session 的 generation authority（复用 FrameCaptureDriver 的单调 generation）。
        /// 必须在 editor.Play() 之前调用，否则 ownership acquisition 不会启动。
        /// </summary>
        internal bool ArmPlanetVisualTimeOwnership(long generation)
        {
            if (_planetVisualTime == null)
            {
                Log.Warn("PlaybackLifecycleHandoff: PlanetVisualTimeOwnership 未安装，无法绑定 generation");
                return false;
            }
            _planetVisualTime.Arm(generation);
            return true;
        }

        /// <summary>
        /// scheduler 的 Prepare 边界（pre-entry 与 gameplay 共用）唯一的视觉时间定位入口。
        /// </summary>
        internal bool TryPreparePlanetVisualTime(
            long generation, long absoluteOutputFrameIndex, int frozenOutputFps, out string error)
        {
            if (_planetVisualTime == null)
            {
                error = "planet-visual-time-ownership-unavailable";
                return false;
            }
            return _planetVisualTime.TryPrepareFrame(
                generation, absoluteOutputFrameIndex, frozenOutputFps, out error);
        }

        internal bool HasResidualPlanetVisualTimeOwnership =>
            _planetVisualTime != null && _planetVisualTime.HasResidualOwnership;

        internal string DescribePlanetVisualTimeState =>
            _planetVisualTime == null ? "unavailable" : _planetVisualTime.DescribeState();

        /// <summary>
        /// ownership acquisition 成功后记录既有 lifecycle marker。
        /// 失败路径不会走到这里：fail-closed 由 ownership 自己转成 session 失败请求。
        /// </summary>
        private void MarkMusicScheduled()
        {
            SawMusicScheduled = true;
            Log.Info("MasterTimeline lifecycle marker: OnMusicScheduled handoff=" + MarkerStatus);
        }

        /// <summary>
        /// 生命周期是否已真正到达 playback 状态：Play 已请求并返回、Start / OnMusicScheduled /
        /// Countdown / PlayerControl 均已观测、当前已提交 state == PlayerControl 且玩家存活。
        /// **不含** paused 条件——paused 是 runtime 条件，只有在到达这里之后仍未清零才是异常。
        /// </summary>
        internal bool IsReadyExceptPaused(object state, bool playerAlive)
        {
            string current = state is Enum e ? e.ToString() : Convert.ToString(state);
            return PlayRequested && PlayReturned && SawStart && SawMusicScheduled && SawCountdown && SawPlayerControl &&
                   string.Equals(current, "PlayerControl", StringComparison.Ordinal) && playerAlive;
        }

        internal bool IsReady(object state, bool playerAlive, bool paused)
        {
            return !paused && IsReadyExceptPaused(state, playerAlive);
        }

        private void OnStateChanged(Enum committedState)
        {
            if (!PlayRequested || committedState == null) return;

            string state = committedState.ToString();
            if (state == "Start") SawStart = true;
            else if (state == "Countdown") SawCountdown = true;
            else if (state == "PlayerControl") SawPlayerControl = true;
            if (state == "Start" || state == "Countdown" || state == "PlayerControl")
                Log.Info("MasterTimeline lifecycle marker: state=" + state + " handoff=" + MarkerStatus);
        }

        private static PlaybackLifecycleHandoff Active { get; set; }

        public bool TryDispose()
        {
            if (ReferenceEquals(Active, this)) Active = null;

            bool success = true;

            // Planet 视觉时间 ownership：先失效 generation，再精确 Unpatch，最后按契约恢复。
            // 它的失败与其它 cleanup 失败同等对待，绝不静默清空。
            if (_planetVisualTime != null)
            {
                try
                {
                    if (_planetVisualTime.TryDispose(out string ownershipCleanupError))
                        _planetVisualTime = null;
                    else
                    {
                        success = false;
                        Log.Warn("PlaybackLifecycleHandoff: PlanetVisualTimeOwnership cleanup 未收敛: " +
                                 (ownershipCleanupError ?? "unknown"));
                    }
                }
                catch (Exception ex)
                {
                    success = false;
                    Log.Exception("PlaybackLifecycleHandoff: PlanetVisualTimeOwnership cleanup 异常", ex);
                }
            }

            if (_changedSubscribed)
            {
                try
                {
                    if (_changedEvent == null || _stateMachine == null || _changedHandler == null)
                        success = false;
                    else
                    {
                        _changedEvent.RemoveEventHandler(_stateMachine, _changedHandler);
                        _changedSubscribed = false;
                    }
                }
                catch (Exception ex)
                {
                    success = false;
                    Log.Exception("PlaybackLifecycleHandoff: Changed 事件取消订阅失败", ex);
                }
            }

            if (!_changedSubscribed)
            {
                _changedEvent = null;
                _changedHandler = null;
                _stateMachine = null;
            }
            if (!_changedSubscribed && !HasResidualPlanetVisualTimeOwnership)
                _controller = null;

            return success && !_changedSubscribed && !HasResidualPlanetVisualTimeOwnership;
        }

        void IDisposable.Dispose()
        {
            TryDispose();
        }

        private static object ReadMember(object instance, string name)
        {
            if (instance == null) return null;
            Type type = instance.GetType();
            try
            {
                PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null) return property.GetValue(instance, null);
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return field?.GetValue(instance);
            }
            catch { return null; }
        }

        private static string ToState(object value)
        {
            return value is Enum e ? e.ToString() : Convert.ToString(value);
        }
    }
}
