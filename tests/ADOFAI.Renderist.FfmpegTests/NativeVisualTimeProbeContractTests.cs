using System;
using System.IO;

namespace ADOFAI.Renderist.FfmpegTests
{
    /// <summary>
    /// TEMP DIAGNOSTIC（与 Export/NativeVisualTimeProbe.cs 同批删除）：
    /// 用自动化方式固定"原生视觉时间只读探针"的边界与接线，避免它以任何形式变成行为修改。
    ///
    /// 断言的是**源码契约**：探针文件里不得出现任何时间/Transform/DOTween/粒子变更调用；
    /// 接线只允许出现在列出的既有执行点上；探针必须自标识为临时诊断构建。
    /// </summary>
    internal static class NativeVisualTimeProbeContractTests
    {
        private const string ProbePath = "Export/NativeVisualTimeProbe.cs";
        private const string SchedulerPath = "Export/DeterministicFrameScheduler.cs";

        public static void Run()
        {
            ReadOnlyBoundaryTests();
            WiringTests();
            IdentificationTests();
            DiscoveryTests();
        }

        // ---------------------------------------------------------------- read-only boundary

        private static void ReadOnlyBoundaryTests()
        {
            TestKit.Run("nvt probe: the probe never mutates time, transforms, tweens or particles", () =>
            {
                string probe = Source(ProbePath);

                // 时间 authority：绝不写 Time.timeScale / Time.captureFramerate / Application.targetFrameRate。
                foreach (string forbidden in new[]
                {
                    "Time.timeScale =", "Time.timeScale=",
                    "Time.captureFramerate =", "Time.captureFramerate=",
                    "Application.targetFrameRate =", "Application.targetFrameRate=",
                    "QualitySettings.vSyncCount =",
                })
                {
                    TestKit.Check(!probe.Contains(forbidden),
                        "the probe must never write time authority state: " + forbidden);
                }

                // Transform / GameObject / Camera：只读，绝不写入。
                foreach (string forbidden in new[]
                {
                    ".position =", ".localPosition =", ".rotation =", ".eulerAngles =",
                    ".SetParent(", ".SetActive(", "Destroy(", "Instantiate(",
                })
                {
                    TestKit.Check(!probe.Contains(forbidden),
                        "the probe must never write scene state: " + forbidden);
                }

                // ParticleSystem：绝不调用任何变更 API（GetParticles 是只读复制，明确允许）。
                foreach (string forbidden in new[]
                {
                    ".Play(", ".Pause(", ".Stop(", ".Clear(", ".Simulate(", ".Emit(",
                    "SetParticles(", "SetParticlesAndTrails(", ".StopEmitting(",
                })
                {
                    TestKit.Check(!probe.Contains(forbidden),
                        "the probe must never drive a ParticleSystem: " + forbidden);
                }

                // DOTween / Harmony / 帧事务：探针不参与任何 ownership。
                foreach (string forbidden in new[]
                {
                    "DG.Tweening", "DOTween.", "_outputFrameIndex =", "_status =", "CommitFrame(",
                    "RequestStop(", "FinishAsync(", "TryWriteFrame(",
                })
                {
                    TestKit.Check(!probe.Contains(forbidden),
                        "the probe must not touch export ownership: " + forbidden);
                }

                TestKit.Check(probe.Contains("GetParticles(_particleSample)"),
                    "the only particle read is a bounded GetParticles sample");
                TestKit.Check(probe.Contains("internal const bool Enabled = true;"),
                    "the probe has a single compile-time switch");
                TestKit.Check(probe.Contains("internal const int MaxRecords ="),
                    "the probe has a global record budget (no unbounded logging)");
                TestKit.Check(probe.Contains("TEMP DIAGNOSTIC"),
                    "the probe is explicitly marked as a temporary diagnostic");
            });

            TestKit.Run("nvt probe: every sample is wrapped so it can never throw into the scheduler", () =>
            {
                string probe = Source(ProbePath);
                TestKit.Check(probe.Contains("catch (Exception ex)") && probe.Contains("Disable(ex)"),
                    "probe failures disable the probe instead of propagating");
                TestKit.Check(probe.Contains("_disabled"),
                    "a disabled probe records nothing further");

                // 异常隔离必须是**分级**的：一次"现在还没准备好"（例如 players 尚未建立时
                // scrController.get_playerOne() 的 players[0] 越界）不得永久禁用整个 session；
                // 单个 ParticleSystem 读不出来也不得让整条记录失败。
                TestKit.Check(probe.Contains("TryProperty"),
                    "read-only property reads must be exception-isolated from the global disable path");
                TestKit.Check(probe.Contains(":propertyFailure="),
                    "an isolated property failure must still be recorded as the per-record reason");
                TestKit.Check(probe.Contains("read-failed:"),
                    "one unreadable ParticleSystem must not fail the whole record");
            });
        }

