# Shared by dev-run and the isolated installer regression tests.
function ConvertTo-InstallAbsolutePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Install path is empty' }
    [IO.Path]::GetFullPath($Path)
}

function Test-InstallPathWithin([string]$Path, [string]$Root) {
    $pathFull = (ConvertTo-InstallAbsolutePath $Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $rootFull = (ConvertTo-InstallAbsolutePath $Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $pathFull.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase) -or
        $pathFull.StartsWith($rootFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-InstallPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string[]]$AllowedRoots
    )
    if ($AllowedRoots.Count -eq 0) { throw 'No allowed install roots were supplied' }
    $absolute = ConvertTo-InstallAbsolutePath $Path
    $matchingRoot = $null
    foreach ($root in $AllowedRoots) {
        if (Test-InstallPathWithin $absolute $root) {
            $matchingRoot = (ConvertTo-InstallAbsolutePath $root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
            break
        }
    }
    if (!$matchingRoot) { throw "Install path is outside the allowed roots: $absolute" }

    # Check every existing ancestor and descendant.  This keeps a junction or
    # symlink from redirecting a later delete/move outside the selected tree.
    $cursor = $absolute
    while ($true) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Install path is a reparse point: $cursor" }
        }
        if ($cursor.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar).Equals($matchingRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = Split-Path -Parent $cursor
        if (!$parent -or $parent -eq $cursor) { throw "Install path ancestor escaped its allowed root: $absolute" }
        $cursor = $parent
    }
    if (Test-Path -LiteralPath $absolute -PathType Container) {
        $reparse = @(Get-ChildItem -LiteralPath $absolute -Force -Recurse | Where-Object {
            $_.Attributes -band [IO.FileAttributes]::ReparsePoint
        })
        if ($reparse.Count -gt 0) { throw "Install path contains a reparse point: $($reparse[0].FullName)" }
    }
    $absolute
}

function Assert-InstallSameVolume {
    param([Parameter(Mandatory)][string[]]$Paths)
    $volumes = @($Paths | ForEach-Object { [IO.Path]::GetPathRoot((ConvertTo-InstallAbsolutePath $_)).ToUpperInvariant() } | Select-Object -Unique)
    if ($volumes.Count -gt 1) { throw "Install rollback paths must share one volume: $($volumes -join ', ')" }
}

function Test-CycleArcHeadlessCommandLine([string]$CommandLine) {
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return $false }
    # Only inspect the first argument after the executable. This prevents a
    # directory name or a later payload value from looking like a callback.
    $match = [regex]::Match($CommandLine.Trim(), '^(?:"[^"]*"|\S+)\s+(?:"([^"]+)"|(\S+))')
    if (!$match.Success) { return $false }
    $firstArgument = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
    foreach ($argument in @('--claude-statusline', '--claude-statusline-bridge', '--claude-stop-failure-bridge', '--apply-update', '--desktop-status', '--desktop-shutdown')) {
        if ($firstArgument.Equals($argument, [StringComparison]::Ordinal)) { return $true }
    }
    $false
}

function Get-CycleArcExecutableMetadata([string]$Path) {
    try {
        $version = (Get-Item -LiteralPath $Path -Force -ErrorAction Stop).VersionInfo
        return [pscustomobject]@{
            ProductName = [string]$version.ProductName
            OriginalFilename = [string]$version.OriginalFilename
        }
    }
    catch {
        return [pscustomobject]@{ ProductName = ''; OriginalFilename = '' }
    }
}

