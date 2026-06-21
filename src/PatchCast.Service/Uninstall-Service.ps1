#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$ServiceName = "PatchCast",
    [switch]$KeepFirewallRule
)

$ErrorActionPreference = "Stop"
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }
    sc.exe delete $ServiceName | Out-Null
    Write-Host "PatchCast service removed."
} else {
    Write-Host "PatchCast service is not installed."
}

if (-not $KeepFirewallRule) {
    Get-NetFirewallRule -DisplayName "$ServiceName TCP *" -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule
}
