[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ServerHost,
    [string]$ExpectedTailnetSuffix = "",
    [Parameter(Mandatory = $true)]
    [string]$SshUser,
    [ValidateRange(1, 65535)]
    [int]$SshPort = 22,
    [string]$IdentityFile = "",
    [string]$PinnedHostKey = "",
    [switch]$WriteConfig,
    [string]$ConfigPath = "",
    [switch]$SkipTailscaleCheck,
    [switch]$AllowAnyLoopbackPort
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"
if (Test-Path Variable:PSNativeCommandArgumentPassing) {
    # Windows OpenSSH expects the legacy Win32 empty-argument encoding for -N "" / -P "".
    $PSNativeCommandArgumentPassing = "Legacy"
}

function Write-Step([string]$Message) {
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Resolve-NativeCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [string[]]$CandidatePaths = @()
    )

    foreach ($candidate in $CandidatePaths) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($command) {
        return $command.Source
    }

    return $null
}

function Normalize-DnsName([string]$Name) {
    if (-not $Name) { return "" }
    return $Name.Trim().TrimEnd('.').ToLowerInvariant()
}

function Get-JsonPropertyValue {
    param([object]$Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Set-JsonPropertyValue {
    param([object]$Object, [string]$Name, [object]$Value)
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value
    } else {
        $property.Value = $Value
    }
}

function Assert-PathIsNotReparsePoint {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$IncludeExistingParents
    )

    $candidate = [IO.Path]::GetFullPath($Path)
    while ($candidate) {
        # Get-Item can still surface a broken link in cases where Test-Path
        # reports false, so inspect the item directly and tolerate only a
        # genuinely absent path component.
        $item = Get-Item -LiteralPath $candidate -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "拒绝使用 reparse point（符号链接或目录联接）：$candidate"
        }

        if (-not $IncludeExistingParents) { break }
        $parent = Split-Path -Parent $candidate
        if (-not $parent -or $parent -eq $candidate) { break }
        $candidate = $parent
    }
}

function Set-ExactPrivateKeyAcl {
    param([Parameter(Mandatory = $true)][string]$Path)

    Assert-PathIsNotReparsePoint $Path -IncludeExistingParents

    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $currentSid = $currentIdentity.User
    $systemSid = New-Object Security.Principal.SecurityIdentifier("S-1-5-18")
    $existingSecurity = Get-Acl -LiteralPath $Path
    $existingOwnerSid = try {
        (New-Object Security.Principal.NTAccount($existingSecurity.Owner)).Translate([Security.Principal.SecurityIdentifier]).Value
    } catch {
        $existingSecurity.Owner
    }
    if ($existingOwnerSid -ne $currentSid.Value) {
        throw "私钥所有者不是当前 Windows 用户；脚本不会尝试提权修改所有者。请移动该文件后重新生成专用密钥。"
    }

    # Build a fresh descriptor containing only a DACL. Reusing the object from
    # Get-Acl with Set-Acl can make PowerShell persist unrelated security
    # sections (notably the SACL), which requires SeSecurityPrivilege even
    # though the current user owns this file. File.SetAccessControl and its
    # modern extension equivalent persist only sections modified on this fresh
    # object, so a normal non-elevated user only writes the intended DACL.
    $security = New-Object Security.AccessControl.FileSecurity
    $security.SetAccessRuleProtection($true, $false)
    $rights = [Security.AccessControl.FileSystemRights]::FullControl
    $inheritance = [Security.AccessControl.InheritanceFlags]::None
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    [void]$security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($currentSid, $rights, $inheritance, $propagation, $allow)))
    [void]$security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($systemSid, $rights, $inheritance, $propagation, $allow)))
    if ($PSVersionTable.PSEdition -eq "Desktop") {
        [IO.File]::SetAccessControl($Path, $security)
    } else {
        $fileInfo = New-Object IO.FileInfo($Path)
        [IO.FileSystemAclExtensions]::SetAccessControl($fileInfo, $security)
    }

    # Re-read and fail closed if Windows did not preserve the exact DACL.
    $verified = Get-Acl -LiteralPath $Path
    $rules = @($verified.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    $allowedSids = @($currentSid.Value, $systemSid.Value)
    $unexpected = @($rules | Where-Object {
        $_.IsInherited -or
        $_.AccessControlType -ne $allow -or
        $allowedSids -notcontains $_.IdentityReference.Value -or
        $_.FileSystemRights -ne $rights -or
        $_.InheritanceFlags -ne $inheritance -or
        $_.PropagationFlags -ne $propagation
    })
    $presentSids = @($rules | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique)
    if (-not $verified.AreAccessRulesProtected -or
        $unexpected.Count -gt 0 -or
        $rules.Count -ne 2 -or
        $presentSids.Count -ne 2 -or
        $presentSids -notcontains $currentSid.Value -or
        $presentSids -notcontains $systemSid.Value) {
        throw "私钥 ACL 未能精确限制为当前用户与 SYSTEM；脚本已停止。"
    }

    $ownerSid = try {
        (New-Object Security.Principal.NTAccount($verified.Owner)).Translate([Security.Principal.SecurityIdentifier]).Value
    } catch {
        $verified.Owner
    }
    if ($ownerSid -ne $currentSid.Value) {
        throw "私钥所有者不是当前 Windows 用户；脚本已停止。"
    }
}

