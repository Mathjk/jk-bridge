# JK Bridge 故障排查

**简体中文** · [English](TROUBLESHOOTING.md)

建议从外向内检查：操作系统依赖、Tailscale 连通性、一次性配对、SSH 隧道，最后才是 Linux 网页。JK Bridge 隧道可以成功连接而某一个网页仍然停止，因此要区分“隧道失败”和“服务健康检查失败”。

> [!NOTE]
> 稳定版 v2.1.0 只使用便携 Windows 软件和一次性 Linux 助手。尚未发布的
> 2.2.0/vNext 开发版本
> 中出现的 Agent 控件、Service Profile 支持或安装器源码，不代表 Linux Agent
> 或受支持的端到端登记流程已经发布。本地标为 `2.2.0` 的产物只是开发/验收
> 构建，不是稳定 Release。

## 先收集一份安全的基础状态

Windows 解压目录中的 PowerShell：

```powershell
& "$env:ProgramFiles\Tailscale\tailscale.exe" status
Get-Command ssh, ssh-keygen
.\Verify-JKBridge.ps1 -PortExpectation Any
```

Linux 上以 JK Bridge 普通用户运行：

```bash
tailscale status
python3 ~/jkbridge-linux.py status
sudo sshd -t
```

不要公开原始配对码、`config.json`、私钥、Tailscale JSON 或 `authorized_keys`。分享诊断前删除主机名、tailnet 名称、用户名、IP、指纹和个人路径。

## Windows 软件无法打开

1. 按照[安装指南](INSTALLATION.zh-CN.md)核对 ZIP 哈希；
2. 完整解压 ZIP，不要从资源管理器的压缩包预览中直接运行 EXE；
3. 如果 Windows 标记了互联网下载文件，可先在 ZIP“属性”中选择“解除锁定”再解压，或运行：

   ```powershell
   Unblock-File .\JKBridge.exe
   ```

4. 从 PowerShell 启动一次；如果异常退出，再查看 `%LOCALAPPDATA%\JKBridge\logs`。

当前 EXE 未签名，因此 SmartScreen 可能显示未知发布者。Release 哈希核验可以确认文件完整性，但不会关闭 SmartScreen。

## 设置页更新不可用或失败

- Preview 1 没有直接更新入口。Preview 2/3 若旧的一步式入口卡住，请从官方
  Preview 4 Release 手工下载安装器和 `.sha256`，或先从托盘显式退出旧版再运行
  已经校验的安装器。
- Preview 4/5 强制直连 GitHub。如果能检查 Release、但在依赖代理的网络中下载
  安装器超时，请手工安装一次 Preview 6。Preview 6 使用 Windows/.NET 系统代理，
  同时继续执行精确主机、重定向、大小和 SHA-256 校验。
- 必须先点“手动检查官方 Release”。只有所选 Release 同时包含精确命名的 EXE
  和 `.sha256` 时，“下载并校验更新”才会出现。
- 下载页会显示字节数、百分比和校验阶段；可点“取消当前更新操作”。哈希不匹配、
  不受信任的跳转、取消或超时都会安全停止，不会启动安装器或退出 JK Bridge。
- 校验完成后再点“开始安装（将退出 JK Bridge）”。普通 Windows 安装向导成功
  启动后，JK Bridge 才安全退出；真实安装进度由向导显示。若向导启动失败，应用
  会保持运行，可重试或使用界面显示的官方 Release 页面手工安装。

## Tailscale 未安装、未登录或看不到 Linux

两端分别运行 `tailscale status`，确认：

- 后端状态正在运行；
- 两台设备位于同一 tailnet；
- Linux 设备在线；
- tailnet ACL/grants 允许 Windows 访问 Linux；
- 设备审批、密钥到期和主机防火墙没有阻断。

登录账号可以不同。如果两台设备误入了不同 tailnet，应退出错误账号，或把另一账号/设备邀请到目标 tailnet。

Windows 还可运行：

```powershell
& "$env:ProgramFiles\Tailscale\tailscale.exe" ping <Linux-MagicDNS-名称>
```

JK Bridge 保存的是精确 MagicDNS 名称。只有 Tailscale IP 改变不需要重新配对。

## 提示“OpenSSH 需要处理”或找不到 OpenSSH

JK Bridge 在 Windows 需要 `ssh.exe` 和 `ssh-keygen.exe`：

```powershell
Get-Command ssh, ssh-keygen
Get-WindowsCapability -Online | Where-Object Name -Like 'OpenSSH.Client*'
```

如果没有安装，以管理员 PowerShell 运行：

```powershell
Add-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0
```

