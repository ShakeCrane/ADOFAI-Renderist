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
        private const int MaxHitsPerFrame = 16;
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

                while (hitCount < MaxHitsPerFrame)
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
                        result = _mPlayerHit.Invoke(player, new object[] { true });
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

                    if (result is bool && !(bool)result) return true;

                    if (ReferenceEquals(current, ReadMember(player, "currFloor")))
                    {
                        error = "hit-did-not-advance";
                        return false;
                    }
                }

                // 因达到 MaxHitsPerFrame 退出：若仍存在 due floor，则不能把 Hit 拖到下一 output frame。
                {
                    object current = ReadMember(player, "currFloor");
                    object next = ReadMember(current, "nextfloor");
                    if (next != null)
                    {
                        double? nextEntry = ToDouble(ReadMember(next, "entryTime"));
                        if (nextEntry.HasValue && !double.IsNaN(nextEntry.Value) && !double.IsInfinity(nextEntry.Value) &&
                            chartTime + DueToleranceSeconds >= nextEntry.Value)
                        {
                            error = "max-hits-per-frame-exceeded";
                            return false;
                        }
                    }
                }

                Log.Debug("RenderistAutoPlay: maxHitsPerFrame reached=" + MaxHitsPerFrame.ToString(CultureInfo.InvariantCulture));
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
