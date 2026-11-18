<#
    Creates (or replaces) the scheduled task that runs the inventory scan on this server.

    Run once on ldctools01 from an elevated PowerShell:

        # Regular service account: you're prompted for its password (stored by Task Scheduler, not by this script).
        .\Register-InventoryScanTask.ps1 -ServiceAccount 'CALEGIS\svc-account'

        # Group managed service account (name ends in $): no password needed.
        .\Register-InventoryScanTask.ps1 -ServiceAccount 'CALEGIS\gmsa-account$'

        # Rebuild servers.txt from AD before each scan (the folder mapz maps to):
        .\Register-InventoryScanTask.ps1 -ServiceAccount 'CALEGIS\svc-account' -AdServersScript '\\LDCSPS15\scripts$\get-adservers.ps1'

    Defaults: runs daily at 02:00 from D:\CT, stops after 4 hours, never two runs at once.
    The account needs "Log on as a batch job" on this server, plus the rights the scan itself needs
    (local admin on the scanned servers, VIEW SERVER STATE + VIEW ANY DATABASE on the SQL instances).
#>
param(
    [Parameter(Mandatory)]
    [string]$ServiceAccount,

    [string]$Folder = 'D:\CT',
    [string]$TaskName = 'MCPDashboard Inventory Scan',
    [string]$TaskPath = '\MCPDashboard\',

    [ValidateSet('Daily', 'Weekly')]
    [string]$Schedule = 'Daily',
    [DayOfWeek]$WeeklyOn = 'Sunday',
    [string]$At = '02:00',

    # UNC path of get-adservers.ps1 (what mapz maps to); the list is rebuilt from it before each scan.
    [string]$AdServersScript
)

$ErrorActionPreference = 'Stop'

$script = Join-Path $Folder 'Run-InventoryScan.ps1'
$required = @($script, (Join-Path $Folder 'MCPDashboard.InventoryScanner.exe'))
$required += if ($AdServersScript) { $AdServersScript } else { Join-Path $Folder 'servers.txt' }
foreach ($file in $required) {
    if (-not (Test-Path $file)) { throw "Not found: $file" }
}

$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument ("-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$script`"" + $(if ($AdServersScript) { " -AdServersScript `"$AdServersScript`"" })) `
    -WorkingDirectory $Folder

$trigger = if ($Schedule -eq 'Weekly') {
    New-ScheduledTaskTrigger -Weekly -DaysOfWeek $WeeklyOn -At $At
} else {
    New-ScheduledTaskTrigger -Daily -At $At
}

$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit (New-TimeSpan -Hours 4) `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries

$task = @{
    TaskName    = $TaskName
    TaskPath    = $TaskPath
    Action      = $action
    Trigger     = $trigger
    Settings    = $settings
    Description = "Scans the servers in $Folder\servers.txt$(if ($AdServersScript) { ', rebuilt from get-adservers.ps1 first' }) (OS, CPU, RAM, SQL Server, AGs, databases, IIS sites). Logs: $Folder\logs, results: $Folder\results."
    Force       = $true
}

if ($ServiceAccount.EndsWith('$')) {
    # gMSA: Task Scheduler fetches the password from AD.
    $principal = New-ScheduledTaskPrincipal -UserId $ServiceAccount -LogonType Password -RunLevel Highest
    Register-ScheduledTask @task -Principal $principal | Out-Null
} else {
    $credential = Get-Credential -UserName $ServiceAccount -Message "Password for $ServiceAccount (stored by Task Scheduler)"
    Register-ScheduledTask @task -User $credential.UserName -Password $credential.GetNetworkCredential().Password -RunLevel Highest | Out-Null
}

Write-Host "Task '$TaskPath$TaskName' registered: $Schedule at $At as $ServiceAccount."
Write-Host "Run it now with:  Start-ScheduledTask -TaskPath '$TaskPath' -TaskName '$TaskName'"
