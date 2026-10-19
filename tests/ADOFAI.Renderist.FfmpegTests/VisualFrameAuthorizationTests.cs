using System;
using System.Globalization;
using ADOFAI.Renderist.Export;

namespace ADOFAI.Renderist.FfmpegTests
{
    /// <summary>
    /// 0.3.12.0 Gate A 的 Unity-free 授权合同回归。
    ///
    /// 固定的是**行为契约**，不是某次实机的具体数值：
    ///   * 各消费者独立回执（不是"第一个读完就全局清空"）；
    ///   * 一次成功/开始过的消费不得在同一逻辑帧重试；
    ///   * 授权只在 Prepare 所在原生帧内有效；过期 generation 一律 stale；
    ///   * EOF 前必须所有已注册消费者都已回执，且 hide/partial 相位的数值投影正确。
    /// </summary>
    internal static class VisualFrameAuthorizationTests
    {
        private const long Generation = 41L;
        private const double Fps30 = 30.0;
        private const double Fps60 = 60.0;

        public static void Run()
        {
            PhaseProjectionTests();
            ReceiptIndependenceTests();
            OneShotConsumptionTests();
            ScopeAndFailureTests();
            EndOfFrameValidationTests();
            InvalidationAndRestartTests();
            RestartLifecycleTests();
            SealedLifecycleOnlyTests();
            ExtremeFpsTests();
        }

        // ---------------------------------------------------------------- restart lifecycle

        /// <summary>
        /// 修正 #2：`Arm` 必须是一次**完整**的 session 复位。否则 Completed / Cancelled / Failed
        /// 之后重新 Arm 时，上一次 session 残留的 Camera 注册会让本次登记失败
        /// （`consumer-already-registered`），restart 永远开不起来。
        /// </summary>
        private static void RestartLifecycleTests()
        {
            TestKit.Run("visual auth: re-arming after Completed / Cancelled / Failed can register Camera again", () =>
            {
                foreach (string terminal in new[] { "completed", "cancelled", "failed" })
                {
                    var authorizer = new VisualFrameAuthorizer();
                    long generation = Generation;

                    authorizer.Arm(generation);
                    TestKit.Check(authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out string firstError),
                        terminal + ": first registration must succeed: " + firstError);

                    VisualFrameAuthorization authorization;
                    string publishError;
                    TestKit.Check(authorizer.TryPublishFrame(generation, 0L, 10L, VisualFramePhase.PreEntry,
                            0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out publishError),
                        terminal + ": publish: " + publishError);

                    double unscaled;
                    double scaled;
                    string consumeError;
                    if (terminal == "failed")
                    {
                        authorizer.FailFrame(VisualConsumerSlot.Camera, "synthetic-terminal-failure");
                    }
                    else
                    {
                        VisualFrameConsumptionKey key = Key(VisualConsumerSlot.Camera, 0L, 10L, 1);
                        authorizer.TryConsume(key, out unscaled, out scaled, out consumeError);
                        authorizer.TryCompleteScope(key, out consumeError);
                        authorizer.CloseCurrentFrame();
                    }

                    // 终态之后的新 session：generation 前进，注册必须重新成功。
                    authorizer.Invalidate();
                    generation++;
                    authorizer.Arm(generation);

                    TestKit.Check(authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out string restartError),
                        terminal + ": the Camera slot must be registrable again after a restart: " + restartError);
                    TestKit.Check(!authorizer.IsConsumerRegistered(VisualConsumerSlot.TweenBatch),
                        terminal + ": a slot that was never registered must stay unregistered");
                    TestKit.CheckEqual(generation, authorizer.Generation, terminal + ": new generation is bound");
                    TestKit.Check(authorizer.TryPublishFrame(generation, 0L, 11L, VisualFramePhase.PreEntry,
                            0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out publishError),
                        terminal + ": the restarted session can publish frame 0: " + publishError);
                    TestKit.CheckEqual(0L, authorization.AbsoluteOutputFrameIndex,
                        terminal + ": the frame sequence restarts from 0");
                    TestKit.Check(authorization.ZeroStep, terminal + ": frame 0 is a zero-step frame again");
                }
            });

            TestKit.Run("visual auth: a leftover EOF failure cannot leak into the restarted session", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);

                VisualFrameAuthorization authorization;
                string error;
                TestKit.Check(authorizer.TryPublishFrame(Generation, 0L, 12L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error), "publish: " + error);
                TestKit.Check(!authorizer.TryValidateEndOfFrame(out error),
                    "an unclaimed frame must fail EOF validation before the restart");
                authorizer.FailFrame(VisualConsumerSlot.Camera, "eof-incomplete");