function ConvertTo-NormalizedEd25519Key([string]$PublicKey) {
    if (-not $PublicKey) { return $null }
    $parts = $PublicKey.Trim() -split '\s+'
    if ($parts.Count -lt 2 -or $parts[0] -ne "ssh-ed25519") { return $null }

    try {
        $decoded = [Convert]::FromBase64String($parts[1])
    } catch {
        return $null
    }

    if ($decoded.Length -ne 51) {
        # An OpenSSH Ed25519 public-key blob is 51 bytes, including type metadata.
        return $null
    }
    if ($decoded[0] -ne 0 -or $decoded[1] -ne 0 -or $decoded[2] -ne 0 -or $decoded[3] -ne 11 -or
        [Text.Encoding]::ASCII.GetString($decoded, 4, 11) -ne "ssh-ed25519" -or
        $decoded[15] -ne 0 -or $decoded[16] -ne 0 -or $decoded[17] -ne 0 -or $decoded[18] -ne 32) {
        return $null
    }

    return "ssh-ed25519 $($parts[1])"
}

function ConvertTo-PortableIdentityPath([string]$Path) {
    $profile = [IO.Path]::GetFullPath($env:USERPROFILE).TrimEnd('\')
    $fullPath = [IO.Path]::GetFullPath($Path)
    if ($fullPath.StartsWith($profile + '\', [StringComparison]::OrdinalIgnoreCase)) {
        return "%USERPROFILE%" + $fullPath.Substring($profile.Length)
    }
    return $fullPath
}

function Test-TailscaleEnvironment([string]$TailscalePath) {
    Write-Step "检查 Tailscale 登录状态与 Linux 节点"
    $statusText = (& $TailscalePath status --json 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "tailscale status --json 执行失败：$($statusText.Trim())"
    }

    try {
        $status = $statusText | ConvertFrom-Json
    } catch {
        throw "无法解析 Tailscale 状态；请先启动并登录 Tailscale。"
    }

    $backendState = [string](Get-JsonPropertyValue $status "BackendState")
    if ($backendState -ne "Running") {
        throw "Tailscale 当前状态为 '$backendState'，需要先登录并保持 Running。"
    }

    $self = Get-JsonPropertyValue $status "Self"
    $selfDns = Normalize-DnsName ([string](Get-JsonPropertyValue $self "DNSName"))
    if (-not $selfDns) {
        throw "Tailscale 未提供本机 MagicDNS 名称；请在 tailnet 中启用 MagicDNS。"
    }
    $dotIndex = $selfDns.IndexOf('.')
    $actualSuffix = if ($dotIndex -ge 0) { $selfDns.Substring($dotIndex + 1) } else { "" }
    $expected = Normalize-DnsName $ExpectedTailnetSuffix
    if ($expected -and $actualSuffix -ne $expected) {
        throw "当前 Tailscale tailnet 为 '$actualSuffix'，预期为 '$expected'。请先切换正确账号。"
    }

    $target = Normalize-DnsName $ServerHost
    $peers = Get-JsonPropertyValue $status "Peer"
    $match = $null
    if ($peers) {
        foreach ($property in $peers.PSObject.Properties) {
            $peerDns = Normalize-DnsName ([string](Get-JsonPropertyValue $property.Value "DNSName"))
            if ($peerDns -eq $target) {
                $match = $property.Value
                break
            }
        }
    }

    if ($null -eq $match) {
        Write-Warning "未在当前 tailnet 中找到精确节点 '$ServerHost'。密钥仍可生成，但连接前必须确认节点名称与账号。"
    } else {
        $online = Get-JsonPropertyValue $match "Online"
        if ($online -eq $false) {
            Write-Warning "已找到 Linux 节点，但它当前离线。"
        } else {
            Write-Host "Tailscale 正常；已找到 Linux 节点 $ServerHost。" -ForegroundColor Green
        }
    }
}

function New-DefaultConfig {
    param([string]$PortableIdentityPath, [string]$NormalizedPinnedHostKey)

    return [ordered]@{
        schemaVersion = 2
        setupCompleted = $true
        expectedTailnetDnsSuffix = (Normalize-DnsName $ExpectedTailnetSuffix)
        server = [ordered]@{
            displayName = "Linux workstation"
            host = (Normalize-DnsName $ServerHost)
            user = $SshUser
            port = $SshPort
            identityFile = $PortableIdentityPath
            hostKeyAlias = "jk-bridge-linux"
            pinnedHostKey = $NormalizedPinnedHostKey
        }
        behavior = [ordered]@{
            autoConnect = $true
            autoReconnect = $true
            closeToTray = $true
            startWithWindows = $false
            theme = "system"
        }
        services = @(
            [ordered]@{
                id = "overleaf"; name = "Overleaf"; description = "论文协作与 LaTeX 编辑"; icon = "Document"
                enabled = $true; favorite = $true
                localPort = 8080; remoteHost = "127.0.0.1"; remotePort = 80
                openUrl = "http://127.0.0.1:8080/project"
                healthPath = "/project"; healthKind = "httpStatus"
            },
            [ordered]@{
                id = "gpu-dashboard"; name = "GPU Dashboard"; description = "显卡状态与任务监控"; icon = "Gauge"
                enabled = $true; favorite = $true
                localPort = 8765; remoteHost = "127.0.0.1"; remotePort = 8765
                openUrl = "http://127.0.0.1:8765/"
                healthPath = "/api/health"; healthKind = "jsonOk"
            }
        )
    }
}

function Save-JKBridgeConfig {
    param(
        [string]$Path,
        [string]$PortableIdentityPath,
        [string]$NormalizedPinnedHostKey
    )

    $directory = Split-Path -Parent $Path
    [IO.Directory]::CreateDirectory($directory) | Out-Null

    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        try {
            $config = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        } catch {
            throw "现有配置无法解析，脚本不会覆盖它：$Path"
        }

        if ($null -eq $config) {
            throw "现有配置内容为空，脚本不会覆盖它：$Path"
        }
        $existingSchema = Get-JsonPropertyValue $config "schemaVersion"
        if ($null -ne $existingSchema -and [int]$existingSchema -notin @(1, 2)) {
            throw "现有配置 schemaVersion=$existingSchema，不支持由此脚本改写。"
        }
        $behavior = Get-JsonPropertyValue $config "behavior"
        if ($null -eq $behavior -or
            $null -eq (Get-JsonPropertyValue $config "services")) {
            throw "现有配置缺少 behavior 或 services；脚本不会猜测并覆盖。"
        }
        # V2 has no motion setting or animations; remove the obsolete draft field
        # when this helper safely rewrites an existing configuration.
        $behavior.PSObject.Properties.Remove("reducedMotion")

        $server = Get-JsonPropertyValue $config "server"
        if ($null -eq $server) {
            $server = [pscustomobject]@{}
            Set-JsonPropertyValue $config "server" $server
        }

        Set-JsonPropertyValue $config "schemaVersion" 2
        Set-JsonPropertyValue $config "setupCompleted" $true
        Set-JsonPropertyValue $config "expectedTailnetDnsSuffix" (Normalize-DnsName $ExpectedTailnetSuffix)
        Set-JsonPropertyValue $server "displayName" "Linux workstation"
        Set-JsonPropertyValue $server "host" (Normalize-DnsName $ServerHost)
        Set-JsonPropertyValue $server "user" $SshUser
        Set-JsonPropertyValue $server "port" $SshPort
        Set-JsonPropertyValue $server "identityFile" $PortableIdentityPath
        Set-JsonPropertyValue $server "hostKeyAlias" "jk-bridge-linux"
        Set-JsonPropertyValue $server "pinnedHostKey" $NormalizedPinnedHostKey
    } else {
        $config = New-DefaultConfig $PortableIdentityPath $NormalizedPinnedHostKey
    }

    $json = $config | ConvertTo-Json -Depth 20
    $temporaryPath = Join-Path $directory (".{0}.{1}.tmp" -f ([IO.Path]::GetFileName($Path)), [Guid]::NewGuid().ToString("N"))
    $backupPath = $Path + ".bak"
    $utf8WithoutBom = New-Object Text.UTF8Encoding($false)

    try {
        [IO.File]::WriteAllText($temporaryPath, $json + [Environment]::NewLine, $utf8WithoutBom)
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            [IO.File]::Replace($temporaryPath, $Path, $backupPath, $true)
        } else {
            [IO.File]::Move($temporaryPath, $Path)
        }
    } finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

if (-not $env:USERPROFILE) {
    throw "USERPROFILE 未设置；此脚本需要在 Windows 用户会话中运行。"
}
if (-not $env:LOCALAPPDATA) {
    throw "LOCALAPPDATA 未设置；此脚本需要在 Windows 用户会话中运行。"
}

$ServerHost = Normalize-DnsName $ServerHost
$ExpectedTailnetSuffix = Normalize-DnsName $ExpectedTailnetSuffix
if ($ServerHost -notmatch '^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?)+$') {
    throw "-ServerHost 必须是 MagicDNS 完整域名，不能是 IP 地址。"
}
$parsedServerAddress = $null
if ([Net.IPAddress]::TryParse($ServerHost, [ref]$parsedServerAddress)) {
    throw "-ServerHost 必须是 MagicDNS 完整域名，不能是 IP 地址。"
}
if (-not $ExpectedTailnetSuffix) {
    $separator = $ServerHost.IndexOf('.')
    if ($separator -lt 1 -or $separator -eq $ServerHost.Length - 1) {
        throw "无法从 -ServerHost 推导 tailnet 后缀，请显式提供 -ExpectedTailnetSuffix。"
    }
    $ExpectedTailnetSuffix = $ServerHost.Substring($separator + 1)
}
if ($ExpectedTailnetSuffix -and
    -not $ServerHost.EndsWith("." + $ExpectedTailnetSuffix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "-ServerHost 不属于 -ExpectedTailnetSuffix。"
}
if ($SshUser -notmatch '^[A-Za-z_][A-Za-z0-9_.-]{0,63}$') {
    throw "-SshUser 包含 SSH 用户名不支持的字符。"
}

if (-not $IdentityFile) {
    $IdentityFile = Join-Path $env:USERPROFILE ".ssh\jk_bridge_ed25519"
}
$IdentityFile = [Environment]::ExpandEnvironmentVariables($IdentityFile)
$IdentityFile = [IO.Path]::GetFullPath($IdentityFile)
if ($IdentityFile.EndsWith(".pub", [StringComparison]::OrdinalIgnoreCase)) {
    throw "-IdentityFile 应指向私钥文件，不能以 .pub 结尾。"
}
Assert-PathIsNotReparsePoint $IdentityFile -IncludeExistingParents
Assert-PathIsNotReparsePoint ($IdentityFile + ".pub") -IncludeExistingParents

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $env:LOCALAPPDATA "JKBridge\config.json"
}
$ConfigPath = [Environment]::ExpandEnvironmentVariables($ConfigPath)
$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)