        // ---------------------------------------------------------------- wiring

        private static void WiringTests()
        {
            TestKit.Run("nvt probe: sampling is wired only at the listed existing execution points", () =>
            {
                string scheduler = Source(SchedulerPath);
                string probe = Source(ProbePath);

                // 由 scheduler 传入的观测阶段（每次采样都由接线点显式给出）。
                foreach (string phase in new[]
                {
                    "\"prepare\"", "\"prepare-preentry\"", "\"post-conductor\"",
                    "\"eof\"", "\"commit\"", "\"preentry-freeze\"", "\"preentry-restore\"",
                    "\"pre-hit\"", "\"g-plus-one\"",
                })
                {
                    TestKit.Check(scheduler.Contains(phase), "missing sampling point: " + phase);
                }

                // 由探针自身产生的阶段（入口只有这三个，且两个等待窗口都被节流）。
                foreach (string phase in new[]
                {
                    "\"session-start\"", "\"await-delivery\"", "\"await-delivery-limit\"",
                    "\"hidden-frame\"", "\"hidden-frame-limit\"",
                    "ph=budget-exhausted", "ph=probe-disabled",
                })
                {
                    TestKit.Check(probe.Contains(phase), "missing probe-side phase: " + phase);
                }

                // 采样必须来自探针自身的三个入口，且调用点数量有界（防止逐 Update 刷采样）。
                int samples = CountOccurrences(scheduler, "NativeVisualTimeProbe.Sample(");
                int delivery = CountOccurrences(scheduler, "NativeVisualTimeProbe.SampleAwaitingDelivery(");
                int sessionStart = CountOccurrences(scheduler, "NativeVisualTimeProbe.SampleSessionStart(");
                int hidden = CountOccurrences(scheduler, "NativeVisualTimeProbe.SampleHiddenPhase(");
                TestKit.Check(samples == 10,
                    "expected exactly 10 direct sample calls, found " + samples);
                TestKit.Check(delivery == 1,
                    "expected exactly 1 throttled awaiting-delivery entry, found " + delivery);
                TestKit.Check(sessionStart == 1,
                    "expected exactly 1 session-start call, found " + sessionStart);
                TestKit.Check(hidden == 1,
                    "expected exactly 1 throttled hidden-frame entry, found " + hidden);

                // 两个高频入口都必须节流（不是每个 Tick / 每个 Unity frame 都记）。
                TestKit.Check(probe.Contains("DeliveryWindowHeadTicks") &&
                              probe.Contains("DeliveryWindowStrideTicks") &&
                              probe.Contains("DeliveryWindowLimit"),
                    "the awaiting-delivery window must be throttled and bounded");
                TestKit.Check(probe.Contains("HiddenFrameHeadTicks") &&
                              probe.Contains("HiddenFrameStrideTicks") &&
                              probe.Contains("HiddenFrameLimit"),
                    "the hidden boundary phase window must be throttled and bounded");
            });

            TestKit.Run("nvt probe: the wiring adds no control-flow change", () =>
            {
                string scheduler = Source(SchedulerPath);

                // 采样调用点前后不得出现新的 return / throw / RequestStop（即只允许"附加"语义）。
                foreach (string marker in new[]
                {
                    "NativeVisualTimeProbe.Sample(\"prepare\"",
                    "NativeVisualTimeProbe.Sample(\"commit\"",
                    "NativeVisualTimeProbe.Sample(\"eof\"",
                    "NativeVisualTimeProbe.SampleSessionStart(",
                })
                {
                    int index = scheduler.IndexOf(marker, StringComparison.Ordinal);
                    TestKit.Check(index >= 0, "wiring marker exists: " + marker);

                    string window = scheduler.Substring(index, Math.Min(220, scheduler.Length - index));
                    TestKit.Check(!window.Contains("return ") && !window.Contains("throw "),
                        "the wiring must not introduce control flow at: " + marker);
                }
            });
        }

        // ---------------------------------------------------------------- identification

