using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ADOFAI.Renderist.Export;

namespace ADOFAI.Renderist.FfmpegTests
{
    internal static class FrameLocalDeliverySafetyTests
    {
        internal static void Run()
        {
            TestKit.Run("f1 safety: stdin owns inner and repeated Dispose closes it once", () =>
            {
                var inner = new DeferredIo();
                var gate = new FrameLocalDeliveryStreamGate(inner, new FrameLocalDeliveryEvidence());
                gate.Dispose(); gate.Open("late-release"); gate.Dispose();
                TestKit.CheckEqual(1, inner.Disposals, "inner owner exactly once");
                TestKit.Check(!gate.CanWrite && !inner.CanWrite && gate.OwnershipSettled, "both closed");
                TestKit.Check(!gate.GateEntered, "opening/disposing is not a write-entry witness");
            });
            TestKit.Run("f1 safety: Dispose wakes a managed hold without ever starting inner IO", () =>
            {
                var inner = new DeferredIo();
                var gate = new FrameLocalDeliveryStreamGate(inner, new FrameLocalDeliveryEvidence());
                Task write = gate.WriteAsync(new byte[6], 0, 6, CancellationToken.None);
                TestKit.Check(gate.GateEntered && !write.IsCompleted, "actual held write");
                gate.Dispose();
                ObserveTerminal(write);
                TestKit.Check(write.IsFaulted && inner.Writes == 0 && gate.OwnershipSettled, "no post-dispose inner call");
            });
            TestKit.Run("f1 safety: cancellation after inner starts cannot truncate its write or flush lifetime", () =>
            {
                var inner = new DeferredIo();
                var gate = new FrameLocalDeliveryStreamGate(inner, new FrameLocalDeliveryEvidence());
                using (var cancel = new CancellationTokenSource())
                {
                    gate.Open("release");
                    Task write = gate.WriteAsync(new byte[6], 0, 6, cancel.Token);
                    TestKit.CheckEqual(1, inner.Writes, "inner really entered");
                    cancel.Cancel(); gate.Dispose(); gate.Dispose();
                    TestKit.Check(!write.IsCompleted && !gate.OwnershipSettled, "Dispose/token do not synthesize terminal");
                    inner.WriteResult.TrySetResult(true);
                    TestKit.Check(inner.FlushEntered.Task.Wait(2000), "flush starts after write terminal");
                    TestKit.Check(!write.IsCompleted, "outer still holds bytes across inner flush");
                    inner.FlushResult.TrySetResult(true);
                    TestKit.Check(write.Wait(2000), "outer terminal after real inner terminal");
                    TestKit.Check(gate.OwnershipSettled && inner.Disposals == 1, "resource convergence");
                }
            });
            TestKit.Run("f1 safety: failed inner write is retained and does not invent a successful flush", () =>
            {
                var inner = new DeferredIo();
                var evidence = new FrameLocalDeliveryEvidence();
                using (var gate = new FrameLocalDeliveryStreamGate(inner, evidence))
                {
                    gate.Open("release");
                    Task write = gate.WriteAsync(new byte[6], 0, 6, CancellationToken.None);
                    inner.WriteResult.TrySetException(new IOException("native-write-failure"));
                    ObserveTerminal(write);
                    TestKit.Check(write.IsFaulted && inner.Flushes == 0, "failure is not success");
                    TestKit.Check(!evidence.Dump().Contains("inner-flush-end=ok"), "no false successful IO evidence");
                }
            });
        }

        private static void ObserveTerminal(Task task)
        {
            try { TestKit.Check(task.Wait(2000), "bounded terminal"); }
            catch (AggregateException) { var observed = task.Exception; }
            TestKit.Check(task.IsCompleted, "actual task terminal");
        }

        private sealed class DeferredIo : Stream
        {
            internal readonly TaskCompletionSource<bool> WriteResult = NewSignal();
            internal readonly TaskCompletionSource<bool> FlushResult = NewSignal();
            internal readonly TaskCompletionSource<bool> FlushEntered = NewSignal();
            internal int Writes, Flushes, Disposals;
            private static TaskCompletionSource<bool> NewSignal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) { Interlocked.Increment(ref Writes); return WriteResult.Task; }
            public override Task FlushAsync(CancellationToken token) { Interlocked.Increment(ref Flushes); FlushEntered.TrySetResult(true); return FlushResult.Task; }
            protected override void Dispose(bool disposing) { if (disposing) Interlocked.Increment(ref Disposals); base.Dispose(disposing); }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => Disposals == 0;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