重新打开 PowerShell 和 JK Bridge。不要为这个问题安装 Windows OpenSSH Server。

## 概览显示“隧道需要处理：未启动”

这只是一个状态，并不能证明 `ssh.exe` 崩溃。按顺序检查：

1. “设备配置”中已经有完整的 Linux 主机、用户、密钥路径和固定主机密钥；
2. Tailscale 与 OpenSSH 检查通过；
3. 点击“连接”，或者完成“配对并连接”；
4. 所有已启用的 Windows 本地端口处于空闲；
5. 在“诊断”中阅读最新的阻断项目。

已配置设备在连接前可运行：

```powershell
.\Verify-JKBridge.ps1 -PortExpectation Free
```

软件显示连接后运行：

```powershell
.\Verify-JKBridge.ps1 -PortExpectation Listening -TestServices
```

## 配对码被拒绝或超时

- 使用以后用于 SSH 的普通 Linux 用户运行 `pair`，不要加 `sudo`；
- Windows 完成前不要关闭 Linux 终端；
- 完整复制 `jkbridge://...`，不要增加空格或换行；
- 配对码 10 分钟或成功一次后失效，不要重复使用旧码；
- 确认 Windows 能通过 Tailscale 到达 Linux MagicDNS 节点；
- 检查主机防火墙是否阻止脚本显示的临时端口；
- 确认 Windows 系统时间基本正确。

临时服务只监听 Linux 的 Tailscale IPv4，不监听普通局域网和公网。

## Agent 登记不可用或无法完成

稳定版 v2.1.0 没有 Agent 登记。vNext 源码现在已有开发版 Linux Agent、
用户级 systemd 打包资产、打包检查和已接线的 Windows 登记客户端。这些资产
尚未完成实机安装/升级/回滚与干净 Windows 验收，也不是稳定 Agent Release。
如果开发流程不完整或失败，请改用“一次性配对”：

```bash
python3 ~/jkbridge-linux.py pair
```

不能把 Agent 地址替换为公网 URL、普通局域网通配监听、Tailscale Funnel、
SOCKS 代理、Exit Node 或子网路由。也不要通过放宽 `authorized_keys` 绕过
Agent 管理。Agent 是不依赖 AI 或模型 API 的确定性本地程序；Agent 停止应当
只影响管理功能，不应影响已经授权的 SSH 隧道。

vNext Setup 页面现在已有明确的“发现 Agent”按钮，背后使用有界、只读发现引擎。
它不会扫描网段或登记设备，只检查 Tailscale 当前已知的在线 peer 的 `45871`
端口，并要求节点、MagicDNS、tailnet、协议和 Agent 版本声明一致。单个结果只会
预选，多个结果必须手选；两种情况都须再点“使用所选地址”，然后填写短期一次性
代码并明确登记。如果没有可信结果，检查两端是否处于预期 tailnet、Agent 是否
监听 Tailscale IPv4 端口，再重试或手工输入该精确地址。该接线流程仍未完成干净
Windows 验收。

任意已登记设备都可以使用认证 `GET /v1/me` 回执与 `/v1/services` 清单手动
同步自己的服务选择。管理员还可以刷新完整设备列表、在明确确认后撤销所选的
非当前设备，并生成一枚 10 分钟有效、仅用一次、只能添加一台普通设备的码。
如果“生成新设备添加码”不可用或被拒绝，先同步 Agent 状态并确认当前设备仍是
管理员；如果目标提示已过期或已使用，请重新生成，不要把码保存到配置、笔记、
截图或命令历史。第一台管理员仍须使用 Linux 初始化/恢复时打印的管理员码。
普通设备只能看到自己，不能生成添加码、列出/撤销其他设备或管理中继；Windows
界面也拒绝撤销当前设备自身。

Preview 1 还存在一个显示问题：Linux Agent 会把 Tailscale 登录名规范化为小写，
而 Windows 可能保留例如 `Mathjk@github` 的字母大小写，导致有效管理员被误报为
“当前设备身份不一致”，角色和添加码也不会显示。Preview 2 已改为账号名大小写
无关比较，同时继续严格核对设备 ID、Tailscale 节点 ID 和 SSH 公钥指纹。出现该
症状时覆盖安装 Preview 2 即可，不需要撤销或重新登记当前管理员设备。

撤销会阻止该设备今后的 Agent 请求和新 SSH 认证，但不能终止已经连接的隧道；
如果需要立即切断，还要在对应 Windows 断开，或另外结束其 SSH 会话。

## Linux 中继计划被阻止，或无法点击应用