$normalizedPinnedHostKey = ConvertTo-NormalizedEd25519Key $PinnedHostKey
if ($WriteConfig -and -not $normalizedPinnedHostKey) {
    throw "使用 -WriteConfig 时必须显式传入已在 Linux 本机核验的 -PinnedHostKey 'ssh-ed25519 AAAA...'。脚本不会从网络自动信任主机密钥。"
}

$openSshDirectory = if ($env:WINDIR) { Join-Path $env:WINDIR "System32\OpenSSH" } else { "" }
$sshKeygen = Resolve-NativeCommand "ssh-keygen.exe" @(
    $(if ($openSshDirectory) { Join-Path $openSshDirectory "ssh-keygen.exe" })
)
if (-not $sshKeygen) {
    throw "找不到 ssh-keygen.exe。请在 Windows 可选功能中安装 OpenSSH Client。"
}

$ssh = Resolve-NativeCommand "ssh.exe" @(
    $(if ($openSshDirectory) { Join-Path $openSshDirectory "ssh.exe" })
)
if (-not $ssh) {
    throw "找不到 ssh.exe。请在 Windows 可选功能中安装 OpenSSH Client。"
}
Write-Host "OpenSSH Client: $ssh" -ForegroundColor Green

if (-not $SkipTailscaleCheck) {
    $tailscaleCandidate = if ($env:ProgramFiles) { Join-Path $env:ProgramFiles "Tailscale\tailscale.exe" } else { "" }
    $tailscale = Resolve-NativeCommand "tailscale.exe" @($tailscaleCandidate)
    if (-not $tailscale) {
        throw "找不到 tailscale.exe。请先安装、启动并登录 Tailscale。"
    }
    Test-TailscaleEnvironment $tailscale
}

