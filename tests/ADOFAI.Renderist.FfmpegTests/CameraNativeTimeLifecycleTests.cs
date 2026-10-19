using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ADOFAI.Renderist.Export;
using HarmonyLib;

namespace ADOFAI.Renderist.FfmpegTests
{
    internal static class CameraNativeTimeLifecycleTests
    {
        private const long Generation = 17;

        public static void Run()
        {
            TestKit.Run("camera lifecycle: Waiting calls finish without consuming or failing", () =>
            {
                using (var f = new Fixture())
                {
                    for (int frame = 1; frame <= 4; frame++)
                    {
                        f.Frame(frame);
                        f.Call("Update", () =>
                        {
                            TestKit.CheckEqual(0f, f.Delta(), "waiting delta");
                            f.Call("UpdateSize", () => TestKit.CheckEqual(0f, f.Delta(), "nested waiting delta"));
                        });
                        f.Call("Update", () => { }); // A native branch with no delta read is legal here.
                    }
                    f.Healthy();
                    TestKit.CheckEqual(0L, f.Auth.PublishedFrameCount, "waiting publishes no output");
                }
            });

            TestKit.Run("camera lifecycle: Waiting to frame 0 to frame 1 shares one coherent zero step", () =>
            {
                using (var f = new Fixture())
                {
                    f.Call("Update", () => TestKit.CheckEqual(0f, f.Delta(), "before first prepare"));
                    VisualFrameAuthorization zero = f.Publish(0, 10);
                    TestKit.CheckEqual(0.0, zero.VisualScaledDeltaSeconds, "immutable frame 0 scaled delta");
                    TestKit.CheckEqual(0.0, zero.VisualUnscaledDeltaSeconds, "immutable frame 0 unscaled delta");
                    f.Call("Update", () =>
                    {
                        TestKit.CheckEqual(0f, f.Delta(), "frame 0 first read");
                        f.Call("UpdateSize", () => TestKit.CheckEqual(0f, f.Delta(), "frame 0 nested read"));
                        TestKit.CheckEqual(0f, f.Delta(), "frame 0 final read");
                    });
                    f.Eof();
                    f.Publish(1, 11);
                    float delta = 0f;
                    f.Call("Update", () =>
                    {
                        delta = f.Delta();
                        f.Call("UpdateSize", () => TestKit.CheckEqual(delta, f.Delta(), "one nested step"));
                        TestKit.CheckEqual(delta, f.Delta(), "repeated root read");
                    });
                    TestKit.CheckEqual((float)(1.0 / 30), delta, "frame 1 full step");
                    TestKit.CheckEqual(100f + delta, CameraNativeTimeOwnership.GetCameraVisualTime(f.Camera),
                        "the clock accumulated frame 1 only once");
                    f.Eof();
                    f.Healthy();
                    TestKit.CheckEqual(0.0, zero.VisualScaledDeltaSeconds, "retained frame 0 stays immutable");
                }
            });

            TestKit.Run("camera lifecycle: EOF to AwaitingDelivery waits without replaying the step", () =>
            {
                using (var f = new Fixture())
                {
                    f.Publish(4, 40);
                    f.Call("Update", () => f.Delta());
                    f.Eof();
                    float clock = CameraNativeTimeOwnership.GetCameraVisualTime(f.Camera);
                    for (int nativeFrame = 41; nativeFrame < 50; nativeFrame++)
                    {
                        f.Frame(nativeFrame);
                        f.Call("Update", () => TestKit.CheckEqual(0f, f.Delta(), "pending delivery delta"));
                    }
                    TestKit.CheckEqual(clock, CameraNativeTimeOwnership.GetCameraVisualTime(f.Camera), "frozen clock");
                    f.Publish(5, 50);
                    f.Call("Update", () => TestKit.CheckEqual((float)(1.0 / 30), f.Delta(), "next step"));
                    f.Eof();
                    f.Healthy();
                }
            });

            TestKit.Run("camera lifecycle: B-1 to Sealed across hidden native frames to G completes every scope", () =>
            {
                using (var f = new Fixture())
                {
                    f.Publish(5, 100);
                    f.Call("Update", () => f.Delta());
                    f.Eof();
                    TestKit.Check(f.Auth.TryPublishLifecycleOnly(Generation, 6, 101, VisualFramePhase.Sealed,
                        1, 30, 1, out _, out string error), error);
                    for (int nativeFrame = 101; nativeFrame < 110; nativeFrame++)
                    {
                        f.Frame(nativeFrame);
                        f.Call("Update", () =>
                        {
                            TestKit.CheckEqual(0f, f.Delta(), "hidden delta");
                            f.Call("UpdateSize", () => TestKit.CheckEqual(0f, f.Delta(), "hidden nested delta"));
                        });
                    }
                    f.Healthy();
                    f.Publish(6, 110, VisualFramePhase.GameplayPartial, 0.1);
                    f.Call("Update", () => TestKit.CheckEqual((float)(0.1 / 30), f.Delta(), "G partial"));
                    f.Eof();
                    f.Frame(111);
                    f.Call("Update", () => TestKit.CheckEqual(0f, f.Delta(), "no post-G leak"));
                    f.Publish(7, 112);
                    f.Call("Update", () => TestKit.CheckEqual((float)(1.0 / 30), f.Delta(), "G+1 full step"));
                    f.Eof();
                    f.Healthy();
                }
            });

            TestKit.Run("camera lifecycle: standalone UpdateSize before and after Update cannot own its receipt", () =>
            {
                using (var f = new Fixture())
                {
                    f.Publish(2, 20);
                    Action size = () => f.Call("UpdateSize", () =>
                    {
                        TestKit.CheckEqual(0f, f.Delta(), "standalone size delta");
                        f.Call("UpdateSize", () => TestKit.CheckEqual(0f, f.Delta(), "nested standalone size delta"));
                    });
                    size();
                    TestKit.Check(!f.Auth.TryValidateEndOfFrame(out _), "UpdateSize did not claim a receipt");
                    f.Call("Update", () =>
                    {
                        float step = f.Delta();
                        f.Call("UpdateSize", () => TestKit.CheckEqual(step, f.Delta(), "size shares main Update"));
                    });
                    size();
                    f.Eof();
                    f.Healthy();
                }
            });

            foreach (long index in new long[] { 0, 1 })
            {
                long outputIndex = index;
                TestKit.Run("camera lifecycle: Authorized Update without a delta read fails at frame " + index, () =>
                {
                    using (var f = new Fixture())
                    {
                        f.Publish(outputIndex, 10);
                        f.Call("Update", () => { });
                        TestKit.Check(f.Failures.Count == 1, "missing consumption requests stop once");
                        TestKit.Check(!f.Auth.TryValidateEndOfFrame(out _), "EOF cannot succeed");
                        f.NoScopes();
                    }
                });
            }

            TestKit.Run("camera lifecycle: a second ordinary Update stays a duplicate", () =>
            {
                using (var f = new Fixture())
                {
                    f.Publish(1, 10);
                    f.Call("Update", () => f.Delta());
                    f.Call("Update", () => TestKit.CheckEqual(0f, f.Delta(), "duplicate denied"));
                    TestKit.Check(f.Failures.Count == 1, "duplicate requests stop once");
                    TestKit.Check(!f.Auth.TryValidateEndOfFrame(out _), "duplicate fails frame");
                }
            });

            foreach (bool waiting in new[] { true, false })
            {
                bool beforePrepare = waiting;
                TestKit.Run("camera lifecycle: exceptional Finalizer clears scopes in " + (waiting ? "Waiting" : "Authorized"), () =>
                {
                    using (var f = new Fixture())
                    {
                        if (!beforePrepare) f.Publish(1, 10);
                        var original = new InvalidOperationException("native failure");
                        Exception observed = null;
                        try { f.Call("Update", () => { f.Delta(); throw original; }); }
                        catch (InvalidOperationException ex) { observed = ex; }
                        TestKit.Check(ReferenceEquals(original, observed), "the original exception is preserved");
                        TestKit.Check(f.Failures.Count == 1, "exception requests scheduler failure");
                        f.NoScopes();
                        TestKit.Check(!f.Auth.TryValidateEndOfFrame(out _), "failed native invocation cannot commit");
                    }
                });
            }

            TestKit.Run("camera lifecycle: caught nested UpdateSize failure cannot be acknowledged by outer Update", () =>
            {
                using (var f = new Fixture())
                {
                    f.Publish(1, 10);
                    f.Call("Update", () =>
                    {
                        f.Delta();
                        try { f.Call("UpdateSize", () => { throw new InvalidOperationException("size failure"); }); }
                        catch (InvalidOperationException) { }
                    });
                    TestKit.Check(f.Failures.Count == 1, "nested Finalizer requests stop");
                    TestKit.Check(!f.Auth.TryValidateEndOfFrame(out _), "caught nested failure remains failed");
                    f.NoScopes();
                }
            });

            TestKit.Run("camera lifecycle: Cancel and Restart ignore late callbacks from the previous owner", () =>
            {
                var auth = new VisualFrameAuthorizer();
                using (var first = new Fixture(auth, Generation))
                {
                    first.Publish(1, 10);
                    object late = first.Prefix("Update");
                    first.Delta();
                    auth.Invalidate();
                    first.Dispose();
                    using (var next = new Fixture(auth, Generation + 1))
                    {
                        next.Publish(0, 11);
                        first.Postfix("Update", late);
                        first.Finalizer("Update", late, new InvalidOperationException("late"));
                        next.Call("Update", () => TestKit.CheckEqual(0f, next.Delta(), "restarted frame 0"));
                        next.Eof();
                        next.Publish(1, 12);
                        next.Call("Update", () => next.Delta());
                        next.Eof();
                        next.Healthy();
                    }
                }
            });

            TestKit.Run("visual lifecycle: lifecycle-only consume and complete pair on every hidden native frame", () =>
            {
                var auth = new VisualFrameAuthorizer();
                auth.Arm(Generation);
                auth.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);
                TestKit.Check(auth.TryPublishLifecycleOnly(Generation, 6, 100, VisualFramePhase.Sealed,
                    1, 30, 1, out _, out string error), error);
                for (int frame = 100; frame < 105; frame++)
                {
                    var key = new VisualFrameConsumptionKey(VisualConsumerSlot.Camera, Generation, 6, frame, frame);
                    TestKit.CheckEqual(VisualFrameConsumeOutcome.NoVisualStep,
                        auth.TryConsume(key, out double u, out double s, out error), error);
                    TestKit.CheckEqual(0.0, u + s, "zero deltas");
                    TestKit.Check(auth.TryCompleteScope(key, out error), "complete hidden frame: " + error);
                    TestKit.Check(!auth.TryCompleteScope(key, out _), "a completed invocation cannot acknowledge again");
                }
            });