vNext 的“高级：Linux 可达目标”已在源码实现，但不属于稳定版 v2.1.0，也尚未
完成 Linux 实机和干净 Windows 验收。“生成计划并探测”是只读操作；只有成功
完成并另外勾选确认后，应用按钮才会可用。

按预览中的具体原因检查：

- Linux 包必须把 `jkbridge_relay.py` 放在 `jkbridge_agent.py` 同一目录；
- 必须有 `/usr/bin/socat` 和可用的用户级 systemd 会话；
- 所选 Linux 中继端口必须未被占用，Linux 也必须能连到明确的目标 DNS/IPv4
  与 TCP 端口；
- 目标只能是一个主机和端口，不能是 URL、通配符、网段、IPv6、广播/组播、
  UDP、SOCKS、Funnel、Exit Node 或子网路由；
- 如果已有同名但不受 Agent 管理的 `jkbridge-relay-<id>.service`，Agent 会
  主动阻止，而不会覆盖或删除它。

任何字段变更，或 Linux/服务清单/设备状态变化，都会让旧计划失效。应重新生成
并核对计划，不要重用旧 token。如果应用超时或结果不确定，先同步 Agent 清单，
并在 Linux 本机检查用户级 unit，再重新规划；不要盲目重复写操作。受管 unit
只监听 Linux `127.0.0.1`，不能用 `0.0.0.0` 或宽泛 `permitopen` 绕过失败。

HTTPS 中继是透明 TCP；TCP 探测成功不代表证书、SNI、Cookie 域、重定向或应用
主机检查一定会接受主机名为 `127.0.0.1` 的 Windows 网址。

## 升级后提示“需要重新配对”

v2.1 的 schema v3 会核对“当前启用的 Linux 远端端口”与“上次配对实际返回的授权端口”。v1/v2 没有保存这份记录，所以升级后需要重新配对一次。设备和服务配置仍然保留；在 Linux 重新运行 `pair`，并在 Windows 保留原专用密钥路径即可。

之后只有新增或改变 Linux 远端端口才会阻断连接。提示会列出缺少的端口；只改变 Windows 本地端口不需要重新授权。

## 出现重复公钥或托管条目警告

如果同一公钥已经存在于非托管 `authorized_keys` 条目中，JK Bridge 会拒绝再加一条受限副本，因为旧条目可能仍然拥有更大权限。

```bash
python3 ~/jkbridge-linux.py status
```

V2 会自动迁移一条完整、严格受限的 V1 条目；个人无限制密钥、残缺条目、重复条目或只模仿注释的条目都会被拒绝。此时应在 Windows 选择一个新的专用密钥路径并重新配对，不要为了消除警告而删除无关的个人 SSH 密钥。

如需人工清理，先在 Linux 本机备份并检查 `~/.ssh/authorized_keys`。JK Bridge 自己的备份位于 `~/.ssh/jkbridge-backups/`。

## SSH 拒绝公钥认证

Linux 运行：

```bash
python3 ~/jkbridge-linux.py status
ls -ld ~/.ssh
ls -l ~/.ssh/authorized_keys
sudo sshd -T | grep -E '^(pubkeyauthentication|allowtcpforwarding)'
```

这些文件应属于当前普通用户；`~/.ssh` 通常为 `0700`，`authorized_keys` 为 `0600`。服务器必须启用公钥认证，TCP 转发必须为 `yes` 或 `local`。

不要把权限放宽成 `0777`、启用密码降级，或把托管条目改成无限制公钥。重新配对可以安全重建托管条目。

## SSH 主机密钥不匹配

立即停止。固定密钥的变化与 Tailscale IP 变化无关。Linux 重装或主动轮换 SSH 主机密钥可能是合理原因，但未核验的不匹配也可能表示连接了错误主机或受到拦截。

在 Linux 本机控制台确认变更；如果确实合法，重新运行一次性配对更新 Windows 配置。不要设置 `StrictHostKeyChecking=no`、使用 `accept-new`，也不要通过删除校验文件来跳过重新配对。

## Windows 本地端口被占用

只查找占用者，不自动结束进程：

```powershell
Get-NetTCPConnection -State Listen -LocalPort 8080 | Select-Object LocalAddress,LocalPort,OwningProcess
Get-Process -Id <OwningProcess>
```

关闭已确认的应用，或给 JK Bridge 换一个 Windows 本地端口。每个已启用服务必须使用不同端口，而且监听地址始终保持 `127.0.0.1`。

新的浏览器地址必须使用更换后的 **Windows 端口**。例如 Windows `8080` 被
占用，你明确改用 `18080`，而 Linux 仍监听 `80`，应打开：

