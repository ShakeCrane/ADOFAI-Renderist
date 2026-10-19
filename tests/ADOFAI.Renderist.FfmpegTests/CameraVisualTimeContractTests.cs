using System;
using System.IO;

namespace ADOFAI.Renderist.FfmpegTests
{
    /// <summary>
    /// 0.3.12.0 Gate A 的**接线契约**测试。
    ///
    /// 断言的是源码契约而不是某次实机的具体数值：
    ///   * Camera 原生时间入口只替换已确认的时间调用点，且 IL 形态不符时整体拒绝安装；
    ///   * 授权合同的接线只出现在列出的既有执行点上（Prepare / EOF 前 / cleanup）；
    ///   * 未接入的 owner（Tween / Particle）在本轮**不**被登记为必须回执的消费者；
    ///   * MasterTimeline / L3-C Finalizing / RGB24 authority 没有被本功能改写。
    ///
    /// 目标 DLL 的实际签名与 IL 位置由 tools/ilprobe 对当前 Assembly-CSharp.dll 逐一核对
    /// （见工作报告的 Patch 签名表）；本文件固定的是源码侧不变量。
    /// </summary>
    internal static class CameraVisualTimeContractTests
    {
        private const string CameraOwnerPath = "Export/CameraNativeTimeOwnership.cs";
        private const string AuthorizerPath = "Export/VisualFrameAuthorizer.cs";
        private const string SchedulerPath = "Export/DeterministicFrameScheduler.cs";
        private const string DriverPath = "Export/FrameCaptureDriver.cs";

        public static void Run()
        {
            TranspilerBoundaryTests();
            InstallFailureBoundaryTests();
            SchedulerWiringTests();
            EndOfFrameGateTests();
            AuthorityBoundaryTests();
        }

        // ---------------------------------------------------------------- transpiler boundary

        private static void TranspilerBoundaryTests()
        {
            TestKit.Run("camera owner: the narrow transpiler declares the exact DLL-verified site counts", () =>
            {
                string owner = Source(CameraOwnerPath);

                // 当前 Assembly-CSharp.dll 0.4.3.0 的 exact 计数（tools/ilprobe 已逐一核对）：
                //   scrCamera.Update                  3 x Time.get_deltaTime   @0197 / 054C / 05B8
                //   scrCamera.UpdateSize              1 x Time.get_deltaTime   @0013
                //   scrCamera.CheckUpdateFrameRateScreen    1 x Time.get_time  @0018
                //   scrCamera.UpdateCustomFrameRateScreen   1 x Time.get_time  @0001
                foreach (string entry in new[]
                {
                    "{ UpdateMethodName, 3 }",
                    "{ UpdateSizeMethodName, 1 }",
                    "{ CheckUpdateFrameRateScreenMethodName, 1 }",
                    "{ UpdateCustomFrameRateScreenMethodName, 1 }",
                })
                {
                    TestKit.Check(owner.Contains(entry),
                        "the exact time-read count must stay pinned to the verified DLL: " + entry);
                }

                TestKit.Check(owner.Contains("camera-transpiler-time-read-count-mismatch"),
                    "a replaced-count mismatch must refuse installation");
                TestKit.Check(owner.Contains("camera-transpiler-il-shape-mismatch"),
                    "an unexpected surrounding instruction shape must refuse installation");
                TestKit.Check(owner.Contains("throw new InvalidOperationException"),
                    "refusal must surface as an install failure, never as a silently partial patch");

                // 只在已确认的字段形态上替换：累计字段 + 自定义 FPS 字段。
                foreach (string field in new[]
                {
                    "\"timer\"", "\"rottimer\"", "\"coltimer\"", "\"pulsetimer\"", "\"lastCustomFPSUpdate\"",
                })
                {
                    TestKit.Check(owner.Contains(field), "the allowed field shape must be declared: " + field);
                }

                // 唯一被替换的两个时间入口：Time.deltaTime / Time.time。绝不替换其它 Time 成员。
                TestKit.Check(owner.Contains("AccessTools.Method(typeof(UnityEngine.Time), \"get_deltaTime\")"),
                    "the only delta entry replaced is Time.get_deltaTime");
                TestKit.Check(owner.Contains("AccessTools.Method(typeof(UnityEngine.Time), \"get_time\")"),
                    "the only absolute entry replaced is Time.get_time");
                foreach (string forbidden in new[]
                {
                    "\"get_timeScale\"", "\"get_unscaledDeltaTime\"", "\"get_fixedDeltaTime\"",
                    "\"get_realtimeSinceStartup\"", "\"set_timeScale\"",
                })
                {
                    TestKit.Check(!owner.Contains(forbidden),
                        "the camera owner must not touch any other Time member: " + forbidden);
                }
            });

            TestKit.Run("camera owner: the replacement keeps stack depth and only swaps the call", () =>
            {
                string owner = Source(CameraOwnerPath);
                string code = Code(CameraOwnerPath);

                TestKit.Check(owner.Contains("new CodeInstruction(OpCodes.Call, helper)"),
                    "the injected helper must be a plain call, not a branch or a duplicate load");
                // The explicit push is mandatory: Time.get_deltaTime / get_time are static
                // getters (consume 0), the helper consumes 1. Without the push the helper
                // would eat the unrelated field value and unbalance add/stfld.
                TestKit.Check(owner.Contains("new CodeInstruction(OpCodes.Ldarg_0)"),
                    "the transpiler must explicitly push the camera instance before the helper");
                TestKit.Check(owner.Contains("loadInstance.labels.AddRange(current.labels)"),
                    "exception-block / branch labels must move to the push, never past it");
                TestKit.Check(!owner.Contains("replacement.labels.AddRange(current.labels)"),
                    "labels must not sit on the helper call (a jump would skip the push)");
                TestKit.Check(!HasOpcodeAssignment(code),
                    "existing instructions must never be mutated in place");
            });
        }