function Get-CycleArcProcessSnapshots {
    $currentSession = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $cimById = @{}
    $cimAvailable = $true
    try {
        foreach ($entry in @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'CycleArc.exe' OR Name = 'CodexMeter.exe' OR Name = 'prometer.exe'" -ErrorAction Stop)) {
            $cimById[[int]$entry.ProcessId] = $entry
        }
    }
    catch {
        $cimAvailable = $false
    }

    foreach ($process in @(Get-Process -Name 'prometer', 'CodexMeter', 'CycleArc' -ErrorAction SilentlyContinue)) {
        if ($process.SessionId -ne $currentSession) { $process.Dispose(); continue }
        $path = ''
        try { $path = ConvertTo-InstallAbsolutePath ([string]$process.Path) } catch { }
        $cim = $cimById[[int]$process.Id]
        $handleAvailable = $false
        try { $null = $process.Handle; $handleAvailable = $true } catch { }
        $commandLineAvailable = $cimAvailable -and $cim -and ![string]::IsNullOrWhiteSpace([string]$cim.CommandLine)
        if (!$commandLineAvailable -and $process.ProcessName.Equals('CycleArc', [StringComparison]::OrdinalIgnoreCase)) {
            if ($process.HasExited) { $process.Dispose(); continue }
            $unclassifiedId = $process.Id
            $process.Dispose()
            throw "Could not inspect CycleArc command lines; refusing to stop PID $unclassifiedId without callback classification."
        }
        [pscustomobject]@{
            Process = $process
            ProcessId = [int]$process.Id
            Path = $path
            CommandLine = if ($cim) { [string]$cim.CommandLine } else { '' }
            SessionId = if ($cim) { [int]$cim.SessionId } else { [int]$process.SessionId }
            ProductName = ''
            OriginalFilename = ''
            CurrentSession = if ($cim) { [int]$cim.SessionId -eq $currentSession } else { [int]$process.SessionId -eq $currentSession }
            CommandLineAvailable = [bool]$commandLineAvailable
            HandleAvailable = $handleAvailable
        }
    }
}

function Get-CycleArcSnapshotValue([object]$Snapshot, [string]$Name) {
    $property = $Snapshot.PSObject.Properties[$Name]
    if ($property) { return $property.Value }
    $null
}

function Get-CycleArcDesktopProcess {
    param(
        [object[]]$ProcessSnapshots,
        [string[]]$KnownExecutablePaths = @()
    )
    $ownsSnapshots = !$PSBoundParameters.ContainsKey('ProcessSnapshots')
    if ($ownsSnapshots) {
        $ProcessSnapshots = @(Get-CycleArcProcessSnapshots)
    }
    $known = @{}
    foreach ($path in $KnownExecutablePaths) {
        try { $known[(ConvertTo-InstallAbsolutePath $path)] = $true } catch { }
    }
    $currentSession = [Diagnostics.Process]::GetCurrentProcess().SessionId
    foreach ($snapshot in @($ProcessSnapshots)) {
        $selected = $false
        try {
        $path = ''
        try { $path = ConvertTo-InstallAbsolutePath ([string](Get-CycleArcSnapshotValue $snapshot 'Path')) } catch { }
        $sessionValue = Get-CycleArcSnapshotValue $snapshot 'SessionId'
        $session = if ($null -ne $sessionValue) { [int]$sessionValue }
            elseif ([bool](Get-CycleArcSnapshotValue $snapshot 'CurrentSession')) { $currentSession }
            else { -1 }
        if ($session -ne $currentSession) { continue }
        if (Test-CycleArcHeadlessCommandLine ([string](Get-CycleArcSnapshotValue $snapshot 'CommandLine'))) { continue }

        $product = [string](Get-CycleArcSnapshotValue $snapshot 'ProductName')
        $original = [string](Get-CycleArcSnapshotValue $snapshot 'OriginalFilename')
        if (!$product -and !$original -and $path) {
            $metadata = Get-CycleArcExecutableMetadata $path
            $product = $metadata.ProductName
            $original = $metadata.OriginalFilename
        }
        $knownPath = $known.ContainsKey($path)
        $commandLineAvailableProperty = $snapshot.PSObject.Properties['CommandLineAvailable']
        $commandLineAvailable = if ($commandLineAvailableProperty) { [bool]$commandLineAvailableProperty.Value } else { $true }
        # A CycleArc.exe with an unreadable command line could be a short-lived
        # Claude callback. Fail closed; the mutex check will produce the clear
        # preflight error instead of killing an unclassified process.
        if (!$commandLineAvailable -and [IO.Path]::GetFileName($path).Equals('CycleArc.exe', [StringComparison]::OrdinalIgnoreCase)) { continue }
        $cycleArcMetadata = $product.Equals('CycleArc', [StringComparison]::OrdinalIgnoreCase) -and
            $original.Equals('CycleArc.dll', [StringComparison]::OrdinalIgnoreCase)
        if (!$knownPath -and !$cycleArcMetadata) { continue }

        $selected = $true
        [pscustomobject]@{
            Process = Get-CycleArcSnapshotValue $snapshot 'Process'
            ProcessId = [int](Get-CycleArcSnapshotValue $snapshot 'ProcessId')
            Path = $path
            CommandLine = [string](Get-CycleArcSnapshotValue $snapshot 'CommandLine')
            SessionId = $session
            ProductName = $product
            OriginalFilename = $original
            CommandLineAvailable = $commandLineAvailable
        }
        }
        finally {
            if ($ownsSnapshots -and !$selected) { $snapshot.Process.Dispose() }
        }
    }
}

