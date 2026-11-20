<#
    Builds the scanner and brings \\LDCTOOLS01\ct (D:\CT on LDCTOOLS01) up to date with this repo in one step.
    Run on your workstation from the repo folder:

        .\Deploy-Scanner.ps1                 # build, then copy every file that changed
        .\Deploy-Scanner.ps1 -Preview        # build, then only list what would change (copies nothing)
        .\Deploy-Scanner.ps1 -RunNow         # ...and start the scheduled scan right away (needs admin on LDCTOOLS01)
        .\Deploy-Scanner.ps1 -SkipBuild      # use what's already in .\publish

    (If scripts are blocked: powershell -ExecutionPolicy Bypass -File .\Deploy-Scanner.ps1)

    Deployed (copied only when different from the server's copy, compared by content):
        MCPDashboard.InventoryScanner.exe   built into .\publish
        appsettings.json                    connection string and discrepancy email settings
    Other files on the share (the .ps1 scripts, servers.txt, logs, results, backup) are never touched. When a change
    touches one of the scripts, add it to $deploy below for that deployment. Never add servers.txt: the job rebuilds
    it from AD every night, so the repo copy is older.

    Every file it replaces is first copied to <share>\backup\<yyyyMMdd-HHmmss> (the last 10 deployments are kept), so
    a bad deploy is rolled back by copying those files back.

    Stops without changing anything while the scan is running (the exe is in use then).
#>
param(
    [string]$Share = '\\LDCTOOLS01\ct',
    [switch]$Preview,
    [switch]$SkipBuild,
    [switch]$RunNow,
    [string]$Server = 'LDCTOOLS01',
    [string]$TaskName = '\MCPDashboard\MCPDashboard Inventory Scan',
    [int]$KeepBackups = 10
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$publish = Join-Path $repo 'publish'

# What goes to the server, and where each file comes from. Add a script here (e.g. 'Run-InventoryScan.ps1' =
# Join-Path $repo 'Run-InventoryScan.ps1') when a change needs it on the server.
$deploy = [ordered]@{
    'MCPDashboard.InventoryScanner.exe' = Join-Path $publish 'MCPDashboard.InventoryScanner.exe'
    'appsettings.json'                  = Join-Path $repo 'appsettings.json'
}

# 1. Build (the project file, not the .sln, so -o gives no NETSDK1194 warning).
if (-not $SkipBuild) {
    Write-Host 'Building...' -ForegroundColor Cyan
    & dotnet publish (Join-Path $repo 'MCPDashboard.InventoryScanner.csproj') -c Release -o $publish --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE). Nothing was deployed." }
}
foreach ($source in $deploy.Values) {
    if (-not (Test-Path $source)) { throw "Missing: $source. Build first (run without -SkipBuild)." }
}

# 2. Is the share there, and is the scan idle?
if (-not (Test-Path $Share)) { throw "Can't reach $Share. Check the share and your access to it." }
$targetExe = Join-Path $Share 'MCPDashboard.InventoryScanner.exe'
if (-not $Preview -and (Test-Path $targetExe)) {
    try {
        $stream = [System.IO.File]::Open($targetExe, 'Open', 'ReadWrite', 'None')
        $stream.Close()
    }
    catch {
        throw "The scan is running on $Server (the exe is in use). Nothing was deployed; try again when it has finished."
    }
}

# 3. Which files differ from the server's copy?
$changes = foreach ($name in $deploy.Keys) {
    $source = $deploy[$name]
    $target = Join-Path $Share $name
    $status = if (-not (Test-Path $target)) { 'new' }
              elseif ((Get-FileHash $source).Hash -ne (Get-FileHash $target).Hash) { 'updated' }
              else { 'unchanged' }
    [pscustomobject]@{ Name = $name; Source = $source; Target = $target; Status = $status }
}

Write-Host ''
foreach ($c in $changes) {
    $color = if ($c.Status -eq 'unchanged') { 'DarkGray' } else { 'Yellow' }
    Write-Host ("  {0,-10} {1}" -f $c.Status, $c.Name) -ForegroundColor $color
}
$toCopy = @($changes | Where-Object Status -ne 'unchanged')

if ($toCopy.Count -eq 0) {
    Write-Host "`n$Share is already up to date." -ForegroundColor Green
}
elseif ($Preview) {
    Write-Host "`nPreview: $($toCopy.Count) file(s) would be copied to $Share. Nothing was changed." -ForegroundColor Cyan
}
else {
    # 4. Back up what's being replaced, then copy.
    $backup = Join-Path (Join-Path $Share 'backup') (Get-Date -Format 'yyyyMMdd-HHmmss')
    foreach ($c in $toCopy | Where-Object Status -eq 'updated') {
        New-Item -ItemType Directory -Force -Path $backup | Out-Null
        Copy-Item $c.Target $backup
    }
    foreach ($c in $toCopy) {
        Copy-Item $c.Source $c.Target -Force
    }

    # Keep the newest few backups.
    Get-ChildItem (Join-Path $Share 'backup') -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -Skip $KeepBackups |
        Remove-Item -Recurse -Force

    Write-Host "`nDeployed $($toCopy.Count) file(s) to $Share." -ForegroundColor Green
    if (Test-Path $backup) { Write-Host "  Replaced files kept in $backup" }
}

# 5. Optionally start the scan now instead of waiting for tonight.
if ($RunNow -and -not $Preview) {
    & schtasks.exe /Run /S $Server /TN $TaskName | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Couldn't start '$TaskName' on $Server (needs admin rights there). It will run on its schedule tonight."
    }
    else {
        Write-Host "Scan started on $Server. Check the newest log in $Share\logs when it finishes."
    }
}
