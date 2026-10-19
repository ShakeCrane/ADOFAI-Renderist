using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using ADOFAI.Renderist.Export;
using ADOFAI.Renderist.Ffmpeg;

namespace ADOFAI.Renderist.FfmpegTests
{
    // Independent net48 host for the actual production protocol, IO, EOF control and settlement.
    // Fixed managed native-frame witness is explicitly NOT a Unity/Mono PlayerLoop test.
    internal sealed class FrameLocalGuardianHost : SynchronizationContext
    {
        private readonly Queue<Action> _posts = new Queue<Action>();
        public override void Post(SendOrPostCallback callback, object state)
        { lock (_posts) _posts.Enqueue(() => callback(state)); }
        private void Drain()
        {
            while (true)
            {
                Action action;
                lock (_posts) { if (_posts.Count == 0) return; action = _posts.Dequeue(); }
                action();
            }
        }

        internal static int Run(string descriptorPath, string scenario)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            var host = new FrameLocalGuardianHost();
            string root = Path.GetDirectoryName(descriptorPath);
            var clock = Stopwatch.StartNew();
            try
            {
                while (!File.Exists(descriptorPath) && clock.ElapsedMilliseconds < 10000) Thread.Sleep(10);
                Environment.SetEnvironmentVariable(FrameLocalDeliveryProbe.DescriptorPathVariable, descriptorPath);
                SetSynchronizationContext(host);
                Require(FrameLocalDeliveryProbe.TryActivate(out var error), "activate: " + error);
                var launcher = new FakeVideoProcessLauncher();
                const int width = 512, height = 512;
                var pipeline = new FfmpegVideoPipeline(new FfmpegVideoPipelineOptions
                {
                    Settings = new FfmpegVideoSettings { Width = width, Height = height, Fps = 30 },
                    Identity = FfmpegVideoPipelineTests.MakeIdentity(FakeVideoProcess.ExecutablePath),
                    FinalPath = Path.Combine(root, scenario + "-not-published.mp4"),
                    ProcessStart = FrameLocalDeliveryProbe.WrapProcessStart(launcher.Create()),
                    StreamWrapper = (stream, role) => role == "stdin" ? FrameLocalDeliveryProbe.WrapStandardInput(stream) : stream,
                    FaultInjection = point => { if (scenario == "cleanup-residual" && point == "teardown") throw new IOException("controlled-cleanup-residual"); },
                });
                FrameLocalDeliveryProbe.AttachPipeline(pipeline);
                Require(Rgb24DeliveryContext.TryCreate(width, height, pipeline, out var context, out error), "context: " + error);
                FrameLocalDeliveryProbe.AttachContext(context);
                Require(pipeline.Start().Started, "pipeline start");
                Require(FrameLocalDeliveryProbe.PrePlayHealthy(out error), "preplay: " + error);

                var slot = FrameLocalDeliveryProbe.Slot;
                var flags = BindingFlags.NonPublic | BindingFlags.Static;
                var monitorField = typeof(FrameLocalDeliveryProbe).GetField("_monitor", flags);
                var channel = (FrameLocalDeliveryGuardian)typeof(FrameLocalDeliveryProbe).GetField("_guardian", flags).GetValue(null);
                // Controlled fault: remove the runtime monitor entirely in the emergency/residual cases.
                // External-soft retains the SAME production monitor with local budgets disabled,
                // so only the independent guardian can originate that 5s request.
                if (scenario == "external-soft" || scenario == "emergency" || scenario == "residual" || scenario == "guardian-exit")
                {
                    var monitor = (FrameLocalDeliveryProbeMonitor)monitorField.GetValue(null);
                    monitor.RequestStop();
                    Require(SpinWait.SpinUntil(monitor.TryJoin, 3000), "original monitor joined");
                    if (scenario == "external-soft")
                    {
                        monitor = new FrameLocalDeliveryProbeMonitor(slot, slot.Evidence,
                            channel["Release"], channel["Cancel"], channel["Shutdown"], channel["Emergency"], null,
                            reason => FrameLocalDeliveryProbe.StreamGate?.Open(reason), 0, 0, channel.Acknowledge);
                        monitorField.SetValue(null, monitor);
                        monitor.Start();
                    }
                    slot.Evidence.Record("fault-injection", scenario == "external-soft" ? "local-budgets-disabled" : "monitor-exited");
                }

                var transaction = context.Transaction;
                Require(transaction.TryBeginFrame(1, 0, out error) == Rgb24TransactionOutcome.Accepted, "begin: " + error);
                byte[] source = transaction.CurrentFrame.SegmentBuffer(0);
                for (int i = 0; i < source.Length; i++) source[i] = (byte)(i * 7);
                Require(transaction.TryCompleteEof(1, 0, out var frame, out error) == Rgb24TransactionOutcome.Accepted, "eof: " + error);
                slot.PrepareFrameEvidence(frame, context.Bridge);
                Require(transaction.TryBeginDelivery(out error, out _) == Rgb24TransactionOutcome.Accepted, "delivery: " + error);
                int commits = 0;
                string stopCode = null;
                slot.RunSingleFrame(transaction, 1, 0, true,
                    scenario == "residual" ? null : channel["Emergency"], scenario == "residual" ? 32000 : 30000,
                    () => { commits++; slot.RecordActualCommit(1, 0, 123); },
                    (code, detail) => { stopCode = code; slot.Evidence.Record("stop-requested", code + ":" + detail); },
                    () => 123, FrameLocalDeliveryProbe.SignalWaitingArmed, FrameLocalDeliveryProbe.SignalWaitExited,
                    scenario == "residual" ? null : channel.GuardianExitHandle);
                Require(commits == (scenario == "success" || scenario == "cleanup-residual" ? 1 : 0), "unique actual commit: " + commits);
                Require(stopCode != null, "stop before EOF returns");
                // Let the external hard-kill actually reap the child before normal L2 cancellation.
                if (scenario == "emergency") Require(SpinWait.SpinUntil(() => channel.EncoderExited, 5000), "external child kill");
                host.Drain(); // canary/retirement callbacks are allowed ONLY after EOF has returned.
                FrameLocalDeliveryProbe.Shutdown();
                var settlement = Stopwatch.StartNew();
                int settlementBudget = scenario == "cleanup-residual" ? 35000 : 5000;
                while (FrameLocalDeliveryProbe.HasResidualOwnership && settlement.ElapsedMilliseconds < settlementBudget)
                {
                    host.Drain();
                    FrameLocalDeliveryProbe.TickSettlement();
                    Thread.Sleep(10);
                }
                Console.WriteLine("MANAGED HOST scenario=" + scenario + " commits=" + commits +
                    " delivered=" + pipeline.DeliveredFrameCount + " residual=" + FrameLocalDeliveryProbe.HasResidualOwnership);
                Console.WriteLine(FrameLocalDeliveryProbe.DumpEvidence());
                Require(launcher.EncoderStarts == 1 && launcher.VerifierStarts == 0, "one encoder and no Finalizing");
                Require(!File.Exists(Path.Combine(root, scenario + "-not-published.mp4")), "no published file");
                if (scenario == "residual" || scenario == "cleanup-residual" || scenario == "guardian-exit")
                {
                    Require(FrameLocalDeliveryProbe.HasResidualOwnership, "no false clean without guardian ACK/exit");
                    return 2;
                }
                Require(!FrameLocalDeliveryProbe.HasResidualOwnership, "all diagnostic resources settled");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Console.WriteLine(FrameLocalDeliveryProbe.DumpEvidence());
                FrameLocalDeliveryProbe.Shutdown();
                return 1;
            }
        }
        private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
    }
}
