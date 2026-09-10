using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// MasterTimeline 的 gameplay handoff：只在 Conductor.Update Postfix 中消费 due floor，
    /// 最终仍调用 ADOFAI 官方 scrPlayer.Hit(true)。
    ///
    /// 该类不拥有时间，也不模拟 replay。RDC.auto 只在单次官方 Hit 调用期间临时置 true，
    /// 以复用游戏已有的 auto-hit 分支，调用完成后立即恢复原值。
    /// </summary>
    internal static class RenderistAutoPlay
    {
        private const string StatePlayerControl = "PlayerControl";
        private const double DueToleranceSeconds = 0.0001;

        private static bool _resolved;
        private static Type _tAdoBase;
        private static Type _tPlayer;
        private static Type _tPlanet;
        private static Type _tRdc;
        private static MethodInfo _mPlayerHit;
        private static MethodInfo _mPlanetRefresh;
        private static MethodInfo _mSetAllPlayerResponsive;

        internal static bool EnsureAvailable(out string error)
        {
            error = null;
            try
            {
                ResolveTypes();
                if (_tAdoBase == null || _tPlayer == null || _tPlanet == null || _tRdc == null ||
                    _mPlayerHit == null || _mPlanetRefresh == null)
                {
                    error = "autoplay-api-unavailable";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static bool CatchUp(int frameIndex, double chartTime, out int hitCount, out string error)
        {
            hitCount = 0;
            error = null;

            if (!EnsureAvailable(out error)) return false;

            try
            {
                object controller = ReadStatic(_tAdoBase, "controller");
                object player = ReadMember(controller, "playerOne");
                if (controller == null || player == null || ReadBool(ReadMember(controller, "paused")) == true ||
                    !string.Equals(ToText(ReadMember(controller, "state")), StatePlayerControl, StringComparison.Ordinal) ||
                    ReadBool(ReadMember(player, "alive")) == false)
                {
                    return true;
                }

                // Progression safety bound：单次 CatchUp（一个输出帧）的合法命中数不会
                // 超过当前谱面 floor 总数（每次 Hit 都要求 seqID 严格向前）。上界来自
                // 实际谱面而非固定魔数；floor collection 不可读时 fail-closed，不猜测。
                IList floors = EditorGameReflection.ReadFloorsList();
                if (floors == null || floors.Count < 1)
                {
                    error = "autoplay-floors-unavailable";
                    return false;
                }
                int progressionBound = floors.Count;

                while (hitCount < progressionBound)
                {
                    object current = ReadMember(player, "currFloor");
                    object next = ReadMember(current, "nextfloor");
                    if (current == null || next == null) return true;

                    double? nextEntry = ToDouble(ReadMember(next, "entryTime"));
                    if (!nextEntry.HasValue || double.IsNaN(nextEntry.Value) || double.IsInfinity(nextEntry.Value))
                    {
                        error = "next-entry-time-unavailable";
                        return false;
                    }

                    if (chartTime + DueToleranceSeconds < nextEntry.Value) return true;

                    int beforeFloor = ToInt(ReadMember(current, "seqID"));
                    int nextFloor = ToInt(ReadMember(next, "seqID"));
                    if (beforeFloor < 0 || nextFloor < 0)
                    {
                        error = "autoplay-floor-seq-unavailable";
                        return false;
                    }
                    object planet = ReadMember(ReadMember(player, "planetarySystem"), "chosenPlanet");
                    if (planet != null)
                    {
                        _mPlanetRefresh.Invoke(planet, null);
                        AlignPlanet(current, planet);
                    }

                    // RDC.auto 事务：必须先成功读取旧值 → 写 true → 官方 Hit → 成功恢复原值。
                    bool? oldAuto = ReadStaticBool(_tRdc, "auto");
                    if (!oldAuto.HasValue)
                    {
                        error = "rdc-auto-read-failed";
                        return false;
                    }

                    Exception hitException = null;
                    bool restoreFailed = false;
                    object result = null;
                    try
                    {
                        if (!SetStatic(_tRdc, "auto", true))
                        {
                            error = "rdc-auto-write-failed";
                            return false;
                        }

                        PrepareHitState(player, controller);
#if DEBUG
                        // TEMPORARY fault injection F4（验证后随 FaultInjection.cs 一并删除）：
                        // 跳过官方 Hit 调用但仍走原事务路径，模拟 progression 不前进。
                        if (FaultInjection.Consume(ref FaultInjection.F4_SkipOneHit, "F4"))
                        {
                            result = null;
                        }
                        else
                        {
                            result = _mPlayerHit.Invoke(player, new object[] { true });
                        }
#else
                        result = _mPlayerHit.Invoke(player, new object[] { true });
#endif
                        hitCount++;

                        Log.Info("MasterTimeline Hit: frameIndex=" + frameIndex.ToString(CultureInfo.InvariantCulture) +
                                 " forcedChartTime=" + chartTime.ToString("0.######", CultureInfo.InvariantCulture) +
                                 " currentFloor=" + beforeFloor.ToString(CultureInfo.InvariantCulture) +
                                 " nextFloor=" + nextFloor.ToString(CultureInfo.InvariantCulture) +
                                 " nextFloorEntryTime=" + nextEntry.Value.ToString("0.######", CultureInfo.InvariantCulture) +
                                 " due=true hitResult=" + ToText(result) +
                                 " afterFloor=" + ToInt(ReadMember(ReadMember(player, "currFloor"), "seqID")).ToString(CultureInfo.InvariantCulture) +
                                 " alive=" + ToText(ReadMember(player, "alive")));
                    }
                    catch (Exception ex)
                    {
                        hitException = ex;
                    }
                    finally
                    {
                        if (!SetStatic(_tRdc, "auto", oldAuto.Value))
                        {
                            restoreFailed = true;
                        }
                    }

                    if (restoreFailed)
                    {
                        if (hitException != null)
                        {
                            Log.Exception("RenderistAutoPlay: Hit 抛异常且 RDC.auto 恢复失败", hitException);
                        }
                        else
                        {
                            Log.Error("RenderistAutoPlay: RDC.auto 恢复失败");
                        }
                        error = "rdc-auto-restore-failed";
                        return false;
                    }

                    if (hitException != null)
                    {
                        Log.Exception("RenderistAutoPlay: 官方 Hit 调用抛异常", hitException);
                        error = "scr-player-hit-threw: " + hitException.Message;
                        return false;
                    }

                    // due-floor transaction 的成功条件是 canonical floor progression
                    // 严格单调向前：after == before（无推进）与 after < before（倒退）
                    // 都立即 fail-closed；多格前进允许（当前官方路径未观察到，仅记录）。
                    // 不把 Hit(bool) 返回 false 单独解释为“无需推进”。
                    object afterCurrent = ReadMember(player, "currFloor");
                    int afterFloor = ToInt(ReadMember(afterCurrent, "seqID"));
                    if (afterCurrent == null || afterFloor < 0)
                    {
                        error = "autoplay-floor-seq-unavailable-after-hit";
                        return false;
                    }
                    if (afterFloor <= beforeFloor)
                    {
                        error = "hit-progression-not-forward";
                        return false;
                    }
                    if (afterFloor != nextFloor)
                    {
                        Log.Debug("RenderistAutoPlay: Hit advanced floor progression from " +
                                  beforeFloor.ToString(CultureInfo.InvariantCulture) + " to " +
                                  afterFloor.ToString(CultureInfo.InvariantCulture) +
                                  " (expected next " +
                                  nextFloor.ToString(CultureInfo.InvariantCulture) + "); accepting forward progression.");
                    }

                    if (result is bool accepted && !accepted)
                    {
                        Log.Debug("RenderistAutoPlay: Hit returned false but currFloor advanced to expected next floor; accepting observed progression.");
                    }
                }

                // 因达到 progression bound 退出：一致谱面下不可达（bound = floor 总数且
                // 每次 Hit 都严格向前）。若此时仍有 due floor，说明 progression 记账
                // 异常，fail-closed，不得把 Hit 拖到下一 output frame。
                {
                    object current = ReadMember(player, "currFloor");
                    object next = ReadMember(current, "nextfloor");
                    if (next != null)
                    {
                        double? nextEntry = ToDouble(ReadMember(next, "entryTime"));
                        if (nextEntry.HasValue && !double.IsNaN(nextEntry.Value) && !double.IsInfinity(nextEntry.Value) &&
                            chartTime + DueToleranceSeconds >= nextEntry.Value)
                        {
                            error = "autoplay-progression-bound-exceeded";
                            return false;
                        }
                    }
                }

                Log.Debug("RenderistAutoPlay: progression bound reached=" +
                          progressionBound.ToString(CultureInfo.InvariantCulture));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void ResolveTypes()
        {
            if (_resolved) return;

            Assembly gameAssembly = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name == "Assembly-CSharp")
                {
                    gameAssembly = assembly;
                    break;
                }
            }

            if (gameAssembly == null) return;

            _tAdoBase = gameAssembly.GetType("ADOBase");
            _tPlayer = gameAssembly.GetType("scrPlayer");
            _tPlanet = gameAssembly.GetType("scrPlanet");
            _tRdc = gameAssembly.GetType("RDC");

            BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _mPlayerHit = _tPlayer?.GetMethod("Hit", instance, null, new[] { typeof(bool) }, null);
            _mPlanetRefresh = _tPlanet?.GetMethod("Update_RefreshAngles", instance, null, Type.EmptyTypes, null);
            if (_tAdoBase != null)
            {
                Type managerType = ReadStatic(_tAdoBase, "playerManager")?.GetType();
                _mSetAllPlayerResponsive = managerType?.GetMethod("SetAllPlayerResponsive", instance, null,
                    new[] { typeof(bool) }, null);
            }

            _resolved = true;
        }

        private static void PrepareHitState(object player, object controller)
        {
            SetMember(controller, "paused", false);
            SetMember(controller, "multipressPenalty", false);
            SetMember(controller, "multipressAndHasPressedFirstPress", false);
            SetMember(player, "consecMultipressCounter", 0);

            object keyTimes = ReadMember(player, "keyTimes");
            MethodInfo clear = keyTimes?.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            clear?.Invoke(keyTimes, null);

            object manager = ReadStatic(_tAdoBase, "playerManager");
            _mSetAllPlayerResponsive?.Invoke(manager, new object[] { true });
        }

        private static void AlignPlanet(object current, object planet)
        {
            if (current == null || planet == null || ReadBool(ReadMember(current, "midSpin")) == true) return;

            object target = ReadMember(planet, "targetExitAngle");
            if (target == null) return;

            SetMember(planet, "angle", target);
            SetMember(planet, "cachedAngle", target);
        }

        private static object ReadStatic(Type type, string name)
        {
            if (type == null) return null;
            try
            {
                PropertyInfo property = type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null) return property.GetValue(null, null);
                FieldInfo field = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                return field?.GetValue(null);
            }
            catch { return null; }
        }

        private static object ReadMember(object instance, string name)
        {
            if (instance == null) return null;
            try
            {
                Type type = instance.GetType();
                PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null) return property.GetValue(instance, null);
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return field?.GetValue(instance);
            }
            catch { return null; }
        }

        private static bool SetStatic(Type type, string name, object value)
        {
            if (type == null) return false;
            try
            {
                PropertyInfo property = type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && property.CanWrite) { property.SetValue(null, value, null); return true; }
                FieldInfo field = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && !field.IsInitOnly) { field.SetValue(null, value); return true; }
            }
            catch { }
            return false;
        }

        private static bool SetMember(object instance, string name, object value)
        {
            if (instance == null) return false;
            try
            {
                Type type = instance.GetType();
                PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && property.CanWrite) { property.SetValue(instance, value, null); return true; }
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && !field.IsInitOnly) { field.SetValue(instance, value); return true; }
            }
            catch { }
            return false;
        }

        private static bool? ReadStaticBool(Type type, string name)
        {
            object value = ReadStatic(type, name);
            return value == null ? (bool?)null : ReadBool(value);
        }

        private static bool? ReadBool(object value)
        {
            if (value == null) return null;
            try { return Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static double? ToDouble(object value)
        {
            if (value == null) return null;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static string ToText(object value)
        {
            return value == null ? "null" : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static int ToInt(object value)
        {
            if (value == null) return -1;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return -1; }
        }
    }
}
