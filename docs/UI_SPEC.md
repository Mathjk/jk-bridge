# JK Bridge UI Specification

## Direction: Quiet Infrastructure

JK Bridge should feel like a precise Windows 11 system instrument: calm while healthy, explicit when intervention is required, and never like a generic analytics dashboard. The recognizable visual motif is a **Bridge Rail** that shows the real dependency chain:

```text
Windows -> Tailscale -> Linux peer -> SSH -> local services
```

The rail, app icon, and status surfaces share the same two-node bridge geometry. V1 uses static state changes rather than traveling or position animations.

## Product name

- Display name: **JK Bridge**
- Descriptor: **Local Services** / **本地服务桥**
- Executable: `JKBridge.exe`
- Repository name remains `win2linux`.

## Window and navigation

- Default size: `1120 x 760`.
- Minimum size: `980 x 680`.
- Native title bar for V1 reliability; Mica/blur is progressive enhancement with a solid fallback.
- Left navigation rail: Device setup, Overview, Services, Diagnostics; Settings anchored at the bottom.
- One navigation level only. Editing uses an in-window right drawer rather than additional windows.
- Main content margin: 28–32 px; section gap: 24 px; control rhythm: 8 px.

```text
┌──────────────────────────────────────────────────────────────┐
│ JK Bridge                                        refresh      │
├──────────────┬───────────────────────────────────────────────┤
│ Device setup │ Your Linux services              Connect all  │
│ Overview     │ linux-box · alice                              │
│ Services     │                                               │
│ Diagnostics  │                                               │
│              │ Windows ─ Tailscale ─ Linux ─ SSH ─ 2/2      │
│              │                                               │
│              │ ┌────────────────┐ ┌────────────────┐        │
│              │ │ Overleaf       │ │ GPU Dashboard  │        │
│              │ │ localhost:8080 │ │ localhost:8765 │        │
│              │ │ Ready     Open │ │ Ready     Open │        │
│              │ └────────────────┘ └────────────────┘        │
│ Settings     │                                               │
└──────────────┴───────────────────────────────────────────────┘
```

## Overview

### Header

- Page title and one-line description.
- The current primary button changes among Connect, Cancel, and Disconnect; retry remains available through Refresh/Diagnostics when a connection needs attention.
- Refresh is secondary and remains available when disconnected.

### Bridge Rail

Each segment has its own icon, label, and short status:

- Windows: prerequisites discovered.
- Tailscale: running and correct tailnet.
- Linux: exact peer found and online.
- SSH: host verified and process alive.
- Services: available count over enabled count.

Color never communicates status alone. Every segment also uses a glyph and text. The issue banner provides the implemented route to Diagnostics.

### Issue surface

Use one inline issue banner for the highest-priority action. Copy is actionable first, technical second:

```text
Local port 8765 is already in use.
Choose another port or close the program using it.   [View diagnostics]
```

Do not display raw stderr as the primary message.

### Service cards

- Two fixed columns inside the supported `980 px` minimum window width.
- Short-code badge, name, local URL, status text, and visible Open button.
- Overview exposes Copy address, Check now, and Open; editing, enabling, and deleting live on the Services page.
- Open remains visible rather than hover-only.
- Do not add decorative uptime, bandwidth, or chart metrics.

## Device setup

- Fresh profiles open here before tunnel preflight.
- The left column presents the single Linux helper command and explains its one-shot lifecycle.
- The right column accepts a complete `jkbridge://` code and an advanced dedicated identity path.
- One **Pair and connect** action drives four visible stages: network, identity key, Linux authorization, and secure connection.
- Pairing failures remain inline and actionable; the code field is cleared after success.
- The security note explains that no Linux password is used, the token is temporary, and the Linux helper exits after one successful request.
- Configured users may return to rotate identity, change Linux nodes, or authorize new remote ports.

## Services page

- Dense list on the left/main surface; fixed editor panel on the right.
- V1 has no search field or peer/service picker.
- Editable fields: name, description, local port, remote port, complete localhost HTTP(S) URL, and enabled state.
- Icon/favorite values and specialized health modes remain configuration-model fields; newly added services use the generic HTTP status health mode and derive its path from the URL.
- Remote host is fixed to `127.0.0.1` in normal mode.
- Save validates duplicate/configured ports and URL safety; connection preflight performs the live local bind check without killing the occupying process.
- Save validates the complete proposed configuration before committing and restarting the bridge once.

## Diagnostics page

The page uses four summary cards followed by expandable recent-log entries:

1. Tailscale executable, backend state, current identity, tailnet.
2. Configured peer, exact DNS match, online state, runtime route.
3. OpenSSH executable, pinned host fingerprint, PID.
4. Per-service HTTP health summary.
5. Recent sanitized log, Copy summary, Open log folder.

