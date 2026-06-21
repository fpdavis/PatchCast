#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$ServiceName = "PatchCast",
    [switch]$RunAsCurrentUser,
    [switch]$RunAsLocalSystem,
    [switch]$SkipFirewallRule
)

$ErrorActionPreference = "Stop"
$executablePath = Join-Path $PSScriptRoot "PatchCast.Service.exe"
$configurationPath = Join-Path $PSScriptRoot "appsettings.json"

if (-not (Test-Path -LiteralPath $executablePath)) {
    throw "PatchCast.Service.exe was not found beside this script: $executablePath"
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Service '$ServiceName' already exists. Run Uninstall-Service.ps1 before reinstalling it."
}

if ($RunAsCurrentUser -and $RunAsLocalSystem) {
    throw "Specify either -RunAsCurrentUser or -RunAsLocalSystem, not both."
}

$newServiceParameters = @{
    Name = $ServiceName
    BinaryPathName = '"' + $executablePath + '"'
    DisplayName = "PatchCast Audio Service"
    Description = "Streams the default Windows playback and microphone devices to authenticated PatchCast clients."
    StartupType = "Automatic"
}

if (-not $RunAsLocalSystem) {
    $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $newServiceParameters.Credential = Get-Credential -UserName $currentUser -Message "Enter the Windows password for the account that owns the audio devices."
}

New-Service @newServiceParameters | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null

if (-not $SkipFirewallRule) {
    $port = 4747
    if (Test-Path -LiteralPath $configurationPath) {
        $configuration = Get-Content -Raw -LiteralPath $configurationPath | ConvertFrom-Json
        if ($configuration.PatchCast.Port) {
            $port = [int]$configuration.PatchCast.Port
        }
    }
    $firewallRuleName = "$ServiceName TCP $port"
    if (-not (Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $firewallRuleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $port | Out-Null
    }
}

Start-Service -Name $ServiceName
Get-Service -Name $ServiceName | Format-Table Name, Status, StartType
Write-Host "PatchCast was installed and started."