        // ---------------------------------------------------------------- install failure

        private static void InstallFailureBoundaryTests()
        {
            TestKit.Run("camera owner: a half-finished install is fully undone and refuses to start", () =>
            {
                string owner = Source(CameraOwnerPath);

                // 逐个登记安装目标与 Patch MethodInfo；已装部分必须可撤销。
                TestKit.Check(owner.Contains("hook.Patched = true;"),
                    "a hook is marked as possibly-patched before Harmony writes anything");
                TestKit.Check(owner.Contains("camera-patch-failed:"),
                    "a Patch exception must fail the install with a stable reason");
                TestKit.Check(owner.Contains("TryUnpatchAll(out _)"),
                    "every failure path must undo the already installed part");

                // 绝不使用全局撤销：只允许 Harmony.Unpatch(target, method) 形式。
                TestKit.Check(!owner.Contains(".UnpatchAll(") && !owner.Contains(".UnpatchCategory("),
                    "cleanup must only remove its own methods");
                TestKit.Check(owner.Contains("_harmony.Unpatch(hook.Target,"),
                    "unpatch must be per target method and per patch method");

                // 撤销失败保留引用并阻止 restart。
                TestKit.Check(owner.Contains("camera-hooks-still-patched"),
                    "an incomplete unpatch must remain residual ownership");

                // 目标缺失 / 不可 Patch 时必须整体拒绝，而不是跳过其中一项。
                TestKit.Check(owner.Contains("camera-method-unavailable:"),
                    "a missing target method must refuse the whole install");
                TestKit.Check(owner.Contains("camera-method-not-patchable:"),
                    "an abstract / generic target must refuse the whole install");

                // 先解析全部目标，再进入 Patch 阶段。
                int resolve = owner.IndexOf("camera-method-unavailable:", StringComparison.Ordinal);
                int patch = owner.IndexOf("hook.Patched = true;", StringComparison.Ordinal);
                TestKit.Check(resolve > 0 && patch > resolve,
                    "all targets are resolved before any Patch is attempted");
            });

            TestKit.Run("camera owner: the Finalizer closes the scope on every path and keeps the original exception", () =>
            {
                string owner = Source(CameraOwnerPath);

                TestKit.Check(owner.Contains("private static void UpdateFinalizer(Exception __exception, NativeCallScope __state)"),
                    "Update must install a Finalizer with the shared __state");
                TestKit.Check(owner.Contains("private static void UpdateSizeFinalizer(Exception __exception, NativeCallScope __state)"),
                    "UpdateSize must install a Finalizer with the shared __state");
                TestKit.Check(owner.Contains("camera-native-update-exception:"),
                    "a native exception must be attributed and recorded");

                // Finalizer 不返回 null：保留原始异常。
                TestKit.Check(!owner.Contains("static Exception UpdateFinalizer") &&
                              !owner.Contains("static Exception UpdateSizeFinalizer"),
                    "the Finalizer must never return null and swallow the game's exception");
                TestKit.Check(owner.Contains("CloseScopeAfterException"),
                    "the Finalizer must fail the frame's authorization receipt");
                TestKit.Check(owner.Contains("finally") && owner.Contains("CloseScope(__state);"),
                    "the scope must be closed idempotently on every path");
            });

            TestKit.Run("camera owner: an out-of-phase call gets zero time instead of a stolen step", () =>
            {
                string owner = Source(CameraOwnerPath);

                TestKit.Check(owner.Contains("camera-delta-out-of-phase:no-native-call-scope"),
                    "a call without an established native call scope must fail closed");
                TestKit.Check(owner.Contains("camera-delta-instance-mismatch"),
                    "a call from an instance this owner does not own must fail closed");
                TestKit.Check(owner.Contains("camera-scope-closed-without-consumption"),
                    "a method that never read the delta in an owned session is a wiring error");
                // 非活动 owner 必须保持原生语义。
                TestKit.Check(owner.Contains("return UnityEngine.Time.deltaTime;"),
                    "outside an owned session the native Time.deltaTime must be returned unchanged");
                TestKit.Check(owner.Contains("return UnityEngine.Time.time;"),
                    "outside an owned session the native Time.time must be returned unchanged");
            });
        }

