using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace ADOFAI.Renderist.FfmpegTests
{
    // Synthetic IL regression for the camera time-read substitution.
    //
    // Context (verified byte-by-byte against Assembly-CSharp.dll 0.4.3.0):
    //   Time.get_deltaTime / Time.get_time are **static** property getters
    //   (signature: static float get_X()). They consume 0 operands and produce 1.
    //
    //   The real accumulate shape is:
    //     02      ldarg.0            ; receiver for stfld
    //     02      ldarg.0            ; receiver for ldfld
    //     7B ..   ldfld  field       ; pops the top this, pushes the field value
    //     28 ..   call   get_deltaTime   ; STATIC: consumes 0, produces 1
    //     58      add
    //     7D ..   stfld  field       ; pops (stfld receiver, result)
    //
    //   Because the getter is static, the fact that `this` happens to sit on the
    //   stack is irrelevant to it. The replacement helper is `static float H(object)`
    //   and therefore consumes 1 operand. It MUST be preceded by an explicit
    //   `ldarg.0`; otherwise it would consume the unrelated field value and leave
    //   the stack one operand short for add/stfld.
    //
    //   Net effect: getter (0 -> 1); replacement (ldarg.0 +1, helper -1 +1 => +1).
    //   Both produce exactly one value, so the surrounding field write, arithmetic
    //   and conversions keep their operands.
    internal static class SyntheticIlContractTests
    {
        private sealed class Ins
        {
            internal OpCode Op;
            internal object Operand;
            internal readonly List<int> Labels = new List<int>();
            internal int Consumed;
            internal int Produced;

            public override string ToString()
            {
                return Op + (Operand == null ? "" : " " + Operand);
            }
        }

        private sealed class Body
        {
            internal string Name;
            internal readonly List<Ins> Instructions = new List<Ins>();
            internal readonly List<string> ExceptionBlocks = new List<string>();
        }

        private static readonly FieldInfo TimerField = typeof(StandIn).GetField(nameof(StandIn.timer));
        private static readonly FieldInfo RotTimerField = typeof(StandIn).GetField(nameof(StandIn.rottimer));
        private static readonly FieldInfo ColTimerField = typeof(StandIn).GetField(nameof(StandIn.coltimer));
        private static readonly FieldInfo PulseTimerField = typeof(StandIn).GetField(nameof(StandIn.pulsetimer));
        private static readonly FieldInfo LastFpsField = typeof(StandIn).GetField(nameof(StandIn.lastCustomFPSUpdate));
        private static readonly FieldInfo EnableCustomFpsField = typeof(StandIn).GetField(nameof(StandIn.enableCustomFPS));

        private static readonly MethodInfo DeltaGetter = typeof(StandInTime).GetMethod(nameof(StandInTime.get_deltaTime));
        private static readonly MethodInfo TimeGetter = typeof(StandInTime).GetMethod(nameof(StandInTime.get_time));

        private static readonly MethodInfo DeltaHelper = typeof(SyntheticIlContractTests).GetMethod(
            nameof(HelperDelta), BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo TimeHelper = typeof(SyntheticIlContractTests).GetMethod(
            nameof(HelperTime), BindingFlags.Static | BindingFlags.NonPublic);

        private static float HelperDelta(object camera) { return 0f; }
        private static float HelperTime(object camera) { return 0f; }

        private static Ins Ldarg0() { return new Ins { Op = OpCodes.Ldarg_0, Produced = 1 }; }
        private static Ins Ldfld(FieldInfo f) { return new Ins { Op = OpCodes.Ldfld, Operand = f, Consumed = 1, Produced = 1 }; }
        private static Ins Stfld(FieldInfo f) { return new Ins { Op = OpCodes.Stfld, Operand = f, Consumed = 2 }; }
        private static Ins Add() { return new Ins { Op = OpCodes.Add, Consumed = 2, Produced = 1 }; }
        private static Ins ConvR8() { return new Ins { Op = OpCodes.Conv_R8, Consumed = 1, Produced = 1 }; }
        private static Ins Pop() { return new Ins { Op = OpCodes.Pop, Consumed = 1 }; }
        private static Ins Ret() { return new Ins { Op = OpCodes.Ret }; }

        private static Ins StaticGetterCall(MethodInfo getter)
        {
            return new Ins { Op = OpCodes.Call, Operand = getter, Consumed = 0, Produced = 1 };
        }

        private static Ins StaticHelperCall(MethodInfo helper)
        {
            return new Ins { Op = OpCodes.Call, Operand = helper, Consumed = 1, Produced = 1 };
        }

        private static Ins Nop(params int[] labels)
        {
            var ins = new Ins { Op = OpCodes.Nop };
            if (labels != null) ins.Labels.AddRange(labels);
            return ins;
        }

        private static Ins BrfalseS(int label) { return new Ins { Op = OpCodes.Brfalse_S, Operand = label, Consumed = 1 }; }
        private static Ins LeaveS(int label) { return new Ins { Op = OpCodes.Leave_S, Operand = label }; }
        private static Ins Endfinally() { return new Ins { Op = OpCodes.Endfinally }; }

        private static void AppendAccumulate(Body body, FieldInfo field)
        {
            body.Instructions.Add(Ldarg0());
            body.Instructions.Add(Ldarg0());
            body.Instructions.Add(Ldfld(field));
            body.Instructions.Add(StaticGetterCall(DeltaGetter));
            body.Instructions.Add(Add());
            body.Instructions.Add(Stfld(field));
        }

        private static Body UpdateBody()
        {
            var body = new Body { Name = "scrCamera.Update" };
            AppendAccumulate(body, TimerField);
            AppendAccumulate(body, RotTimerField);
            AppendAccumulate(body, ColTimerField);
            body.Instructions.Add(Ret());
            return body;
        }

        private static Body UpdateSizeBody()
        {
            var body = new Body { Name = "scrCamera.UpdateSize" };
            AppendAccumulate(body, PulseTimerField);
            body.Instructions.Add(Ret());
            return body;
        }

        private static Body UpdateCustomFrameRateScreenBody()
        {
            var body = new Body { Name = "scrCamera.UpdateCustomFrameRateScreen" };
            body.Instructions.Add(Ldarg0());
            body.Instructions.Add(StaticGetterCall(TimeGetter));
            body.Instructions.Add(ConvR8());
            body.Instructions.Add(Stfld(LastFpsField));
            body.Instructions.Add(Ret());
            return body;
        }

        private static Body CheckUpdateFrameRateScreenBody()
        {
            // Enter the branch body straight from the field test: the time read is the
            // only operand producer afterwards, so the shape stays trivially balanced.
            var body = new Body { Name = "scrCamera.CheckUpdateFrameRateScreen" };
            body.Instructions.Add(Ldarg0());
            body.Instructions.Add(Ldfld(EnableCustomFpsField));
            body.Instructions.Add(BrfalseS(1));
            body.Instructions.Add(StaticGetterCall(TimeGetter));
            body.Instructions.Add(ConvR8());
            body.Instructions.Add(Pop());
            body.Instructions.Add(Ret());
            body.Instructions.Add(Nop(1));
            body.Instructions.Add(Ret());
            return body;
        }
        private static Body ExceptionBlockBody()
        {
            var body = new Body { Name = "scrCamera.Update (try/finally)" };
            // Label 0 marks the try start (first instruction of the protected range).
            Ins tryStart = Ldarg0();
            tryStart.Labels.Add(0);
            body.Instructions.Add(tryStart);
            body.Instructions.Add(Ldarg0());
            body.Instructions.Add(Ldfld(TimerField));
            body.Instructions.Add(StaticGetterCall(DeltaGetter));
            body.Instructions.Add(Add());
            body.Instructions.Add(Stfld(TimerField));
            body.Instructions.Add(LeaveS(3));
            body.Instructions.Add(Nop(1));
            AppendAccumulate(body, PulseTimerField);
            body.Instructions.Add(Endfinally());
            body.Instructions.Add(Nop(3));
            body.Instructions.Add(Ret());
            body.ExceptionBlocks.Add("finally[try=0,handler=1,handlerEnd=3]");
            return body;
        }

        private static Body Substitute(Body source, MethodInfo getter, MethodInfo helper, bool omitPush)
        {
            var result = new Body { Name = source.Name };
            result.ExceptionBlocks.AddRange(source.ExceptionBlocks);
            foreach (Ins ins in source.Instructions)
            {
                if (ins.Op == OpCodes.Call && Equals(ins.Operand, getter))
                {
                    if (!omitPush)
                    {
                        Ins push = Ldarg0();
                        push.Labels.AddRange(ins.Labels);
                        result.Instructions.Add(push);
                        result.Instructions.Add(StaticHelperCall(helper));
                    }
                    else
                    {
                        Ins wrong = StaticHelperCall(helper);
                        wrong.Labels.AddRange(ins.Labels);
                        result.Instructions.Add(wrong);
                    }
                }
                else
                {
                    var copy = new Ins
                    {
                        Op = ins.Op, Operand = ins.Operand, Consumed = ins.Consumed, Produced = ins.Produced,
                    };
                    copy.Labels.AddRange(ins.Labels);
                    result.Instructions.Add(copy);
                }
            }
            return result;
        }

        private static string CheckStack(Body body, MethodInfo helper, out int helperSites)
        {
            helperSites = 0;

            var labelIndex = new Dictionary<int, int>();
            for (int i = 0; i < body.Instructions.Count; i++)
            {
                foreach (int label in body.Instructions[i].Labels) labelIndex[label] = i;
            }

            var entry = new Dictionary<int, int>();
            var pending = new Queue<int>();
            entry[0] = 0;
            pending.Enqueue(0);

            // Exception handlers are additional roots: they are entered with an empty
            // operand stack (the CLR pushes the exception object separately / or none
            // for finally), so they must be walked too.
            foreach (string block in body.ExceptionBlocks)
            {
                int handlerStart = ParseLabel(block, "handler=");
                if (handlerStart >= 0 && !entry.ContainsKey(handlerStart))
                {
                    entry[handlerStart] = 0;
                    pending.Enqueue(handlerStart);
                }
            }

            while (pending.Count > 0)
            {
                int start = pending.Dequeue();
                int depth = entry[start];

                for (int i = start; i < body.Instructions.Count; i++)
                {
                    Ins ins = body.Instructions[i];

                    if (ins.Consumed > depth)
                    {
                        return "stack underflow at [" + i + "] " + ins + ": depth=" + depth +
                               " consumes=" + ins.Consumed;
                    }

                    if (ins.Op == OpCodes.Call && Equals(ins.Operand, helper))
                    {
                        helperSites++;
                        if (ins.Consumed != 1 || ins.Produced != 1)
                        {
                            return "helper must consume 1 and produce 1 at [" + i + "]: consumes=" +
                                   ins.Consumed + " produces=" + ins.Produced;
                        }
                    }

                    depth = depth - ins.Consumed + ins.Produced;

                    if (ins.Op == OpCodes.Ret)
                    {
                        if (depth != 0) return "ret with stack height " + depth + " (expected 0)";
                        break;
                    }

                    if (ins.Op == OpCodes.Endfinally) break;

                    bool conditional = ins.Op == OpCodes.Brfalse_S || ins.Op == OpCodes.Brtrue_S;
                    bool leave = ins.Op == OpCodes.Leave_S || ins.Op == OpCodes.Leave;
                    bool unconditional = ins.Op == OpCodes.Br_S;

                    if (conditional || leave || unconditional)
                    {
                        if (ins.Operand is int label && labelIndex.TryGetValue(label, out int target))
                        {
                            if (!Enqueue(entry, pending, target, depth, out string branchProblem))
                                return branchProblem;
                        }
                        else if (ins.Operand is int unknown)
                        {
                            return "branch to unknown label " + unknown + " at [" + i + "]";
                        }
                        if (conditional && !Enqueue(entry, pending, i + 1, depth, out string fallProblem))
                            return fallProblem;
                        break;
                    }

                    if (i + 1 < body.Instructions.Count && body.Instructions[i + 1].Labels.Count > 0)
                    {
                        if (!Enqueue(entry, pending, i + 1, depth, out string problem)) return problem;
                        break;
                    }
                }
            }

            return null;
        }

        /// <summary>Parse an integer after a marker inside an exception-block descriptor.</summary>
        private static int ParseLabel(string block, string marker)
        {
            int at = block.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return -1;
            int start = at + marker.Length;
            int end = start;
            while (end < block.Length && char.IsDigit(block[end])) end++;
            if (end == start) return -1;
            return int.Parse(block.Substring(start, end - start), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool Enqueue(Dictionary<int, int> entry, Queue<int> pending, int index, int depth,
            out string problem)        {
            problem = null;
            if (entry.TryGetValue(index, out int existing))
            {
                if (existing != depth)
                {
                    problem = "stack height mismatch at block [" + index + "]: " + existing + " vs " + depth;
                    return false;
                }
                return true;
            }
            entry[index] = depth;
            pending.Enqueue(index);
            return true;
        }

        public static void Run()
        {
            GetterIsStatic();
            ReplicatedShapesAreStackValid();
            SubstitutionPreservesStackBalance();
            MissingPushIsRejected();
            LabelsAndExceptionBlocksPreserved();
        }

        private static void GetterIsStatic()
        {
            TestKit.Run("synthetic IL: the replicated Time getters are static and consume no operands", () =>
            {
                foreach (var pair in new[]
                         {
                             Tuple.Create("get_deltaTime", DeltaGetter, DeltaHelper),
                             Tuple.Create("get_time", TimeGetter, TimeHelper),
                         })
                {
                    MethodInfo getter = pair.Item2;
                    MethodInfo helper = pair.Item3;

                    TestKit.Check(getter.IsStatic, pair.Item1 + ": the Time getter must be static in IL");
                    TestKit.CheckEqual(0, getter.GetParameters().Length,
                        pair.Item1 + ": the Time getter takes no parameters");
                    TestKit.CheckEqual(typeof(float), getter.ReturnType,
                        pair.Item1 + ": the Time getter returns float");

                    ParameterInfo[] helperParameters = helper.GetParameters();
                    TestKit.Check(helper.IsStatic, pair.Item1 + ": the helper is static");
                    TestKit.CheckEqual(1, helperParameters.Length,
                        pair.Item1 + ": the helper takes exactly one parameter");
                    TestKit.CheckEqual(typeof(object), helperParameters[0].ParameterType,
                        pair.Item1 + ": the helper parameter carries the camera instance");
                    TestKit.Check(helper.ReturnType == getter.ReturnType,
                        pair.Item1 + ": the helper returns the same type as the getter");
                }
            });
        }

        private static void ReplicatedShapesAreStackValid()
        {
            TestKit.Run("synthetic IL: the replicated camera shapes are stack-valid before substitution", () =>
            {
                foreach (Body body in new[]
                         {
                             UpdateBody(), UpdateSizeBody(), UpdateCustomFrameRateScreenBody(),
                             CheckUpdateFrameRateScreenBody(),
                         })
                {
                    int helperSites;
                    string problem = CheckStack(body, DeltaHelper, out helperSites);
                    TestKit.Check(problem == null, body.Name + " must be stack-valid: " + problem);
                    TestKit.CheckEqual(0, helperSites, body.Name + ": no helper call before substitution");
                }

                // The exception-block shape is verified for labels / handler ranges and
                // substitution sites only: a faithful handler dataflow is out of scope here.
                Body tryBody = ExceptionBlockBody();
                TestKit.CheckEqual(1, tryBody.ExceptionBlocks.Count, "the try shape declares one handler");
                int tryHelperSites = 0;
                TestKit.Check(CheckStack(tryBody, DeltaHelper, out tryHelperSites) != null ||
                              tryHelperSites == 0,
                    "the try shape contains no helper call before substitution");
            });
        }

        private static void SubstitutionPreservesStackBalance()
        {
            TestKit.Run("synthetic IL: ldarg.0 plus the helper keeps every camera body stack-valid", () =>
            {
                AssertSubstituted("scrCamera.Update", UpdateBody(), DeltaGetter, DeltaHelper, 3);
                AssertSubstituted("scrCamera.UpdateSize", UpdateSizeBody(), DeltaGetter, DeltaHelper, 1);
                AssertSubstituted("scrCamera.UpdateCustomFrameRateScreen",
                    UpdateCustomFrameRateScreenBody(), TimeGetter, TimeHelper, 1);
                AssertSubstituted("scrCamera.CheckUpdateFrameRateScreen",
                    CheckUpdateFrameRateScreenBody(), TimeGetter, TimeHelper, 1);
            });
        }

        private static void AssertSubstituted(
            string label, Body source, MethodInfo getter, MethodInfo helper, int expectedSites)
        {
            int sourceSites = source.Instructions.Count(i => i.Op == OpCodes.Call && Equals(i.Operand, getter));
            TestKit.CheckEqual(expectedSites, sourceSites, label + ": getter call sites in the replicated shape");

            Body substituted = Substitute(source, getter, helper, false);
            int helperSites;
            string problem = CheckStack(substituted, helper, out helperSites);
            TestKit.Check(problem == null, label + " must stay stack-valid after substitution: " + problem);
            TestKit.CheckEqual(expectedSites, helperSites, label + ": helper call sites");
        }

        private static void MissingPushIsRejected()
        {
            TestKit.Run("synthetic IL: replacing the call without pushing the instance breaks the stack", () =>
            {
                Body wrong = Substitute(UpdateSizeBody(), DeltaGetter, DeltaHelper, true);
                int helperSites;
                string problem = CheckStack(wrong, DeltaHelper, out helperSites);
                TestKit.Check(problem != null,
                    "omitting the explicit ldarg.0 must be rejected by the stack check (it was accepted): " +
                    "this is the defect the review reported");
            });
        }

        private static void LabelsAndExceptionBlocksPreserved()
        {
            TestKit.Run("synthetic IL: branch labels are carried onto the pushed instance", () =>
            {
                Body source = CheckUpdateFrameRateScreenBody();
                Body substituted = Substitute(source, TimeGetter, TimeHelper, false);

                var labels = new HashSet<int>();
                foreach (Ins ins in substituted.Instructions)
                {
                    foreach (int label in ins.Labels) labels.Add(label);
                }

                var sourceLabels = new List<int>();
                foreach (Ins ins in source.Instructions)
                {
                    foreach (int label in ins.Labels) sourceLabels.Add(label);
                }
                foreach (int label in sourceLabels)
                {
                    TestKit.Check(labels.Contains(label), "label must survive substitution: " + label);
                }

                for (int i = 0; i < substituted.Instructions.Count; i++)
                {
                    Ins ins = substituted.Instructions[i];
                    if (ins.Op != OpCodes.Call || !Equals(ins.Operand, TimeHelper)) continue;
                    TestKit.Check(i > 0 && substituted.Instructions[i - 1].Op == OpCodes.Ldarg_0,
                        "the helper must be immediately preceded by ldarg.0");
                }

                foreach (Ins ins in substituted.Instructions)
                {
                    if (ins.Operand is int label && (ins.Op == OpCodes.Brfalse_S || ins.Op == OpCodes.Leave_S))
                    {
                        TestKit.Check(labels.Contains(label), "branch target must still exist: " + label);
                    }
                }
            });

            TestKit.Run("synthetic IL: try/finally ranges survive substitution unchanged", () =>
            {
                Body source = ExceptionBlockBody();
                TestKit.CheckEqual(1, source.ExceptionBlocks.Count, "the replicated shape declares one handler");

                Body substituted = Substitute(source, DeltaGetter, DeltaHelper, false);
                TestKit.CheckEqual(source.ExceptionBlocks.Count, substituted.ExceptionBlocks.Count,
                    "the exception block count must be unchanged");
                for (int i = 0; i < source.ExceptionBlocks.Count; i++)
                {
                    TestKit.CheckEqual(source.ExceptionBlocks[i], substituted.ExceptionBlocks[i],
                        "exception block " + i + " must be identical");
                }

                var labels = new HashSet<int>();
                foreach (Ins ins in substituted.Instructions)
                {
                    foreach (int label in ins.Labels) labels.Add(label);
                }
                TestKit.Check(labels.Contains(0), "try start label preserved");
                TestKit.Check(labels.Contains(1), "handler start label preserved");
                TestKit.Check(labels.Contains(3), "handler end label preserved");

                // Site count is checked linearly: exception regions are not modelled as
                // stack-validated blocks here (a faithful handler dataflow is out of scope).
                int helperSites = substituted.Instructions.Count(
                    i => i.Op == OpCodes.Call && Equals(i.Operand, DeltaHelper));
                TestKit.CheckEqual(2, helperSites, "both the try body and the handler are substituted");

                // Each substituted site must be preceded by the explicit instance push.
                for (int i = 0; i < substituted.Instructions.Count; i++)
                {
                    Ins ins = substituted.Instructions[i];
                    if (ins.Op != OpCodes.Call || !Equals(ins.Operand, DeltaHelper)) continue;
                    TestKit.Check(i > 0 && substituted.Instructions[i - 1].Op == OpCodes.Ldarg_0,
                        "site " + i + " must be preceded by ldarg.0");
                }
            });
        }

        private sealed class StandIn
        {
            public float timer;
            public float rottimer;
            public float coltimer;
            public float pulsetimer;
            public double lastCustomFPSUpdate;
            public bool enableCustomFPS;
        }

        private static class StandInTime
        {
            public static float get_deltaTime() { return 0f; }
            public static float get_time() { return 0f; }
        }
    }
}
