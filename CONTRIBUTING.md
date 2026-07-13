# Contributing to JK Bridge

Thank you for improving JK Bridge. Documentation, tests, accessibility, diagnostics, and small focused fixes are all welcome.

## Before opening an issue

- Read the [installation guide](docs/INSTALLATION.md) and [troubleshooting guide](docs/TROUBLESHOOTING.md).
- Search existing issues.
- Remove hostnames, tailnet names, usernames, IP addresses, public keys, tokens, personal paths, and runtime configuration from screenshots and logs.
- Report security problems privately through [SECURITY.md](SECURITY.md), never in a public issue.

## Pull requests

1. Fork the repository and create a focused branch.
2. Keep each pull request limited to one coherent change.
3. Add or update tests for behavior changes.
4. Update both README languages when user-facing setup or behavior changes.
5. Run the required checks:

   ```bash
   dotnet test JKBridge.slnx -c Release
   python3 -m unittest discover -s tests/linux -p 'test_*.py'
   ./scripts/check-public-release.sh
   dotnet build JKBridge.slnx -c Release --no-restore -warnaserror
   ```

6. Explain the problem, security impact, test evidence, and any remaining Windows acceptance work in the pull request.

Do not commit generated `artifacts/`, `bin/`, `obj/`, runtime configuration, logs, known-hosts files, private keys, real public keys, or machine-specific identity.

Changes to pairing, SSH arguments, `authorized_keys`, key ACLs, host-key pinning, atomic writes, redaction, or release packaging need explicit security reasoning and regression tests. Read the [development guide](docs/DEVELOPMENT.md) first.

## 中文说明

提交 Issue 或 Pull Request 前，请先阅读[中文安装指南](docs/INSTALLATION.zh-CN.md)和[中文故障排查](docs/TROUBLESHOOTING.zh-CN.md)。截图、日志和命令输出必须删除主机名、tailnet、用户名、IP、公钥、令牌、个人路径与运行配置。涉及安全漏洞时请按照 [SECURITY.md](SECURITY.md) 私密报告，不要公开披露。
