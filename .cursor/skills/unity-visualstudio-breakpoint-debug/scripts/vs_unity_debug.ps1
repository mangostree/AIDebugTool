param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet("diagnose", "compile-unity", "set-breakpoint", "attach-unity", "trigger-static", "wait-break", "dump-locals", "continue", "run-session")]
    [string]$Command,

    [string]$File,
    [int]$Line = 0,
    [string]$TypeName = "CursorBreakpointDebugProbe",
    [string]$MethodName = "GenerateRandomAndLog",
    [int]$TimeoutSec = 30,
    [int]$UnityCompileTimeoutSec = 180,
    [int]$Depth = 2,
    [int]$MaxMembers = 40,
    [string]$BridgeClient = ".cursor\skills\unity-proactive-debug\scripts\editor_bridge_client.py",
    [switch]$UseGenericAttach,
    [switch]$SkipUnityCompile
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

function Write-JsonResult {
    param([object]$Value, [int]$ExitCode = 0)
    $Value | ConvertTo-Json -Depth 20
    exit $ExitCode
}

function New-ErrorResult {
    param([string]$Code, [string]$Message)
    [ordered]@{
        ok = $false
        error = $Code
        message = $Message
    }
}

function Get-Dte {
    $versions = @("17.0", "16.0", "15.0")
    foreach ($version in $versions) {
        $progId = "VisualStudio.DTE.$version"
        try {
            $dte = [Runtime.InteropServices.Marshal]::GetActiveObject($progId)
            if ($null -ne $dte) {
                return $dte
            }
        }
        catch {
            # Try the next Visual Studio version.
        }
    }

    throw "No running Visual Studio DTE instance found. Open Visual Studio with this Unity solution first."
}

function Get-DebugModeName {
    param([int]$Mode)
    switch ($Mode) {
        1 { return "design" }
        2 { return "break" }
        3 { return "run" }
        default { return "unknown:$Mode" }
    }
}

