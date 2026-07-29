# JK Bridge Windows installer

This directory adds an optional Inno Setup installer without replacing the
existing portable ZIP release. Both forms use the same self-contained
`win-x64` single-file application.

## User-visible behavior

The installer is per-user and installs under
`%LOCALAPPDATA%\Programs\JKBridge`. It does not need administrator rights for
the application itself. It creates a Start menu shortcut and offers unchecked
options for a desktop shortcut and current-user startup.

Before copying the application, the wizard checks for:

- Tailscale (`tailscale.exe`); and
- Windows OpenSSH Client (`ssh.exe` and `ssh-keygen.exe`).

If Tailscale is missing, the user may explicitly ask the installer to open the
official Tailscale download page. JK Bridge never downloads Tailscale, saves a
Tailscale credential, or signs in on the user's behalf. The installer detects
the application executable only; Tailscale sign-in and tailnet policy are
validated by Tailscale and JK Bridge at runtime.

If OpenSSH Client is missing, the user may explicitly select its installation.
After JK Bridge is installed, Windows shows a UAC consent dialog and a small
PowerShell helper adds only `OpenSSH.Client~~~~0.0.1.0`. Cancelling UAC or a
failed optional-feature operation does not roll back JK Bridge; the feature can
be installed later from Windows Settings. Silent installs never install Windows
features and never open external pages.

Uninstall removes the installed executable, shortcuts, and a startup value only
when that value still points to the installed executable. It deliberately
preserves `%LOCALAPPDATA%\JKBridge` and the dedicated key under the user's `.ssh`
directory. Removing an application must not silently revoke or destroy device
identity; revoke the device on Linux before deleting its data.

## Build prerequisites

- Windows 11 x64 (the installer itself targets Windows 11 x64)
- .NET 10 SDK selected by `global.json`
- Inno Setup 6.3 or later, including `ISCC.exe`
- PowerShell 5.1 or PowerShell 7

From the repository root:

```powershell
.\scripts\build-installer.ps1
```

The build publishes an isolated payload and creates:

```text
artifacts/installer/output/JKBridge-Setup-win-x64-<version>.exe
artifacts/installer/output/JKBridge-Setup-win-x64-<version>.exe.sha256
```

It does not call or modify `scripts/publish-win-x64.sh`; the portable ZIP build
and its checksums remain unchanged. Use `-SkipPublish` only when
`artifacts\installer\payload\JKBridge.exe` was already produced and validated.

## External code signing

The repository contains no certificate, private key, password, or signing
account. Pass an Inno Setup sign-tool definition at build time:

```powershell
.\scripts\build-installer.ps1 `
  -SignToolName 'jkbridge-sign' `
  -SignToolCommand 'signtool.exe sign /sha1 CERTIFICATE_THUMBPRINT /fd SHA256 /tr https://timestamp.example.invalid /td SHA256 $f'
```

Replace the placeholder thumbprint and timestamp URL in the release environment.
Prefer a certificate in the Windows certificate store, a hardware-backed key,
or a managed CI signing service. Do not put a PFX, password, real thumbprint, or
secret-bearing command in the repository. When configured, the same external
tool signs `JKBridge.exe`, the installer, and the generated uninstaller.

## CI integration

Keep the existing Linux ZIP job. Add a separate Windows job so failure of the
installer toolchain cannot silently replace or weaken the portable release:

```yaml
windows-installer:
  runs-on: windows-latest
  steps:
    - uses: actions/checkout@v7
    - uses: actions/setup-dotnet@v5
      with:
        global-json-file: global.json
    - name: Install Inno Setup
      run: choco install innosetup --yes --no-progress
    - name: Build installer
      shell: pwsh
      run: .\scripts\build-installer.ps1
    - uses: actions/upload-artifact@v7
      with:
        name: JKBridge-Setup-win-x64
        path: |
          artifacts/installer/output/*.exe
          artifacts/installer/output/*.sha256
        if-no-files-found: error
```

For a signed release, replace the example build step with the organization's
signing service or inject only non-secret sign-tool configuration. Never expose
certificate passwords on the command line. Run the normal repository tests and
privacy scan before this packaging job, then test install, upgrade, startup,
UAC cancellation, and uninstall on a clean Windows 11 x64 VM.
