<#
    Runs MCPDashboard.InventoryScanner.exe for the scheduled task and keeps a log of each run.

    With -AdServersScript, the server list is rebuilt first: the script (get-adservers.ps1) is run from its own folder
    with -AdServersArgs (default -PrimaryNodes) and what it returns is written to servers.txt. If it fails, reports any
    error or returns nothing, the previous servers.txt is scanned instead and the log says so. Use the script's UNC path, not a mapped
    drive: drive mappings (mapz) don't exist in a scheduled task's session.

    Task Scheduler doesn't keep a program's console output, so this writes it to logs\scan-<yyyyMMdd-HHmmss>.log,
    and removes logs and results folders older than -KeepDays so D:\CT doesn't fill up. Each results folder also gets
    the servers.txt that was scanned and discrepancies.csv (new servers, servers no longer in the list, ...).

    After the scan the results are written to the dashboard database (appsettings.json); pass -NoDashboard to only
    write the result files.

    The exit code is passed through, so the task's "Last Run Result" shows it:
        0 = all good, 2 = some servers unreachable, 3 = some servers couldn't be written to the dashboard (see the log),
        1 = server list or appsettings.json connection string missing.
#>
param(
    [string]$Folder = $PSScriptRoot,
    [int]$KeepDays = 30,
    [switch]$NoDashboard,
    [string]$AdServersScript,
    [string[]]$AdServersArgs = @('-PrimaryNodes'),

    # Only build the list from -AdServersScript, write it to servers-adtest.txt and stop: no scan, servers.txt untouched.
    [switch]$ListOnly
)

# Not 'Stop': in Windows PowerShell 5.1 that turns any stderr line from the exe into a terminating error.
$ErrorActionPreference = 'Continue'

$exe = Join-Path $Folder 'MCPDashboard.InventoryScanner.exe'
$serverList = Join-Path $Folder 'servers.txt'
$logs = Join-Path $Folder 'logs'
$results = Join-Path $Folder 'results'
New-Item -ItemType Directory -Force -Path $logs | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$log = Join-Path $logs "scan-$stamp.log"
$output = Join-Path $results $stamp

function Write-Log([string]$message) {
    "$(Get-Date -Format 'HH:mm:ss') $message" | Out-File -FilePath $log -Encoding utf8 -Append
}

# Runs get-adservers in its own powershell.exe, from its own folder (as mapz + .\get-adservers.ps1 does). In this
# process it would run inside try { }, which turns errors it normally shrugs off (e.g. psping.dll not loading from the
# share) into ones that stop it. Each line comes back tagged: OUT <host>, WRN <warning>, ERR <error>.
$adServersCommand = @'
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
Set-Location -LiteralPath '{DIR}'
& '{SCRIPT}' {ARGS} 2>&1 3>&1 | ForEach-Object {
    if ($_ -is [System.Management.Automation.ErrorRecord]) { "ERR`t$(([string]$_) -replace '\s*\r?\n\s*', ' ')" }
    elseif ($_ -is [System.Management.Automation.WarningRecord]) { "WRN`t$($_.Message)" }
    elseif ($_ -is [string]) { "OUT`t$_" }
    else {
        $item = $_
        $property = 'DNSHostName', 'ComputerName', 'Name' | Where-Object { $item.PSObject.Properties[$_] -and $item.$_ } | Select-Object -First 1
        "OUT`t$(if ($property) { $item.$property } else { $item })"
    }
}
exit 0
'@

# Errors that don't affect the list: psping is only used by get-adservers -Verify.
$ignoredErrors = 'psping'

if ($ListOnly) {
    if (-not $AdServersScript) { throw '-ListOnly needs -AdServersScript.' }
    $serverList = Join-Path $Folder 'servers-adtest.txt'
    Remove-Item $serverList -ErrorAction SilentlyContinue
}

if ($AdServersScript) {
    Write-Log "Building the server list: $AdServersScript $($AdServersArgs -join ' ')"
    try {
        # '-PrimaryNodes' stays a switch; values are quoted.
        $argText = ($AdServersArgs | ForEach-Object { if ($_.StartsWith('-')) { $_ } else { "'$($_.Replace("'", "''"))'" } }) -join ' '
        $command = $adServersCommand.
            Replace('{DIR}', (Split-Path $AdServersScript -Parent).Replace("'", "''")).
            Replace('{SCRIPT}', $AdServersScript.Replace("'", "''")).
            Replace('{ARGS}', $argText)
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        $lines = @(& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded 2>&1)
        $childExit = $LASTEXITCODE

        $hosts = @()
        $errors = 0
        foreach ($line in $lines) {
            $text = [string]$line
            if ($line -is [System.Management.Automation.ErrorRecord]) { $text = "ERR`t$text" }
            if ($text -match '^#< CLIXML|^<Objs ' -or -not $text.Trim()) { continue }
            $tag, $value = $text -split "`t", 2
            switch ($tag) {
                'OUT' { if ($value.Trim() -and $value.Trim() -notmatch '\s') { $hosts += $value.Trim() } }
                'WRN' { Write-Log "  get-adservers: $value" }
                'ERR' {
                    if ($value -match $ignoredErrors) { Write-Log "  get-adservers error (ignored): $value" }
                    else { Write-Log "  get-adservers ERROR: $value"; $errors++ }
                }
                default { Write-Log "  get-adservers: $text" }
            }
        }
        $hosts = $hosts | Sort-Object -Unique

        if ($childExit -ne 0 -or $errors -gt 0) {
            # E.g. if the cluster lookup fails, cluster names and AG listeners aren't filtered out and would be added as servers.
            Write-Log "WARNING: get-adservers failed (exit code $childExit, $errors error(s)); scanning the previous servers.txt instead."
        }
        elseif (@($hosts).Count -eq 0) {
            Write-Log "WARNING: the script returned no servers; scanning the previous servers.txt instead."
        }
        else {
            $hosts | Set-Content -Path $serverList -Encoding ascii
            Write-Log "$(Split-Path $serverList -Leaf) rebuilt: $(@($hosts).Count) server(s)."
        }
    }
    catch {
        Write-Log "WARNING: building the server list failed, scanning the previous servers.txt instead: $_"
    }
}

if ($ListOnly) {
    Get-Content $log
    exit $(if (Test-Path $serverList) { 0 } else { 1 })
}

Push-Location $Folder
try {
    if (Test-Path $serverList) {
        # Keep the list that was scanned with its results.
        New-Item -ItemType Directory -Force -Path $output | Out-Null
        Copy-Item $serverList (Join-Path $output 'servers.txt')
    }

    $exeArgs = @('--input', $serverList, '--output', $output)
    if (-not $NoDashboard) { $exeArgs += '--update-dashboard' }
    & $exe @exeArgs *>&1 |
        Out-File -FilePath $log -Encoding utf8 -Append
    $exitCode = $LASTEXITCODE
}
catch {
    $_ | Out-File -FilePath $log -Encoding utf8 -Append
    $exitCode = 1
}
finally {
    Pop-Location
}

# Clean up old runs.
$cutoff = (Get-Date).AddDays(-$KeepDays)
Get-ChildItem -Path $logs -Filter 'scan-*.log' -File -ErrorAction SilentlyContinue |
    Where-Object LastWriteTime -lt $cutoff | Remove-Item -Force
Get-ChildItem -Path $results -Directory -ErrorAction SilentlyContinue |
    Where-Object LastWriteTime -lt $cutoff | Remove-Item -Recurse -Force

exit $exitCode
