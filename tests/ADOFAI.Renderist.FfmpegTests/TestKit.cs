using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ADOFAI.Renderist.FfmpegTests
{
    /// <summary>用于把"环境不具备该前置条件"与"断言失败"区分开。</summary>
    internal sealed class SkipTestException : Exception
    {
        public SkipTestException(string message) : base(message)
        {
        }
    }

    /// <summary>极简测试运行器：不引入测试框架依赖，保持离线可构建。</summary>
    internal static class TestKit
    {
        private static int _passed;
        private static int _failed;
        private static int _skipped;
        private static readonly List<string> FailureMessages = new List<string>();

        public static int Passed { get { return _passed; } }
        public static int Failed { get { return _failed; } }
        public static int Skipped { get { return _skipped; } }
        public static IReadOnlyList<string> Failures { get { return FailureMessages; } }

        public static void Run(string name, Action body)
        {
            try
            {
                body();
                _passed++;
                Console.WriteLine("PASS  " + name);
            }
            catch (SkipTestException ex)
            {
                _skipped++;
                Console.WriteLine("SKIP  " + name + "  (" + ex.Message + ")");
            }
            catch (Exception ex)
            {
                _failed++;
                string message = name + ": " + ex.Message;
                FailureMessages.Add(message);
                Console.WriteLine("FAIL  " + name);
                Console.WriteLine("        " + ex.Message);
            }
        }

        public static void Check(bool condition, string message)
        {
            if (!condition)
                throw new Exception(message);
        }

        public static void CheckEqual(object expected, object actual, string what)
        {
            bool equal = expected == null ? actual == null : expected.Equals(actual);
            if (!equal)
            {
                throw new Exception(what + ": expected <" + Describe(expected) + "> actual <" + Describe(actual) + ">");
            }
        }

        public static void CheckNotEmpty(string value, string what)
        {
            if (string.IsNullOrEmpty(value))
                throw new Exception(what + ": expected non-empty");
        }

        private static string Describe(object value)
        {
            return value == null ? "null" : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static string NewWorkDirectory(string root, string label)
        {
            string path = Path.Combine(root, label + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(path);
            return path;
        }

        public static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
            }
        }
    }
}
