using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ADOFAI.Renderist.Export;
using ADOFAI.Renderist.Ffmpeg;

namespace ADOFAI.Renderist.FfmpegTests
{
    /// <summary>
    /// L2 pipeline ↔ L3 交付事务的**受控 IO 背压**合同回归。
    ///
    /// 与 <see cref="Rgb24BackpressureTests"/> 的分工：那边用可控 transport 固定 L3-A 事务自身的
    /// 合同；这里把真实 <see cref="FfmpegVideoPipeline"/>（真实 stdin 写入路径）+
    /// <see cref="FfmpegRgb24FrameTransport"/> + <see cref="Rgb24FrameTransaction"/> 接起来，用测试内的
    /// <see cref="ControllableStdinStream"/> 制造"写入已接受但 Completion 仍 pending"与
    /// "取消已观察但底层写入尚未终止"，以固定跨层契约：
    ///   * L2 的取消只在**观察**层面发生，不等于写入终态，缓冲在此之前不得复用；
    ///   * frame Completion 与 pipeline <c>CleanupTask</c> 是两个**独立**屏障，任一未收敛即拒绝 restart；
    ///   * 双屏障都收敛后 residual 清除、lease exactly-once 释放、新 generation 可启动；
    ///   * 迟到旧 context 回调不得触碰新 context / 新 lease；
    ///   * success 路径的外层 Task 必须覆盖真实 inner 写入的整个寿命。
    /// </summary>
    internal static class PipelineBackpressureTests
    {
        internal static void Run(string root)
        {
            TestKit.Run("pipeline backpressure: cancellation is observed while the frame task and lease stay pending", () =>
            {
                using (var f = new Fixture(root, "cancel", ControllableStdinMode.CancelObservedDelayedTerminal))
                {
                    f.Begin(7);
                    Check(f.Stream.IsWritePending, "WriteAsync returned pending");
                    Check(f.Context.Pool.HasOutstandingLease, "lease held");
                    Check(!f.Stream.CancellationObserved.IsCompleted, "not cancelled yet");
                    Check(!f.Context.TryStopAndDrain("user-stop", out _), "stop begins convergence");
                    f.Stream.CancellationObserved.GetAwaiter().GetResult();
                    Check(!f.Pipeline.Cancel("duplicate-stop"), "L2 cancel accepted once");
                    Check(f.Stream.IsWritePending, "cancel observed is not terminal");
                    Check(!f.Stream.HasForwardedInner, "cancelled bytes never forwarded");
                    Check(!f.Stream.ReleaseSuccess(), "wrong mode release rejected");
                    Check(!f.Context.TryStopAndDrain("user-stop", out _), "frame barrier remains");
                    Check(f.Context.Pool.OutstandingLease.DeliveryPinned, "buffer still pinned");
                    f.Pipeline.CleanupTask.GetAwaiter().GetResult();
                    Check(f.Stream.IsWritePending, "cleanup first, frame second");
                    Check(f.Stream.IsDisposed, "L2 disposed stdin before frame terminal");
                    Check(f.Context.HasResidualOwnership, "residual after cleanup");
                    var next = f.FreshContext();
                    var slot = f.Context;
                    Check(!Rgb24DeliveryContext.TryReplace(ref slot, next, false, out _), "restart rejected");
                    Check(ReferenceEquals(slot, f.Context), "old context retained");
                    Check(f.Stream.ReleaseTerminal(), "terminal release accepted once");
                    Check(!f.Stream.ReleaseTerminal(), "duplicate terminal rejected");
                    f.Stream.WriteTerminal.GetResultIfCanceled();
                    f.Pump.WaitForPost(); // deliberately keep the old callback queued
                    Check(f.Context.TryStopAndDrain("user-stop", out _), "both barriers converged");
                    Check(!f.Context.HasResidualOwnership, "residual clear");
                    Check(f.Context.Pool.ReleaseCount == 1, "lease released once");
                    Check(Rgb24DeliveryContext.TryReplace(ref slot, next, false, out _), "restart accepted");
                    Check(next.Transaction.TryBeginFrame(9, 0, out _) == Rgb24TransactionOutcome.Accepted,
                        "new generation begins");
                    var newFrame = next.Transaction.CurrentFrame;
                    f.Pump.Pump(); // late old context callback cannot mutate the new context
                    Check(ReferenceEquals(slot, next), "new context preserved");
                    Check(ReferenceEquals(next.Transaction.CurrentFrame, newFrame), "new lease preserved");
                    next.Transaction.TryAbort(out _);
                }
            });

            TestKit.Run("pipeline backpressure: a frame terminal before cleanup still holds the cleanup barrier", () =>
            {
                using (var f = new Fixture(root, "reverse", ControllableStdinMode.CancelObservedDelayedTerminal, true))
                {
                    Check(f.Stdout.Entered.Wait(30000), "stdout pump entered deferred EOF");
                    f.Begin(8);
                    f.Pipeline.Cancel("user-stop");
                    f.Stream.CancellationObserved.GetAwaiter().GetResult();
                    Check(f.Stream.ReleaseTerminal(), "release");
                    var next = f.FreshContext();
                    var slot = f.Context;
                    Check(!Rgb24DeliveryContext.TryReplace(ref slot, next, false, out _), "restart rejected before drain");
                    f.Stream.WriteTerminal.GetResultIfCanceled();
                    f.Pump.WaitAndPump();
                    // 即使帧侧已经收敛，L3 仍必须独立等待 CleanupTask。
                    Check(!f.Pipeline.CleanupTask.IsCompleted, "cleanup deliberately pending");
                    Check(!f.Context.TryStopAndDrain("user-stop", out _), "cleanup barrier remains");
                    Check(!Rgb24DeliveryContext.TryReplace(ref slot, next, false, out _), "restart still rejected");
                    f.Stdout.ReleaseEof();
                    f.Pipeline.CleanupTask.GetAwaiter().GetResult();
                    Check(f.Context.TryStopAndDrain("user-stop", out _), "converged");
                    Check(Rgb24DeliveryContext.TryReplace(ref slot, next, false, out _), "restart");
                }
            });

            TestKit.Run("pipeline backpressure: success release spans the complete inner write", () =>
            {
                var inner = new PendingInnerStream();
                var stream = new ControllableStdinStream(inner, ControllableStdinMode.DelayedSuccess);
                var bytes = new byte[18];
                Task write = stream.WriteAsync(bytes, 0, bytes.Length, CancellationToken.None);
                Check(!write.IsCompleted, "async pending return");
                Check(stream.ReleaseSuccess(), "release forwarded");
                Check(stream.HasForwardedInner && inner.Entered.Wait(30000), "inner owns buffer");
                Check(!write.IsCompleted, "outer task spans inner lifetime");
                Check(!stream.ReleaseSuccess(), "duplicate release");
                inner.Complete();
                write.GetAwaiter().GetResult();
                Check(!stream.IsWritePending, "terminal after inner");
                stream.Dispose();
            });

            TestKit.Run("pipeline backpressure: fault and Dispose share one terminal authority", () =>
            {
                var stream = new ControllableStdinStream(new MemoryStream(), ControllableStdinMode.CancelObservedDelayedTerminal);
                Task write = stream.WriteAsync(new byte[1], 0, 1, CancellationToken.None);
                Check(stream.Fault(new IOException("injected")), "fault accepted");
                Check(!stream.Fault(new IOException("duplicate")), "duplicate fault rejected");
                Check(write.IsFaulted, "fault terminal");
                stream.Dispose();
                stream.Dispose();
                Check(!stream.ReleaseTerminal(), "release after fault rejected");

                var disposed = new ControllableStdinStream(new MemoryStream(), ControllableStdinMode.DelayedSuccess);
                Task pending = disposed.WriteAsync(new byte[1], 0, 1, CancellationToken.None);
                disposed.Dispose();
                Check(pending.IsFaulted, "dispose without cancellation terminates");
                Check(!disposed.ReleaseSuccess(), "release after dispose rejected");
            });
        }