```text
http://127.0.0.1:18080/project -> Linux 127.0.0.1:80
```

只把 `8080` 改成 `18080` 并同步修改回环浏览器网址，不需要重新取得 Linux
授权。不要为了处理 Windows 端口冲突而修改 Linux 远端端口。vNext 简单编辑器
计划自动生成网址并建议空闲端口，但不能静默更改稳定地址；v2.1.0 用户仍需手动
更新并核对明确网址。

## 隧道已连接，但某个网页打不开

在 Linux 检查完全相同的目标：

```bash
curl -v http://127.0.0.1:<远端端口>/<路径>
ss -ltnp | grep ':<远端端口>\b'
```

常见原因：网页没有启动、实际端口不同、只监听在另一个容器/网络命名空间、Windows URL 路径错误、网页要求 HTTPS，或健康响应很慢/特殊。

JK Bridge 负责 TCP 转发，不会修复网页路由、登录、TLS、容器或反向代理配置。

如果服务使用自签名 HTTPS，而端口和浏览器访问本身正常，可在“服务”编辑器把健康方式改为“TCP 端口”。这只验证本地转发能建立 TCP 连接，不替浏览器信任证书。即使 HTTP/TLS 健康检查失败，只要 SSH 转发存在，“打开”仍然可用；浏览器中的证书决定仍由你完成。

## 新服务提示不允许转发

Linux 公钥使用精确 `permitopen` 白名单。在 Windows 启用服务，停止当前隧道，在 Linux 再运行 `pair`，然后使用原密钥重新配对。托管条目会加入新的 Linux 目标端口。

只改变 Windows 本地端口不需要重新授权；改变或新增 Linux 远端端口才需要。

## 重启或休眠恢复后没有重连

- 确认登录后 Tailscale 自己正在运行并保持登录；
- 打开“随 Windows 启动”后不要移动 `JKBridge.exe`，启动项保存的是绝对路径；
- 手工启动一次软件并查看“诊断”；
- 休眠时软件会主动停止隧道，恢复后重新预检，请给 Tailscale 一点恢复时间；
- Linux Tailscale IP 改变属于正常情况，不需要重新配对。

## 重置或撤销访问

只列出和删除 JK Bridge 托管的 Linux 授权：

```bash
python3 ~/jkbridge-linux.py status
python3 ~/jkbridge-linux.py remove --fingerprint 'SHA256:...'
```

如需完全重置 Windows，先退出软件、在 Linux 撤销公钥并关闭开机启动，再删除 `%LOCALAPPDATA%\JKBridge` 和“设备配置”显示的专用密钥。未确认密钥确为 JK Bridge 专用前，不要删除。

## 复制配置或导入服务后，新电脑仍没有授权

不要把整个 `config.json` 或私钥迁移到另一台 Windows。授权回执属于旧密钥；
多台设备共享一个私钥也会导致无法独立撤销。新电脑应使用自己的密钥配对，验证
成功后再撤销旧指纹。

vNext 软件已有原生 Service Profile 文件选择器：导出只写入服务白名单；导入
先进行只读校验和差异预览，并且只有再次明确确认才应用。这个源码实现不属于
v2.1.0，仍需完成干净 Windows 验收。

如果已经无法导出 Service Profile，2.2.0 开发版导入器也能读取精确的
v2.1/schema v3 `config.json` 作为旧格式来源。它会验证旧文档，但只把 Services
投影到预览；旧服务器、tailnet、行为设置、私钥路径、授权回执和设备状态全部
丢弃。不能用旧文件覆盖新电脑配置，并且仍要把该旧格式来源当作敏感文件处理。

档案刻意不包含私钥、配对码、设备凭据、服务器身份或授权回执，因此导入服务
定义永远不能直接取得 Linux 授权。如果预览列出新的 Linux 远端端口，应在可用
时完成经过验证的 Agent 同步，或使用一次性配对。取消文件选择或不勾选确认不会
产生更改；如果预览后本机服务已经变化，应重新选择并预览文件。

## 仍然无法解决

可[提交 GitHub Issue](https://github.com/Mathjk/jk-bridge/issues/new/choose)，并提供：

- JK Bridge 与 Windows 版本；
- Linux 发行版与 OpenSSH 版本；
- 失败的准确步骤；
- 已脱敏的 `Verify-JKBridge.ps1` 和 Linux `status` 输出；
- Tailscale ping、SSH 隧道和 Linux 本机 `curl` 是否分别成功。

如果怀疑安全漏洞或凭据泄露，请使用 [SECURITY.md](../SECURITY.md) 的私密流程，不要公开提交 Issue。
