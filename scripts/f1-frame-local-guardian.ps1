<#
.SYNOPSIS
    F1-A 受限诊断的外部 guardian（独立进程；不进入发布 ZIP）。

.DESCRIPTION
    为**指定游戏 PID / 启动时间 + 一次性 run nonce**建立 descriptor 与命名事件，
    并在游戏外驱动 Release / Cancel / Emergency，做身份校验与定向 encoder 终止。

    契约（PROJECT_UNDERSTANDING §2.2.26 E/H）：
      * 命名对象使用 Local\ 会话范围 + 不可复用 nonce；显式区分 Release / Cancel /
        Shutdown / Emergency / Ack，不依赖同名旧信号。
      * 只终止**经身份核实**的本 run encoder：必须给出 pid，且 pid 不得是游戏进程，
        且可执行路径必须与期望路径一致。**禁止按进程名批量 kill，禁止终止游戏。**
      * 本脚本不修改游戏、不注入、不使用 native detour。

.PARAMETER Action
    Arm | Release | Cancel | Emergency | Shutdown | Status | Terminate | WriteDescriptor | Rehearsal

.PARAMETER GamePid
    目标游戏进程 PID（Arm / WriteDescriptor 必需）。

.PARAMETER Nonce
    一次性 run nonce（Arm / WriteDescriptor 必需；不可复用）。

.EXAMPLE
    pwsh -File scripts/f1-frame-local-guardian.ps1 -Action WriteDescriptor -GamePid 1234 -Nonce run-20261019-a

.EXAMPLE
    pwsh -File scripts/f1-frame-local-guardian.ps1 -Action Rehearsal
#>
[CmdletBinding()]
param(
    [ValidateSet('Arm','Release','Cancel','Emergency','Shutdown','Status','Terminate','WriteDescriptor','Rehearsal')]
    [string]$Action = 'Status',
    [int]$GamePid = 0,
    [string]$Nonce = '',
    [string]$EventPrefix = 'Renderist.F1',
    [string]$DescriptorPath = '',
    [int]$ExpirySeconds = 600,
    [int]$TimeoutSeconds = 30,
    [int]$EncoderPid = 0,
    [long]$EncoderStartUtcTicks = 0,
    [string]$ExpectedEncoderPath = '',
    [string]$LogPath = ''
)

$ErrorActionPreference = 'Stop'

function Write-GLog([string]$message) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss.fff'), $message
    Write-Host $line
    if ($LogPath) {
        try { Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8 } catch { }
    }
}

function Fail([string]$message) {
    throw $message
}

function Get-EventName([string]$kind) {
    return ("Local\{0}.{1}.{2}" -f $EventPrefix, $Nonce, $kind)
}

function Open-Event([string]$kind, [bool]$create) {
    $name = Get-EventName $kind
    if ($create) {
        return [System.Threading.EventWaitHandle]::new($false, [System.Threading.EventResetMode]::ManualReset, $name)
    }
    return [System.Threading.EventWaitHandle]::OpenExisting($name)
}

function Get-ProcessIdentity([int]$targetPid) {
    try {
        $p = [System.Diagnostics.Process]::GetProcessById($targetPid)
        try { return @{ Ok = $true; Pid = $p.Id; Start = $p.StartTime.ToUniversalTime().Ticks; Path = $p.MainModule.FileName } }
        finally { $p.Dispose() }
    } catch {
        return @{ Ok = $false; Error = $_.Exception.GetType().Name }
    }
}

