using System;

// Only the native reads at the Camera owner's boundary are replaced. Tests execute
// the production owner and authorizer, including their actual Harmony callbacks.
// These stand-ins do not model Unity's PlayerLoop or verify Mono patch installation.
namespace UnityEngine
{
    internal class Object
    {
        public int GetInstanceID() { return GetHashCode(); }
    }

    internal static class Time
    {
        public static int frameCount { get; set; }
        public static float deltaTime { get; set; } = 0.125f;
        public static float time { get; set; } = 100f;
    }
}

namespace ADOFAI.Renderist.Logging
{
    internal static class Log
    {
        public static void Info(string message) { }
        public static void Warn(string message) { }
        public static void Error(string message) { }
        public static void Exception(string message, Exception exception) { }
    }
}