Write-Step "创建或复用 JK Bridge 专用 Ed25519 密钥"
$identityDirectory = Split-Path -Parent $IdentityFile
[IO.Directory]::CreateDirectory($identityDirectory) | Out-Null
Assert-PathIsNotReparsePoint $identityDirectory -IncludeExistingParents

if (-not (Test-Path -LiteralPath $IdentityFile -PathType Leaf)) {
    if (Test-Path -LiteralPath ($IdentityFile + ".pub") -PathType Leaf) {
        throw "只找到公钥而没有私钥。脚本不会覆盖，请先移动该文件或选择另一个 -IdentityFile。"
    }

    $keygenOutput = (& $sshKeygen -q -t ed25519 -a 100 -N '""' -C "JK Bridge dedicated tunnel key" -f $IdentityFile 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $IdentityFile -PathType Leaf)) {
        throw "专用 SSH 密钥生成失败：$($keygenOutput.Trim())"
    }
    Write-Host "已生成：$IdentityFile" -ForegroundColor Green
} else {
    Write-Host "专用私钥已存在，将安全复用而不覆盖：$IdentityFile" -ForegroundColor Yellow
    Write-Warning "若该公钥已经以无限制旧条目存在于 Linux authorized_keys，请勿再添加同一公钥的受限副本；应改用新的 -IdentityFile 生成不同密钥后再迁移。"
}