        private static void Check(bool condition, string message) => TestKit.Check(condition, message);

        /// <summary>真实 L2 pipeline + 真实 L3 context 的组合夹具（进程为既有 fake 进程）。</summary>
        private sealed class Fixture : IDisposable
        {
            internal readonly FfmpegVideoPipeline Pipeline;
            internal readonly Rgb24DeliveryContext Context;
            internal readonly MainThreadPump Pump = new MainThreadPump();
            internal ControllableStdinStream Stream;
            internal DeferredEofStream Stdout;
            private readonly string _directory;

            internal Fixture(string root, string name, ControllableStdinMode mode, bool deferStdout = false)
            {
                _directory = Path.Combine(root, "pipeline-backpressure-" + name);
                Directory.CreateDirectory(_directory);
                var options = new FfmpegVideoPipelineOptions
                {
                    Settings = new FfmpegVideoSettings { Width = 64, Height = 48, Fps = 30 },
                    Identity = FfmpegVideoPipelineTests.MakeIdentity(FakeVideoProcess.ExecutablePath),
                    FinalPath = Path.Combine(_directory, "out.mp4"),
                    ProcessStart = new FakeVideoProcessLauncher().Create(),
                    StreamWrapper = (inner, role) => role == "stdin"
                        ? (Stream = new ControllableStdinStream(inner, mode))
                        : role == "stdout" && deferStdout ? (Stdout = new DeferredEofStream(inner)) : inner,
                };
                Pipeline = new FfmpegVideoPipeline(options);
                Check(Pipeline.Start().Started, "L2 start");
                var old = SynchronizationContext.Current;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(Pump);
                    Check(Rgb24DeliveryContext.TryCreate(64, 48, Pipeline, out Context, out _), "L3 context");
                }
                finally { SynchronizationContext.SetSynchronizationContext(old); }
            }