        private static void IdentificationTests()
        {
            TestKit.Run("nvt probe: the diagnostic build identifies itself and stays out of the release zip", () =>
            {
                TestKit.Check(Source("Export/EditorExportSession.cs")
                        .Contains("AppendBool(sb, \"diagnosticProbe\", NativeVisualTimeProbe.Enabled, true)"),
                    "session metadata must flag the diagnostic build");
                TestKit.Check(Source("ModEntry.cs").Contains("TEMP DIAGNOSTIC BUILD"),
                    "ModEntry must warn that this is not the accepted L3-C build");

                string csproj = Source("ADOFAI.Renderist.csproj");
                TestKit.Check(csproj.Contains("UnityEngine.ParticleSystemModule"),
                    "the probe needs the ParticleSystem module reference");
                TestKit.Check(csproj.Contains("TEMP DIAGNOSTIC"),
                    "the temporary reference must be marked for removal");

                int reference = csproj.IndexOf("UnityEngine.ParticleSystemModule", StringComparison.Ordinal);
                int block = csproj.IndexOf("</Reference>", reference, StringComparison.Ordinal);
                TestKit.Check(block > reference, "reference block is well formed");
                string referenceBlock = csproj.Substring(reference, block - reference);
                TestKit.Check(referenceBlock.Contains("<Private>false</Private>"),
                    "a game DLL must never be copied into bin/ or the release zip");
            });
        }

        // ---------------------------------------------------------------- instance discovery

        /// <summary>
        /// 首次取证失败的直接原因：`scrController` 上根本没有名为 `chosenPlanet` 的**字段**
        /// （它是只读属性），于是 `GetField` 返回 null，探针把"反射定位失败"记成了
        /// "ParticleSystem 不存在"。这些断言把正确的发现路径固定下来，防止回归。
        /// </summary>
        private static void DiscoveryTests()
        {
            TestKit.Run("nvt probe: particle discovery uses the DLL-confirmed members, not a guessed field", () =>
            {
                string probe = Source(ProbePath);

                TestKit.Check(!probe.Contains("GetField(\"chosenPlanet\""),
                    "chosenPlanet is a read-only property on scrController: it must never be resolved as a field");

                // 当前 DLL 已确认的发现链：scrController.planetarySystem -> List<scrPlanet>
                // -> scrPlanet.planetRenderer -> PlanetRenderer.tailParticles / tailParticlesCoop。
                foreach (string member in new[]
                {
                    "\"planetarySystem\"", "\"planetRenderer\"",
                    "\"allPlanets\"", "\"planetList\"", "\"availablePlanets\"",
                    "\"tailParticles\"", "\"tailParticlesCoop\"",
                    "\"coreParticles\"", "\"sparks\"", "\"deathExplosion\"",
                })
                {
                    TestKit.Check(probe.Contains(member),
                        "the DLL-confirmed discovery member must be resolved: " + member);
                }

                TestKit.Check(probe.Contains("GetInstanceID() == id"),
                    "particle systems must be de-duplicated by Unity instance identity");
            });

            TestKit.Run("nvt probe: runtime instance absence is reported, never cached as a resolution failure", () =>
            {
                string probe = Source(ProbePath);

                // 类型元数据缓存与运行时实例发现必须是两层：每次采样都重新发现实例。
                TestKit.Check(probe.Contains("DiscoverInstances();"),
                    "every sample must re-discover runtime instances");
                TestKit.Check(probe.Contains("_typesResolved"),
                    "only type metadata may be cached");
                TestKit.Check(probe.Contains("_planetReason"),
                    "a missing instance must record an explicit per-record reason");

                foreach (string reason in new[]
                {
                    "controller-null", "planetarySystem-null:routes=",
                    "no-planet-instances:", "no-particlesystem:",
                })
                {
                    TestKit.Check(probe.Contains(reason),
                        "the failure reason must be explicit, not a silent None: " + reason);
                }

                // 层级扫描只缓存**成功**结果：加入缓存必须发生在拿到结果之后。
                int add = probe.IndexOf("_hierarchyScanned.Add(planetId)", StringComparison.Ordinal);
                int guard = probe.IndexOf("if (found == null || found.Length == 0) return;", StringComparison.Ordinal);
                TestKit.Check(add > 0 && guard > 0 && add > guard,
                    "the hierarchy scan may only be cached after it actually produced a result");
            });

            TestKit.Run("nvt probe: particle age is sampled at every observation point", () =>
            {
                string probe = Source(ProbePath);

                // 旧实现只在 prepare / eof / commit 采样年龄，因此无法回答
                // "同一逻辑帧的等待期间 remainingLifetime 是否减少"。
                TestKit.Check(!probe.Contains("string.Equals(phase, \"prepare\""),
                    "age sampling must not be gated on a phase list any more");
                TestKit.Check(probe.Contains("AppendAges(full);"),
                    "age samples must be appended for every record");
                TestKit.Check(probe.Contains("|age=none:reason="),
                    "a record with no sampleable system must say why");
            });
        }

        // ---------------------------------------------------------------- helpers

        private static int CountOccurrences(string text, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        private static string Source(string relative)
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "ADOFAI.Renderist"))) dir = dir.Parent;
            if (dir == null) throw new Exception("repository source not found");
            return File.ReadAllText(Path.Combine(dir.FullName, "src", "ADOFAI.Renderist", relative)).Replace("\r\n", "\n");
        }
    }
}