                authorizer.Invalidate();
                authorizer.Arm(Generation + 1);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);

                TestKit.Check(authorizer.TryPublishFrame(Generation + 1, 0L, 13L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error),
                    "the new session publishes: " + error);
                TestKit.Check(authorizer.TryPublishFrame(Generation + 1, 1L, 14L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error),
                    "the new session also publishes frame 1: " + error);

                VisualFrameConsumptionKey key = KeyForGeneration(
                    Generation + 1, VisualConsumerSlot.Camera, 1L, 14L, 1);
                double unscaled;
                double scaled;
                TestKit.CheckEqual(VisualFrameConsumeOutcome.Consumed,
                    authorizer.TryConsume(key, out unscaled, out scaled, out error),
                    "the restarted session consumes normally: " + error);
                TestKit.Check(authorizer.TryCompleteScope(key, out error), "complete: " + error);
                TestKit.Check(authorizer.TryValidateEndOfFrame(out error),
                    "the restarted session validates: " + error);
            });
        }

        // ---------------------------------------------------------------- sealed lifecycle-only

        /// <summary>
        /// 修正 #5：hidden / sealed **不是**一帧新的输出提交。它不占用输出帧序列，
        /// 跨越任意多个原生 Unity frame 只返回 0 增量（不算重复消费），
        /// 并且不会阻塞紧随其后的 G。
        /// </summary>
        private static void SealedLifecycleOnlyTests()
        {
            TestKit.Run("visual auth: the sealed hidden window does not occupy an output frame slot", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);

                VisualFrameAuthorization authorization;
                string error;
                // B-1 是最后一个真实 pre-entry 输出帧。
                TestKit.Check(authorizer.TryPublishFrame(Generation, 5L, 100L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error), "B-1 publish: " + error);
                TestKit.Check(authorizer.TryPublishLifecycleOnly(Generation, 6L, 101L, VisualFramePhase.Sealed,
                        0.0, Fps30, 0.0, out authorization, out error), "sealed publish: " + error);
                TestKit.Check(authorization.LifecycleOnly, "the sealed authorization must be lifecycle-only");
                TestKit.Check(!authorization.AdvancesOutputFrameSequence,
                    "a lifecycle-only authorization must not advance the output frame sequence");
                TestKit.Check(!authorization.GrantsVisualStep, "sealed grants no visual step");

                // 多个 hidden Unity frame：每次都只得到 0 增量，且**不是**重复消费。
                for (int nativeFrame = 101; nativeFrame <= 104; nativeFrame++)
                {
                    VisualFrameConsumptionKey hiddenKey = Key(VisualConsumerSlot.Camera, 6L, nativeFrame, nativeFrame);
                    double unscaled;
                    double scaled;
                    string consumeError;
                    TestKit.CheckEqual(VisualFrameConsumeOutcome.NoVisualStep,
                        authorizer.TryConsume(hiddenKey,
                            out unscaled, out scaled, out consumeError),
                        "hidden native frame " + nativeFrame + " must get an explicit zero step");
                    TestKit.CheckEqual(0.0, unscaled, "hidden frame " + nativeFrame + " unscaled delta");
                    TestKit.CheckEqual(0.0, scaled, "hidden frame " + nativeFrame + " scaled delta");
                    TestKit.Check(authorizer.TryCompleteScope(hiddenKey, out consumeError),
                        "hidden call must complete its own receipt: " + consumeError);
                }

                // G 仍然发布**同一个** output frame index（6），序列不被 Sealed 阻塞。
                TestKit.Check(authorizer.TryPublishFrame(Generation, 6L, 110L, VisualFramePhase.GameplayPartial,
                        1.6, 0.25 / Fps30, Fps30, 0.5, 0.25, out authorization, out error),
                    "G must still be able to publish the same output frame index after the sealed window: " + error);
                TestKit.Check(authorization.AdvancesOutputFrameSequence,
                    "G is a real output frame and advances the sequence");
                TestKit.Check(!authorization.ZeroStep, "G is not a zero-step frame");

                double gUnscaled;
                double gScaled;
                string gError;
                TestKit.CheckEqual(VisualFrameConsumeOutcome.Consumed,
                    authorizer.TryConsume(Key(VisualConsumerSlot.Camera, 6L, 110L, 1),
                        out gUnscaled, out gScaled, out gError),
                    "G consumes normally: " + gError);
                TestKit.CheckEqual(0.25 / Fps30 * 0.5, gScaled, "G scaled delta uses the partial fraction once");
            });

            TestKit.Run("visual auth: after the sealed window a real frame still cannot be consumed twice", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);

                VisualFrameAuthorization authorization;
                string error;
                TestKit.Check(authorizer.TryPublishLifecycleOnly(Generation, 0L, 200L, VisualFramePhase.Sealed,
                        0.0, Fps30, 0.0, out authorization, out error), "sealed publish: " + error);
                TestKit.Check(authorizer.TryPublishFrame(Generation, 1L, 300L, VisualFramePhase.Gameplay,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error), "gameplay publish: " + error);

                double unscaled;
                double scaled;
                VisualFrameConsumptionKey key = Key(VisualConsumerSlot.Camera, 1L, 300L, 1);
                TestKit.CheckEqual(VisualFrameConsumeOutcome.Consumed,
                    authorizer.TryConsume(key, out unscaled, out scaled, out error), "consume");
                TestKit.Check(authorizer.TryCompleteScope(key, out error), "complete");

                // 真实帧仍然严格执行"一次消费"：另一个作用域在同一原生帧重试必须失败。
                TestKit.CheckEqual(VisualFrameConsumeOutcome.DuplicateConsumption,
                    authorizer.TryConsume(Key(VisualConsumerSlot.Camera, 1L, 300L, 2),
                        out unscaled, out scaled, out error),
                    "a real frame must still reject a second consumption");
                // 也不允许在别的原生帧补走。
                TestKit.CheckEqual(VisualFrameConsumeOutcome.OutOfPhase,
                    authorizer.TryConsume(Key(VisualConsumerSlot.Camera, 1L, 301L, 3),
                        out unscaled, out scaled, out error),
                    "a real frame must still be pinned to its prepared native frame");
            });
        }

        // ---------------------------------------------------------------- phase projection

        private static void PhaseProjectionTests()
        {
            TestKit.Run("visual auth: bootstrap and sealed phases grant no visual step at all", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);

                VisualFrameAuthorization authorization;
                string error;
                TestKit.Check(authorizer.TryPublishLifecycleOnly(Generation, 0L, 100L,
                        VisualFramePhase.Bootstrap, 0.0, Fps30, 1.0, out authorization, out error),
                    "bootstrap publish: " + error);
                TestKit.CheckEqual(0.0, authorization.VisualUnscaledDeltaSeconds, "bootstrap unscaled delta");
                TestKit.CheckEqual(0.0, authorization.VisualScaledDeltaSeconds, "bootstrap scaled delta");
                TestKit.Check(!authorization.GrantsVisualStep, "bootstrap must not grant a visual step");

                double unscaled;
                double scaled;
                VisualFrameConsumeOutcome outcome = authorizer.TryConsume(
                    Key(VisualConsumerSlot.Camera, 0L, 100L, 1), out unscaled, out scaled, out error);
                TestKit.CheckEqual(VisualFrameConsumeOutcome.NoVisualStep, outcome, "bootstrap consume outcome");
                TestKit.CheckEqual(0.0, unscaled, "bootstrap consumed unscaled delta");
                TestKit.CheckEqual(0.0, scaled, "bootstrap consumed scaled delta");

                TestKit.Check(authorizer.TryPublishLifecycleOnly(Generation, 1L, 101L,
                        VisualFramePhase.Sealed, 5.0, Fps30, 0.0, out authorization, out error),
                    "sealed publish: " + error);
                TestKit.CheckEqual(0.0, authorization.VisualUnscaledDeltaSeconds, "sealed unscaled delta");
                TestKit.CheckEqual(0.0, authorization.VisualScaledDeltaSeconds, "sealed scaled delta");
                TestKit.Check(!authorization.GrantsVisualStep, "sealed must not grant a visual step");
            });

            TestKit.Run("visual auth: ordinary pre-entry / gameplay step is 1/FPS with no pitch factor", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);

                VisualFrameAuthorization authorization;
                string error;
                // chart sample delta 与 visual delta 刻意取不同值：视觉增量绝不能从 pitch 派生。
                TestKit.Check(authorizer.TryPublishFrame(Generation, 7L, 700L, VisualFramePhase.PreEntry,
                        1.234, 0.4, Fps30, 1.0, 0.0, out authorization, out error), "pre-entry publish: " + error);
                TestKit.CheckEqual(1.0 / Fps30, authorization.VisualUnscaledDeltaSeconds, "pre-entry unscaled");
                TestKit.CheckEqual(1.0 / Fps30, authorization.VisualScaledDeltaSeconds, "pre-entry scaled (timeScale 1)");
                TestKit.CheckEqual(0.4, authorization.ChartSampleDeltaSeconds, "chart delta is carried separately");

                TestKit.Check(authorizer.TryPublishFrame(Generation, 8L, 701L, VisualFramePhase.Gameplay,
                        1.634, 0.4, Fps30, 0.64, 0.0, out authorization, out error), "gameplay publish: " + error);
                TestKit.CheckEqual(1.0 / Fps30, authorization.VisualUnscaledDeltaSeconds, "gameplay unscaled");
                TestKit.CheckEqual((1.0 / Fps30) * 0.64, authorization.VisualScaledDeltaSeconds,
                    "gameplay scaled uses the base timeScale");
            });

            TestKit.Run("visual auth: G partial uses partialFraction/FPS and applies base timeScale exactly once", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);

                // 真实场景：step = pitch/FPS = 1/30，partialStep 只有 step 的 37%。
                const double timeScale = 0.5;
                const double fraction = 0.37;
                VisualFrameAuthorization authorization;
                string error;
                TestKit.Check(authorizer.TryPublishFrame(Generation, 1L, 42L, VisualFramePhase.GameplayPartial,
                        10.0, fraction * (1.0 / Fps30), Fps30, timeScale, fraction,
                        out authorization, out error), "partial publish: " + error);

                TestKit.CheckEqual(fraction, authorization.PartialFraction, "partial fraction is carried verbatim");
                TestKit.CheckEqual(fraction / Fps30, authorization.VisualUnscaledDeltaSeconds,
                    "partial unscaled = partialFraction / FPS");
                TestKit.CheckEqual((fraction / Fps30) * timeScale, authorization.VisualScaledDeltaSeconds,
                    "partial scaled multiplies the base timeScale exactly once");

                // 反例守卫：若把 fraction 再乘一次（0.37^2）或直接使用完整 1/FPS，
                // 两个断言都会失败 —— 这正是本测试要固定下来的边界。
                TestKit.Check(authorization.VisualScaledDeltaSeconds < (1.0 / Fps30) * timeScale,
                    "a partial step must be strictly smaller than a full step at the same timeScale");
            });

            TestKit.Run("visual auth: 60 FPS and float projection stay consistent with the double authority", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);

                VisualFrameAuthorization authorization;
                string error;
                TestKit.Check(authorizer.TryPublishFrame(Generation, 1L, 10L, VisualFramePhase.PreEntry,
                        0.0, 1.0 / Fps60, Fps60, 1.0, 0.0, out authorization, out error), "60fps publish: " + error);

                double unscaled;
                double scaled;
                TestKit.CheckEqual(VisualFrameConsumeOutcome.Consumed,
                    authorizer.TryConsume(Key(VisualConsumerSlot.Camera, 1L, 10L, 1), out unscaled, out scaled, out error),
                    "60fps consume");

                // DOTween / Unity 的 delta 是 float；float 化只允许表示误差，不允许变成 0 或量级错误。
                float projected = (float)unscaled;
                TestKit.Check(projected > 0f, "float projection must remain a real positive step");
                TestKit.CheckEqual(projected, (float)(1.0 / Fps60), "float projection equals 1/60");
            });

            TestKit.Run("visual auth: publish rejects a non-finite or backwards frame projection", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);

                VisualFrameAuthorization authorization;
                string error;
                TestKit.Check(!authorizer.TryPublishFrame(Generation, 0L, 5L, VisualFramePhase.PreEntry,
                        double.NaN, 0.0, Fps30, 1.0, 0.0, out authorization, out error), "NaN chart sample must fail");
                TestKit.CheckNotEmpty(error, "NaN chart sample error");
                TestKit.Check(!authorizer.HasCurrentAuthorization, "a rejected publish must not leave an authorization");

                TestKit.Check(authorizer.TryPublishFrame(Generation, 0L, 5L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error), "first publish: " + error);
                TestKit.Check(!authorizer.TryPublishFrame(Generation, 3L, 6L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error),
                    "skipping a frame index must be rejected");
                TestKit.Check(!authorizer.TryPublishFrame(Generation, 0L, 6L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error),
                    "re-publishing the same frame index must be rejected");
                TestKit.Check(!authorizer.TryPublishFrame(Generation, 1L, 6L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 1.5, out authorization, out error),
                    "a partial fraction above 1 must be rejected");
                TestKit.Check(authorizer.TryPublishFrame(Generation, 1L, 6L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error),
                    "the next frame index is accepted after the rejections: " + error);
            });
        }

        // ---------------------------------------------------------------- receipts

        private static void ReceiptIndependenceTests()
        {
            TestKit.Run("visual auth: consumers keep independent receipts and are validated individually", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.TweenBatch, out _);

                VisualFrameAuthorization authorization;
                string error;
                TestKit.Check(authorizer.TryPublishFrame(Generation, 1L, 20L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error), "publish: " + error);

                double unscaled;
                double scaled;
                VisualFrameConsumptionKey cameraKey = Key(VisualConsumerSlot.Camera, 1L, 20L, 1);
                TestKit.CheckEqual(VisualFrameConsumeOutcome.Consumed,
                    authorizer.TryConsume(cameraKey, out unscaled, out scaled, out error), "camera consume");
                TestKit.Check(authorizer.TryCompleteScope(cameraKey, out error), "camera complete: " + error);

                // Camera 读完**不**清空 Tween batch 的回执要求。
                TestKit.Check(!authorizer.TryValidateEndOfFrame(out error), "Tween batch receipt still missing");
                TestKit.Check(error.StartsWith("eof-missing-receipt:slot=TweenBatch", StringComparison.Ordinal),
                    "missing receipt must name the exact slot, got: " + error);
                TestKit.Check(authorizer.Describe(20L).Contains("Camera=completed"), "camera receipt is recorded");
                TestKit.Check(authorizer.Describe(20L).Contains("TweenBatch=none"), "tween receipt is absent");

                VisualFrameConsumptionKey tweenKey = Key(VisualConsumerSlot.TweenBatch, 1L, 20L, 2);
                TestKit.CheckEqual(VisualFrameConsumeOutcome.Consumed,
                    authorizer.TryConsume(tweenKey, out unscaled, out scaled, out error), "tween consume");
                TestKit.Check(authorizer.TryCompleteScope(tweenKey, out error), "tween complete: " + error);
                TestKit.Check(authorizer.TryValidateEndOfFrame(out error), "both receipts present: " + error);
            });

            TestKit.Run("visual auth: an unregistered slot is never required and can never consume", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);

                VisualFrameAuthorization authorization;
                string error;
                TestKit.Check(authorizer.TryPublishFrame(Generation, 0L, 30L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error), "publish: " + error);

                TestKit.Check(!authorizer.IsConsumerRegistered(VisualConsumerSlot.ParticleBatch),
                    "the particle batch is not registered in Gate A");

                double unscaled;
                double scaled;
                VisualFrameConsumeOutcome outcome = authorizer.TryConsume(
                    Key(VisualConsumerSlot.ParticleBatch, 0L, 30L, 1), out unscaled, out scaled, out error);
                TestKit.CheckEqual(VisualFrameConsumeOutcome.NotRegistered, outcome, "unregistered consume outcome");
                TestKit.Check(!authorizer.TryValidateEndOfFrame(out error),
                    "camera receipt is still required while the batch is unregistered");
                TestKit.Check(error.Contains("Camera"), "validation still names the registered slot: " + error);

                TestKit.Check(!authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out error),
                    "registering the same slot twice must fail");
                TestKit.CheckEqual("consumer-already-registered:Camera", error, "duplicate registration error");
            });
        }

        // ---------------------------------------------------------------- one-shot consumption

        private static void OneShotConsumptionTests()
        {
            TestKit.Run("visual auth: nested in-scope call reuses the step, a second scope does not", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);

                VisualFrameAuthorization authorization;
                string error;
                TestKit.Check(authorizer.TryPublishFrame(Generation, 3L, 300L, VisualFramePhase.Gameplay,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error), "publish: " + error);

                double unscaled;
                double scaled;
                // Camera.Update 的作用域 id = 7，嵌套的 UpdateSize 复用同一个 id。
                VisualFrameConsumptionKey outer = Key(VisualConsumerSlot.Camera, 3L, 300L, 7);
                TestKit.CheckEqual(VisualFrameConsumeOutcome.Consumed,
                    authorizer.TryConsume(outer, out unscaled, out scaled, out error), "outer consume");
                TestKit.CheckEqual(1.0 / Fps30, unscaled, "outer delta");

                double nestedUnscaled;
                double nestedScaled;
                TestKit.CheckEqual(VisualFrameConsumeOutcome.NestedInScope,
                    authorizer.TryConsume(outer, out nestedUnscaled, out nestedScaled, out error),
                    "nested consume shares the scope");
                TestKit.CheckEqual(unscaled, nestedUnscaled, "nested call sees the same unscaled delta");
                TestKit.CheckEqual(scaled, nestedScaled, "nested call sees the same scaled delta");

                TestKit.Check(authorizer.TryCompleteScope(outer, out error), "outer complete: " + error);

                // 第二次独立调用（例如 LateUpdate 里再调一次，或输入触发的独立 UpdateSize）
                // 已经是另一个作用域：绝不能在同一逻辑帧再领一步。
                double secondUnscaled;
                double secondScaled;
                VisualFrameConsumeOutcome second = authorizer.TryConsume(
                    Key(VisualConsumerSlot.Camera, 3L, 300L, 8), out secondUnscaled, out secondScaled, out error);
                TestKit.CheckEqual(VisualFrameConsumeOutcome.DuplicateConsumption, second,
                    "a new scope in the same native frame must be rejected");
                TestKit.CheckEqual(0.0, secondUnscaled, "rejected consume returns no time");
                TestKit.CheckEqual(0.0, secondScaled, "rejected consume returns no scaled time");
                TestKit.CheckNotEmpty(error, "duplicate consumption error detail");
            });

            TestKit.Run("visual auth: an out-of-phase call cannot consume and cannot be retried into the frame", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);

                VisualFrameAuthorization authorization;
                string error;
                TestKit.Check(authorizer.TryPublishFrame(Generation, 2L, 200L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error), "publish: " + error);

                double unscaled;
                double scaled;
                // 早于 Prepare 的原生帧（例如上一 Unity frame 的迟到调用）。
                TestKit.CheckEqual(VisualFrameConsumeOutcome.OutOfPhase,
                    authorizer.TryConsume(Key(VisualConsumerSlot.Camera, 2L, 199L, 1), out unscaled, out scaled, out error),
                    "a call in an earlier native frame is out of phase");
                // 晚于本帧消费相位的等待帧。
                TestKit.CheckEqual(VisualFrameConsumeOutcome.OutOfPhase,
                    authorizer.TryConsume(Key(VisualConsumerSlot.Camera, 2L, 201L, 1), out unscaled, out scaled, out error),
                    "a call in a later native frame is out of phase");
                // 跨逻辑帧的消费同样不允许。
                TestKit.CheckEqual(VisualFrameConsumeOutcome.OutOfPhase,
                    authorizer.TryConsume(Key(VisualConsumerSlot.Camera, 3L, 200L, 1), out unscaled, out scaled, out error),
                    "a call for another logical frame is out of phase");

                // out-of-phase 调用不占用本帧授权：本帧仍可以被正确消费一次。
                TestKit.CheckEqual(VisualFrameConsumeOutcome.Consumed,
                    authorizer.TryConsume(Key(VisualConsumerSlot.Camera, 2L, 200L, 1), out unscaled, out scaled, out error),
                    "the in-phase call still consumes exactly once");
            });

            TestKit.Run("visual auth: a zero scope id is an explicit wiring error", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);

                VisualFrameAuthorization authorization;
                string error;
                TestKit.Check(authorizer.TryPublishFrame(Generation, 0L, 1L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error), "publish: " + error);

                double unscaled;
                double scaled;
                TestKit.CheckEqual(VisualFrameConsumeOutcome.InvalidScope,
                    authorizer.TryConsume(Key(VisualConsumerSlot.Camera, 0L, 1L, 0), out unscaled, out scaled, out error),
                    "scope id 0 must be rejected");
            });

            TestKit.Run("visual auth: repeating the same logical frame across native frames stays impossible", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);

                VisualFrameAuthorization authorization;
                string error;
                TestKit.Check(authorizer.TryPublishFrame(Generation, 1L, 400L, VisualFramePhase.Gameplay,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error), "publish: " + error);

                double unscaled;
                double scaled;
                VisualFrameConsumptionKey key = Key(VisualConsumerSlot.Camera, 1L, 400L, 1);
                TestKit.CheckEqual(VisualFrameConsumeOutcome.Consumed,
                    authorizer.TryConsume(key, out unscaled, out scaled, out error), "consume");
                TestKit.Check(authorizer.TryCompleteScope(key, out error), "complete");
                TestKit.Check(authorizer.TryValidateEndOfFrame(out error), "validate: " + error);

                // 逻辑帧已提交、授权已关闭：等待期间的任何原生 Update 都只能看到 Waiting。
                authorizer.CloseCurrentFrame();
                VisualFrameAuthorization peeked;
                VisualSessionVisualState state;
                TestKit.Check(!authorizer.TryPeekCurrentState(out peeked, out state),
                    "a closed frame reports no current authorization");
                TestKit.CheckEqual(VisualSessionVisualState.Waiting, state,
                    "the post-EOF window is an explicit Waiting state, not a failure");
                TestKit.CheckEqual(VisualFrameConsumeOutcome.OutOfPhase,
                    authorizer.TryConsume(Key(VisualConsumerSlot.Camera, 1L, 401L, 2), out unscaled, out scaled, out error),
                    "a consumer that skips the state check is still rejected as out of phase");
                TestKit.Check(!authorizer.TryValidateEndOfFrame(out error), "no current authorization to validate");
                TestKit.CheckEqual("eof-validation-no-current-authorization", error, "validation error");
            });

            TestKit.Run("visual auth: frame 0 grants an explicit zero step instead of the nominal 1/FPS", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);

                VisualFrameAuthorization authorization;
                string error;
                TestKit.Check(authorizer.TryPublishFrame(Generation, 0L, 500L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error), "frame0 publish: " + error);

                TestKit.Check(authorization.ZeroStep, "frame 0 must be marked as a zero-step authorization");
                TestKit.Check(!authorization.GrantsVisualStep, "frame 0 must not grant a visual step");
                TestKit.Check(authorization.AdvancesOutputFrameSequence,
                    "frame 0 is still a real output frame and occupies the sequence");
                TestKit.CheckEqual(0.0, authorization.VisualUnscaledDeltaSeconds,
                    "the immutable snapshot reports the same zero step as consumption");
                TestKit.CheckEqual(0.0, authorization.VisualScaledDeltaSeconds, "snapshot scaled zero step");

                double unscaled;
                double scaled;
                string consumeError;
                TestKit.CheckEqual(VisualFrameConsumeOutcome.NoVisualStep,
                    authorizer.TryConsume(Key(VisualConsumerSlot.Camera, 0L, 500L, 1),
                        out unscaled, out scaled, out consumeError),
                    "frame 0 consume outcome");
                TestKit.CheckEqual(0.0, unscaled, "frame 0 must hand the consumer zero unscaled time");
                TestKit.CheckEqual(0.0, scaled, "frame 0 must hand the consumer zero scaled time");

                // frame 1 起才恢复普通步长：初始化 bootstrap **不得**靠额外 Unity Update 补步。
                TestKit.Check(authorizer.TryPublishFrame(Generation, 1L, 501L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error), "frame1 publish: " + error);
                TestKit.Check(!authorization.ZeroStep, "frame 1 is a normal step");
                TestKit.Check(authorizer.TryConsume(Key(VisualConsumerSlot.Camera, 1L, 501L, 2),
                        out unscaled, out scaled, out error) == VisualFrameConsumeOutcome.Consumed,
                    "frame 1 consumes normally: " + error);
                TestKit.CheckEqual(1.0 / Fps30, unscaled, "frame 1 grants exactly one output step");
            });
        }

        // ---------------------------------------------------------------- scope failures

        private static void ScopeAndFailureTests()
        {
            TestKit.Run("visual auth: a scope that never completes blocks the frame at EOF", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);

                VisualFrameAuthorization authorization;
                string error;
                TestKit.Check(authorizer.TryPublishFrame(Generation, 1L, 50L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error), "publish: " + error);

                double unscaled;
                double scaled;
                TestKit.CheckEqual(VisualFrameConsumeOutcome.Consumed,
                    authorizer.TryConsume(Key(VisualConsumerSlot.Camera, 1L, 50L, 3), out unscaled, out scaled, out error),
                    "consume");

                TestKit.Check(!authorizer.TryValidateEndOfFrame(out error),
                    "an in-flight (never completed) receipt must fail EOF validation");
                TestKit.Check(error.StartsWith("eof-incomplete-receipt:slot=Camera", StringComparison.Ordinal),
                    "incomplete receipt names the slot, got: " + error);

                // 原生方法抛异常：Finalizer 走 FailFrame，而不是 TryCompleteScope。
                authorizer.FailFrame(VisualConsumerSlot.Camera, "native-update-exception:NullReferenceException");
                TestKit.Check(!authorizer.TryValidateEndOfFrame(out error), "failed frame must not validate");
                TestKit.Check(error.Contains("native-update-exception"), "failure reason is preserved: " + error);

                // 失败帧不得重试。
                TestKit.CheckEqual(VisualFrameConsumeOutcome.FrameFailed,
                    authorizer.TryConsume(Key(VisualConsumerSlot.Camera, 1L, 50L, 3), out unscaled, out scaled, out error),
                    "a failed frame must not be re-consumed");
            });

            TestKit.Run("visual auth: completing without consuming, or twice, is a wiring error", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);

                VisualFrameAuthorization authorization;
                string error;
                TestKit.Check(authorizer.TryPublishFrame(Generation, 1L, 60L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error), "publish: " + error);

                TestKit.Check(!authorizer.TryCompleteScope(Key(VisualConsumerSlot.Camera, 1L, 60L, 1), out error),
                    "completing without consuming must fail");
                TestKit.CheckEqual("complete-without-consumption:slot=Camera", error, "complete-without-consume error");

                double unscaled;
                double scaled;
                VisualFrameConsumptionKey key = Key(VisualConsumerSlot.Camera, 1L, 60L, 4);
                TestKit.CheckEqual(VisualFrameConsumeOutcome.Consumed,
                    authorizer.TryConsume(key, out unscaled, out scaled, out error), "consume");
                TestKit.Check(authorizer.TryCompleteScope(key, out error), "first complete: " + error);
                TestKit.Check(!authorizer.TryCompleteScope(key, out error), "second complete must fail");
                TestKit.CheckEqual("complete-already-completed:slot=Camera", error, "double complete error");

                TestKit.Check(!authorizer.TryCompleteScope(Key(VisualConsumerSlot.Camera, 1L, 60L, 5), out error),
                    "completing a different scope must fail");
                TestKit.Check(error.StartsWith("complete-scope-mismatch", StringComparison.Ordinal),
                    "scope mismatch error: " + error);
            });
        }

        // ---------------------------------------------------------------- EOF validation

        private static void EndOfFrameValidationTests()
        {
            TestKit.Run("visual auth: EOF completeness covers a multi-consumer frame exactly once", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.TweenBatch, out _);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.ParticleBatch, out _);

                VisualFrameAuthorization authorization;
                string error;
                TestKit.Check(authorizer.TryPublishFrame(Generation, 12L, 1200L, VisualFramePhase.Gameplay,
                        0.0, 0.0, Fps60, 1.0, 0.0, out authorization, out error), "publish: " + error);

                double unscaled;
                double scaled;
                int scope = 1;
                foreach (VisualConsumerSlot slot in new[]
                         {
                             VisualConsumerSlot.Camera, VisualConsumerSlot.TweenBatch, VisualConsumerSlot.ParticleBatch,
                         })
                {
                    VisualFrameConsumptionKey key = Key(slot, 12L, 1200L, scope++);
                    TestKit.CheckEqual(VisualFrameConsumeOutcome.Consumed,
                        authorizer.TryConsume(key, out unscaled, out scaled, out error), "consume " + slot);
                    TestKit.Check(authorizer.TryCompleteScope(key, out error), "complete " + slot + ": " + error);
                }

                TestKit.Check(authorizer.TryValidateEndOfFrame(out error), "all receipts present: " + error);
                TestKit.CheckEqual(0L, authorizer.ConsumedFrameCount, "counters only advance on close");
                authorizer.CloseCurrentFrame();
                TestKit.CheckEqual(1L, authorizer.ConsumedFrameCount, "one frame was closed");
                TestKit.CheckEqual(1L, authorizer.PublishedFrameCount, "one frame was published");
            });

            TestKit.Run("visual auth: otherwise valid receipts cannot rescue a failed consumer", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.TweenBatch, out _);

                VisualFrameAuthorization authorization;
                string error;
                TestKit.Check(authorizer.TryPublishFrame(Generation, 0L, 70L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error), "publish: " + error);

                double unscaled;
                double scaled;
                VisualFrameConsumptionKey cameraKey = Key(VisualConsumerSlot.Camera, 0L, 70L, 1);
                VisualFrameConsumptionKey tweenKey = Key(VisualConsumerSlot.TweenBatch, 0L, 70L, 2);
                authorizer.TryConsume(cameraKey, out unscaled, out scaled, out error);
                authorizer.TryCompleteScope(cameraKey, out error);
                authorizer.TryConsume(tweenKey, out unscaled, out scaled, out error);
                authorizer.TryCompleteScope(tweenKey, out error);
                TestKit.Check(authorizer.TryValidateEndOfFrame(out error), "baseline validate: " + error);

                authorizer.FailFrame(VisualConsumerSlot.TweenBatch, "tween-native-exception");
                TestKit.Check(!authorizer.TryValidateEndOfFrame(out error),
                    "a per-consumer failure must fail the frame even with every receipt present");
                TestKit.CheckEqual("eof-consumer-failed:slot=TweenBatch,reason=tween-native-exception", error,
                    "the failure is attributed to the owning slot");
                TestKit.Check(authorizer.CurrentFrameFailed, "the frame is flagged failed");
            });
        }

        // ---------------------------------------------------------------- invalidation / restart

        private static void InvalidationAndRestartTests()
        {
            TestKit.Run("visual auth: invalidation makes every consume and validation stale", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);

                VisualFrameAuthorization authorization;
                string error;
                TestKit.Check(authorizer.TryPublishFrame(Generation, 0L, 80L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error), "publish: " + error);

                authorizer.Invalidate();
                TestKit.Check(!authorizer.IsArmed, "invalidated authorizer is not armed");

                double unscaled;
                double scaled;
                TestKit.CheckEqual(VisualFrameConsumeOutcome.StaleAuthorization,
                    authorizer.TryConsume(Key(VisualConsumerSlot.Camera, 0L, 80L, 1), out unscaled, out scaled, out error),
                    "consume after invalidation is stale");
                TestKit.Check(!authorizer.TryValidateEndOfFrame(out error), "validation after invalidation fails");
                TestKit.CheckEqual("eof-validation-stale-authorization", error, "stale validation error");
                TestKit.Check(!authorizer.TryPublishFrame(Generation, 1L, 81L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error),
                    "publish after invalidation is rejected");
            });

            TestKit.Run("visual auth: re-arming with a new generation clears receipts and rejects the old generation", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);

                VisualFrameAuthorization authorization;
                string error;
                TestKit.Check(authorizer.TryPublishFrame(Generation, 0L, 90L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error), "publish: " + error);
                double unscaled;
                double scaled;
                VisualFrameConsumptionKey key = Key(VisualConsumerSlot.Camera, 0L, 90L, 1);
                authorizer.TryConsume(key, out unscaled, out scaled, out error);
                authorizer.TryCompleteScope(key, out error);
                authorizer.Invalidate();

                const long nextGeneration = Generation + 1;
                authorizer.Arm(nextGeneration);
                TestKit.CheckEqual(nextGeneration, authorizer.Generation, "new generation is bound");
                TestKit.Check(!authorizer.TryValidateEndOfFrame(out error),
                    "a freshly armed authorizer has no current authorization");

                // 旧 generation 的 owner 调用（迟到 Finalizer / Prefix）绝不能污染新 session。
                TestKit.CheckEqual(VisualFrameConsumeOutcome.StaleAuthorization,
                    authorizer.TryConsume(Key(VisualConsumerSlot.Camera, 0L, 90L, 2), out unscaled, out scaled, out error),
                    "the previous generation stays stale after re-arm");

                TestKit.Check(authorizer.TryPublishFrame(nextGeneration, 0L, 91L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error),
                    "the new generation can publish frame 0 again: " + error);
                TestKit.CheckEqual(nextGeneration, authorization.Generation, "published authorization carries the new generation");
                TestKit.CheckEqual(0L, authorization.AbsoluteOutputFrameIndex, "frame numbering restarts with the new session");
            });

            TestKit.Run("visual auth: a zero generation is never a valid authority", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                TestKit.Check(!authorizer.IsArmed, "unarmed by default");

                string error;
                TestKit.Check(!authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out error),
                    "consumers cannot register before arming");
                TestKit.CheckEqual("authorizer-not-armed", error, "registration error");

                VisualFrameAuthorization authorization;
                TestKit.Check(!authorizer.TryPublishFrame(0L, 0L, 1L, VisualFramePhase.PreEntry,
                        0.0, 0.0, Fps30, 1.0, 0.0, out authorization, out error),
                    "generation 0 must be rejected");

                bool threw = false;
                try
                {
                    authorizer.Arm(0L);
                }
                catch (ArgumentOutOfRangeException)
                {
                    threw = true;
                }
                TestKit.Check(threw, "Arm(0) must throw rather than bind an invalid authority");
            });
        }

        // ---------------------------------------------------------------- extreme but legal FPS

        private static void ExtremeFpsTests()
        {
            TestKit.Run("visual auth: every legal Output FPS projects a positive representable step", () =>
            {
                int[] samples =
                {
                    1, 2, 24, 29, 30, 59, 60, 61, 120, 240, 1000, 10000, 1000000, 1000000000, int.MaxValue,
                };

                for (int i = 0; i < samples.Length; i++)
                {
                    int fps = samples[i];
                    var authorizer = new VisualFrameAuthorizer();
                    authorizer.Arm(Generation);
                    authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);

                    VisualFrameAuthorization authorization;
                    string error;
                    TestKit.Check(authorizer.TryPublishFrame(Generation, 1L, 1L, VisualFramePhase.PreEntry,
                            0.0, 1.0 / fps, fps, 1.0, 0.0, out authorization, out error),
                        "publish at fps=" + fps.ToString(CultureInfo.InvariantCulture) + ": " + error);

                    double expected = 1.0 / fps;
                    TestKit.CheckEqual(expected, authorization.VisualUnscaledDeltaSeconds,
                        "unscaled step at fps=" + fps.ToString(CultureInfo.InvariantCulture));
                    TestKit.Check((float)authorization.VisualUnscaledDeltaSeconds > 0f,
                        "float step must stay positive at fps=" + fps.ToString(CultureInfo.InvariantCulture));

                    // 完整步与 G partial 步的数值关系在任何合法 FPS 下都必须成立。
                    TestKit.Check(authorizer.TryPublishFrame(Generation, 2L, 2L, VisualFramePhase.GameplayPartial,
                            0.0, 0.5 / fps, fps, 1.0, 0.5, out authorization, out error),
                        "partial publish at fps=" + fps.ToString(CultureInfo.InvariantCulture) + ": " + error);
                    TestKit.CheckEqual(0.5 * expected, authorization.VisualUnscaledDeltaSeconds,
                        "half step at fps=" + fps.ToString(CultureInfo.InvariantCulture));
                }
            });

            TestKit.Run("visual auth: a partial fraction inside the tolerance is clamped, outside it rejected", () =>
            {
                var authorizer = new VisualFrameAuthorizer();
                authorizer.Arm(Generation);
                authorizer.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);

                VisualFrameAuthorization authorization;
                string error;
                double justAboveOne = 1.0 + 1e-12;
                TestKit.Check(authorizer.TryPublishFrame(Generation, 1L, 1L, VisualFramePhase.GameplayPartial,
                        0.0, 0.0, Fps30, 1.0, justAboveOne, out authorization, out error),
                    "a fraction within the float tolerance must be accepted and clamped: " + error);
                TestKit.CheckEqual(1.0, authorization.PartialFraction, "clamped fraction");
                TestKit.CheckEqual(1.0 / Fps30, authorization.VisualUnscaledDeltaSeconds, "clamped fraction yields a full step");

                TestKit.Check(!authorizer.TryPublishFrame(Generation, 1L, 2L, VisualFramePhase.GameplayPartial,
                        0.0, 0.0, Fps30, 1.0, 1.5, out authorization, out error),
                    "a fraction clearly above 1 must be rejected");
                TestKit.Check(!authorizer.TryPublishFrame(Generation, 1L, 2L, VisualFramePhase.GameplayPartial,
                        0.0, 0.0, Fps30, 1.0, -0.25, out authorization, out error),
                    "a negative fraction must be rejected");
            });
        }

        // ---------------------------------------------------------------- helpers

        private static VisualFrameConsumptionKey Key(
            VisualConsumerSlot slot, long frame, long nativeFrame, int scope)
        {
            return new VisualFrameConsumptionKey(slot, Generation, frame, nativeFrame, scope);
        }

        /// <summary>restart 场景需要显式指定新 generation（<see cref="Key"/> 固定用首个 session）。</summary>
        private static VisualFrameConsumptionKey KeyForGeneration(
            long generation, VisualConsumerSlot slot, long frame, long nativeFrame, int scope)
        {
            return new VisualFrameConsumptionKey(slot, generation, frame, nativeFrame, scope);
        }
    }
}
