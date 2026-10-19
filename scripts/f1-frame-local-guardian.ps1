<#
.SYNOPSIS
    F1 diagnostic guardian. Persistent Arm runs outside the runtime and monitor.
    WaitingArmed starts 5s Cancel / 10s direct Emergency + verified child kill / 30s residual.
    WaitExited disarms wait budgets and starts a separate 30s settlement budget.
#>
[CmdletBinding()]
param(
    [ValidateSet('Arm','Release','Cancel','Emergency','Shutdown','Status','Terminate','WriteDescriptor','Rehearsal')]
    [string]$Action='Status',
    [int]$GamePid=0, [string]$Nonce='', [string]$EventPrefix='Renderist.F1',
    [string]$DescriptorPath='', [int]$ExpirySeconds=600, [int]$TimeoutSeconds=30,
    [int]$EncoderPid=0, [long]$EncoderStartUtcTicks=0,
    [string]$ExpectedEncoderPath='', [string]$ExpectedEncoderSha256='',
    [string]$LogPath='', [string]$HarnessPath=''
)
$ErrorActionPreference='Stop'
$kinds=@('Ready','Ack','Live','Release','Cancel','Emergency','Shutdown','ReleaseAck','CancelAck',
    'EmergencyAck','ShutdownAck','EncoderReady','EncoderAck','WaitingArmed','WriteGateEntered',
    'WaitExited','Settled','GuardianClosed')