function Resolve-SourcePath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "File is required."
    }

    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }

    return [IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Get-UnityProcessCandidate {
    $processes = @(Get-Process -Name Unity -ErrorAction SilentlyContinue | Sort-Object -Property StartTime -Descending)
    if ($processes.Count -eq 0) {
        return $null
    }

    return $processes[0]
}

function Find-DteProcess {
    param([object]$Dte, [int]$ProcessId)
    foreach ($proc in $Dte.Debugger.LocalProcesses) {
        try {
            if ([int]$proc.ProcessID -eq $ProcessId) {
                return $proc
            }
        }
        catch {
            # Skip inaccessible process entries.
        }
    }

    return $null
}

function Test-DteCommandExists {
    param([object]$Dte, [string]$CommandName)
    try {
        foreach ($cmd in $Dte.Commands) {
            if ([string]$cmd.Name -eq $CommandName) {
                return $true
            }
        }
    }
    catch {
        return $false
    }

    return $false
}

function Attach-UnityDebugger {
    param([object]$Dte, [object]$UnityProcess, [bool]$AllowGenericAttach)

    $unityCommand = "Debug.AttachUnityDebugger"
    if (Test-DteCommandExists -Dte $Dte -CommandName $unityCommand) {
        $Dte.ExecuteCommand($unityCommand)
        Start-Sleep -Seconds 2
        return [ordered]@{
            ok = $true
            mode = "unity_debugger_command"
            unityProcessId = $UnityProcess.Id
            debuggerMode = Get-DebugModeName ([int]$Dte.Debugger.CurrentMode)
        }
    }

    if (-not $AllowGenericAttach) {
        return [ordered]@{
            ok = $false
            error = "unity_attach_command_missing"
            message = "Visual Studio command Debug.AttachUnityDebugger was not found. Generic Process.Attach is intentionally not used for Unity breakpoint tests because it can attach the wrong debug engine and leave C# symbols unloaded."
        }
    }

    $dteProcess = Find-DteProcess -Dte $Dte -ProcessId $UnityProcess.Id
    if ($null -eq $dteProcess) {
        return [ordered]@{
            ok = $false
            error = "process_not_visible"
            message = "Visual Studio cannot see Unity process $($UnityProcess.Id)."
        }
    }

    $dteProcess.Attach()
    Start-Sleep -Milliseconds 500
    return [ordered]@{
        ok = $true
        mode = "generic_process_attach"
        warning = "Generic Process.Attach can leave Unity C# breakpoints unbound. Prefer Debug.AttachUnityDebugger."
        unityProcessId = $UnityProcess.Id
        debuggerMode = Get-DebugModeName ([int]$Dte.Debugger.CurrentMode)
    }
}

function Set-VisualStudioBreakpoint {
    param([object]$Dte, [string]$SourceFile, [int]$SourceLine)

    if ($SourceLine -le 0) {
        throw "Line must be greater than zero."
    }

    $resolved = Resolve-SourcePath $SourceFile
    if (-not (Test-Path -LiteralPath $resolved)) {
        throw "File not found: $resolved"
    }

    $window = $Dte.ItemOperations.OpenFile($resolved)
    $window.Activate()
    $selection = $Dte.ActiveDocument.Selection
    $selection.GotoLine($SourceLine, $true)
    $Dte.ExecuteCommand("Debug.ToggleBreakpoint")
    Start-Sleep -Milliseconds 500

    return [ordered]@{
        ok = $true
        file = $resolved
        line = $SourceLine
        mode = "visual_studio_toggle_breakpoint"
        breakpointCount = $Dte.Debugger.Breakpoints.Count
    }
}

function Get-PythonLaunch {
    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($null -ne $python) {
        return [ordered]@{
            filePath = $python.Source
            prefixArgs = @()
        }
    }

    $py = Get-Command py -ErrorAction SilentlyContinue
    if ($null -ne $py) {
        return [ordered]@{
            filePath = $py.Source
            prefixArgs = @("-3")
        }
    }

    throw "Python was not found on PATH."
}

function Start-BridgeStaticInvoke {
    param([string]$TargetType, [string]$TargetMethod, [string]$ClientPath)
    $resolvedClient = Resolve-SourcePath $ClientPath
    if (-not (Test-Path -LiteralPath $resolvedClient)) {
        throw "Bridge client not found: $resolvedClient"
    }

    $launch = Get-PythonLaunch
    $stdout = Join-Path ([IO.Path]::GetTempPath()) ("cursor-unity-bridge-{0}.out.txt" -f ([Guid]::NewGuid().ToString("N")))
    $stderr = Join-Path ([IO.Path]::GetTempPath()) ("cursor-unity-bridge-{0}.err.txt" -f ([Guid]::NewGuid().ToString("N")))
    $args = @()
    $args += $launch.prefixArgs
    $args += @($resolvedClient, "invoke_static", $TargetType, $TargetMethod)

    $process = Start-Process -FilePath $launch.filePath -ArgumentList $args -NoNewWindow -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    return [ordered]@{
        processId = $process.Id
        stdout = $stdout
        stderr = $stderr
        typeName = $TargetType
        methodName = $TargetMethod
    }
}

function Invoke-BridgeClientSync {
    param([string]$ClientPath, [string[]]$ClientArgs, [int]$WaitTimeoutSec)
    $resolvedClient = Resolve-SourcePath $ClientPath
    if (-not (Test-Path -LiteralPath $resolvedClient)) {
        throw "Bridge client not found: $resolvedClient"
    }

    $launch = Get-PythonLaunch
    $stdout = Join-Path ([IO.Path]::GetTempPath()) ("cursor-unity-bridge-{0}.out.txt" -f ([Guid]::NewGuid().ToString("N")))
    $stderr = Join-Path ([IO.Path]::GetTempPath()) ("cursor-unity-bridge-{0}.err.txt" -f ([Guid]::NewGuid().ToString("N")))
    $args = @()
    $args += $launch.prefixArgs
    $args += $resolvedClient
    $args += $ClientArgs

    $process = Start-Process -FilePath $launch.filePath -ArgumentList $args -NoNewWindow -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (-not $process.WaitForExit($WaitTimeoutSec * 1000)) {
        try {
            $process.Kill()
        }
        catch {
            # ignore
        }

        return [ordered]@{
            ok = $false
            exitCode = $null
            stdout = ""
            stderr = "Timed out after $WaitTimeoutSec seconds."
            json = $null
        }
    }

    $outText = ""
    $errText = ""
    if (Test-Path -LiteralPath $stdout) {
        $outText = Get-Content -LiteralPath $stdout -Raw
    }

    if (Test-Path -LiteralPath $stderr) {
        $errText = Get-Content -LiteralPath $stderr -Raw
    }

    $json = $null
    try {
        if (-not [string]::IsNullOrWhiteSpace($outText)) {
            $json = $outText | ConvertFrom-Json
        }
    }
    catch {
        $json = $null
    }

    return [ordered]@{
        ok = ($process.ExitCode -eq 0)
        exitCode = $process.ExitCode
        stdout = $outText
        stderr = $errText
        json = $json
    }
}

function Invoke-UnityCompile {
    param([string]$ClientPath, [int]$WaitTimeoutSec)
    $refresh = Invoke-BridgeClientSync -ClientPath $ClientPath -ClientArgs @("refresh") -WaitTimeoutSec 30
    if (-not $refresh.ok) {
        return [ordered]@{
            ok = $false
            error = "refresh_failed"
            refresh = $refresh
        }
    }

    $deadline = (Get-Date).AddSeconds($WaitTimeoutSec)
    $lastStatus = $null
    while ((Get-Date) -lt $deadline) {
        $lastStatus = Invoke-BridgeClientSync -ClientPath $ClientPath -ClientArgs @("compile_status") -WaitTimeoutSec 30
        if ($lastStatus.ok -and $null -ne $lastStatus.json -and $lastStatus.json.ok -eq $true) {
            if ($lastStatus.json.result.isCompiling -eq $false) {
                $ping = Invoke-BridgeClientSync -ClientPath $ClientPath -ClientArgs @("ping") -WaitTimeoutSec 30
                return [ordered]@{
                    ok = ($ping.ok -and $null -ne $ping.json -and $ping.json.ok -eq $true)
                    action = "compile_idle"
                    refresh = $refresh
                    last = $lastStatus
                    ping = $ping
                }
            }
        }

        Start-Sleep -Milliseconds 500
    }

    return [ordered]@{
        ok = $false
        error = "compile_timeout"
        message = "Unity did not become compile-idle within $WaitTimeoutSec seconds."
        refresh = $refresh
        last = $lastStatus
    }
}

function Convert-Expression {
    param([object]$Expression, [int]$CurrentDepth, [int]$MaxDepth, [int]$MaxCount)

    $result = [ordered]@{}
    foreach ($prop in @("Name", "Value", "Type")) {
        try {
            $result[$prop.ToLowerInvariant()] = [string]$Expression.$prop
        }
        catch {
            $result[$prop.ToLowerInvariant()] = "<unavailable>"
        }
    }

    if ($CurrentDepth -lt $MaxDepth) {
        $members = @()
        try {
            $i = 0
            foreach ($member in $Expression.DataMembers) {
                if ($i -ge $MaxCount) {
                    $members += [ordered]@{ name = "<truncated>"; value = "max member count reached"; type = "" }
                    break
                }

                $members += Convert-Expression -Expression $member -CurrentDepth ($CurrentDepth + 1) -MaxDepth $MaxDepth -MaxCount $MaxCount
                $i++
            }
        }
        catch {
            $members = @()
        }

        if ($members.Count -gt 0) {
            $result["members"] = $members
        }
    }

    return $result
}

try {
    switch ($Command) {
        "compile-unity" {
            $compile = Invoke-UnityCompile -ClientPath $BridgeClient -WaitTimeoutSec $UnityCompileTimeoutSec
            Write-JsonResult $compile ($(if ($compile.ok) { 0 } else { 1 }))
        }

        "diagnose" {
            $dte = Get-Dte
            $unity = Get-UnityProcessCandidate
            $commands = @()
            try {
                foreach ($cmd in $dte.Commands) {
                    if ([string]$cmd.Name -like "*Unity*") {
                        $commands += [string]$cmd.Name
                    }
                }
            }
            catch {
                $commands = @()
            }

            Write-JsonResult ([ordered]@{
                ok = $true
                visualStudio = [ordered]@{
                    version = [string]$dte.Version
                    solution = [string]$dte.Solution.FullName
                    debuggerMode = Get-DebugModeName ([int]$dte.Debugger.CurrentMode)
                    unityCommands = $commands
                }
                unityProcess = if ($null -eq $unity) { $null } else { [ordered]@{ id = $unity.Id; path = $unity.Path; startTime = $unity.StartTime } }
            })
        }

        "set-breakpoint" {
            if ($Line -le 0) {
                Write-JsonResult (New-ErrorResult "bad_args" "Line must be greater than zero.") 2
            }

            $dte = Get-Dte
            $breakpoint = Set-VisualStudioBreakpoint -Dte $dte -SourceFile $File -SourceLine $Line
            Write-JsonResult $breakpoint
        }

        "attach-unity" {
            $dte = Get-Dte
            $unity = Get-UnityProcessCandidate
            if ($null -eq $unity) {
                Write-JsonResult (New-ErrorResult "no_unity_process" "No Unity process is running.") 1
            }

            $attach = Attach-UnityDebugger -Dte $dte -UnityProcess $unity -AllowGenericAttach ([bool]$UseGenericAttach)
            Write-JsonResult $attach ($(if ($attach.ok) { 0 } else { 1 }))
        }

        "trigger-static" {
            $invoke = Start-BridgeStaticInvoke -TargetType $TypeName -TargetMethod $MethodName -ClientPath $BridgeClient
            Write-JsonResult ([ordered]@{
                ok = $true
                bridgeInvoke = $invoke
            })
        }

        "wait-break" {
            $dte = Get-Dte
            $deadline = (Get-Date).AddSeconds($TimeoutSec)
            while ((Get-Date) -lt $deadline) {
                $mode = [int]$dte.Debugger.CurrentMode
                if ($mode -eq 2) {
                    Write-JsonResult ([ordered]@{
                        ok = $true
                        debuggerMode = Get-DebugModeName $mode
                    })
                }

                Start-Sleep -Milliseconds 250
            }

            Write-JsonResult (New-ErrorResult "wait_timeout" "Debugger did not enter break mode within $TimeoutSec seconds.") 1
        }

        "dump-locals" {
            $dte = Get-Dte
            if ([int]$dte.Debugger.CurrentMode -ne 2) {
                Write-JsonResult (New-ErrorResult "not_in_break_mode" "Debugger must be stopped at a breakpoint before reading locals.") 1
            }

            $frame = $dte.Debugger.CurrentStackFrame
            if ($null -eq $frame) {
                Write-JsonResult (New-ErrorResult "no_stack_frame" "No current stack frame is available.") 1
            }

            $locals = @()
            foreach ($local in $frame.Locals) {
                $locals += Convert-Expression -Expression $local -CurrentDepth 0 -MaxDepth $Depth -MaxCount $MaxMembers
            }

            Write-JsonResult ([ordered]@{
                ok = $true
                functionName = [string]$frame.FunctionName
                locals = $locals
            })
        }

        "continue" {
            $dte = Get-Dte
            $before = Get-DebugModeName ([int]$dte.Debugger.CurrentMode)
            $dte.Debugger.Go($false)
            Write-JsonResult ([ordered]@{
                ok = $true
                previousDebuggerMode = $before
                action = "go"
            })
        }

        "run-session" {
            if ($Line -le 0) {
                Write-JsonResult (New-ErrorResult "bad_args" "Line must be greater than zero.") 2
            }

            $compile = $null
            if (-not $SkipUnityCompile) {
                $compile = Invoke-UnityCompile -ClientPath $BridgeClient -WaitTimeoutSec $UnityCompileTimeoutSec
                if (-not $compile.ok) {
                    Write-JsonResult ([ordered]@{
                        ok = $false
                        error = "unity_compile_failed"
                        message = "Unity must finish compiling before breakpoint debugging starts."
                        compile = $compile
                    }) 1
                }
            }

            $dte = Get-Dte
            $resolved = Resolve-SourcePath $File
            if (-not (Test-Path -LiteralPath $resolved)) {
                Write-JsonResult (New-ErrorResult "file_not_found" $resolved) 2
            }

            $unity = Get-UnityProcessCandidate
            if ($null -eq $unity) {
                Write-JsonResult (New-ErrorResult "no_unity_process" "No Unity process is running.") 1
            }

            $breakpoint = Set-VisualStudioBreakpoint -Dte $dte -SourceFile $resolved -SourceLine $Line
            $attach = Attach-UnityDebugger -Dte $dte -UnityProcess $unity -AllowGenericAttach ([bool]$UseGenericAttach)
            if (-not $attach.ok) {
                Write-JsonResult $attach 1
            }

            $invoke = Start-BridgeStaticInvoke -TargetType $TypeName -TargetMethod $MethodName -ClientPath $BridgeClient

            $deadline = (Get-Date).AddSeconds($TimeoutSec)
            $hit = $false
            while ((Get-Date) -lt $deadline) {
                if ([int]$dte.Debugger.CurrentMode -eq 2) {
                    $hit = $true
                    break
                }

                Start-Sleep -Milliseconds 250
            }

            if (-not $hit) {
                Write-JsonResult ([ordered]@{
                    ok = $false
                    error = "wait_timeout"
                    message = "Debugger did not enter break mode within $TimeoutSec seconds."
                    bridgeInvoke = $invoke
                }) 1
            }

            $frame = $dte.Debugger.CurrentStackFrame
            $locals = @()
            foreach ($local in $frame.Locals) {
                $locals += Convert-Expression -Expression $local -CurrentDepth 0 -MaxDepth $Depth -MaxCount $MaxMembers
            }

            $dte.Debugger.Go($false)
            Start-Sleep -Milliseconds 500

            $bridgeOutput = ""
            $bridgeError = ""
            try {
                $bridgeProcess = Get-Process -Id $invoke.processId -ErrorAction SilentlyContinue
                if ($null -ne $bridgeProcess) {
                    $bridgeProcess.WaitForExit(10000) | Out-Null
                }

                if (Test-Path -LiteralPath $invoke.stdout) {
                    $bridgeOutput = Get-Content -LiteralPath $invoke.stdout -Raw
                }

                if (Test-Path -LiteralPath $invoke.stderr) {
                    $bridgeError = Get-Content -LiteralPath $invoke.stderr -Raw
                }
            }
            catch {
                $bridgeError = $_.Exception.Message
            }

            Write-JsonResult ([ordered]@{
                ok = $true
                compile = $compile
                breakpoint = $breakpoint
                attach = $attach
                unityProcessId = $unity.Id
                functionName = [string]$frame.FunctionName
                locals = $locals
                bridgeInvoke = $invoke
                bridgeOutput = $bridgeOutput
                bridgeError = $bridgeError
                continued = $true
            })
        }
    }
}
catch {
    Write-JsonResult (New-ErrorResult "exception" $_.Exception.Message) 1
}