# Restrict a newly created key immediately, before any further processing;
# reused dedicated keys are normalized before they are read as well.
Set-ExactPrivateKeyAcl $IdentityFile
Write-Host "私钥 ACL 已限制为当前用户与 SYSTEM。" -ForegroundColor Green

$derivedPublicText = (& $sshKeygen -y -P '""' -f $IdentityFile 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "现有私钥无法以无口令方式使用。JK Bridge 不会保存口令；请改用新的 -IdentityFile。"
}
$clientPublicKey = ConvertTo-NormalizedEd25519Key $derivedPublicText
if (-not $clientPublicKey) {
    throw "ssh-keygen 未能从私钥导出有效的 Ed25519 公钥。"
}

$publicKeyLine = "$clientPublicKey JK Bridge dedicated tunnel key"
Assert-PathIsNotReparsePoint ($IdentityFile + ".pub") -IncludeExistingParents
[IO.File]::WriteAllText($IdentityFile + ".pub", $publicKeyLine + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))

$fingerprintText = (& $sshKeygen -lf ($IdentityFile + ".pub") -E sha256 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -eq 0 -and $fingerprintText) {
    Write-Host "客户端公钥指纹：$fingerprintText" -ForegroundColor Green
} else {
    Write-Warning "未能显示客户端公钥指纹；迁移旧授权前请在 Windows 手工运行 ssh-keygen -lf `"$IdentityFile.pub`" -E sha256。"
}

$portableIdentityPath = ConvertTo-PortableIdentityPath $IdentityFile
if ($WriteConfig) {
    Write-Step "原子更新 JK Bridge 配置"
    Save-JKBridgeConfig $ConfigPath $portableIdentityPath $normalizedPinnedHostKey
    Write-Host "已写入：$ConfigPath" -ForegroundColor Green
    Write-Host "若原配置存在，替换时会保留：$ConfigPath.bak"
} else {
    Write-Warning "尚未写入 JK Bridge 配置。核验 Linux SSH 主机公钥后，请使用 -WriteConfig 与 -PinnedHostKey 再运行一次。"
}

$permitOpenOptions = if ($AllowAnyLoopbackPort) {
    'permitopen="127.0.0.1:*"'
} else {
    'permitopen="127.0.0.1:80",permitopen="127.0.0.1:8765"'
}
$authorizedKeyLine = 'restrict,port-forwarding,command="/usr/bin/false",from="100.64.0.0/10,fd7a:115c:a1e0::/48",' + $permitOpenOptions + ' ' + $clientPublicKey + ' JK Bridge dedicated tunnel key'

Write-Step "需要在 Linux 主机上人工完成的唯一授权步骤"
Write-Host "脚本没有、也不会修改 Linux ~/.ssh/authorized_keys。" -ForegroundColor Yellow
Write-Host "请在 Linux 用户 '$SshUser' 的 ~/.ssh/authorized_keys 中确认没有相同公钥后，仅添加下面这一整行：" -ForegroundColor White
Write-Host ""
Write-Host $authorizedKeyLine -ForegroundColor White
Write-Host ""
Write-Host "Linux 权限应为：chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys"
if ($AllowAnyLoopbackPort) {
    Write-Warning "已显式启用任意 Linux IPv4 回环端口。此密钥可访问 127.0.0.1 上的所有 TCP 服务；仅在确有动态新增服务需求时使用。"
} else {
    Write-Host "默认仅允许 Linux 127.0.0.1:80 与 127.0.0.1:8765。新增服务时请人工增加精确 permitopen，或明确使用 -AllowAnyLoopbackPort。"
}

Write-Step "Linux SSH 主机密钥的独立核验"
Write-Host "请直接在 Linux 控制台运行："
Write-Host "  sudo ssh-keygen -y -f /etc/ssh/ssh_host_ed25519_key"
Write-Host "得到的 ssh-ed25519 公钥必须通过可信渠道核对，然后才能作为 -PinnedHostKey 写入配置。"
Write-Host "不要从首次 SSH 连接提示或不可信网络复制并自动接受主机密钥。" -ForegroundColor Yellow

Write-Host "`n首次配置准备完成。详细步骤见 README.zh-CN.md 或 docs/INSTALLATION.zh-CN.md。" -ForegroundColor Green