function Write-GLog([string]$message) {
    $line='[{0}] nonce={1} guardianPid={2} {3}' -f [DateTime]::UtcNow.ToString('o'),$Nonce,$PID,$message
    Write-Host $line
    if($LogPath) { Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8 }
}
function Fail([string]$message) { throw $message }
function Open-Event([string]$kind) { [Threading.EventWaitHandle]::OpenExisting("Local\$EventPrefix.$Nonce.$kind") }
function Read-Kv([string]$path) {
    $v=@{}
    foreach($line in [IO.File]::ReadAllLines($path)) {
        if(-not $line.Trim() -or $line.StartsWith('#')) { continue }
        $eq=$line.IndexOf('=')
        if($eq -le 0) { Fail 'invalid identity line' }
        $key=$line.Substring(0,$eq)
        if($v.ContainsKey($key)) { Fail 'duplicate identity key' }
        $v[$key]=$line.Substring($eq+1)
    }
    return $v
}
function Write-Immutable([string]$path,[string[]]$lines) {
    $staging=$path+'.tmp-'+[Guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllLines($staging,$lines,[Text.UTF8Encoding]::new($false))
        [IO.File]::Move($staging,$path)
    } finally { if([IO.File]::Exists($staging)) { [IO.File]::Delete($staging) } }
}
function Get-Identity([int]$targetPid) {
    $process=[Diagnostics.Process]::GetProcessById($targetPid)
    try {
        return @{pid=$process.Id; startUtcTicks=$process.StartTime.ToUniversalTime().Ticks;
            path=$process.MainModule.FileName; sha256=(Get-FileHash -LiteralPath $process.MainModule.FileName -Algorithm SHA256).Hash}
    } finally { $process.Dispose() }
}
function Assert-Bound($record,$descriptor) {
    if($record.runNonce -ne $Nonce -or [int]$record.gamePid -ne $GamePid -or
        [long]$record.gameStartUtcTicks -ne [long]$descriptor.gameStartUtcTicks) { Fail 'identity run mismatch' }
}
function Assert-Encoder($process,$record) {
    if([int]$record.pid -le 0 -or [int]$record.pid -eq $GamePid) { Fail 'refusing to terminate the game process' }
    if([long]$record.startUtcTicks -le 0 -or $process.StartTime.ToUniversalTime().Ticks -ne [long]$record.startUtcTicks) { Fail 'encoder start time mismatch' }
    if(-not $record.path -or -not [string]::Equals($process.MainModule.FileName,$record.path,[StringComparison]::OrdinalIgnoreCase)) { Fail 'encoder path mismatch' }
    if(-not $record.sha256 -or $record.sha256.Length -ne 64 -or
        (Get-FileHash -LiteralPath $process.MainModule.FileName -Algorithm SHA256).Hash -ne $record.sha256) { Fail 'encoder hash mismatch' }
}
function Stop-VerifiedEncoder($process,$record) {
    if([int]$record.pid -eq $GamePid) { Fail 'refusing to terminate the game process' }
    $null=$process.Handle
    if($process.HasExited) { Write-GLog 'exact encoder already exited'; return }
    Assert-Encoder $process $record
    Write-GLog ('encoder identity reverified pid='+$record.pid+' start='+$record.startUtcTicks+' path='+$record.path+' sha256='+$record.sha256)
    $process.Kill()
    if(-not $process.WaitForExit(5000)) { Fail 'encoder exit timeout' }
    Write-GLog ('encoder terminated pid='+$record.pid)
}
function Invoke-WriteDescriptor {
    if($Nonce -notmatch '^[a-zA-Z0-9-]+$') { Fail 'unique Nonce required (letters/digits/hyphens)' }
    if(-not $DescriptorPath) { Fail 'DescriptorPath is required' }
    if($GamePid -le 0 -or $ExpirySeconds -le 0) { Fail 'positive GamePid/ExpirySeconds required' }
    $identity=Get-Identity $GamePid
    Write-Immutable $DescriptorPath @('# F1 single-use descriptor',('runNonce='+$Nonce),('gamePid='+$GamePid),
        ('gameStartUtcTicks='+$identity.startUtcTicks),('eventPrefix='+$EventPrefix),('expirySeconds='+$ExpirySeconds),
        ('createdUtcTicks='+[DateTime]::UtcNow.Ticks))
    Write-GLog ('descriptor written '+$DescriptorPath+' gameStart='+$identity.startUtcTicks)
}
function Invoke-Arm {
    if(-not $DescriptorPath -or -not $Nonce -or $GamePid -le 0) { Fail 'Arm requires DescriptorPath/Nonce/GamePid' }
    $descriptorFile=$DescriptorPath
    if(-not [IO.File]::Exists($descriptorFile)) { $descriptorFile=$DescriptorPath+'.claimed.'+$Nonce }
    $descriptor=Read-Kv $descriptorFile
    Assert-Bound $descriptor $descriptor
    if($descriptor.eventPrefix -ne $EventPrefix -or [long]$descriptor.createdUtcTicks -le 0 -or
        [int]$descriptor.expirySeconds -le 0 -or [DateTime]::UtcNow.Ticks -lt [long]$descriptor.createdUtcTicks -or
        [DateTime]::UtcNow.Ticks-[long]$descriptor.createdUtcTicks -ge [long]$descriptor.expirySeconds*[TimeSpan]::TicksPerSecond) { Fail 'descriptor expired or mismatched' }
    $game=Get-Identity $GamePid
    if([long]$game.startUtcTicks -ne [long]$descriptor.gameStartUtcTicks) { Fail 'game start identity mismatch' }
    $handles=@{}; $encoder=$null
    try {
        $setup=[Diagnostics.Stopwatch]::StartNew()
        while(-not $handles.ContainsKey('Ready') -and $setup.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
            try { $handles.Ready=Open-Event 'Ready' } catch [Threading.WaitHandleCannotBeOpenedException] { Start-Sleep -Milliseconds 25 }
        }
        if(-not $handles.ContainsKey('Ready') -or -not $handles.Ready.WaitOne($TimeoutSeconds*1000)) { Fail 'Ready timeout' }
        foreach($kind in $kinds) { if($kind -ne 'Ready') { $handles[$kind]=Open-Event $kind } }
        $self=Get-Identity $PID
        Write-Immutable ($DescriptorPath+'.'+$Nonce+'.guardian') @(
            ('runNonce='+$Nonce),('gamePid='+$GamePid),('gameStartUtcTicks='+$game.startUtcTicks),
            ('pid='+$self.pid),('startUtcTicks='+$self.startUtcTicks),('path='+$self.path),('sha256='+$self.sha256))
        $handles.Ack.Set() | Out-Null
        if(-not $handles.Live.WaitOne($TimeoutSeconds*1000)) { Fail 'runtime Live ACK timeout' }
        Write-GLog ('Ready/Ack/Live PASS gamePid='+$GamePid+' start='+$game.startUtcTicks)
        $index=[Threading.WaitHandle]::WaitAny([Threading.WaitHandle[]]@($handles.EncoderReady,$handles.Settled),$TimeoutSeconds*1000)
        if($index -eq [Threading.WaitHandle]::WaitTimeout) { $handles.Emergency.Set() | Out-Null; Fail 'encoder identity/settlement timeout' }
        if($index -eq 0) {
            $encoderRecord=Read-Kv ($DescriptorPath+'.'+$Nonce+'.encoder')
            Assert-Bound $encoderRecord $descriptor
            $encoder=[Diagnostics.Process]::GetProcessById([int]$encoderRecord.pid)
            $null=$encoder.Handle
            Assert-Encoder $encoder $encoderRecord
            Write-GLog ('encoder identity PASS pid='+$encoderRecord.pid+' start='+$encoderRecord.startUtcTicks+' sha256='+$encoderRecord.sha256)
            $handles.EncoderAck.Set() | Out-Null
            $index=[Threading.WaitHandle]::WaitAny([Threading.WaitHandle[]]@($handles.WaitingArmed,$handles.Settled),$TimeoutSeconds*1000)
            if($index -eq [Threading.WaitHandle]::WaitTimeout) { $handles.Emergency.Set() | Out-Null; Fail 'WaitingArmed/settlement timeout' }
        } else { $index=1 }
        $waitClock=[Diagnostics.Stopwatch]::StartNew(); $settleClock=$null; $soft=$false; $hard=$false
        if($index -eq 0) { Write-GLog 'WaitingArmed observed; automatic budgets 5/10/30s started' }
        while($true) {
            if($handles.Settled.WaitOne(0)) {
                if($encoder -and -not $encoder.HasExited) { Fail 'false settlement: exact encoder alive' }
                Write-GLog 'settlement snapshot ACK; frame/cleanup/lease/observer/thread/callback/inner closed'
                $handles.GuardianClosed.Set() | Out-Null
                if($soft -or $hard) { Write-GLog 'VERDICT=FAILSAFE-TRIGGERED resources=settled'; return 2 }
                Write-GLog 'VERDICT=SETTLED'; return 0
            }
            if($null -eq $settleClock -and $handles.WaitExited.WaitOne(0)) {
                $settleClock=[Diagnostics.Stopwatch]::StartNew()
                Write-GLog ('WaitExited at '+$waitClock.ElapsedMilliseconds+'ms; wait budgets disarmed; settlement budget 30s')
            }
            if($null -eq $settleClock) {
                if(-not $soft -and $waitClock.ElapsedMilliseconds -ge 5000) {
                    $soft=$true; $handles.Cancel.Set() | Out-Null
                    Write-GLog ('automatic-soft-abort elapsedMs='+$waitClock.ElapsedMilliseconds+' request=Cancel (acceptance requires CancelAck)')
                }
                if(-not $hard -and $waitClock.ElapsedMilliseconds -ge 10000) {
                    $hard=$true; $handles.Emergency.Set() | Out-Null
                    Write-GLog ('automatic-emergency elapsedMs='+$waitClock.ElapsedMilliseconds+' direct=Emergency')
                    if($encoder) { Stop-VerifiedEncoder $encoder $encoderRecord }
                }
                if($waitClock.ElapsedMilliseconds -ge 30000) {
                    Write-GLog ('VERDICT=FAIL/RESIDUAL waiting did not exit; elapsedMs='+$waitClock.ElapsedMilliseconds+
                        ' CancelAck='+$handles.CancelAck.WaitOne(0)+' EmergencyAck='+$handles.EmergencyAck.WaitOne(0))
                    return 3
                }
            } elseif($settleClock.ElapsedMilliseconds -ge 30000) {
                Write-GLog ('VERDICT=FAIL/RESIDUAL settlement incomplete elapsedMs='+$settleClock.ElapsedMilliseconds); return 3
            }
            $handles.Settled.WaitOne(25) | Out-Null
        }
    } finally { if($encoder) { $encoder.Dispose() }; foreach($handle in $handles.Values) { $handle.Dispose() } }
}
function Invoke-SetKind([string]$kind) {
    $handle=$null; $ack=$null
    try {
        $handle=Open-Event $kind; $ack=Open-Event ($kind+'Ack')
        $handle.Set() | Out-Null; Write-GLog ('request sent '+$kind)
        if(-not $ack.WaitOne($TimeoutSeconds*1000)) { Fail ($kind+' handling ACK timeout') }
        Write-GLog ('runtime handling ACK '+$kind+'; acceptance/rejection is recorded by the runtime slot')
    } finally { if($handle) { $handle.Dispose() }; if($ack) { $ack.Dispose() } }
}
try {
    switch($Action) {
        'WriteDescriptor' { Invoke-WriteDescriptor }
        'Arm' { $code=Invoke-Arm; exit $code }
        {$_ -in @('Release','Cancel','Emergency','Shutdown')} { Invoke-SetKind $Action }
        'Terminate' {
            if($GamePid -le 0 -or $EncoderPid -le 0) { Fail 'explicit GamePid/EncoderPid required' }
            $p=[Diagnostics.Process]::GetProcessById($EncoderPid)
            try { Stop-VerifiedEncoder $p @{pid=$EncoderPid;startUtcTicks=$EncoderStartUtcTicks;path=$ExpectedEncoderPath;sha256=$ExpectedEncoderSha256} }
            finally { $p.Dispose() }
        }
        'Status' {
            foreach($kind in $kinds) {
                $h=$null
                try { $h=Open-Event $kind; Write-GLog ($kind+'='+$h.WaitOne(0)) }
                catch [Threading.WaitHandleCannotBeOpenedException] { Write-GLog ($kind+'=absent') }
                finally { if($h) { $h.Dispose() } }
            }
        }
        'Rehearsal' { & (Join-Path $PSScriptRoot '..\tests\f1-guardian-rehearsal.ps1') -HarnessPath $HarnessPath; exit $LASTEXITCODE }
    }
} catch { Write-GLog ('FAIL '+$_.Exception.Message); exit 1 }