        // ---------------------------------------------------------------- scheduler wiring

        private static void SchedulerWiringTests()
        {
            TestKit.Run("scheduler wiring: authorization is published only at the existing Prepare boundary", () =>
            {
                string scheduler = Source(SchedulerPath);

                int publications = CountOccurrences(scheduler, "TryPublishVisualFrameAuthorization(");
                // 定义处 + PrepareFrame + PreparePreEntryFrame + sealed boundary = 4。
                TestKit.CheckEqual(4, publications,
                    "authorization must be published from exactly the Prepare boundary and the sealed transition");

                TestKit.Check(scheduler.Contains("VisualFramePhase.PreEntry, _forcedSongPosition"),
                    "pre-entry frames publish the pre-entry phase");
                TestKit.Check(scheduler.Contains("VisualFramePhase.GameplayPartial"),
                    "the G frame publishes the partial phase");
                TestKit.Check(scheduler.Contains("VisualFramePhase.Sealed"),
                    "the hidden transition publishes a no-visual-step phase");

                // 授权发布必须早于同帧的 Planet 定位与 capture request。
                int publish = scheduler.IndexOf("TryPublishVisualFrameAuthorization(\n                    index, phase,",
                    StringComparison.Ordinal);
                int planet = scheduler.IndexOf("if (!TryPreparePlanetVisualTime(index))", StringComparison.Ordinal);
                TestKit.Check(publish > 0 && planet > publish,
                    "the authorization must be published before the same frame's visual positioning");
            });

            TestKit.Run("scheduler wiring: the registered consumer set is exactly the implemented owners", () =>
            {
                string scheduler = Source(SchedulerPath);

                TestKit.Check(scheduler.Contains("TryRegisterConsumer(VisualConsumerSlot.Camera"),
                    "the camera consumer is registered");
                // Gate A 尚未接入 Tween / Particle owner：它们**不得**被登记为必须回执的消费者，
                // 否则它们的缺失回执会把每一帧都判为失败。
                TestKit.Check(!scheduler.Contains("TryRegisterConsumer(VisualConsumerSlot.TweenBatch"),
                    "the tween batch must not be required before its owner exists");
                TestKit.Check(!scheduler.Contains("TryRegisterConsumer(VisualConsumerSlot.ParticleBatch"),
                    "the particle batch must not be required before its owner exists");
            });

            TestKit.Run("scheduler wiring: cleanup invalidates the authorization and keeps residual ownership", () =>
            {
                string scheduler = Source(SchedulerPath);

                TestKit.Check(scheduler.Contains("private static bool DisposeCameraNativeTimeOwnership"),
                    "the scheduler owns the camera ownership lifecycle");
                TestKit.Check(scheduler.Contains("_visualAuthorizer.Invalidate();"),
                    "invalidation must precede unpatch so in-flight callbacks only see stale");
                TestKit.Check(scheduler.Contains("camera-native-time-dispose:"),
                    "a failed dispose must be reported as a cleanup failure");
                TestKit.Check(scheduler.Contains("_cameraNativeTime != null && _cameraNativeTime.HasResidualOwnership"),
                    "an unpatch failure must block the next session through the residual gate");
                TestKit.Check(scheduler.Contains("VisualFrameAuthorizer _visualAuthorizer = new VisualFrameAuthorizer()"),
                    "there is exactly one authorizer for the session");
                TestKit.Check(scheduler.Contains("ArmVisualFrameAuthorization(_captureGeneration"),
                    "the authorizer reuses the capture driver generation instead of inventing a second authority");
            });
        }