function Stop-CycleArcDesktopProcess {
    param(
        [Parameter(Mandatory)][object]$ProcessRecord,
        [int]$TimeoutMilliseconds = 10000
    )
    $processProperty = $ProcessRecord.PSObject.Properties['Process']
    $process = if ($processProperty) { $processProperty.Value } else { $null }
    # A PID alone is insufficient: only stop the captured process object.
    if (!$process) { return $false }
    try {
        # Retaining this handle on the Process object prevents a PID-reuse lookup
        # from redirecting the stop to a different process.
        $null = $process.Handle
        $process.Refresh()
        if ($process.HasExited) { return $false }
        $actualPath = ConvertTo-InstallAbsolutePath ([string]$process.Path)
    }
    catch { return $false }
    $expectedPath = ''
    try { $expectedPath = ConvertTo-InstallAbsolutePath ([string](Get-CycleArcSnapshotValue $ProcessRecord 'Path')) } catch { return $false }
    if (!$expectedPath -or !$actualPath.Equals($expectedPath, [StringComparison]::OrdinalIgnoreCase)) { return $false }
    try { Stop-Process -InputObject $process -Force -ErrorAction Stop } catch { if (!$process.HasExited) { throw } }
    if (!$process.WaitForExit($TimeoutMilliseconds)) { throw "CycleArc process $($ProcessRecord.ProcessId) did not exit within $TimeoutMilliseconds ms" }
    $true
}

function Assert-InstallDesktopMutexAbsent {
    param([string]$MutexName = 'Local\ProMeter.SingleInstance')
    try {
        $mutex = [Threading.Mutex]::OpenExisting($MutexName)
        $mutex.Dispose()
        throw "CycleArc single-instance mutex is still present: $MutexName"
    }
    catch [Threading.WaitHandleCannotBeOpenedException] { return }
    catch [UnauthorizedAccessException] { throw "Could not verify CycleArc single-instance mutex: $MutexName" }
}

