# Security policy

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability, leaked credential, authentication bypass, unsafe forwarding rule, host-key validation problem, or privacy exposure.

Use [GitHub private vulnerability reporting](https://github.com/Mathjk/jk-bridge/security/advisories/new). Include the affected version, reproduction steps, impact, and a minimal redacted proof of concept. Do not include real private keys, pairing codes, passwords, Tailscale credentials, or unrelated personal data.

The maintainer will acknowledge a usable report when it is reviewed, investigate it privately, and coordinate disclosure after a fix or documented mitigation is available. No bounty or response-time guarantee is currently offered.

## Supported versions

| Version | Security updates |
|---|---|
| Latest GitHub Release | Supported |
| Older releases and untagged builds | Update to the latest release before reporting behavior |

The project's threat model, trust boundaries, key restrictions, and revocation procedure are documented in [docs/SECURITY.md](docs/SECURITY.md).

## 中文

怀疑存在安全漏洞、凭据泄露、认证绕过、危险转发规则、主机密钥校验问题或隐私泄露时，请勿提交公开 Issue。请使用 [GitHub 私密漏洞报告](https://github.com/Mathjk/jk-bridge/security/advisories/new)，并提供受影响版本、复现步骤、影响和已脱敏的最小证明。不要提交真实私钥、配对码、密码、Tailscale 凭据或无关个人数据。
