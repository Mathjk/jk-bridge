#Requires -Version 5.1
#Requires -RunAsAdministrator

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$capabilityName = 'OpenSSH.Client~~~~0.0.1.0'
$capability = Get-WindowsCapability -Online -Name $capabilityName

if ($null -eq $capability) {
    throw "Windows capability '$capabilityName' was not found."
}

if ($capability.State -ne 'Installed') {
    Write-Host 'Installing the Windows OpenSSH Client optional feature...'
    $capability = Add-WindowsCapability -Online -Name $capabilityName
}

if ($capability.State -ne 'Installed') {
    $capability = Get-WindowsCapability -Online -Name $capabilityName
}

if ($capability.State -ne 'Installed') {
    throw "Windows reported OpenSSH Client state '$($capability.State)' after installation."
}

$openSshDirectory = Join-Path $env:WINDIR 'System32\OpenSSH'
$sshExecutable = Join-Path $openSshDirectory 'ssh.exe'
$sshKeygenExecutable = Join-Path $openSshDirectory 'ssh-keygen.exe'

if (-not (Test-Path -LiteralPath $sshExecutable -PathType Leaf) -or
    -not (Test-Path -LiteralPath $sshKeygenExecutable -PathType Leaf)) {
    throw 'OpenSSH Client is installed, but ssh.exe or ssh-keygen.exe is not available yet. Restart Windows and try JK Bridge again.'
}

Write-Host 'Windows OpenSSH Client is ready.'