        // ---------------------------------------------------------------- EOF gate

        private static void EndOfFrameGateTests()
        {
            TestKit.Run("driver wiring: the authorization gate runs before any readback or success return", () =>
            {
                string driver = Source(DriverPath);

                TestKit.Check(driver.Contains("public delegate void EndOfFrameGate(long frameIndex);"),
                    "the driver exposes a read-only end-of-frame gate");
                TestKit.Check(driver.Contains("RunEndOfFrameGateOrThrow(frameIndex);"),
                    "the gate must actually be invoked");

                int gate = driver.IndexOf("RunEndOfFrameGateOrThrow(frameIndex);", StringComparison.Ordinal);
                int rgb24 = driver.IndexOf("TryRunRgb24CapturePipeline(rgb24Frame, out string rgb24Error)",
                    StringComparison.Ordinal);
                int logOnly = driver.IndexOf("log-only-downsample-chain-present", StringComparison.Ordinal);
                int png = driver.IndexOf("EncodeToPNG()", StringComparison.Ordinal);
                TestKit.Check(gate > 0, "the gate call exists");
                TestKit.Check(gate < rgb24, "the gate must precede the RGB24 readback");
                TestKit.Check(gate < logOnly, "the gate must precede the log-only success return");
                TestKit.Check(gate < png, "the gate must precede PNG encoding and writing");

                // 关口未注册时行为与旧版完全一致。
                TestKit.Check(driver.Contains("if (gate == null) return;"),
                    "an unregistered gate must leave the previous behaviour untouched");
                TestKit.Check(driver.Contains("_endOfFrameGate = null;"),
                    "the gate must not survive the session");
            });

            TestKit.Run("scheduler wiring: the gate validates completeness, closes the frame, and fails the session", () =>
            {
                string scheduler = Source(SchedulerPath);

                TestKit.Check(scheduler.Contains("TryValidateEndOfFrame(out validationError)"),
                    "the gate must use the authorizer's completeness validation");
                TestKit.Check(scheduler.Contains("_visualAuthorizer.CloseCurrentFrame();"),
                    "a validated frame must be closed so late native updates cannot consume it");
                TestKit.Check(scheduler.Contains("RequestStop(\"visual-frame-authorization-failed\""),
                    "a failed validation must fail the session, never silently continue");
            });
        }

        // ---------------------------------------------------------------- authority boundary