            internal void Begin(long generation)
            {
                Check(Context.Transaction.TryBeginFrame(generation, 0, out _) == Rgb24TransactionOutcome.Accepted,
                    "begin frame");
                Check(Context.Transaction.TryCompleteEof(generation, 0, out _, out _) == Rgb24TransactionOutcome.Accepted,
                    "complete EOF");
                Check(Context.Transaction.TryBeginDelivery(out _, out _) == Rgb24TransactionOutcome.Accepted,
                    "L2 accepted write");
                Check(Stream.Entered.Wait(30000), "L2 reached stdin WriteAsync");
            }

            internal Rgb24DeliveryContext FreshContext()
            {
                var old = SynchronizationContext.Current;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(Pump);
                    Check(Rgb24DeliveryContext.TryCreate(64, 48, Pipeline, out var next, out _), "fresh context");
                    return next;
                }
                finally { SynchronizationContext.SetSynchronizationContext(old); }
            }

            public void Dispose()
            {
                Pipeline.Cancel("test-end");
                Stdout?.ReleaseEof();
                if (Stream != null && Stream.IsWritePending)
                {
                    if (Stream.CancellationObserved.IsCompleted) Stream.ReleaseTerminal();
                    else Stream.Fault(new IOException("test-end"));
                }
                Pipeline.CleanupTask?.Wait(30000);
                TestKit.TryDeleteDirectory(_directory);
            }
        }

        /// <summary>只在测试显式 Pump 时才执行回调的主线程上下文。</summary>
        private sealed class MainThreadPump : SynchronizationContext
        {
            private readonly Queue<Action> _callbacks = new Queue<Action>();
            private readonly object _sync = new object();
            private readonly ManualResetEventSlim _posted = new ManualResetEventSlim(false);

            public override void Post(SendOrPostCallback callback, object state)
            {
                lock (_sync) _callbacks.Enqueue(() => callback(state));
                _posted.Set();
            }

            internal void WaitAndPump()
            {
                WaitForPost();
                Pump();
            }

            internal void WaitForPost() => Check(_posted.Wait(30000), "main-thread completion posted");

            internal void Pump()
            {
                while (true)
                {
                    Action action;
                    lock (_sync)
                    {
                        if (_callbacks.Count == 0) { _posted.Reset(); return; }
                        action = _callbacks.Dequeue();
                    }
                    action();
                }
            }
        }

        /// <summary>写入保持 pending 的 inner，用于证明外层 Task 覆盖 inner 的完整寿命。</summary>
        private sealed class PendingInnerStream : MemoryStream
        {
            private readonly TaskCompletionSource<bool> _terminal =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly ManualResetEventSlim Entered = new ManualResetEventSlim(false);

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                Entered.Set();
                return _terminal.Task;
            }

            internal void Complete() => _terminal.TrySetResult(true);
        }

        /// <summary>把 stdout EOF 推迟到测试显式释放，用来单独制造 CleanupTask 尚未收敛。</summary>
        private sealed class DeferredEofStream : Stream
        {
            private readonly Stream _inner;
            private readonly TaskCompletionSource<int> _eof =
                new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly ManualResetEventSlim Entered = new ManualResetEventSlim(false);

            internal DeferredEofStream(Stream inner) { _inner = inner; }
            internal void ReleaseEof() => _eof.TrySetResult(0);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            { Entered.Set(); return _eof.Task; }

            public override int Read(byte[] buffer, int offset, int count)
            { Entered.Set(); return _eof.Task.GetAwaiter().GetResult(); }

            public override bool CanRead => true;
            public override bool CanWrite => false;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
        }
    }

    internal static class BackpressureTaskExtensions
    {
        /// <summary>等待已终结为 cancelled 的写入，不把预期的取消当失败。</summary>
        internal static void GetResultIfCanceled(this Task task)
        {
            try { task.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
    }
}
