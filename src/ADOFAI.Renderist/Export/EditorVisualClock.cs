using System;
using System.Reflection;
using HarmonyLib;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 保留的 Forced Visual Clock bridge；当前尚未接入 production MasterTimeline。
    ///
    /// 职责（严格限定）：
    ///   * 保存原时间锚点（startSongPosition / pitch）
    ///   * 建立锚点
    ///   * 设置当前 output frame 的视觉时间（forcedSongPosition）
    ///   * 通过 songposition_minusi 的 getter Postfix / setter Prefix
    ///     在 Active 期间维持 forcedSongPosition
    ///   * 撤销 Patch 与关闭强制
    ///
    /// 绝不包含：DVA catch-up、scrPlayer.Hit、Planet 对齐、Multipress 清理。
    /// 生产路径不依赖 Diagnostics.EditorVisualClockPoc。
    /// </summary>
    internal static class EditorVisualClock
    {
        private static bool _active;
        private static bool _hooksRegistered;
        private static double _forcedSongPosition;

        public static bool IsActive => _active;

        public static double ForcedSongPosition => _forcedSongPosition;

        /// <summary>当前 forced songposition（只读，诊断/日志用）。</summary>
        public static double Current => _forcedSongPosition;

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
            UnregisterForcedClockHooks();

            try
            {
                Harmony harmony = ModEntry.Harmony;
                if (harmony == null) return false;

                if (!EditorGameReflection.TryGetSongPositionAccessors(out MethodInfo getter, out MethodInfo setter))
                {
                    return false;
                }

                if (getter != null)
                {
                    harmony.Patch(getter,
                        postfix: new HarmonyMethod(typeof(EditorVisualClock), nameof(SongPosGetterPostfix)));
                }
                if (setter != null)
                {
                    harmony.Patch(setter,
                        prefix: new HarmonyMethod(typeof(EditorVisualClock), nameof(SongPosSetterPrefix)));
                }

                _hooksRegistered = true;
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("EditorVisualClock: 注册 forced clock Harmony Patch 失败", ex);
                // 部分注册也立即撤销，避免留下半套 hook。
                UnregisterForcedClockHooks();
                return false;
            }
        }

        public static void UnregisterForcedClockHooks()
        {
            if (!_hooksRegistered) return;

            Harmony harmony = ModEntry.Harmony;
            if (harmony != null)
            {
                try
                {
                    if (EditorGameReflection.TryGetSongPositionAccessors(out MethodInfo getter, out MethodInfo setter))
                    {
                        if (getter != null) harmony.Unpatch(getter, HarmonyPatchType.All, ModEntry.HarmonyId);
                        if (setter != null) harmony.Unpatch(setter, HarmonyPatchType.All, ModEntry.HarmonyId);
                    }
                }
                catch (Exception ex)
                {
                    Log.Exception("EditorVisualClock: 撤销 forced clock Harmony Patch 失败", ex);
                }
            }

            _hooksRegistered = false;
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
