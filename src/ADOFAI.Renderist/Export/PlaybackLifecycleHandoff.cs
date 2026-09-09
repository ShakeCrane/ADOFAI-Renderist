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
        private object _controller;
        private object _stateMachine;
        private EventInfo _changedEvent;
        private Action<Enum> _changedHandler;
        private MethodInfo _musicScheduled;

        internal bool PlayRequested { get; private set; }
        internal bool PlayReturned { get; private set; }
        internal bool SawStart { get; private set; }
        internal bool SawCountdown { get; private set; }
        internal bool SawPlayerControl { get; private set; }
        internal bool SawMusicScheduled { get; private set; }

        internal string MarkerStatus => "requested=" + PlayRequested + ",returned=" + PlayReturned + ",start=" + SawStart + ",music=" + SawMusicScheduled + ",countdown=" + SawCountdown + ",playerControl=" + SawPlayerControl;

        internal PlaybackLifecycleHandoff(Harmony harmony)
        {
            _harmony = harmony ?? throw new ArgumentNullException(nameof(harmony));
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

                _musicScheduled = _controller.GetType().GetMethod("OnMusicScheduled",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (_musicScheduled == null)
                {
                    error = "on-music-scheduled-unavailable";
                    return false;
                }

                _changedHandler = OnStateChanged;
                _changedEvent.AddEventHandler(_stateMachine, _changedHandler);
                _harmony.Patch(_musicScheduled,
                    postfix: new HarmonyMethod(typeof(PlaybackLifecycleHandoff), nameof(OnMusicScheduledPostfix)));
                Log.Info("MasterTimeline lifecycle handoff established: state=" + initialState + " startSatisfied=" + SawStart);
                Active = this;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Dispose();
                return false;
            }
        }

        internal bool IsCurrentController(object instance)
        {
            return ReferenceEquals(_controller, instance);
        }

        internal void MarkPlayRequested() { PlayRequested = true; }
        internal void MarkPlayReturned() { PlayReturned = true; }

        internal bool IsReady(object state, bool playerAlive, bool paused)
        {
            string current = state is Enum e ? e.ToString() : Convert.ToString(state);
            return PlayRequested && PlayReturned && SawStart && SawMusicScheduled && SawCountdown && SawPlayerControl &&
                   string.Equals(current, "PlayerControl", StringComparison.Ordinal) && playerAlive && !paused;
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

        private static void OnMusicScheduledPostfix(object __instance)
        {
            PlaybackLifecycleHandoff handoff = Active;
            if (handoff != null && handoff.IsCurrentController(__instance) && handoff.PlayRequested)
            {
                handoff.SawMusicScheduled = true;
                Log.Info("MasterTimeline lifecycle marker: OnMusicScheduled handoff=" + handoff.MarkerStatus);
            }
        }

        public void Dispose()
        {
            if (ReferenceEquals(Active, this)) Active = null;

            try
            {
                if (_changedEvent != null && _stateMachine != null && _changedHandler != null)
                    _changedEvent.RemoveEventHandler(_stateMachine, _changedHandler);
            }
            catch { }

            try
            {
                if (_musicScheduled != null)
                    _harmony.Unpatch(_musicScheduled, HarmonyPatchType.All, ModEntry.HarmonyId);
            }
            catch { }

            _changedEvent = null;
            _changedHandler = null;
            _musicScheduled = null;
            _stateMachine = null;
            _controller = null;
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
