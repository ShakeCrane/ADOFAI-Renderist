using System;
using System.Reflection;
using HarmonyLib;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// MasterTimeline Deterministic Gameplay Handoff 使用的 Forced Visual Clock bridge。
    ///
    /// 职责（严格限定）：
    ///   * 保存原时间锚点（startSongPosition / pitch）
    ///   * 建立锚点
    ///   * 设置当前 output frame 的视觉时间（forcedSongPosition）
    ///   * 通过 songposition_minusi 的 getter Postfix / setter Prefix
    ///     在 Active 期间维持 forcedSongPosition
    ///   * 精确撤销 Patch 与关闭强制
    ///
    /// 绝不包含：DVA catch-up、scrPlayer.Hit、Planet 对齐、Multipress 清理。
    /// </summary>
    internal static class EditorVisualClock
    {
        private static bool _active;
        private static double _forcedSongPosition;

        // 实际成功注册的 original MethodInfo，用于精确撤销（不依赖单一 bool）。
        private static MethodInfo _patchedGetter;
        private static MethodInfo _patchedSetter;

        public static bool IsActive => _active;

        internal static bool HasTrackedHooks => _patchedGetter != null || _patchedSetter != null;

        public static double ForcedSongPosition => _forcedSongPosition;

        public static void SetActive(bool active)
        {
            _active = active;
        }

        public static void SetForcedSongPosition(double value)
        {
            _forcedSongPosition = value;
        }

        // ================================================================
        // Harmony
        // ================================================================

        public static bool RegisterForcedClockHooks()
        {
            if (!UnregisterForcedClockHooks())
            {
                return false;
            }

            try
            {
                Harmony harmony = ModEntry.Harmony;
                if (harmony == null) return false;

                if (!EditorGameReflection.TryGetSongPositionAccessors(out MethodInfo getter, out MethodInfo setter))
                {
                    return false;
                }
                if (getter == null || setter == null)
                {
                    return false;
                }

                _patchedGetter = getter;
                harmony.Patch(getter,
                    postfix: new HarmonyMethod(typeof(EditorVisualClock), nameof(SongPosGetterPostfix)));

                try
                {
                    _patchedSetter = setter;
                    harmony.Patch(setter,
                        prefix: new HarmonyMethod(typeof(EditorVisualClock), nameof(SongPosSetterPrefix)));
                }
                catch (Exception ex)
                {
                    Log.Exception("EditorVisualClock: 注册 setter Prefix 失败", ex);
                    UnregisterForcedClockHooks();
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("EditorVisualClock: 注册 forced clock Harmony Patch 失败", ex);
                UnregisterForcedClockHooks();
                return false;
            }
        }

        public static bool UnregisterForcedClockHooks()
        {
            _active = false;
            bool success = true;

            if (_patchedGetter != null)
            {
                if (PreciseUnpatch(_patchedGetter, nameof(SongPosGetterPostfix)))
                    _patchedGetter = null;
                else
                    success = false;
            }

            if (_patchedSetter != null)
            {
                if (PreciseUnpatch(_patchedSetter, nameof(SongPosSetterPrefix)))
                    _patchedSetter = null;
                else
                    success = false;
            }

            return success && !HasTrackedHooks;
        }

        private static bool PreciseUnpatch(MethodInfo original, string patchName)
        {
            if (original == null) return true;
            Harmony harmony = ModEntry.Harmony;
            if (harmony == null) return false;
            try
            {
                MethodInfo patch = AccessTools.Method(typeof(EditorVisualClock), patchName);
                if (patch == null) return false;
                harmony.Unpatch(original, patch);
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("EditorVisualClock: 精确撤销 " + patchName + " 失败", ex);
                return false;
            }
        }

        // ---- Getter：Active 时返回 forcedSongPosition ----
        private static void SongPosGetterPostfix(ref double __result)
        {
            if (_active)
            {
                __result = _forcedSongPosition;
            }
        }

        // ---- Setter：Active 时把传入值替换为 forcedSongPosition ----
        private static void SongPosSetterPrefix(ref double value)
        {
            if (_active)
            {
                value = _forcedSongPosition;
            }
        }
    }
}
