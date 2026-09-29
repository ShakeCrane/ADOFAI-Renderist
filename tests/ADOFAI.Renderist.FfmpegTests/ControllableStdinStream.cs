using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ADOFAI.Renderist.FfmpegTests
{
    /// <summary>可控 stdin 的行为模式。</summary>
    internal enum ControllableStdinMode
    {
        /// <summary>首个 WriteAsync 返回 pending；release 后由后台真实写入，外层 Task 覆盖完整寿命。</summary>
        DelayedSuccess = 0,

        /// <summary>首个 WriteAsync 返回 pending；取消只被"观察"，直到显式 terminal 才结束该 Task。</summary>
        CancelObservedDelayedTerminal = 1,
    }

    /// <summary>
    /// **测试专用**的可控 stdin 流：让"写入已接受但 Completion 仍 pending"、"取消已观察但底层操作
    /// 尚未终止"这两种真实 IO 形态在 net48 harness 中可确定性复现，不需要真实 FFmpeg 或 Unity。
    ///
    /// 契约（由 PipelineBackpressureTests 固定）：
    ///   * 只有**第一个** <see cref="WriteAsync(byte[], int, int, CancellationToken)"/> 被拦截并立即返回
    ///     一个 pending Task；后续写入直接转发给 inner。
    ///   * 取消回调只记录"已观察"，**不**结束写入 —— 取消 observation 与写入 terminal 是两个独立事实。
    ///   * 单一 terminal authority：<see cref="ReleaseSuccess"/> / <see cref="ReleaseTerminal"/> /
    ///     <see cref="Fault"/> / <see cref="Dispose(bool)"/> 都只允许产生一次终态，且重复调用被拒绝。
    ///   * CancelObservedDelayedTerminal 模式下，即使 L2 cleanup 已经 Dispose 了 stdin，已观察的取消
    ///     仍然保持 pending，直到显式 terminal。
    /// </summary>
    internal sealed class ControllableStdinStream : Stream
    {
        private readonly Stream _inner;
        private readonly ControllableStdinMode _mode;
        private readonly object _sync = new object();
        private readonly TaskCompletionSource<bool> _entered = NewSignal();
        private readonly TaskCompletionSource<bool> _cancelObserved = NewSignal();
        private TaskCompletionSource<bool> _write;
        private CancellationTokenRegistration _registration;
        private CancellationToken _token;
        private byte[] _buffer;
        private int _offset;
        private int _count;
        private bool _forwarding;
        private bool _terminal;
        private bool _disposed;
        private Exception _deferredFault;

        internal ControllableStdinStream(Stream inner, ControllableStdinMode mode)
        {
            _inner = inner ?? throw new ArgumentNullException("inner");
            _mode = mode;
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>首个 WriteAsync 已经进入并返回 pending。</summary>
        internal Task Entered { get { return _entered.Task; } }

        /// <summary>取消 token 已被观察（不代表写入已终止）。</summary>
        internal Task CancellationObserved { get { return _cancelObserved.Task; } }

        /// <summary>被拦截写入的终态 Task（尚未开始时为 null）。</summary>
        internal Task WriteTerminal { get { lock (_sync) return _write?.Task; } }

        internal bool IsWritePending { get { lock (_sync) return _write != null && !_write.Task.IsCompleted; } }
        internal bool HasForwardedInner { get { lock (_sync) return _forwarding; } }
        internal bool IsDisposed { get { lock (_sync) return _disposed; } }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException("offset");

            Task task;
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ControllableStdinStream));
                if (_write != null) return _inner.WriteAsync(buffer, offset, count, cancellationToken);
                _write = NewSignal();
                _token = cancellationToken;
                _buffer = buffer;
                _offset = offset;
                _count = count;
                task = _write.Task;
                _registration = cancellationToken.Register(() => _cancelObserved.TrySetResult(true));
                _entered.TrySetResult(true);
            }
            return task;
        }

        /// <summary>CancelObservedDelayedTerminal：只有已观察取消之后才允许把写入终结为 cancelled。</summary>
        internal bool ReleaseTerminal()
        {
            lock (_sync)
            {
                if (_mode != ControllableStdinMode.CancelObservedDelayedTerminal || _write == null ||
                    !_cancelObserved.Task.IsCompleted || _terminal || _forwarding) return false;
                _terminal = true;
                _write.TrySetCanceled();
            }
            _registration.Dispose();
            return true;
        }

        /// <summary>DelayedSuccess：把缓冲转发给 inner，外层 Task 覆盖 inner 写入的整个寿命。</summary>
        internal bool ReleaseSuccess()
        {
            byte[] buffer;
            int offset, count;
            lock (_sync)
            {
                if (_mode != ControllableStdinMode.DelayedSuccess || _write == null || _terminal || _forwarding)
                    return false;
                if (_cancelObserved.Task.IsCompleted || _disposed)
                {
                    _terminal = true;
                    _write.TrySetCanceled();
                    // Complete below, outside the lock.
                }
                else _forwarding = true;
                buffer = _buffer;
                offset = _offset;
                count = _count;
            }
            if (!_forwarding)
            {
                _registration.Dispose();
                return true;
            }
            // FileStream.WriteAsync 在目标 Mono 上可能在返回 Task 之前就做实际工作，因此绝不在测试
            // 主线程上发起 inner 写入。
            Task.Run(() => ForwardInner(buffer, offset, count));
            return true;
        }

        private async Task ForwardInner(byte[] buffer, int offset, int count)
        {
            Exception failure = null;
            try
            {
                _token.ThrowIfCancellationRequested();
                await _inner.WriteAsync(buffer, offset, count, _token).ConfigureAwait(false);
            }
            catch (Exception ex) { failure = ex; }

            lock (_sync)
            {
                if (_terminal) return;
                _terminal = true;
                if (_deferredFault != null) _write.TrySetException(_deferredFault);
                else if (failure is OperationCanceledException) _write.TrySetCanceled();
                else if (failure != null) _write.TrySetException(failure);
                else _write.TrySetResult(true);
            }
            _registration.Dispose();
        }

        /// <summary>注入写入故障；转发中则延迟到 inner 返回之后再终结（inner 仍持有调用方缓冲）。</summary>
        internal bool Fault(Exception error)
        {
            if (error == null) throw new ArgumentNullException("error");
            bool terminalNow;
            lock (_sync)
            {
                if (_write == null || _terminal || _deferredFault != null) return false;
                if (_forwarding) _deferredFault = error;
                else { _terminal = true; _write.TrySetException(error); }
                terminalNow = _terminal;
            }
            if (terminalNow) _registration.Dispose();
            return true;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                bool terminalNow;
                lock (_sync)
                {
                    if (_disposed) return;
                    _disposed = true;
                    // 取消已观察时故意保持 pending：直到显式 terminal，即使 L2 cleanup 已经 Dispose。
                    if (_write != null && !_terminal && !_forwarding &&
                        !(_mode == ControllableStdinMode.CancelObservedDelayedTerminal && _cancelObserved.Task.IsCompleted))
                    {
                        _terminal = true;
                        _write.TrySetException(new ObjectDisposedException(nameof(ControllableStdinStream)));
                    }
                    terminalNow = _terminal;
                }
                if (terminalNow) _registration.Dispose();
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