function New-Descriptor([int]$targetPid, [string]$nonce, [string]$path) {
    $identity = Get-ProcessIdentity $targetPid
    if (-not $identity.Ok) { Fail ("game process identity unavailable: " + $identity.Error) }

    $lines = @(
        "# F1-A diagnostic descriptor. Single use. Not part of the release.",
        ("runNonce=" + $nonce),
        ("gamePid=" + $identity.Pid),
        ("gameStartUtcTicks=" + $identity.Start),
        ("eventPrefix=" + $EventPrefix),
        ("expirySeconds=" + $ExpirySeconds)
    )
    $dir = Split-Path -Parent $path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [System.IO.File]::WriteAllText($path, ($lines -join "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
    Write-GLog ("descriptor written: " + $path + " pid=" + $identity.Pid + " nonce=" + $nonce)
}

function Assert-Game([int]$targetPid) {
    if ($targetPid -le 0) { Fail 'GamePid is required' }
    $identity = Get-ProcessIdentity $targetPid
    if (-not $identity.Ok) { Fail ("game process not found: " + $targetPid) }
    return $identity
}

# --------------------------------------------------------------------------- actions

function Invoke-WriteDescriptor {
    if (-not $Nonce) { Fail 'Nonce is required (single use)' }
    if (-not $DescriptorPath) {
        $tempRoot = [System.IO.Path]::GetTempPath()
        if ([string]::IsNullOrWhiteSpace($tempRoot)) { $tempRoot = (Get-Location).Path }
        $DescriptorPath = Join-Path $tempRoot ("renderist-f1-" + $Nonce + ".descriptor")
    }
    New-Descriptor $GamePid $Nonce $DescriptorPath
    Write-GLog ("point the game at it with: set " + 'RENDERIST_F1_PROBE_FILE=' + $DescriptorPath)
}

function Invoke-Arm {
    if (-not $Nonce) { Fail 'Nonce is required (single use)' }
    $identity = Assert-Game $GamePid

    $handles = @{}
    foreach ($kind in @('Ready','Ack','Release','Cancel','Shutdown','Emergency')) {
        # Ready/Ack 由 runtime 创建；guardian 用 OpenExisting 或短轮询等待出现。
        $handles[$kind] = $null
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $ready = $null
    while (-not $ready -and (Get-Date) -lt $deadline) {
        try { $ready = Open-Event 'Ready' $false } catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $ready) { Fail 'runtime Ready event never appeared (no probe arm)' }
    Write-GLog 'runtime Ready event opened'
    # Object existence precedes readiness: the runtime may still be creating the other handles.
    if (-not $ready.WaitOne([TimeSpan]::FromSeconds($TimeoutSeconds))) {
        $ready.Dispose()
        Fail 'runtime did not raise Ready within the handshake window'
    }

    $ack = $null
    try { $ack = Open-Event 'Ack' $false } catch { Fail 'Ack event missing' }
    $release = $null; $cancel = $null; $shutdown = $null; $emergency = $null
    try { $release = Open-Event 'Release' $false } catch { Fail 'Release event missing' }
    try { $cancel = Open-Event 'Cancel' $false } catch { Fail 'Cancel event missing' }
    try { $shutdown = Open-Event 'Shutdown' $false } catch { Fail 'Shutdown event missing' }
    try { $emergency = Open-Event 'Emergency' $false } catch { Fail 'Emergency event missing' }

    if (-not $ready.WaitOne([TimeSpan]::FromSeconds($TimeoutSeconds))) {
        Fail 'runtime did not raise Ready within the handshake window'
    }
    $ack.Set()
    Write-GLog 'handshake: Ready observed, Ack set'

    Write-GLog ("armed: pid=" + $identity.Pid + " nonce=" + $Nonce +
                " events=" + (Get-EventName 'Ready'))
    foreach ($h in @($ready,$ack,$release,$cancel,$shutdown,$emergency)) { try { $h.Dispose() } catch { } }
}

function Invoke-SetKind([string]$kind) {
    if (-not $Nonce) { Fail 'Nonce is required' }
    try {
        $h = Open-Event $kind $false
        $h.Set()
        $h.Dispose()
        Write-GLog ("event set: " + $kind + " (" + (Get-EventName $kind) + ")")
    } catch {
        Fail ("cannot set " + $kind + ": " + $_.Exception.GetType().Name)
    }
}

function Invoke-Status {
    if (-not $Nonce) { Fail 'Nonce is required' }
    foreach ($kind in @('Ready','Ack','Release','Cancel','Shutdown','Emergency')) {
        $state = 'absent'
        try {
            $h = Open-Event $kind $false
            $state = if ($h.WaitOne(0)) { 'signalled' } else { 'open' }
            $h.Dispose()
        } catch { }
        Write-GLog ("  " + $kind + " = " + $state)
    }
}

function Invoke-Terminate {
    if ($EncoderPid -le 0) { Fail 'EncoderPid is required (no batch kill by name)' }
    if ($GamePid -le 0) { Fail 'GamePid is required to protect the game process' }
    if ($EncoderPid -eq $GamePid) { Fail 'refusing to terminate the game process' }
    if (-not $ExpectedEncoderPath) { Fail 'ExpectedEncoderPath is required for identity verification' }
    if ($EncoderStartUtcTicks -le 0) { Fail 'EncoderStartUtcTicks is required (PID reuse protection)' }

    $target = Get-ProcessIdentity $EncoderPid
    if (-not $target.Ok) { Fail 'encoder process not found (already exited?)' }

    $process = [System.Diagnostics.Process]::GetProcessById($EncoderPid)
    try {
    if ($process.HasExited) { Write-GLog 'encoder already exited'; return }
    if ($process.StartTime.ToUniversalTime().Ticks -ne $EncoderStartUtcTicks) {
        Fail 'encoder start time mismatch; refusing to terminate a reused PID'
    }

    $actualPath = $null
    try { $actualPath = $process.MainModule.FileName } catch { }
    if (-not $actualPath) { Fail 'cannot read encoder path; refusing to kill an unidentified process' }
    if (-not [string]::Equals($actualPath, $ExpectedEncoderPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        Fail ("encoder path mismatch: expected=" + $ExpectedEncoderPath + " actual=" + $actualPath)
    }

    $process.Kill()
    $exited = $process.WaitForExit(5000)
    Write-GLog ("encoder terminated: pid=" + $EncoderPid + " exited=" + $exited + " path=" + $actualPath)
    if (-not $exited) { Fail 'encoder did not exit within 5s' }
    } finally { $process.Dispose() }
}

# --------------------------------------------------------------------------- rehearsal

function Invoke-Rehearsal {
    Write-GLog '== F1-A complete guardian rehearsal (independent processes; no Unity/game) =='
    $script:Nonce = 'rehearsal-' + [Guid]::NewGuid().ToString('N')
    $script:EventPrefix = 'Renderist.F1Rehearsal'
    $guardianScript = $PSCommandPath
    $hostExe = (Get-Command powershell.exe).Source
    $caseRoot = Join-Path ([IO.Path]::GetTempPath()) ('renderist-f1-' + $script:Nonce)
    [IO.Directory]::CreateDirectory($caseRoot) | Out-Null
    $runtimeScript = Join-Path $caseRoot 'runtime.ps1'
    # ASCII child: create all named objects before Ready; always dispose them.
    $runtimeBody = @'
param([string]$Nonce,[string]$Prefix)
$ErrorActionPreference='Stop'
$handles=@{}
try {
    foreach($kind in @('Ready','Ack','Release','Cancel','Emergency','Shutdown','Live')) {
        $handles[$kind]=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,"Local\$Prefix.$Nonce.$kind")
    }
    $handles.Ready.Set() | Out-Null
    if(-not $handles.Ack.WaitOne(15000)) { throw 'Ack timeout' }
    $handles.Live.Set() | Out-Null
    foreach($kind in @('Release','Cancel','Emergency','Shutdown')) {
        if(-not $handles[$kind].WaitOne(30000)) { throw ($kind+' timeout') }
        Write-Output ('observed '+$kind)
        $handles.Ack.Set() | Out-Null
    }
} catch { Write-Error $_; exit 1 }
finally { foreach($h in $handles.Values) { $h.Dispose() } }
'@
    [IO.File]::WriteAllText($runtimeScript,$runtimeBody,[Text.UTF8Encoding]::new($true))
    $runtime=$null; $child=$null; $held=@{}; $ownedProcesses=@(); $pass=$false
    function Run-GuardianChild([string]$label,[string]$arguments,[bool]$expectSuccess,[string]$expectedOutput='') {
        $outFile=Join-Path $caseRoot ($label+'.out'); $errFile=Join-Path $caseRoot ($label+'.err')
        $actionProcess=Start-Process -FilePath $hostExe -WindowStyle Hidden -PassThru -ArgumentList (
            '-NoProfile -ExecutionPolicy Bypass -File "'+$guardianScript+'" '+$arguments
        ) -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        $null=$actionProcess.Handle
        try {
            if(-not $actionProcess.WaitForExit(15000)) {
                $actionProcess.Kill(); $actionProcess.WaitForExit(5000) | Out-Null
                Fail ($label+' child timed out')
            }
            if(($actionProcess.ExitCode -eq 0) -ne $expectSuccess) {
                Fail ($label+' unexpected exit='+$actionProcess.ExitCode+' stderr='+[IO.File]::ReadAllText($errFile))
            }
            if($expectedOutput -and -not [IO.File]::ReadAllText($outFile).Contains($expectedOutput)) {
                Fail ($label+' did not report the expected identity rejection: '+$expectedOutput)
            }
            Write-GLog ($label+' verified exit='+$actionProcess.ExitCode)
        } finally { $actionProcess.Dispose() }
    }
    try {
        $runtime=Start-Process -FilePath $hostExe -PassThru -WindowStyle Hidden -ArgumentList (
            '-NoProfile -ExecutionPolicy Bypass -File "'+$runtimeScript+'" -Nonce '+$script:Nonce+' -Prefix '+$script:EventPrefix
        ) -RedirectStandardOutput (Join-Path $caseRoot 'runtime.out') -RedirectStandardError (Join-Path $caseRoot 'runtime.err')
        $null=$runtime.Handle
        $ownedProcesses+=@{ Process=$runtime; Start=$runtime.StartTime.ToUniversalTime().Ticks; Path=$runtime.MainModule.FileName }
        $deadline=[Diagnostics.Stopwatch]::StartNew()
        while(-not $held.ContainsKey('Ready') -and $deadline.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
            try { $held.Ready=Open-Event 'Ready' $false }
            catch { if($runtime.HasExited) { Fail ('runtime early exit: '+[IO.File]::ReadAllText((Join-Path $caseRoot 'runtime.err'))) }; Start-Sleep -Milliseconds 50 }
        }
        if(-not $held.ContainsKey('Ready')) { Fail 'runtime Ready event never appeared' }
        if(-not $held.Ready.WaitOne(5000)) { Fail 'Ready not signalled' }
        foreach($kind in @('Ack','Release','Cancel','Emergency','Shutdown','Live')) { $held[$kind]=Open-Event $kind $false }
        $script:GamePid=$runtime.Id
        $identity=Assert-Game $runtime.Id
        if($identity.Start -ne $ownedProcesses[0].Start) { Fail 'runtime identity mismatch' }
        Write-GLog ('runtime identity verified pid='+$identity.Pid+' start='+$identity.Start+' path='+$identity.Path)
        Run-GuardianChild 'descriptor' ('-Action WriteDescriptor -GamePid '+$runtime.Id+' -Nonce '+$script:Nonce+' -DescriptorPath "'+(Join-Path $caseRoot 'run.descriptor')+'"') $true
        $descriptor=[IO.File]::ReadAllText((Join-Path $caseRoot 'run.descriptor'))
        if($descriptor -notmatch ('gameStartUtcTicks='+$identity.Start)) { Fail 'descriptor identity not preserved' }
        Run-GuardianChild 'arm' ('-Action Arm -GamePid '+$runtime.Id+' -Nonce '+$script:Nonce+' -EventPrefix '+$script:EventPrefix+' -TimeoutSeconds 5') $true
        if(-not $held.Live.WaitOne(5000)) { Fail 'runtime did not acknowledge Ready/Ack' }
        Write-GLog 'Ready/Ack/Live handshake PASS'
        foreach($kind in @('Release','Cancel','Emergency')) {
            $held.Ack.Reset() | Out-Null
            Run-GuardianChild $kind ('-Action '+$kind+' -Nonce '+$script:Nonce+' -EventPrefix '+$script:EventPrefix) $true
            if(-not $held.Ack.WaitOne(5000)) { Fail ($kind+' not observed by runtime') }
            Write-GLog ($kind+' observed and acknowledged by runtime')
        }
        $child=Start-Process -FilePath $hostExe -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile -Command "Start-Sleep -Seconds 120"'
        $childIdentity=Get-ProcessIdentity $child.Id
        if(-not $childIdentity.Ok) { Fail 'controlled encoder identity unavailable' }
        $ownedProcesses+=@{ Process=$child; Start=$childIdentity.Start; Path=$childIdentity.Path }
        $baseArgs='-Action Terminate -EncoderPid '+$child.Id+' -EncoderStartUtcTicks '+$childIdentity.Start+' -ExpectedEncoderPath "'+$childIdentity.Path+'"'
        Run-GuardianChild 'protect-game' ($baseArgs+' -GamePid '+$child.Id) $false 'refusing to terminate the game process'
        if($child.HasExited) { Fail 'protected child was terminated' }
        Run-GuardianChild 'reject-path' ($baseArgs.Replace($childIdentity.Path,$childIdentity.Path+'.mismatch')+' -GamePid '+$runtime.Id) $false 'encoder path mismatch'
        if($child.HasExited) { Fail 'path rejection killed child' }
        Run-GuardianChild 'reject-start' ($baseArgs.Replace([string]$childIdentity.Start,'1')+' -GamePid '+$runtime.Id) $false 'encoder start time mismatch'
        if($child.HasExited) { Fail 'start rejection killed child' }
        Run-GuardianChild 'terminate' ($baseArgs+' -GamePid '+$runtime.Id) $true
        if(-not $child.WaitForExit(5000)) { Fail 'controlled encoder still alive' }
        if($runtime.HasExited) { Fail 'protected runtime was terminated' }
        Write-GLog 'targeted termination PASS (PID/start/path checked; protected runtime alive)'
        $held.Ack.Reset() | Out-Null
        Run-GuardianChild 'Shutdown' ('-Action Shutdown -Nonce '+$script:Nonce+' -EventPrefix '+$script:EventPrefix) $true
        if(-not $held.Ack.WaitOne(5000)) { Fail 'Shutdown not observed' }
        if(-not $runtime.WaitForExit(5000)) { Fail 'runtime did not exit after Shutdown' }
        if($runtime.ExitCode -ne 0) { Fail ('runtime failed: '+[IO.File]::ReadAllText((Join-Path $caseRoot 'runtime.err'))) }
        $observed=[IO.File]::ReadAllText((Join-Path $caseRoot 'runtime.out'))
        foreach($kind in @('Release','Cancel','Emergency','Shutdown')) {
            if($observed -notmatch ('observed '+$kind)) { Fail ('missing child evidence: '+$kind) }
        }
        foreach($h in $held.Values) { $h.Dispose() }; $held.Clear()
        foreach($kind in @('Ready','Ack','Release','Cancel','Emergency','Shutdown','Live')) {
            $unexpected=$null
            try { $unexpected=Open-Event $kind $false } catch [Threading.WaitHandleCannotBeOpenedException] { }
            if($null -ne $unexpected) { $unexpected.Dispose(); Fail ('named object residual: '+$kind) }
        }
        Write-GLog 'resource convergence PASS (children exited; seven named objects absent)'
        $pass=$true
        Write-GLog 'complete rehearsal PASS'
    } finally {
        foreach($h in $held.Values) { $h.Dispose() }
        foreach($record in $ownedProcesses) {
            $owned=$record.Process
            try {
                if(-not $owned.HasExited) {
                    if($owned.StartTime.ToUniversalTime().Ticks -ne $record.Start -or $owned.MainModule.FileName -ne $record.Path) {
                        throw 'child identity changed; refusing cleanup kill'
                    }
                    $owned.Kill()
                    if(-not $owned.WaitForExit(5000)) { throw 'child cleanup did not converge' }
                }
            } finally { $owned.Dispose() }
        }
        # Nonrecursive deletion, only files in the directory created for this nonce.
        if($pass) {
            foreach($file in [IO.Directory]::GetFiles($caseRoot)) { Remove-Item -LiteralPath $file -Force }
            [IO.Directory]::Delete($caseRoot)
        } else { Write-GLog ('failed rehearsal evidence: '+$caseRoot) }
    }
}

function Write-Log2([string]$m) { Write-GLog $m }

# --------------------------------------------------------------------------- dispatch

try {
switch ($Action) {
    'WriteDescriptor' { Invoke-WriteDescriptor }
    'Arm'             { Invoke-Arm }
    'Release'         { Invoke-SetKind 'Release' }
    'Cancel'          { Invoke-SetKind 'Cancel' }
    'Emergency'       { Invoke-SetKind 'Emergency' }
    'Shutdown'        { Invoke-SetKind 'Shutdown' }
    'Status'          { Invoke-Status }
    'Terminate'       { Invoke-Terminate }
    'Rehearsal'       { Invoke-Rehearsal }
}
} catch { Write-GLog ('FAIL '+$_.Exception.Message); exit 1 }
