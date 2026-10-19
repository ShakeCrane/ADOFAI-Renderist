param([string]$HarnessPath='')
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
if(-not $HarnessPath) { $HarnessPath=Join-Path $repoRoot 'tests\ADOFAI.Renderist.FfmpegTests\bin\Release\ADOFAI.Renderist.FfmpegTests.exe' }
if(-not [IO.File]::Exists($HarnessPath)) { throw 'build net48 harness before Rehearsal' }
$root=Join-Path $repoRoot ('.build-tools\f1a-safety\guardian-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$hostExe=(Get-Command powershell.exe).Source
$guardianScript=Join-Path $repoRoot 'scripts\f1-frame-local-guardian.ps1'
$kinds=@('Ready','Ack','Live','Release','Cancel','Emergency','Shutdown','ReleaseAck','CancelAck',
    'EmergencyAck','ShutdownAck','EncoderReady','EncoderAck','WaitingArmed','WriteGateEntered',
    'WaitExited','Settled','GuardianClosed')
function Read-Kv([string]$path) {
    $v=@{}
    foreach($line in [IO.File]::ReadAllLines($path)) { $eq=$line.IndexOf('='); if($eq -gt 0) { $v[$line.Substring(0,$eq)]=$line.Substring($eq+1) } }
    return $v
}
function Identity($p) {
    $null=$p.Handle
    $clock=[Diagnostics.Stopwatch]::StartNew(); $imagePath=$null
    while(-not $imagePath -and $clock.ElapsedMilliseconds -lt 3000) {
        $p.Refresh()
        $imagePath=$p.MainModule.FileName
        if(-not $imagePath) { Start-Sleep -Milliseconds 10 }
    }
    if(-not $imagePath) { throw 'helper image path unavailable' }
    return @{start=$p.StartTime.ToUniversalTime().Ticks; path=$imagePath; hash=(Get-FileHash -LiteralPath $imagePath).Hash}
}
function Open-Event([string]$kind) { [Threading.EventWaitHandle]::OpenExisting("Local\$eventPrefix.$nonce.$kind") }
function Launch-Action([string]$label,[string]$arguments) {
    $p=Start-Process -FilePath $hostExe -WindowStyle Hidden -PassThru -ArgumentList (
        '-NoProfile -ExecutionPolicy Bypass -File "'+$guardianScript+'" '+$arguments
    ) -RedirectStandardOutput (Join-Path $root ($label+'.out')) -RedirectStandardError (Join-Path $root ($label+'.err'))
    $null=$p.Handle
    return $p
}
function Await-Exit($p,[int]$expected,[string]$label,[int]$ms=45000) {
    if(-not $p.WaitForExit($ms)) { throw ($label+' timeout; evidence='+$root) }
    if($p.ExitCode -ne $expected) { throw ($label+' exit='+$p.ExitCode+' expected='+$expected+' evidence='+$root) }
}
function Await-Event([string]$kind) {
    $h=$null; $clock=[Diagnostics.Stopwatch]::StartNew()
    try {
        while(-not $h -and $clock.ElapsedMilliseconds -lt 10000) {
            try { $h=Open-Event $kind } catch [Threading.WaitHandleCannotBeOpenedException] { Start-Sleep -Milliseconds 25 }
        }
        if(-not $h -or -not $h.WaitOne(10000)) { throw ($kind+' timeout; evidence='+$root) }
    } finally { if($h) { $h.Dispose() } }
}
try {
    # Negative identity checks use a controlled unrelated child, never the user's game/encoder.
    $identityChild=Start-Process -FilePath $hostExe -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile -Command "Start-Sleep -Seconds 90"'
    $null=$identityChild.Handle
    $childIdentity=Identity $identityChild
    try {
        foreach($rejection in @('protected','start','path','hash')) {
            $protectedPid=$PID; $expectedStart=$childIdentity.start; $expectedPath=$childIdentity.path; $expectedHash=$childIdentity.hash
            $expectedError=''
            switch($rejection) {
                'protected' { $protectedPid=$identityChild.Id; $expectedError='refusing to terminate the game process' }
                'start' { $expectedStart=1; $expectedError='encoder start time mismatch' }
                'path' { $expectedPath+='.mismatch'; $expectedError='encoder path mismatch' }
                'hash' { $expectedHash=('0'*64); $expectedError='encoder hash mismatch' }
            }
            $reject=Launch-Action ('reject-'+$rejection) ('-Action Terminate -GamePid '+$protectedPid+' -EncoderPid '+$identityChild.Id+
                ' -EncoderStartUtcTicks '+$expectedStart+' -ExpectedEncoderPath "'+$expectedPath+'" -ExpectedEncoderSha256 '+$expectedHash)
            try { Await-Exit $reject 1 ('identity rejection '+$rejection) } finally { $reject.Dispose() }
            if(-not [IO.File]::ReadAllText((Join-Path $root ('reject-'+$rejection+'.out'))).Contains($expectedError)) { throw ('wrong rejection reason '+$rejection) }
            if($identityChild.HasExited) { throw ('identity rejection killed child '+$rejection) }
        }
        $terminate=Launch-Action 'identity-correct' ('-Action Terminate -GamePid '+$PID+' -EncoderPid '+$identityChild.Id+
            ' -EncoderStartUtcTicks '+$childIdentity.start+' -ExpectedEncoderPath "'+$childIdentity.path+'" -ExpectedEncoderSha256 '+$childIdentity.hash)
        try { Await-Exit $terminate 0 'correct identity termination' } finally { $terminate.Dispose() }
        if(-not $identityChild.WaitForExit(5000)) { throw 'controlled child survived correct termination' }
        Write-Output 'identity rejection PASS protected/start/path/hash; only correct child terminated'
    } finally {
        if(-not $identityChild.HasExited) {
            $now=Identity $identityChild
            if($now.start -ne $childIdentity.start -or $now.path -ne $childIdentity.path -or $now.hash -ne $childIdentity.hash) { throw 'identity child changed' }
            $identityChild.Kill(); $identityChild.WaitForExit(5000) | Out-Null
        }
        $identityChild.Dispose()
    }
    foreach($scenario in @('success','cancel','failure','external-soft','emergency','residual','cleanup-residual','guardian-exit')) {
        $nonce='rehearsal-'+[Guid]::NewGuid().ToString('N'); $eventPrefix='Renderist.F1Rehearsal'
        $descriptor=Join-Path $root ($scenario+'.descriptor')
        $runtime=$null; $guard=$null; $owned=@()
        try {
            $runtime=Start-Process -FilePath $HarnessPath -WindowStyle Hidden -PassThru -ArgumentList (
                '--f1-guardian-host "'+$descriptor+'" '+$scenario
            ) -RedirectStandardOutput (Join-Path $root ($scenario+'-runtime.out')) -RedirectStandardError (Join-Path $root ($scenario+'-runtime.err'))
            $null=$runtime.Handle; $owned+=@{process=$runtime;identity=(Identity $runtime)}
            $common=' -Nonce '+$nonce+' -EventPrefix '+$eventPrefix+' -TimeoutSeconds 10'
            $write=Launch-Action ($scenario+'-descriptor') ('-Action WriteDescriptor -GamePid '+$runtime.Id+' -DescriptorPath "'+$descriptor+'"'+$common)
            try { Await-Exit $write 0 'descriptor' } finally { $write.Dispose() }
            $guard=Launch-Action ($scenario+'-guardian') ('-Action Arm -GamePid '+$runtime.Id+' -DescriptorPath "'+$descriptor+'"'+$common)
            $owned+=@{process=$guard;identity=(Identity $guard)}
            Await-Event 'WaitingArmed'; Await-Event 'WriteGateEntered'
            if($scenario -in @('success','failure','cleanup-residual')) {
                if($scenario -eq 'failure') {
                    $e=Read-Kv ($descriptor+'.'+$nonce+'.encoder')
                    $terminate=Launch-Action 'failure-terminate' ('-Action Terminate -GamePid '+$runtime.Id+' -EncoderPid '+$e.pid+
                        ' -EncoderStartUtcTicks '+$e.startUtcTicks+' -ExpectedEncoderPath "'+$e.path+'" -ExpectedEncoderSha256 '+$e.sha256)
                    try { Await-Exit $terminate 0 'targeted failure' } finally { $terminate.Dispose() }
                }
                $release=Launch-Action ($scenario+'-release') ('-Action Release'+$common)
                try { Await-Exit $release 0 'Release ACK' } finally { $release.Dispose() }
            }
            if($scenario -eq 'cancel') {
                $cancelProcess=Launch-Action 'cancel-request' ('-Action Cancel'+$common)
                try { Await-Exit $cancelProcess 0 'Cancel ACK' } finally { $cancelProcess.Dispose() }
            }
            if($scenario -eq 'guardian-exit') {
                $guard.Kill()
                if(-not $guard.WaitForExit(5000)) { throw 'controlled guardian did not exit' }
            }
            $expectedGuard=0; $expectedRuntime=0
            if($scenario -in @('external-soft','emergency')) { $expectedGuard=2 }
            if($scenario -in @('residual','cleanup-residual')) { $expectedGuard=3; $expectedRuntime=2 }
            if($scenario -eq 'guardian-exit') { $expectedGuard=$guard.ExitCode; $expectedRuntime=2 }
            Await-Exit $guard $expectedGuard ($scenario+' guardian')
            Await-Exit $runtime $expectedRuntime ($scenario+' runtime')
            $external=[IO.File]::ReadAllText((Join-Path $root ($scenario+'-guardian.out')))
            $internal=[IO.File]::ReadAllText((Join-Path $root ($scenario+'-runtime.out')))
            if($scenario -in @('external-soft','emergency','residual') -and -not $external.Contains('automatic-soft-abort')) { throw 'missing automatic 5s evidence' }
            if($scenario -in @('emergency','residual') -and -not $external.Contains('automatic-emergency')) { throw 'missing independent 10s evidence' }
            if($scenario -in @('residual','cleanup-residual') -and -not $external.Contains('VERDICT=FAIL/RESIDUAL')) { throw 'missing 30s residual evidence' }
            if($scenario -eq 'cleanup-residual' -and ($external.Contains('automatic-emergency') -or $external.Contains('automatic-soft-abort'))) { throw 'wait deadlines were not disarmed' }
            if($scenario -eq 'guardian-exit' -and -not $internal.Contains('f1-guardian-exited-during-wait')) { throw 'guardian process-exit handle did not wake the actual waiter' }
            if($scenario -notin @('residual','cleanup-residual','guardian-exit') -and -not $internal.Contains('probe-resources-closed')) { throw 'missing actual runtime settlement' }
            foreach($kind in $kinds) {
                $unexpected=$null
                try { $unexpected=Open-Event $kind } catch [Threading.WaitHandleCannotBeOpenedException] { }
                if($unexpected) { $unexpected.Dispose(); throw ('named event residual '+$kind) }
            }
            Write-Output ('rehearsal '+$scenario+' PASS; runtime='+$expectedRuntime+' guardian='+$expectedGuard+' named events absent=18')
        } finally {
            $encoderFile=$descriptor+'.'+$nonce+'.encoder'
            if([IO.File]::Exists($encoderFile)) {
                $record=Read-Kv $encoderFile; $child=$null
                try {
                    $child=[Diagnostics.Process]::GetProcessById([int]$record.pid)
                    if(-not $child.HasExited) {
                        $observed=Identity $child
                        if($child.Id -eq $runtime.Id -or $observed.start -ne [long]$record.startUtcTicks -or $observed.path -ne $record.path -or $observed.hash -ne $record.sha256) { throw 'encoder cleanup identity mismatch' }
                        $child.Kill(); $child.WaitForExit(5000) | Out-Null
                    }
                } catch [ArgumentException] { } finally { if($child) { $child.Dispose() } }
            }
            foreach($record in $owned) {
                $p=$record.process
                try {
                    if(-not $p.HasExited) {
                        $now=Identity $p
                        if($now.start -ne $record.identity.start -or $now.path -ne $record.identity.path -or $now.hash -ne $record.identity.hash) { throw 'owned helper identity changed' }
                        $p.Kill(); $p.WaitForExit(5000) | Out-Null
                    }
                } finally { $p.Dispose() }
            }
        }
    }
    Write-Output ('AUTOMATIC REHEARSAL PASS evidence='+$root)
    exit 0
} catch { Write-Output ('REHEARSAL FAIL '+$_+' evidence='+$root); exit 1 }