            TestKit.Run("visual lifecycle: zero-step nested consumption and immutable snapshot agree", () =>
            {
                var auth = new VisualFrameAuthorizer();
                auth.Arm(Generation);
                auth.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);
                TestKit.Check(auth.TryPublishFrame(Generation, 0, 10, VisualFramePhase.PreEntry, 0, 0, 30, 1, 1,
                    out VisualFrameAuthorization a, out string error), error);
                var key = new VisualFrameConsumptionKey(VisualConsumerSlot.Camera, Generation, 0, 10, 1);
                TestKit.CheckEqual(VisualFrameConsumeOutcome.NoVisualStep, auth.TryConsume(key, out double u, out double s, out error), error);
                TestKit.CheckEqual(VisualFrameConsumeOutcome.NestedInScope, auth.TryConsume(key, out double nu, out double ns, out error), error);
                TestKit.CheckEqual(u, nu, "nested unscaled delta");
                TestKit.CheckEqual(s, ns, "nested scaled delta");
                TestKit.CheckEqual(a.VisualScaledDeltaSeconds, s, "snapshot scaled delta");
                TestKit.CheckEqual(a.VisualUnscaledDeltaSeconds, u, "snapshot unscaled delta");
                TestKit.Check(auth.TryCompleteScope(key, out error), error);
                TestKit.Check(auth.TryValidateEndOfFrame(out error), error);
            });