# Restart Manager, the API Windows itself uses to ask "who is using this file?". Read-only:
# this only lists the processes holding a file open. It never asks them to restart or close.
#
# Waiting for the desktop process and its mutex is necessary but not sufficient: a rename of
# the installation directory can still be denied by a handle neither check can see, which is
# how a same-version repair fails with Windows error 5 right after the desktop has exited.
# This names that holder instead of leaving it unattributable.
if (-not ('CycleArc.RestartManager' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CycleArc {
    [StructLayout(LayoutKind.Sequential)]
    public struct RM_UNIQUE_PROCESS {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct RM_PROCESS_INFO {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    public static class RestartManager {
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        public static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

        [DllImport("rstrtmgr.dll")]
        public static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        public static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
            uint nApplications, [In] RM_UNIQUE_PROCESS[] rgApplications, uint nServices, string[] rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        public static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
            [In, Out] RM_PROCESS_INFO[] rgAffectedApps, ref uint lpdwRebootReasons);
    }
}
'@ -ErrorAction SilentlyContinue
}

function Get-FileLockingProcess {
    <#
    .SYNOPSIS
        The processes holding any of these files open, as reported by Restart Manager.
    .DESCRIPTION
        Diagnostic only. Returns an empty list when nothing holds them, or when Restart Manager
        itself is unavailable - an unanswerable question is never reported as "nobody".
    #>
    param([Parameter(Mandatory)][string[]]$Path)
    if (-not ('CycleArc.RestartManager' -as [type])) { return @() }
    $files = @($Path | Where-Object { $_ -and (Test-Path -LiteralPath $_) })
    if ($files.Count -eq 0) { return @() }
    $session = [uint32]0
    $key = New-Object Text.StringBuilder 256
    try {
        if ([CycleArc.RestartManager]::RmStartSession([ref]$session, 0, $key) -ne 0) { return @() }
    }
    catch { return @() }
    try {
        $noProcesses = New-Object 'CycleArc.RM_UNIQUE_PROCESS[]' 0
        if ([CycleArc.RestartManager]::RmRegisterResources($session, [uint32]$files.Count, $files,
                0, $noProcesses, 0, $null) -ne 0) {
            return @()
        }
        $needed = [uint32]0
        $count = [uint32]0
        $reasons = [uint32]0
        $none = New-Object 'CycleArc.RM_PROCESS_INFO[]' 0
        # ERROR_MORE_DATA (234) is the expected first answer; it reports how many there are.
        $status = [CycleArc.RestartManager]::RmGetList($session, [ref]$needed, [ref]$count, $none, [ref]$reasons)
        if ($status -eq 0 -or $needed -eq 0) { return @() }
        if ($status -ne 234) { return @() }
        $count = $needed
        $infos = New-Object 'CycleArc.RM_PROCESS_INFO[]' $needed
        if ([CycleArc.RestartManager]::RmGetList($session, [ref]$needed, [ref]$count, $infos, [ref]$reasons) -ne 0) {
            return @()
        }
        $results = @()
        for ($i = 0; $i -lt $count; $i++) {
            $processId = $infos[$i].Process.dwProcessId
            $imagePath = ''
            try {
                $process = Get-Process -Id $processId -ErrorAction Stop
                try { $imagePath = [string]$process.Path } catch { }
                $process.Dispose()
            }
            catch { }
            $results += [pscustomobject]@{
                ProcessId = $processId
                Name      = $infos[$i].strAppName
                Path      = $imagePath
            }
        }
        $results
    }
    finally { try { [void][CycleArc.RestartManager]::RmEndSession($session) } catch { } }
}

function Get-InstallDirectoryLock {
    <#
    .SYNOPSIS
        Who is holding files inside an installation directory, if anyone.
    #>
    param([Parameter(Mandatory)][string]$InstallRoot, [int]$MaxFiles = 60)
    $root = ConvertTo-InstallAbsolutePath $InstallRoot
    if (!(Test-Path -LiteralPath $root -PathType Container)) { return @() }
    # Executables and libraries first: those are what stay mapped after a process exits.
    $files = @(Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object @{ Expression = { if ($_.Extension -in @('.exe', '.dll')) { 0 } else { 1 } } },
                    @{ Expression = { $_.Length }; Descending = $true } |
        Select-Object -First $MaxFiles |
        ForEach-Object { $_.FullName })
    Get-FileLockingProcess -Path $files
}

function Format-InstallDirectoryLock {
    param([object[]]$Lock)
    if (!$Lock -or $Lock.Count -eq 0) { return 'nothing was reported as holding the installation' }
    ($Lock | ForEach-Object { "PID $($_.ProcessId) $($_.Name)$(if ($_.Path) { " ($($_.Path))" })" }) -join '; '
}

function Wait-InstallDesktopReleased {
    param(
        [int]$ProcessId = 0,
        [string]$MutexName = 'Local\ProMeter.SingleInstance',
        [int]$TimeoutSeconds = 30,
        [int]$PollMilliseconds = 500,
        [string]$InstallRoot
    )
    # Observation only: a desktop that will not let go is reported, never
    # terminated. Both conditions are necessary before an installer may touch a
    # live installation, and neither proves the installation directory can
    # actually be replaced -- a directory rename can still be denied by a handle
    # this check cannot see. Setup's own log says what finally blocked it.
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    $processAlive = $false
    $mutexHeld = $false
    while ($true) {
        $processAlive = $false
        if ($ProcessId -gt 0) {
            $running = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
            if ($running) {
                $processAlive = !$running.HasExited
                $running.Dispose()
            }
        }
        $mutexHeld = $true
        try {
            $mutex = [Threading.Mutex]::OpenExisting($MutexName)
            $mutex.Dispose()
        }
        catch [Threading.WaitHandleCannotBeOpenedException] { $mutexHeld = $false }
        # An unreadable mutex is treated as held: failing closed keeps an
        # installer away from a desktop whose state could not be confirmed.
        catch [UnauthorizedAccessException] { $mutexHeld = $true }
        # The process and the mutex going away does not mean the directory can be renamed:
        # a handle on a file inside it denies that, and Restart Manager is what can see it.
        # This is the check that was missing when a same-version repair hit Windows error 5
        # immediately after the desktop had exited.
        $lock = @()
        if (!$processAlive -and !$mutexHeld -and $InstallRoot) {
            $lock = @(Get-InstallDirectoryLock -InstallRoot $InstallRoot)
        }
        if (!$processAlive -and !$mutexHeld -and $lock.Count -eq 0) {
            return [pscustomobject]@{
                ElapsedSeconds = [math]::Round($deadline.Elapsed.TotalSeconds, 1)
                ProcessId = $ProcessId
                MutexName = $MutexName
                InstallRoot = $InstallRoot
            }
        }
        if ($deadline.Elapsed.TotalSeconds -ge $TimeoutSeconds) { break }
        Start-Sleep -Milliseconds $PollMilliseconds
    }
    $remaining = @()
    if ($processAlive) { $remaining += "PID $ProcessId is still running" }
    if ($mutexHeld) { $remaining += "single-instance mutex $MutexName is still held" }
    if ($InstallRoot) {
        $lock = @(Get-InstallDirectoryLock -InstallRoot $InstallRoot)
        if ($lock.Count -gt 0) {
            $remaining += "the installation is held by $(Format-InstallDirectoryLock $lock)"
        }
    }
    throw ("CycleArc did not release its installation within $TimeoutSeconds seconds: " +
        ($remaining -join '; ') + '. Refusing to run the installer over a live installation.')
}

function Invoke-InstallGit {
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string[]]$Arguments
    )
    $root = ConvertTo-InstallAbsolutePath $RepoRoot
    $output = @(& git -C $root @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = [int]$LASTEXITCODE
    $text = $output -join "`n"
    if ($exitCode -ne 0) {
        $detail = if ($text) { ": $text" } else { '' }
        throw "Git metadata lookup failed for $root$detail"
    }
    $text
}

function Assert-CycleArcTree {
    param([Parameter(Mandatory)][string]$Root)
    $rootFull = ConvertTo-InstallAbsolutePath $Root
    foreach ($relative in @('CycleArc.sln', 'src/CycleArc/CycleArc.csproj')) {
        $candidate = Join-Path $rootFull $relative
        if (!(Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "Git primary worktree is not a CycleArc tree: missing $candidate" }
        Assert-InstallPath -Path $candidate -AllowedRoots @($rootFull) | Out-Null
    }
}

function Resolve-InstallLayout {
    param([Parameter(Mandatory)][string]$RepoRoot)
    $currentRoot = ConvertTo-InstallAbsolutePath $RepoRoot
    Assert-CycleArcTree -Root $currentRoot
    $gitMetadata = Join-Path $currentRoot '.git'
    $gitMetadataItem = Get-Item -LiteralPath $gitMetadata -Force -ErrorAction SilentlyContinue
    if (!$gitMetadataItem) {
        return [pscustomobject]@{
            CurrentRoot = $currentRoot
            PrimaryRoot = $currentRoot
            InstallRoot = $currentRoot
            LocalDir = Join-Path $currentRoot 'publish/local'
            BackupDir = Join-Path $currentRoot 'publish/local.previous'
            UsesGitPrimary = $false
        }
    }
    if ($gitMetadataItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Git metadata is a reparse point: $gitMetadata"
    }

    # A .git file/directory exists, so Git failures are metadata failures, not
    # permission to silently fall back to a different installation directory.
    $reportedRoot = (Invoke-InstallGit -RepoRoot $currentRoot -Arguments @('rev-parse', '--show-toplevel')).Trim()
    if (!$reportedRoot -or !(Test-InstallPathWithin $reportedRoot $currentRoot) -or
        !(ConvertTo-InstallAbsolutePath $reportedRoot).Equals($currentRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Git reported an unexpected current worktree: '$reportedRoot'"
    }
    $commonDirText = (Invoke-InstallGit -RepoRoot $currentRoot -Arguments @('rev-parse', '--path-format=absolute', '--git-common-dir')).Trim()
    if (!$commonDirText -or $commonDirText -match "`r|`n") { throw 'Git common-dir metadata is malformed' }
    $commonDir = ConvertTo-InstallAbsolutePath $commonDirText
    if (!(Test-Path -LiteralPath $commonDir -PathType Container)) { throw "Git common dir is not a directory: $commonDir" }
    Assert-InstallPath -Path $commonDir -AllowedRoots @($commonDir) | Out-Null
    if ([IO.Path]::GetFileName($commonDir).ToLowerInvariant() -ne '.git') { throw "Git common dir is not a .git directory: $commonDir" }
    $primaryRoot = Split-Path -Parent $commonDir
    if (!$primaryRoot -or !(Test-Path -LiteralPath $primaryRoot -PathType Container)) { throw "Git primary worktree root is invalid: $primaryRoot" }
    Assert-InstallPath -Path $primaryRoot -AllowedRoots @($primaryRoot) | Out-Null
    Assert-CycleArcTree -Root $primaryRoot

    [pscustomobject]@{
        CurrentRoot = $currentRoot
        PrimaryRoot = $primaryRoot
        InstallRoot = $primaryRoot
        LocalDir = Join-Path $primaryRoot 'publish/local'
        BackupDir = Join-Path $primaryRoot 'publish/local.previous'
        UsesGitPrimary = $true
    }
}

# The installation that already exists, in the order the setup window uses: the registered
# uninstall entry, then a recognisable installation at either known root. The default for a
# new install moved under Programs, so an installation still at the former root has to keep
# being found rather than being left behind or installed over a second time.
function Get-ManagedInstallRoot {
    $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
    $roots = @((Join-Path $localAppData 'Programs/CycleArc'), (Join-Path $localAppData 'CycleArc'))
    $uninstall = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CycleArc' -ErrorAction SilentlyContinue
    $fromRegistry = ''
    if ($uninstall) {
        try { $fromRegistry = [string]$uninstall.InstallLocation } catch { }
    }
    if (![string]::IsNullOrWhiteSpace($fromRegistry)) {
        return ConvertTo-InstallAbsolutePath $fromRegistry
    }
    foreach ($root in $roots) {
        $current = Join-Path $root 'current/CycleArc.exe'
        $updater = Join-Path $root 'Update.exe'
        if ((Test-Path -LiteralPath $current -PathType Leaf) -and (Test-Path -LiteralPath $updater -PathType Leaf)) {
            return ConvertTo-InstallAbsolutePath $root
        }
    }
    $null
}

function Test-ManagedInstallRoot([string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Root)) { return $false }
    $full = ConvertTo-InstallAbsolutePath $Root
    (Test-Path -LiteralPath (Join-Path $full 'current/CycleArc.exe') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $full 'Update.exe') -PathType Leaf)
}

function New-InstallLease {
    param(
        [Parameter(Mandatory)][string]$InstallRoot,
        [Parameter(Mandatory)][string[]]$AllowedRoots
    )
    $root = Assert-InstallPath -Path $InstallRoot -AllowedRoots $AllowedRoots
    $publish = Join-Path $root 'publish'
    Assert-InstallPath -Path $publish -AllowedRoots $AllowedRoots | Out-Null
    if (!(Test-Path -LiteralPath $publish -PathType Container)) {
        New-Item -ItemType Directory -Path $publish -Force | Out-Null
        Assert-InstallPath -Path $publish -AllowedRoots $AllowedRoots | Out-Null
    }
    $leasePath = Join-Path $publish '.dev-run.install.lock'
    if (Test-Path -LiteralPath $leasePath) { Assert-InstallPath -Path $leasePath -AllowedRoots $AllowedRoots | Out-Null }
    try {
        return [IO.File]::Open($leasePath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
    catch [IO.IOException] {
        throw "Another dev-run installation is using $leasePath"
    }
}

function Copy-ValidatedExecutable {
    param(
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$DestinationDirectory,
        [Parameter(Mandatory)][string[]]$SourceRoots,
        [Parameter(Mandatory)][string[]]$DestinationRoots
    )
    $source = Assert-InstallPath -Path $SourcePath -AllowedRoots $SourceRoots
    if ([IO.Path]::GetFileName($source) -cne 'CycleArc.exe' -or !(Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Only the validated CycleArc.exe may be copied: $source"
    }
    $sourceItem = Get-Item -LiteralPath $source
    if ($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Executable is a reparse point: $source" }
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    $destination = Assert-InstallPath -Path $DestinationDirectory -AllowedRoots $DestinationRoots
    if (!(Test-Path -LiteralPath $destination -PathType Container)) {
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Assert-InstallPath -Path $destination -AllowedRoots $DestinationRoots | Out-Null
    }
    if (@(Get-ChildItem -LiteralPath $destination -Force).Count -ne 0) {
        throw "Install staging directory must be empty: $destination"
    }
    $target = Join-Path $destination 'CycleArc.exe'
    Copy-Item -LiteralPath $source -Destination $target -Force
    Assert-InstallPath -Path $target -AllowedRoots $DestinationRoots | Out-Null
    $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sourceHash -cne $targetHash) { throw "Copied CycleArc.exe hash mismatch ($sourceHash vs $targetHash)" }
    $files = @(Get-ChildItem -LiteralPath $destination -File -Recurse)
    $directories = @(Get-ChildItem -LiteralPath $destination -Directory -Recurse)
    if ($files.Count -ne 1 -or $files[0].Name -cne 'CycleArc.exe' -or $directories.Count -ne 0) {
        throw "Install staging must contain exactly CycleArc.exe: $destination"
    }
    $target
}

function Invoke-InstallRetry([scriptblock]$Action, [int]$Attempts = 30, [int]$DelayMilliseconds = 500) {
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try { & $Action; return }
        catch {
            if ($attempt -eq $Attempts) { throw }
            Start-Sleep -Milliseconds $DelayMilliseconds
        }
    }
}

function Resolve-InstalledExecutable([string]$Directory) {
    # Exact legacy filenames are required to restore an earlier installation after a failed upgrade.
    foreach ($name in @('CycleArc.exe', 'CodexMeter.exe', 'prometer.exe')) {
        $candidate = Join-Path $Directory $name
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw 'No supported executable in the installation directory'
}

function Install-StagedApp {
    param(
        [string]$StagingDir, [string]$LocalDir, [string]$BackupDir,
        [scriptblock]$Validate,
        [scriptblock]$Move = { param($Source, $Destination) Move-Item -LiteralPath $Source -Destination $Destination },
        [scriptblock]$Start = {
            param($Directory)
            $exe = Resolve-InstalledExecutable $Directory
            $running = Start-Process -FilePath $exe -ArgumentList @('--show') -WorkingDirectory $Directory -WindowStyle Hidden -PassThru
            Start-Sleep -Seconds 2
            if ($running.HasExited) { throw "CycleArc exited at startup: $($running.ExitCode)" }
            Write-Host "CycleArc running: $exe (PID $($running.Id))"
        },
        [int]$Attempts = 30, [int]$DelayMilliseconds = 500
    )
    $StagingDir = ConvertTo-InstallAbsolutePath $StagingDir
    $LocalDir = ConvertTo-InstallAbsolutePath $LocalDir
    $BackupDir = ConvertTo-InstallAbsolutePath $BackupDir
    Assert-InstallSameVolume -Paths @($StagingDir, $LocalDir, $BackupDir)
    $oldMoved = $false
    $newMoved = $false
    $hadLocal = Test-Path -LiteralPath $LocalDir
    try {
        foreach ($target in @($StagingDir, $LocalDir, $BackupDir)) { & $Validate $target }
        if (Test-Path -LiteralPath $BackupDir) {
            Invoke-InstallRetry { & $Validate $BackupDir; Remove-Item -LiteralPath $BackupDir -Recurse -Force } $Attempts $DelayMilliseconds
        }
        if ($hadLocal) {
            Invoke-InstallRetry {
                & $Validate $LocalDir
                & $Validate $BackupDir
                & $Move $LocalDir $BackupDir
            } $Attempts $DelayMilliseconds
            $oldMoved = $true
        }
        Invoke-InstallRetry {
            & $Validate $StagingDir
            & $Validate $LocalDir
            & $Move $StagingDir $LocalDir
        } $Attempts $DelayMilliseconds
        $newMoved = $true
        & $Start $LocalDir
    }
    catch {
        $failure = $_
        # Never restore a stale backup if moving the current installation failed.
        if ($newMoved) {
            Invoke-InstallRetry {
                & $Validate $LocalDir
                & $Validate $StagingDir
                & $Move $LocalDir $StagingDir
            } $Attempts $DelayMilliseconds
        }
        if ($oldMoved) {
            Invoke-InstallRetry {
                & $Validate $BackupDir
                & $Validate $LocalDir
                & $Move $BackupDir $LocalDir
            } $Attempts $DelayMilliseconds
        }
        if ($hadLocal) { & $Start $LocalDir }
        throw $failure
    }
}
