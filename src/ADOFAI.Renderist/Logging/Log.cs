using System;

namespace ADOFAI.Renderist.Logging
{
    /// <summary>
    /// Thin static wrapper around UMM's ModLogger.
    /// All messages are prefixed with "[Renderist] " for grep-friendly logs.
    /// Debug-level output is gated on <see cref="Settings.VerboseLogging"/>.
    /// </summary>
    internal static class Log
    {
        private const string Prefix = "[Renderist] ";

        public static void Info(string message)
        {
            ModEntry.Logger?.Log(Prefix + message);
        }

        public static void Warn(string message)
        {
            ModEntry.Logger?.Warning(Prefix + message);
        }

        public static void Error(string message)
        {
            ModEntry.Logger?.Error(Prefix + message);
        }

        public static void Exception(string context, Exception ex)
        {
            ModEntry.Logger?.LogException(Prefix + context, ex);
        }

        public static void Debug(string message)
        {
            if (ModEntry.Settings != null && ModEntry.Settings.VerboseLogging)
            {
                ModEntry.Logger?.Log(Prefix + "[debug] " + message);
            }
        }
    }
}
