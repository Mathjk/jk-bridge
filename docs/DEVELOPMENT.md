# JK Bridge development guide

This document is the shortest path from a clean clone to a verified release package. Read [Architecture](ARCHITECTURE.md), [Security](SECURITY.md), and the [pairing protocol](PAIRING_PROTOCOL.md) before changing trust boundaries or persistent formats.

## Toolchain

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), pinned by `global.json`
- Python 3.9 or later
- Bash, `ripgrep`, `sha256sum`, and `zip` for release packaging
- Windows 11 x64 with Tailscale and OpenSSH Client for final UI/runtime acceptance

The app can be built and tested on Linux. The release script cross-publishes a self-contained `win-x64` executable, but tray, startup, Job Object, suspend/resume, and real OpenSSH behavior still require Windows acceptance.

## Repository map

```text
src/JKBridge.Core/
  Configuration/       schema, validation, atomic persistence
  Diagnostics/         structured logging and redaction
  Health/              per-service HTTP health probes
  Models/              persistent configuration models
  Pairing/             pairing-code parsing and HTTP client
  Processes/           safe external-process abstraction
  Security/            Ed25519 public-key parsing
  Tailscale/           status parsing and exact peer matching
  Tunneling/           strict OpenSSH command construction

src/JKBridge.App/
  Platform/            Windows executable lookup, startup, Job Object, single instance
  Services/            app controller, pairing, Tailscale, SSH process, runtime state
  ViewModels/          UI state and commands
  Views/               Avalonia windows and pages
  Styles/              colors and shared controls
  Assets/              source icon and Windows icon resources

scripts/
  jkbridge-linux.py    no-install Linux pairing/status/revocation helper
  setup-windows.ps1   advanced V1/manual recovery tool
  verify-windows.ps1  Windows prerequisite and runtime diagnostics
  check-public-release.sh  privacy and credential gate
  publish-win-x64.sh  tested, checksummed release packaging

tests/
  JKBridge.Core.Tests/ .NET core, app-service, and view-model tests
  linux/               Python helper mutation and security tests
```

`JKBridge.Core` intentionally has no Avalonia dependency. The app layer owns UI and Windows integration; external commands are built as argument lists rather than shell-interpolated strings.

## Build and test

From the repository root:

```bash
dotnet restore JKBridge.slnx
dotnet build JKBridge.slnx -c Release --no-restore -warnaserror
dotnet test JKBridge.slnx -c Release --no-build
python3 -m unittest discover -s tests/linux -p 'test_*.py'
./scripts/check-public-release.sh
./scripts/check-doc-links.sh
```

If `markdownlint-cli` is installed, validate the documentation with the repository configuration:

```bash
markdownlint README*.md CONTRIBUTING.md SECURITY.md docs/*.md docs/decisions/*.md
```

Run the Avalonia app on the current development platform:

```bash
dotnet run --project src/JKBridge.App/JKBridge.App.csproj
```

Do not use a real production profile for automated tests. Tests must generate temporary paths and example identities.

## Package Windows x64

```bash
./scripts/publish-win-x64.sh
```

This command runs the privacy gate and both test suites before publishing. It produces:

```text
artifacts/JKBridge-win-x64.zip
artifacts/JKBridge-win-x64.zip.sha256
artifacts/delivery/JKBridge.exe
artifacts/delivery/SHA256SUMS
```

Every packaged file must be listed in `SHA256SUMS`. The `artifacts/` directory is ignored and must never be committed.

## Change rules by area

### Configuration

- Preserve schema migration and a safe unconfigured first-run state.
- Validate before persistence.
- Keep same-directory temporary writes, flush, backup, and atomic replacement.
- Never store pairing tokens, private-key contents, passwords, or fixed Tailscale IPs.

### Pairing and Linux authorization

- Pairing remains one-shot, size-limited, proxy-free, redirect-free, and Tailscale-interface-only.
- A marker/comment alone never proves an `authorized_keys` entry is managed.
- Destinations remain exact IPv4 loopback ports; no wildcard `permitopen` in the normal path.
- File ownership, symlink, locking, backup, mode, and atomic-write tests are mandatory.

### SSH tunnel

- Keep `StrictHostKeyChecking=yes`, the dedicated known-hosts file, and the pinned alias.
- Keep `BatchMode=yes`, `IdentitiesOnly=yes`, password/interactive authentication disabled, and `ExitOnForwardFailure=yes`.
- All listeners and destinations remain `127.0.0.1`.
- Never invoke an external command through a shell string.

### Diagnostics and UI

- User paths and deployment identity must stay redacted.
- Never log tokens, authorization headers, private keys, raw Tailscale JSON, environment dumps, or HTTP bodies.
- Blocking status text should say what failed and what the user can do next.

## Documentation checks

When behavior, prerequisites, package contents, or commands change:

1. update both `README.md` and `README.zh-CN.md`;
2. update both installation/troubleshooting languages where applicable;
3. update architecture, security, protocol, acceptance, or changelog documents as needed;
4. verify every relative link and every official external download URL;
5. check the rendered GitHub README, not only the Markdown source.

## Windows acceptance

Use [WINDOWS_ACCEPTANCE.md](WINDOWS_ACCEPTANCE.md) on a clean or representative Windows 11 x64 machine. At minimum test:

- first-run unconfigured setup;
- one-shot pairing and helper exit;
- two simultaneous forwards;
- tray hide/show/exit;
- optional startup after login;
- sleep/resume and Tailscale recovery;
- occupied port, offline peer, denied key, and host-key mismatch;
- revocation and safe re-pairing.

## Version and release flow

The repository uses semantic versions.

1. Update the app version and `CHANGELOG.md`.
2. Run all tests, privacy scan, package build, checksum verification, and Windows acceptance.
3. Commit the release state.
4. Create an annotated `vMAJOR.MINOR.PATCH` tag.
5. Push the commit and tag.
6. GitHub Actions rebuilds from the tag and attaches the ZIP and detached checksum.
7. Download the public assets, verify their checksum, and inspect their contents independently.

Never create a public tag from a dirty working tree or upload a locally configured runtime directory.