        private static void AuthorityBoundaryTests()
        {
            TestKit.Run("authority: the authorizer stays Unity-free and owns no timeline state", () =>
            {
                // 只扫描可执行语句：注释与 XML 文档刻意讨论这些名字，不能算作依赖。
                string authorizer = Code(AuthorizerPath);

                foreach (string forbidden in new[]
                {
                    "UnityEngine", "DG.Tweening", "HarmonyLib", "Time.", "GameObject", "RenderTexture",
                })
                {
                    TestKit.Check(!authorizer.Contains(forbidden),
                        "the authorization contract must stay Unity-free: " + forbidden);
                }

                // 它不是第二套时间权威：不推进帧号、不写 scheduler 状态。
                TestKit.Check(!Code(SchedulerPath).Contains("_visualAuthorizer._outputFrameIndex"),
                    "the authorizer must not reach into the scheduler's frame counter");
                TestKit.Check(Source(AuthorizerPath).Contains("publish-frame-sequence-broken"),
                    "frame publication is strictly sequential");
            });

            TestKit.Run("authority: Gate A did not change MasterTimeline, L3-C or RGB24 rules", () =>
            {
                string scheduler = Source(SchedulerPath);

                // 逻辑时间权威与交付语义必须原样保留。
                TestKit.Check(scheduler.Contains("MasterTimeline.FrameSample sample = _timeline.Prepare(gameplayFrameIndex);"),
                    "MasterTimeline remains the only logical time authority");
                TestKit.Check(scheduler.Contains("private static bool TryDetachRgb24CompletionHandoff()"),
                    "the L3-C completion handoff is untouched");
                TestKit.Check(scheduler.Contains("_rgb24Delivery.TryTakePendingEnvelope(out envelope, out bridgeError)"),
                    "the RGB24 delivery path is untouched");
                TestKit.Check(scheduler.Contains("if (_canonicalCompletionStateSeen &&"),
                    "canonical completion / End Tail authority is untouched");

                // 授权不是帧计数：它绝不影响逻辑帧计数与 L3-C 判定。
                int gate = scheduler.IndexOf("private static void TryValidateVisualFrameAtEndOfFrame(", StringComparison.Ordinal);
                TestKit.Check(gate > 0, "the gate exists in the scheduler");
                string gateBody = scheduler.Substring(gate, Math.Min(1800, scheduler.Length - gate));
                TestKit.Check(!gateBody.Contains("_outputFrameIndex++") && !gateBody.Contains("_capturedFrameCount++") &&
                              !gateBody.Contains("CommitFrame("),
                    "the gate must not advance any frame counter or commit a frame");
            });

            TestKit.Run("authority: the camera owner owns no capture or render-texture state", () =>
            {
                string owner = Code(CameraOwnerPath);

                foreach (string forbidden in new[]
                {
                    "RenderTexture", "targetTexture", "ReadPixels", "Graphics.Blit", "Camera.main",
                    "SetupRTCam", "QualitySettings", "Application.targetFrameRate", "Time.timeScale =",
                })
                {
                    TestKit.Check(!owner.Contains(forbidden),
                        "the camera owner must not take over capture / RT / global time state: " + forbidden);
                }

                TestKit.Check(Source(CameraOwnerPath).Contains("GetCameraDeltaTime(object cameraInstance)"),
                    "the helper receives the instance so scope / ownership can be checked per instance");
                TestKit.Check(Source(CameraOwnerPath).Contains("cameraInstance"),
                    "the injected call uses ldarg.0 as the instance argument");
            });
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// 真·赋值形态（<c>.opcode =</c> 而不是 <c>.opcode ==</c>）：只有它才算"原地改写指令"。
        /// </summary>
        private static bool HasOpcodeAssignment(string code)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(code, @"\.opcode\s*=\s*[^=]") ||
                   System.Text.RegularExpressions.Regex.IsMatch(code, @"\.operand\s*=\s*[^=]");
        }

        private static int CountOccurrences(string text, string needle)        {
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

        /// <summary>
        /// 与 <see cref="Source"/> 相同，但剔除注释行：注释与 XML 文档会刻意提到
        /// "不得依赖 / 不得接管"的成员名，只有可执行语句才能作为依赖或接管的证据。
        /// </summary>
        private static string Code(string relative)
        {
            string[] lines = Source(relative).Split('\n');
            var kept = new System.Collections.Generic.List<string>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                kept.Add(lines[i]);
            }
            return string.Join("\n", kept.ToArray());
        }
    }
}
