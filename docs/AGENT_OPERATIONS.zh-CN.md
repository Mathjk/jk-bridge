# vNext Agent 多设备与管理员操作

**简体中文** · [English](AGENT_OPERATIONS.md)

> [!IMPORTANT]
> 本文描述尚未稳定发布的 v2.2.0/vNext Agent 流程。稳定版 v2.1.0 仍使用
> 一次性配对。每台 Windows 都必须拥有自己的 SSH 密钥和 Agent 凭据。

## 权限角色

| 角色 | 同步自身服务 | 生成普通设备添加码 | 查看/撤销设备 | 管理清单/中继 |
|---|---:|---:|---:|---:|
| `admin` | 可以 | 可以 | 可以，但不能撤销自己 | 可以 |
| `device` | 可以 | 不可以 | 不可以 | 不可以 |

第一台管理员和每一台替换管理员都必须使用 Linux 本机签发的管理员代码。
管理员 Windows 生成的添加码永远只能创建普通 `device`，不能把普通设备提升为
`admin`。

## 添加普通 Windows 设备

新设备需要 Windows 11 x64、处于同一且策略允许的 Tailnet 的 Tailscale、
Windows OpenSSH Client 和 JK Bridge 安装包。然后：

1. 在已登记的管理员 Windows 打开“设备配置”，刷新 Agent 状态，点击
   “生成新设备添加码”。
2. 把界面显示的 10 分钟、一次性代码直接交给新电脑。不要保存到笔记、截图、
   URL 或配置文件。
3. 新电脑明确点击“发现 Agent”，核对并选择候选，再点击“使用所选地址”。
4. 填写独立的设备名称和一次性代码，点击“使用 Agent 登记并连接”。
5. 同步服务并处理可能的 Windows 本地端口冲突。Windows 监听仍只位于
   `127.0.0.1`；Linux 授权仍只包含精确的 Linux 目标端口。

Linux Agent 完成一次性准备后，添加普通设备不需要再操作 Linux 终端。

## 添加或更换管理员

管理员权限不能靠复制配置转移，普通设备也不能原地提升。必须先登记一台新的
管理员设备，在 Linux 普通用户终端运行：

```bash
python3 ~/.local/lib/jkbridge/jkbridge_agent.py \
  --state-dir ~/.local/state/jkbridge-agent \
  recovery-code --role admin --ttl 900
```

把代码直接粘贴到新 Windows 的登记界面。代码只能使用一次，Linux 只保存其
校验值。新管理员能够刷新完整设备列表并正常连接后：

1. 如果需要立即断开旧隧道，先在旧管理员电脑退出 JK Bridge；
2. 在新管理员上刷新设备列表，选择旧管理员，勾选明确确认后撤销；
3. 如果旧电脑也不应继续访问 Tailnet，再单独从 Tailscale 管理后台移除它。

Agent 不允许删除最后一台管理员；Windows 界面也不允许撤销当前设备。因此更换
管理员始终是“先登记新的 → 验证可用 → 再撤销旧的”，不是直接转移或改角色。
撤销会阻止该设备今后的 Agent 请求和新 SSH 连接，但无法终止已经建立的 SSH
会话。

如果全部管理员设备都丢失，在 Linux 本机重新签发 `admin` 恢复码，登记新的
管理员，再由它撤销丢失设备。

## 撤销普通设备

在管理员 Windows 刷新 Agent 设备列表，选择一台非当前设备，核对其稳定身份，
勾选确认并撤销。如果需要立即断开，先让该设备退出 JK Bridge。不要通过复制、
覆盖或手工编辑另一台设备的密钥文件来撤销。

## 安全迁移与日常检查

- 迁移本地服务选择时，只导出/导入不含秘密的 Service Profile。不要复制私钥、
  Agent 凭据、授权回执或整个 `config.json`。
- Windows 本地端口可以与 Linux 服务端口不同。浏览器访问
  `http(s)://127.0.0.1:<Windows 本地端口>/`；SSH 授权只记录精确的 Linux
  端口。
- 新增或改变 Linux 目标端口时，需要执行 Agent 清单事务或明确的一次性重新
  配对；只改变 Windows 本地端口或路径不需要扩大 Linux 权限。
- Agent 负责登记和管理；已经授权的 SSH 隧道是独立数据通道，不依赖 Agent
  始终在线。

Linux 管理员可以只读查看非秘密状态：

```bash
~/.local/bin/jkbridge-agentctl status
python3 ~/.local/lib/jkbridge/jkbridge_agent.py \
  --state-dir ~/.local/state/jkbridge-agent status
```