## Settings page

- Linux profile: paired host/FQDN, user, SSH port, key path, pinned host public key, and a visible re-pair action.
- Behavior: auto-connect, auto-reconnect, close to tray, start with Windows.
- Appearance: System/Light/Dark.
- About/data: product version, configuration directory, loopback-only exposure statement, and an action that opens Diagnostics; the log-folder action is available there.

Changing server identity or any forwarding field clearly warns that the active bridge will reconnect.

## Tray behavior

V1 uses an Avalonia `TrayIcon` with a native menu:

```text
Open JK Bridge
Connect / Disconnect
----------------
Open Overleaf
Open GPU Dashboard
----------------
Diagnostics
Settings
----------------
Exit
```

- Left click restores the window.
- Close hides the window when close-to-tray is enabled.
- Tooltip is the static `JK Bridge`; connection state remains visible in the window.
- The app does not blink or continuously animate the tray icon.
- V1 does not send background notifications.

## State vocabulary

| Runtime state | Chinese copy | Accent | Primary action |
|---|---|---|---|
| Initializing | 正在检查环境… | Info | 取消 |
| Stopped | 未连接 | Neutral | 连接全部 |
| Starting | 正在连接… | Info | 取消 |
| Connected | 已连接 · 全部服务可用 | Success | 断开 |
| Degraded | 部分可用 · 有服务需要处理 | Warning | 断开 |
| Reconnecting | 连接中断，稍后重试 | Warning | 取消（“刷新”可立即重试） |
| Needs action | 需要处理 | Error | 连接全部（详情由问题横幅进入诊断） |

Service vocabulary:

- 等待隧道
- 正在检查
- 可用
- 远端服务未启动
- 转发未被服务器允许
- 本地端口被占用
- 已停用

## Visual tokens

### Theme

Follow the Windows system theme by default. Use the Avalonia Fluent theme as the component baseline. The brand appears mainly in the Bridge Rail and icon, not every primary button.

Dark base tokens:

```text
Window       #0C1116
Surface      #121A21
SurfaceAlt   #17222B
Border       #2A3842
Text         #ECF2F4
TextMuted    #9BACB3
Iris         #7D79F2
BridgeTeal   #39B8AE
Success      #54C987
Warning      #E7AD55
Error        #F07178
```

Light tokens use Fluent neutral resources with the same Iris/Teal identity. Semantic brushes are centralized in XAML resources and never scattered as literal component colors.

### Typography

- UI: `Segoe UI Variable Text`, then `Segoe UI` fallback.
- Headings/metrics: `Segoe UI Variable Display` or `Bahnschrift` where available.
- Hostnames, ports, and URLs: `Cascadia Mono`, then monospace fallback.
- Page title: 28/36 semibold.
- Section title: 20/28 semibold.
- Service name/body: 14/20 semibold/regular.
- Secondary and technical detail: 12/16.

### Geometry

- Cards, drawers, and banners: 8 px radius.
- Buttons and fields: 4–6 px radius.
- Card padding: 16 px.
- Minimum click target: 36 x 36 px; primary actions at least 40 px high.
- Borders are preferred over large shadows; elevation remains subtle.

### Icons

Use one coherent Fluent-like vector set. Regular outlines are default; filled forms communicate selection. Do not mix emoji, Font Awesome, and unrelated icon styles. The app icon is two rounded endpoints joined by a bridge, simplified at small sizes.

## Motion

V1 has no traveling, position, drawer-entrance, or page-transition animations. Hover and state feedback come from immediate Fluent control-state styling.

## Accessibility

- Text contrast at least 4.5:1; icons/controls at least 3:1.
- State is always color + icon + text.
- Primary navigation and connection/open actions have explicit automation names; remaining standard controls use Avalonia's content-derived names and require Windows screen-reader acceptance.
- Logical keyboard order and visible Fluent focus rings are required; V1 has no separate overlay drawer to close with Escape.
- Support high DPI and 200% text scaling within the documented `980 x 680` minimum layout.
- Example screen-reader label: `Overleaf, available, local address 127.0.0.1 port 8080, Open button`.

## Reference principles

- Fluent 2 accessibility, typography, iconography, and motion.
- Windows geometry and long-lived Mica surfaces with solid fallbacks.
- Tailscale's split between a full diagnostic window and a quiet tray presence.
- Linear's precise neutral density and token-based themes.
- Raycast's visible contextual actions and keyboard affordances.

The references inform behavior and rhythm; the Bridge Rail and localhost-service model remain JK Bridge's own visual identity.
