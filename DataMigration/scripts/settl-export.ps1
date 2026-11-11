<#
.SYNOPSIS
    Nightly Settl export to a network share for the SonWin migration.

.DESCRIPTION
    Reads Settl only (never writes to it) and writes only to the share. Runs the OpsCli next to
    this script. Every step writes a log to -LogDir and ends with OK or NOT OK.

    Steps, in the order you use them:
      Trial       Export 5 customers to <Share>\<Env>-trial and check them. Safe to repeat.
      Run         Export every active customer to <Share>\<Env> and check the result.
      Check       Check the newest run on the share again, without exporting.
      Schedule    Register the nightly Run as a Windows scheduled task (run as administrator).
      Unschedule  Remove that scheduled task.

    Full guide: DataMigration/docs/settl-export-runbook.md

.EXAMPLE
    .\settl-export.ps1 Trial -Share \\fileserver\sonwin\settl-export

.EXAMPLE
    .\settl-export.ps1 Run -Share \\fileserver\sonwin\settl-export -PaymentCsv D:\settl\payment-agreements.csv

.EXAMPLE
    .\settl-export.ps1 Schedule -Env production -Share \\fileserver\sonwin\settl-export -PaymentCsv D:\settl\payment-agreements.csv
#>
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('Trial', 'Run', 'Check', 'Schedule', 'Unschedule')]
    [string] $Step,

    # The share folder. Each environment gets its own folder below it.
    [Parameter(Mandatory)]
    [string] $Share,

    [Alias('Env')]
    [ValidateSet('featuretest', 'production', 'production-hev', 'production-vhel')]
    [string] $Environment = 'featuretest',

    # The 'Payment Agreement' CSV exported from Settl. Optional; when given it must be less than 24 hours old.
    [string] $PaymentCsv,

    # Time of day for the nightly run (Schedule step).
    [string] $At = '02:15',

    [string] $Exe = (Join-Path $PSScriptRoot 'DataMigration.OpsCli.exe'),

    [string] $LogDir = 'C:\Services\DataMigration\data\settl-export-logs'
)

$ErrorActionPreference = 'Stop'
$TaskName = "Settl export to share ($Environment)"

function Invoke-OpsCli([string[]] $Arguments, [string] $Name) {
    if (-not (Test-Path $Exe)) { throw "OpsCli not found at $Exe. Deploy OpsCli, or pass -Exe <path to DataMigration.OpsCli.exe>." }
    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    $log = Join-Path $LogDir ('{0}-{1}-{2:yyyyMMdd-HHmmss}.log' -f $Name, $Environment, (Get-Date))
    Write-Host "> DataMigration.OpsCli.exe $($Arguments -join ' ')"
    # OpsCli logs progress to stderr. Windows PowerShell 5.1 would stop the script on the first
    # stderr line under 'Stop', so relax it here and pass every line on as plain text.
    $ErrorActionPreference = 'Continue'
    & $Exe @Arguments 2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath $log | Out-Host
    $code = $LASTEXITCODE
    Write-Host "  exit code $code, log: $log"
    return $code
}

function Get-ExportArguments([switch] $Trial) {
    $arguments = @('export-active-customers', '--env', $Environment, '--deliver-to', $Share)
    if ($Trial) { $arguments += @('--limit', '5') }
    if ($PaymentCsv) { $arguments += @('--payment-agreements', $PaymentCsv) }
    return $arguments
}

function Write-Result([int] $Code) {
    if ($Code -eq 0) { Write-Host 'OK' -ForegroundColor Green }
    else { Write-Host 'NOT OK - read the log above; the runbook lists what each message means' -ForegroundColor Red }
}

switch ($Step) {
    'Trial' {
        Write-Host "Trial: reads 5 customers from Settl $Environment, writes to $Share\$Environment-trial"
        $code = Invoke-OpsCli (Get-ExportArguments -Trial) 'trial'
        if ($code -eq 0) { $code = Invoke-OpsCli @('check-share-delivery', '--env', $Environment, '--deliver-to', $Share, '--trial') 'check' }
        Write-Result $code
        exit $code
    }
    'Run' {
        Write-Host "Run: reads every active customer from Settl $Environment, writes to $Share\$Environment"
        $code = Invoke-OpsCli (Get-ExportArguments) 'run'
        if ($code -eq 0) { $code = Invoke-OpsCli @('check-share-delivery', '--env', $Environment, '--deliver-to', $Share) 'check' }
        Write-Result $code
        exit $code
    }
    'Check' {
        $code = Invoke-OpsCli @('check-share-delivery', '--env', $Environment, '--deliver-to', $Share) 'check'
        Write-Result $code
        exit $code
    }
    'Schedule' {
        $runArguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" Run -Environment $Environment -Share `"$Share`""
        if ($PaymentCsv) { $runArguments += " -PaymentCsv `"$PaymentCsv`"" }
        $credential = Get-Credential -Message "Account the nightly job runs as. It needs Settl access (as when you run OpsCli) and write access to $Share."
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $runArguments -WorkingDirectory $PSScriptRoot
        $trigger = New-ScheduledTaskTrigger -Daily -At $At
        $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 6) -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
            -User $credential.UserName -Password $credential.GetNetworkCredential().Password -Force | Out-Null
        Write-Host "Scheduled '$TaskName' daily at $At as $($credential.UserName)."
        Write-Host "To run it once now: Start-ScheduledTask -TaskName '$TaskName'"
        Write-Result 0
    }
    'Unschedule' {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "Removed '$TaskName'."
        Write-Result 0
    }
}