            TestKit.Run("visual lifecycle: old-generation completion cannot complete a colliding new receipt", () =>
            {
                var auth = new VisualFrameAuthorizer();
                auth.Arm(Generation);
                auth.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);
                var old = new VisualFrameConsumptionKey(VisualConsumerSlot.Camera, Generation, 0, 10, 1);
                auth.Invalidate();
                auth.Arm(Generation + 1);
                auth.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);
                auth.TryPublishFrame(Generation + 1, 0, 10, VisualFramePhase.PreEntry, 0, 0, 30, 1, 1, out _, out _);
                var current = new VisualFrameConsumptionKey(VisualConsumerSlot.Camera, Generation + 1, 0, 10, 1);
                auth.TryConsume(current, out _, out _, out _);
                TestKit.Check(!auth.TryCompleteScope(old, out _), "old generation is rejected even when every other key field matches");
                TestKit.Check(!auth.TryValidateEndOfFrame(out _), "the new scope remains incomplete");
                TestKit.Check(auth.TryCompleteScope(current, out string error), error);
            });

            TestKit.Run("visual lifecycle: an unfinished hidden invocation cannot be replaced by another zero step", () =>
            {
                var auth = new VisualFrameAuthorizer();
                auth.Arm(Generation);
                auth.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);
                auth.TryPublishLifecycleOnly(Generation, 6, 100, VisualFramePhase.Sealed, 1, 30, 1, out _, out _);
                var first = new VisualFrameConsumptionKey(VisualConsumerSlot.Camera, Generation, 6, 100, 1);
                var next = new VisualFrameConsumptionKey(VisualConsumerSlot.Camera, Generation, 6, 101, 2);
                auth.TryConsume(first, out _, out _, out _);
                TestKit.CheckEqual(VisualFrameConsumeOutcome.DuplicateConsumption,
                    auth.TryConsume(next, out _, out _, out _), "unfinished scope is not a completed lifecycle call");
                TestKit.Check(auth.TryCompleteScope(first, out string error), error);
                TestKit.CheckEqual(VisualFrameConsumeOutcome.NoVisualStep,
                    auth.TryConsume(next, out _, out _, out error), error);
                TestKit.Check(!auth.TryCompleteScope(first, out _), "late completion cannot acknowledge the next invocation");
                TestKit.Check(auth.TryCompleteScope(next, out error), error);
            });

            TestKit.Run("visual lifecycle: a failed receipt cannot be marked complete", () =>
            {
                var auth = new VisualFrameAuthorizer();
                auth.Arm(Generation);
                auth.TryRegisterConsumer(VisualConsumerSlot.Camera, out _);
                auth.TryPublishFrame(Generation, 1, 10, VisualFramePhase.PreEntry, 0, 0, 30, 1, 1, out _, out _);
                var key = new VisualFrameConsumptionKey(VisualConsumerSlot.Camera, Generation, 1, 10, 1);
                auth.TryConsume(key, out _, out _, out _);
                auth.FailFrame(VisualConsumerSlot.Camera, "native exception");
                TestKit.Check(!auth.TryCompleteScope(key, out _), "failure remains terminal for this receipt");
                TestKit.Check(!auth.TryValidateEndOfFrame(out _), "no commit after failure");
            });

            TestKit.Run("camera lifecycle: an old scope cannot replay its cached delta into a new authorization", () =>
            {
                using (var f = new Fixture())
                {
                    f.Publish(1, 10);
                    object old = f.Prefix("Update");
                    TestKit.Check(f.Delta() > 0, "first authorized read");
                    // Deliberately exercise a stale in-flight callback across a closure/publication.
                    f.Auth.CloseCurrentFrame();
                    f.Publish(2, 11);
                    TestKit.CheckEqual(0f, f.Delta(), "cached time must not escape its original key");
                    f.Postfix("Update", old);
                    f.Finalizer("Update", old, null);
                    TestKit.CheckEqual(1, f.Failures.Count, "phase mismatch fails once");
                    TestKit.Check(!f.Auth.TryValidateEndOfFrame(out _), "the new authorization is not acknowledged");
                    f.NoScopes();
                }
            });

            TestKit.Run("camera lifecycle: a native frame after Prepare cannot claim the real output step", () =>
            {
                using (var f = new Fixture())
                {
                    f.Publish(1, 10);
                    f.Frame(11);
                    f.Call("Update", () => TestKit.CheckEqual(0f, f.Delta(), "late native frame receives no time"));
                    TestKit.CheckEqual(1, f.Failures.Count, "out-of-phase still fails closed");
                    TestKit.Check(!f.Auth.TryValidateEndOfFrame(out _), "missing current-frame receipt blocks EOF");
                }
            });
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly VisualFrameAuthorizer Auth;
            internal readonly CameraNativeTimeOwnership Owner;
            internal readonly object Camera = new object();
            internal readonly List<string> Failures = new List<string>();
            private readonly long _generation;
            private bool _disposed;

            internal Fixture(VisualFrameAuthorizer auth = null, long generation = Generation)
            {
                _generation = generation;
                Auth = auth ?? new VisualFrameAuthorizer();
                Auth.Arm(generation);
                TestKit.Check(Auth.TryRegisterConsumer(VisualConsumerSlot.Camera, out string error), error);
                Owner = new CameraNativeTimeOwnership(new Harmony("renderist.tests.camera.lifecycle"), Auth, Failures.Add);
                // Only native installation/instance acquisition is bypassed. All scope,
                // delta, acknowledgement and Finalizer operations below are production code.
                Set("_installed", true);
                Set("_active", true);
                Set("_generation", generation);
                Set("_cameraInstance", Camera);
                Set("_visualClockBase", 100.0);
                typeof(CameraNativeTimeOwnership).GetProperty("Active", BindingFlags.NonPublic | BindingFlags.Static)
                    .SetValue(null, Owner);
                Frame(1);
            }

            private void Set(string name, object value)
            {
                typeof(CameraNativeTimeOwnership).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(Owner, value);
            }

            internal void Frame(int frame) { UnityEngine.Time.frameCount = frame; }
            internal float Delta() { return CameraNativeTimeOwnership.GetCameraDeltaTime(Camera); }
            internal VisualFrameAuthorization Publish(long index, int frame,
                VisualFramePhase phase = VisualFramePhase.PreEntry, double fraction = 1)
            {
                Frame(frame);
                TestKit.Check(Auth.TryPublishFrame(_generation, index, frame, phase, index / 30.0, 1.0 / 30,
                    30, 1, fraction, out VisualFrameAuthorization value, out string error), error);
                return value;
            }

            internal object Prefix(string method)
            {
                object[] args = { Camera, null };
                Invoke(method + "Prefix", args);
                return args[1];
            }
            internal void Postfix(string method, object state) { Invoke(method + "Postfix", new[] { state }); }
            internal void Finalizer(string method, object state, Exception ex) { Invoke(method + "Finalizer", new object[] { ex, state }); }

            internal void Call(string method, Action body)
            {
                object state = Prefix(method);
                Exception error = null;
                try
                {
                    body();
                    Postfix(method, state);
                }
                catch (Exception ex) { error = ex; throw; }
                finally { Finalizer(method, state, error); }
            }

            private static void Invoke(string method, object[] args)
            {
                typeof(CameraNativeTimeOwnership).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);
            }

            internal void Eof()
            {
                TestKit.Check(Auth.TryValidateEndOfFrame(out string error), "EOF before readback: " + error);
                Auth.CloseCurrentFrame();
            }
            internal void Healthy()
            {
                TestKit.CheckEqual(0, Failures.Count, "no failure: " + string.Join(";", Failures));
                NoScopes();
            }
            internal void NoScopes()
            {
                var scopes = (ICollection)typeof(CameraNativeTimeOwnership).GetField("NativeCallScopes",
                    BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                TestKit.CheckEqual(0, scopes.Count, "no leaked native scope");
            }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                TestKit.Check(Owner.TryDispose(out string error), "dispose: " + error);
                NoScopes();
            }
        }
    }
}
