#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes PatchCast.Service to src\PatchCast.Service\publish.

.DESCRIPTION
    A reliable alternative to the Visual Studio Publish UI. VS stores its own
    target folder in FolderProfile.pubxml.user (_PublishTargetUrl) and can switch
    the profile to a "Web Deploy" type that has no folder location, which makes
    the output land in the wrong place. This script calls 'dotnet publish' with
    an explicit output directory so the result is always deterministic.

    Output is framework-dependent by default (requires the ASP.NET Core 9 Runtime
    on the target). Pass -SelfContained for a portable build that needs no runtime.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -SelfContained
#>
[CmdletBinding()]
param(
    [string]$Output,
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Output) { $Output = Join-Path $root 'src\PatchCast.Service\publish' }
$project = Join-Path $root 'src\PatchCast.Service\PatchCast.Service.csproj'
$selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }

Write-Host "Publishing $project"
Write-Host "  -> $Output (Release, win-x64, self-contained=$selfContainedValue)"
dotnet publish $project -c Release -r win-x64 --self-contained $selfContainedValue -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
Write-Host "Publish complete: $Output"
