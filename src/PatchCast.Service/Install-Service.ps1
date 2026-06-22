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

# New-Service does not grant the "Log on as a service" right, so a service set to
# run as a specific user fails to start with Error 1069 (logon failure). Grant the
# right explicitly via the LSA policy API (idempotent).
Add-Type @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class LogonAsServiceRight
{
    [StructLayout(LayoutKind.Sequential)]
    struct LSA_UNICODE_STRING { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    struct LSA_OBJECT_ATTRIBUTES { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public int Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern uint LsaOpenPolicy(IntPtr SystemName, ref LSA_OBJECT_ATTRIBUTES ObjectAttributes, int DesiredAccess, out IntPtr PolicyHandle);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern uint LsaAddAccountRights(IntPtr PolicyHandle, byte[] AccountSid, LSA_UNICODE_STRING[] UserRights, int CountOfRights);
    [DllImport("advapi32.dll")] static extern uint LsaClose(IntPtr PolicyHandle);
    [DllImport("advapi32.dll")] static extern int LsaNtStatusToWinError(uint Status);
    const int POLICY_ALL_ACCESS = 0x00000FFF;

    public static void Grant(byte[] accountSid)
    {
        var attributes = new LSA_OBJECT_ATTRIBUTES();
        IntPtr policy;
        uint status = LsaOpenPolicy(IntPtr.Zero, ref attributes, POLICY_ALL_ACCESS, out policy);
        if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status));
        try
        {
            const string privilege = "SeServiceLogonRight";
            var rights = new LSA_UNICODE_STRING[1];
            rights[0].Buffer = Marshal.StringToHGlobalUni(privilege);
            rights[0].Length = (ushort)(privilege.Length * 2);
            rights[0].MaximumLength = (ushort)((privilege.Length + 1) * 2);
            try
            {
                status = LsaAddAccountRights(policy, accountSid, rights, 1);
                if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status));
            }
            finally { Marshal.FreeHGlobal(rights[0].Buffer); }
        }
        finally { LsaClose(policy); }
    }
}
"@

function Grant-LogonAsServiceRight([string]$Account) {
    $sid = (New-Object System.Security.Principal.NTAccount($Account)).Translate([System.Security.Principal.SecurityIdentifier])
    $sidBytes = New-Object byte[] $sid.BinaryLength
    $sid.GetBinaryForm($sidBytes, 0)
    [LogonAsServiceRight]::Grant($sidBytes)
}

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
    $newServiceParameters.Credential = Get-Credential -UserName $currentUser -Message "Enter this account's Windows PASSWORD (not a Windows Hello PIN). A service cannot log on with a PIN."
    Grant-LogonAsServiceRight -Account $newServiceParameters.Credential.UserName
    Write-Host "Granted 'Log on as a service' to $($newServiceParameters.Credential.UserName)."
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
