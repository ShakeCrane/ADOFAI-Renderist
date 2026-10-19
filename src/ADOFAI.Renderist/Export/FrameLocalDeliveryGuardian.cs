using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ADOFAI.Renderist.Export
{
    // F1 diagnostics only. Identity files are immutable, nonce-scoped evidence, never product settings.
    internal sealed class FrameLocalProcessIdentity
    {
        internal int Pid;
        internal long Start;
        internal string Path, Sha256;

        internal static string HashFile(string path)
        {
            using (var input = File.OpenRead(path))
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "");
        }

        internal static FrameLocalProcessIdentity Capture(Process process)
        {
            return new FrameLocalProcessIdentity
            {
                Pid = process.Id, Start = process.StartTime.ToUniversalTime().Ticks,
                Path = process.MainModule.FileName, Sha256 = HashFile(process.MainModule.FileName),
            };
        }

        internal bool Matches(Process process)
        {
            return Pid > 0 && Start > 0 && !string.IsNullOrEmpty(Path) && Sha256?.Length == 64 &&
                   process.Id == Pid && process.StartTime.ToUniversalTime().Ticks == Start &&
                   string.Equals(process.MainModule.FileName, Path, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(HashFile(Path), Sha256, StringComparison.OrdinalIgnoreCase);
        }

        internal static Dictionary<string, string> Read(string path)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) throw new InvalidDataException("f1-identity-line");
                values.Add(line.Substring(0, eq), line.Substring(eq + 1));
            }
            return values;
        }

        internal static FrameLocalProcessIdentity ReadBound(string path, string nonce, int gamePid, long gameStart)
        {
            var values = Read(path);
            if (values["runNonce"] != nonce || values["gamePid"] != gamePid.ToString(CultureInfo.InvariantCulture) ||
                values["gameStartUtcTicks"] != gameStart.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException("f1-identity-run-mismatch");
            return new FrameLocalProcessIdentity
            {
                Pid = int.Parse(values["pid"], CultureInfo.InvariantCulture),
                Start = long.Parse(values["startUtcTicks"], CultureInfo.InvariantCulture),
                Path = values["path"], Sha256 = values["sha256"],
            };
        }

        internal void WriteBound(string path, string nonce, int gamePid, long gameStart)
        {
            // No overwrite or partial read; files intentionally remain as diagnostic evidence.
            string staging = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllLines(staging, new[]
                {
                    "runNonce=" + nonce, "gamePid=" + gamePid.ToString(CultureInfo.InvariantCulture),
                    "gameStartUtcTicks=" + gameStart.ToString(CultureInfo.InvariantCulture),
                    "pid=" + Pid.ToString(CultureInfo.InvariantCulture), "startUtcTicks=" + Start.ToString(CultureInfo.InvariantCulture),
                    "path=" + Path, "sha256=" + Sha256,
                }, new UTF8Encoding(false));
                File.Move(staging, path);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }

        public override string ToString() => "pid=" + Pid + ",start=" + Start + ",path=" + Path + ",sha256=" + Sha256;
    }

    internal sealed class FrameLocalDeliveryGuardian : IDisposable
    {
        internal static readonly string[] Kinds =
        {
            "Ready", "Ack", "Live", "Release", "Cancel", "Emergency", "Shutdown",
            "ReleaseAck", "CancelAck", "EmergencyAck", "ShutdownAck", "EncoderReady", "EncoderAck",
            "WaitingArmed", "WriteGateEntered", "WaitExited", "Settled", "GuardianClosed",
        };
        private readonly Dictionary<string, EventWaitHandle> _events = new Dictionary<string, EventWaitHandle>();
        private readonly FrameLocalDeliveryEvidence _evidence;
        private readonly string _nonce, _controlPath;
        private readonly int _gamePid;
        private readonly long _gameStart;
        private Process _guardianWitness, _encoderWitness;
        private WaitHandle _guardianExit;
        private bool _closed;
        internal FrameLocalProcessIdentity EncoderIdentity { get; private set; }
        internal EventWaitHandle this[string kind] => _events[kind];
        internal bool Closed => _closed;
        internal WaitHandle GuardianExitHandle => _guardianExit;
        internal bool EncoderExited => _encoderWitness == null || _encoderWitness.HasExited;

        internal FrameLocalDeliveryGuardian(FrameLocalDeliveryProbeDescriptor descriptor, string descriptorPath,
            FrameLocalDeliveryEvidence evidence)
        {
            _nonce = descriptor.RunNonce; _gamePid = descriptor.GameProcessId; _gameStart = descriptor.GameStartUtcTicks;
            _controlPath = descriptorPath + "." + _nonce; _evidence = evidence;
            try
            {
                foreach (string kind in Kinds)
                {
                    var handle = new EventWaitHandle(false, EventResetMode.ManualReset,
                        "Local\\" + descriptor.EventPrefix + "." + _nonce + "." + kind, out bool created);
                    if (!created) { handle.Dispose(); throw new InvalidOperationException("f1-reused-event:" + kind); }
                    _events.Add(kind, handle);
                }
            }
            catch { Dispose(); throw; }
        }

        internal void Connect()
        {
            this["Ready"].Set();
            if (!this["Ack"].WaitOne(FrameLocalDeliveryProbe.SoftAbortMs)) throw new TimeoutException("f1-handshake-timeout");
            var guardian = FrameLocalProcessIdentity.ReadBound(_controlPath + ".guardian", _nonce, _gamePid, _gameStart);
            if (guardian.Pid == _gamePid) throw new InvalidDataException("f1-guardian-is-host");
            _guardianWitness = Process.GetProcessById(guardian.Pid);
            var held = _guardianWitness.Handle;
            if (!guardian.Matches(_guardianWitness)) throw new InvalidDataException("f1-guardian-identity");
            _guardianExit = new ProcessExitWaitHandle(_guardianWitness);
            _evidence.Record("guardian-identity", guardian.ToString());
            this["Live"].Set();
        }

        internal void RegisterEncoder(Process encoder, string expectedPath)
        {
            if (EncoderIdentity != null) throw new InvalidOperationException("f1-second-encoder-start");
            EncoderIdentity = FrameLocalProcessIdentity.Capture(encoder);
            if (EncoderIdentity.Pid == _gamePid ||
                !string.Equals(System.IO.Path.GetFullPath(expectedPath), EncoderIdentity.Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("f1-encoder-path-or-host");
            _encoderWitness = Process.GetProcessById(EncoderIdentity.Pid);
            var held = _encoderWitness.Handle;
            EncoderIdentity.WriteBound(_controlPath + ".encoder", _nonce, _gamePid, _gameStart);
            this["EncoderReady"].Set();
            if (!this["EncoderAck"].WaitOne(FrameLocalDeliveryProbe.SoftAbortMs))
                throw new TimeoutException("f1-encoder-ack-timeout");
            if (_guardianWitness.HasExited || !EncoderIdentity.Matches(_encoderWitness) || _encoderWitness.HasExited)
                throw new InvalidOperationException("f1-preplay-identity-or-exit");
            _evidence.Record("encoder-identity-ack", EncoderIdentity.ToString());
        }

        internal bool IsLive => !_closed && _guardianWitness != null && !_guardianWitness.HasExited;
        internal void Signal(string kind) { this[kind].Set(); _evidence.Record("channel", kind); }
        internal void Acknowledge(string kind)
        {
            string name = kind.Split('-')[0];
            name = char.ToUpperInvariant(name[0]) + name.Substring(1) + "Ack";
            if (_events.ContainsKey(name)) Signal(name);
        }

        // Called only once all runtime IO/observer/waiter/monitor/callback resources are settled.
        // The guardian acknowledges this final snapshot and exits before runtime closes its handles.
        internal bool TryClose(out string error)
        {
            error = null;
            if (_closed) return true;
            if (!EncoderExited) { error = "encoder-alive"; return false; }
            if (!this["Settled"].WaitOne(0)) Signal("Settled");
            if (!this["GuardianClosed"].WaitOne(0)) { error = "guardian-final-ack-pending"; return false; }
            if (_guardianWitness == null || !_guardianWitness.HasExited) { error = "guardian-exit-pending"; return false; }
            _evidence.Record("guardian-exited", "ack=true");
            Dispose();
            return true;
        }

        public void Dispose()
        {
            if (_closed) return;
            foreach (var handle in _events.Values) handle.Dispose();
            _events.Clear();
            _guardianExit?.Dispose();
            _guardianWitness?.Dispose(); _encoderWitness?.Dispose();
            _closed = true;
        }

        private sealed class ProcessExitWaitHandle : WaitHandle
        {
            internal ProcessExitWaitHandle(Process process) { SafeWaitHandle = new SafeWaitHandle(process.Handle, false); }
        }
    }
}
