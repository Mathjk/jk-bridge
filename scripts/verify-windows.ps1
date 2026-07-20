[CmdletBinding()]
param(
    [string]$ServerHost = "",
    [string]$ExpectedTailnetSuffix = "",
    [string]$IdentityFile = "",
    [string]$ConfigPath = "",
    [ValidateSet("Auto", "Free", "Listening")]
    [string]$PortExpectation = "Auto",
    [switch]$TestServices
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"
if (Test-Path Variable:PSNativeCommandArgumentPassing) {
    $PSNativeCommandArgumentPassing = "Legacy"
}

$script:Failures = New-Object System.Collections.Generic.List[string]
$script:Warnings = New-Object System.Collections.Generic.List[string]
$script:TailscalePath = $null
$script:SshPath = $null
$script:SshKeygenPath = $null
$script:TailscaleStatus = $null
$script:MatchedPeer = $null
$script:Config = $null
$script:Services = @()
$script:ClientFingerprint = $null

function Resolve-NativeCommand {
    param([string]$Name, [string[]]$CandidatePaths = @())
    foreach ($candidate in $CandidatePaths) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($command) { return $command.Source }
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

function Test-PathIsReparsePoint {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$IncludeExistingParents
    )

    $candidate = [IO.Path]::GetFullPath($Path)
    while ($candidate) {
        $item = Get-Item -LiteralPath $candidate -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            return $true
        }

        if (-not $IncludeExistingParents) { break }
        $parent = Split-Path -Parent $candidate
        if (-not $parent -or $parent -eq $candidate) { break }
        $candidate = $parent
    }

    return $false
}

function ConvertTo-SidValue([string]$Identity) {
    if (-not $Identity) { return $null }
    try {
        return (New-Object Security.Principal.NTAccount($Identity)).Translate([Security.Principal.SecurityIdentifier]).Value
    } catch {
        try {
            return (New-Object Security.Principal.SecurityIdentifier($Identity)).Value
        } catch {
            return $null
        }
    }
}

function Assert-ExactPrivateKeyAcl([string]$Path) {
    if (Test-PathIsReparsePoint $Path -IncludeExistingParents) {
        throw "私钥或其父路径是 reparse point（符号链接或目录联接）：$Path"
    }

    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $systemSid = "S-1-5-18"
    $security = Get-Acl -LiteralPath $Path
    $ownerSid = ConvertTo-SidValue $security.Owner
    if ($ownerSid -ne $currentSid) {
        throw "私钥所有者不是当前 Windows 用户。请重新运行 Setup-JKBridge.ps1。"
    }
    if (-not $security.AreAccessRulesProtected) {
        throw "私钥仍继承父目录 ACL。请重新运行 Setup-JKBridge.ps1。"
    }

    $fullControl = [Security.AccessControl.FileSystemRights]::FullControl
    $noInheritance = [Security.AccessControl.InheritanceFlags]::None
    $noPropagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    $rules = @($security.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    $allowedSids = @($currentSid, $systemSid)
    $unexpected = @($rules | Where-Object {
        $_.IsInherited -or
        $_.AccessControlType -ne $allow -or
        $allowedSids -notcontains $_.IdentityReference.Value -or
        $_.FileSystemRights -ne $fullControl -or
        $_.InheritanceFlags -ne $noInheritance -or
        $_.PropagationFlags -ne $noPropagation
    })
    $presentSids = @($rules | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique)
    if ($unexpected.Count -gt 0 -or
        $rules.Count -ne 2 -or
        $presentSids.Count -ne 2 -or
        $presentSids -notcontains $currentSid -or
        $presentSids -notcontains $systemSid) {
        throw "私钥 ACL 不是精确的【当前用户 + SYSTEM】两条显式完全控制规则。请重新运行 Setup-JKBridge.ps1。"
    }
}

function Get-TailscalePeerIpv4([object]$Peer) {
    $values = @(Get-JsonPropertyValue $Peer "TailscaleIPs")
    foreach ($value in $values) {
        $address = $null
        if ([Net.IPAddress]::TryParse([string]$value, [ref]$address) -and
            $address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) {
            $bytes = $address.GetAddressBytes()
            if ($bytes[0] -eq 100 -and ($bytes[1] -band 0xC0) -eq 64) {
                return $address.ToString()
            }
        }
    }
    return $null
}

function Add-VerificationWarning([string]$Message) {
    $script:Warnings.Add($Message)
    Write-Host "  WARN  $Message" -ForegroundColor Yellow
}

function Invoke-VerificationCheck {
    param([string]$Name, [scriptblock]$Action)
    Write-Host "`n[$Name]" -ForegroundColor Cyan
    try {
        & $Action
        Write-Host "  PASS" -ForegroundColor Green
    } catch {
        $message = $_.Exception.Message
        $script:Failures.Add("${Name}: $message")
        Write-Host "  FAIL  $message" -ForegroundColor Red
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
    if ($decoded.Length -ne 51) { return $null }
    if ($decoded[0] -ne 0 -or $decoded[1] -ne 0 -or $decoded[2] -ne 0 -or $decoded[3] -ne 11 -or
        [Text.Encoding]::ASCII.GetString($decoded, 4, 11) -ne "ssh-ed25519" -or
        $decoded[15] -ne 0 -or $decoded[16] -ne 0 -or $decoded[17] -ne 0 -or $decoded[18] -ne 32) {
        return $null
    }
    return "ssh-ed25519 $($parts[1])"
}

function Get-Sha256PublicKeyFingerprint([string]$PublicKey) {
    $normalized = ConvertTo-NormalizedEd25519Key $PublicKey
    if (-not $normalized) { return $null }
    $blob = [Convert]::FromBase64String(($normalized -split '\s+')[1])
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash($blob)
    } finally {
        $sha256.Dispose()
    }
    return "SHA256:$([Convert]::ToBase64String($hash).TrimEnd('='))"
}

function Get-TcpListeners([int]$Port) {
    return @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
}

function Test-ServiceEndpoint([object]$Service) {
    $url = [string](Get-JsonPropertyValue $Service "openUrl")
    $healthPath = [string](Get-JsonPropertyValue $Service "healthPath")
    $healthKind = [string](Get-JsonPropertyValue $Service "healthKind")
    $name = [string](Get-JsonPropertyValue $Service "name")
    $localPort = [int](Get-JsonPropertyValue $Service "localPort")

    if ($healthKind -ieq "tcpConnect") {
        $tcpClient = New-Object Net.Sockets.TcpClient
        try {
            $connect = $tcpClient.ConnectAsync("127.0.0.1", $localPort)
            if (-not $connect.Wait(3000) -or -not $tcpClient.Connected) {
                throw "$name 的 TCP 端口在 3 秒内未连通。"
            }
        } catch {
            throw "$name TCP 健康检查失败：$($_.Exception.GetBaseException().Message)"
        } finally {
            $tcpClient.Dispose()
        }
        Write-Host "  $name -> TCP 127.0.0.1:$localPort"
        return
    }

    $baseUri = [Uri]$url
    $healthUri = [Uri]::new($baseUri, $healthPath)

    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $true
    $client = New-Object Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(3)
    try {
        $response = $client.GetAsync($healthUri).GetAwaiter().GetResult()
        try {
            $statusCode = [int]$response.StatusCode
            $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        } finally {
            $response.Dispose()
        }
    } catch {
        throw "$name 健康检查失败：$($_.Exception.Message)"
    } finally {
        $client.Dispose()
        $handler.Dispose()
    }

    if ($statusCode -lt 200 -or $statusCode -gt 499) {
        throw "$name 返回 HTTP $statusCode。"
    }

    if ($healthKind -ieq "jsonOk") {
        try { $json = $content | ConvertFrom-Json } catch { throw "$name 未返回有效 JSON。" }
        if ((Get-JsonPropertyValue $json "ok") -ne $true) {
            throw "$name 的 JSON 未包含 ok=true。"
        }
    }

    Write-Host "  $name -> HTTP $statusCode ($healthUri)"
}

if (-not $env:LOCALAPPDATA) {
    throw "LOCALAPPDATA 未设置；请在 Windows 用户会话中运行。"
}
if (-not $ConfigPath) {
    $ConfigPath = Join-Path $env:LOCALAPPDATA "JKBridge\config.json"
}
$ConfigPath = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($ConfigPath))

Invoke-VerificationCheck "JK Bridge 配置" {
    if (Test-Path -LiteralPath $ConfigPath -PathType Leaf) {
        try {
            $script:Config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
        } catch {
            throw "配置不是有效 JSON：$ConfigPath"
        }
        if ($null -eq $script:Config) { throw "配置内容为空：$ConfigPath" }

        $server = Get-JsonPropertyValue $script:Config "server"
        if ($null -eq $server) { throw "配置缺少 server。" }
        if (-not $script:ServerHost) { $script:ServerHost = [string](Get-JsonPropertyValue $server "host") }
        if (-not $script:ExpectedTailnetSuffix) {
            $script:ExpectedTailnetSuffix = [string](Get-JsonPropertyValue $script:Config "expectedTailnetDnsSuffix")
        }
        if (-not $script:IdentityFile) { $script:IdentityFile = [string](Get-JsonPropertyValue $server "identityFile") }
        $script:Services = @(Get-JsonPropertyValue $script:Config "services")
        Write-Host "  配置：$ConfigPath"
    } else {
        Add-VerificationWarning "尚无配置文件；将按项目默认值检查。首次设置请运行 Setup-JKBridge.ps1。"
    }

    if (-not $script:ServerHost) {
        throw "没有可验证的 Linux 主机。请先在 JK Bridge 中完成一键配对，或显式传入 -ServerHost。"
    }
    if (-not $script:ExpectedTailnetSuffix) {
        $separator = $script:ServerHost.IndexOf('.')
        if ($separator -lt 1 -or $separator -eq $script:ServerHost.Length - 1) {
            throw "无法从 Linux 主机名推导 tailnet 后缀，请显式传入 -ExpectedTailnetSuffix。"
        }
        $script:ExpectedTailnetSuffix = $script:ServerHost.Substring($separator + 1)
    }
    if (-not $script:IdentityFile) { $script:IdentityFile = "%USERPROFILE%\.ssh\jk_bridge_ed25519" }
    if ($script:Services.Count -eq 0) {
        $script:Services = @(
            [pscustomobject]@{ name = "Overleaf"; enabled = $true; localPort = 8080; openUrl = "http://127.0.0.1:8080/project"; healthPath = "/project"; healthKind = "httpStatus" },
            [pscustomobject]@{ name = "GPU Dashboard"; enabled = $true; localPort = 8765; openUrl = "http://127.0.0.1:8765/"; healthPath = "/api/health"; healthKind = "jsonOk" },
            [pscustomobject]@{ name = "Investment News"; enabled = $true; localPort = 8793; openUrl = "http://127.0.0.1:8793/"; healthPath = "/"; healthKind = "httpStatus" }
        )
    }

    $script:ServerHost = Normalize-DnsName $script:ServerHost
    $script:ExpectedTailnetSuffix = Normalize-DnsName $script:ExpectedTailnetSuffix
    if (-not $ServerHost.EndsWith("." + $ExpectedTailnetSuffix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Linux 主机名不属于预期 tailnet：$ServerHost"
    }
}

Invoke-VerificationCheck "Tailscale 可执行文件" {
    $candidate = if ($env:ProgramFiles) { Join-Path $env:ProgramFiles "Tailscale\tailscale.exe" } else { "" }
    $script:TailscalePath = Resolve-NativeCommand "tailscale.exe" @($candidate)
    if (-not $script:TailscalePath) { throw "找不到 tailscale.exe；请安装 Tailscale。" }
    Write-Host "  $script:TailscalePath"
}

Invoke-VerificationCheck "Tailscale 状态与 tailnet" {
    if (-not $script:TailscalePath) { throw "上一项未找到 Tailscale。" }
    $statusText = (& $script:TailscalePath status --json 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "tailscale status 执行失败。" }
    try { $script:TailscaleStatus = $statusText | ConvertFrom-Json } catch { throw "Tailscale 状态 JSON 无法解析。" }

    $state = [string](Get-JsonPropertyValue $script:TailscaleStatus "BackendState")
    if ($state -ne "Running") { throw "状态为 '$state'，预期为 Running。" }
    $self = Get-JsonPropertyValue $script:TailscaleStatus "Self"
    $selfDns = Normalize-DnsName ([string](Get-JsonPropertyValue $self "DNSName"))
    if (-not $selfDns) { throw "Tailscale 未提供 MagicDNS 名称；请在 tailnet 中启用 MagicDNS。" }
    $dot = $selfDns.IndexOf('.')
    $suffix = if ($dot -ge 0) { $selfDns.Substring($dot + 1) } else { "" }
    if ($ExpectedTailnetSuffix -and $suffix -ne $ExpectedTailnetSuffix) {
        throw "当前 tailnet 为 '$suffix'，预期 '$ExpectedTailnetSuffix'。"
    }
    Write-Host "  Backend: $state"
    Write-Host "  Tailnet: $suffix"
}

Invoke-VerificationCheck "Linux 节点身份" {
    if (-not $script:TailscaleStatus) { throw "没有可用的 Tailscale 状态。" }
    $peerCollection = Get-JsonPropertyValue $script:TailscaleStatus "Peer"
    $peerMatches = @()
    if ($peerCollection) {
        foreach ($property in $peerCollection.PSObject.Properties) {
            $peerDns = Normalize-DnsName ([string](Get-JsonPropertyValue $property.Value "DNSName"))
            if ($peerDns -eq $ServerHost) { $peerMatches += $property.Value }
        }
    }
    if ($peerMatches.Count -eq 0) { throw "当前 tailnet 中没有精确匹配的节点 '$ServerHost'。" }
    if ($peerMatches.Count -gt 1) { throw "当前 tailnet 中有多个精确匹配节点 '$ServerHost'，拒绝猜测目标。" }
    $script:MatchedPeer = $peerMatches[0]
    if ((Get-JsonPropertyValue $script:MatchedPeer "Online") -ne $true) { throw "Linux 节点存在但当前离线。" }
    Write-Host "  $ServerHost 在线"
}

Invoke-VerificationCheck "MagicDNS 解析" {
    $addresses = @()
    $resolutionError = $null
    try {
        if (Get-Command Resolve-DnsName -ErrorAction SilentlyContinue) {
            $addresses = @(Resolve-DnsName $ServerHost -ErrorAction Stop |
                Where-Object { $_.Type -eq "A" -or $_.Type -eq "AAAA" } |
                ForEach-Object { $_.IPAddress })
        } else {
            $addresses = @([Net.Dns]::GetHostAddresses($ServerHost) | ForEach-Object { $_.IPAddressToString })
        }
    } catch {
        $resolutionError = $_.Exception.Message
    }

    if ($addresses.Count -eq 0) {
        $fallback = if ($script:MatchedPeer) { Get-TailscalePeerIpv4 $script:MatchedPeer } else { $null }
        if ($fallback) {
            Add-VerificationWarning "MagicDNS 未解析，但已唯一确认在线节点；JK Bridge 可仅本次使用 Tailscale IPv4 $fallback，不会写入配置。"
            return
        }
        $suffix = if ($resolutionError) { "：$resolutionError" } else { "。" }
        throw "没有解析到 A/AAAA 地址，且无唯一在线 peer IPv4 可安全回退$suffix"
    }
    Write-Host "  $ServerHost -> $($addresses -join ', ')"
}

Invoke-VerificationCheck "Windows OpenSSH Client" {
    $openSshDirectory = if ($env:WINDIR) { Join-Path $env:WINDIR "System32\OpenSSH" } else { "" }
    $sshCandidate = if ($openSshDirectory) { Join-Path $openSshDirectory "ssh.exe" } else { "" }
    $keygenCandidate = if ($openSshDirectory) { Join-Path $openSshDirectory "ssh-keygen.exe" } else { "" }
    $script:SshPath = Resolve-NativeCommand "ssh.exe" @($sshCandidate)
    $script:SshKeygenPath = Resolve-NativeCommand "ssh-keygen.exe" @($keygenCandidate)
    if (-not $script:SshPath -or -not $script:SshKeygenPath) {
        throw "找不到 ssh.exe 或 ssh-keygen.exe；请安装 OpenSSH Client 可选功能。"
    }
    Write-Host "  ssh: $script:SshPath"
}

Invoke-VerificationCheck "专用 SSH 私钥" {
    if (-not $env:USERPROFILE) { throw "USERPROFILE 未设置。" }
    $expandedIdentity = [Environment]::ExpandEnvironmentVariables($IdentityFile)
    $expandedIdentity = [IO.Path]::GetFullPath($expandedIdentity)
    if (-not (Test-Path -LiteralPath $expandedIdentity -PathType Leaf)) {
        throw "找不到私钥：$expandedIdentity。请运行 Setup-JKBridge.ps1。"
    }
    Assert-ExactPrivateKeyAcl $expandedIdentity
    if (-not $script:SshKeygenPath) { throw "缺少 ssh-keygen.exe，无法检查私钥。" }
    $keyText = (& $script:SshKeygenPath -y -P '""' -f $expandedIdentity 2>&1 | Out-String)
    $script:ClientFingerprint = Get-Sha256PublicKeyFingerprint $keyText
    if ($LASTEXITCODE -ne 0 -or -not $script:ClientFingerprint) {
        throw "私钥不是可无交互使用的 Ed25519 密钥。"
    }
    Write-Host "  私钥存在、类型正确且不需要口令（未显示密钥内容）。"
}

Invoke-VerificationCheck "转发授权记录" {
    if (-not $script:Config) {
        Add-VerificationWarning "配置不存在，无法核对 Linux 端口授权记录。"
        return
    }
    $authorization = Get-JsonPropertyValue $script:Config "authorization"
    if ($null -eq $authorization -or (Get-JsonPropertyValue $authorization "isKnown") -ne $true) {
        throw "没有可信的 Linux 授权记录；请在新版 JK Bridge 中重新配对一次。"
    }
    $recordedFingerprint = [string](Get-JsonPropertyValue $authorization "clientKeyFingerprint")
    if ($recordedFingerprint -ne $script:ClientFingerprint) {
        throw "授权记录中的客户端指纹与当前专用私钥不一致；请重新配对。"
    }

    $desiredPorts = @(
        $script:Services |
            Where-Object { (Get-JsonPropertyValue $_ "enabled") -ne $false } |
            ForEach-Object { [int](Get-JsonPropertyValue $_ "remotePort") } |
            Sort-Object -Unique
    )
    $grantedPorts = @(
        @(Get-JsonPropertyValue $authorization "remotePorts") |
            ForEach-Object { [int]$_ } |
            Sort-Object -Unique
    )
    $missingPorts = @($desiredPorts | Where-Object { $grantedPorts -notcontains $_ })
    if ($missingPorts.Count -gt 0) {
        throw "启用服务需要尚未授权的 Linux 端口：$($missingPorts -join ', ')。请重新配对。"
    }
    $extraPorts = @($grantedPorts | Where-Object { $desiredPorts -notcontains $_ })
    if ($extraPorts.Count -gt 0) {
        Add-VerificationWarning "Linux 仍保留未使用端口授权：$($extraPorts -join ', ')；建议重新配对收紧权限。"
    }
    Write-Host "  客户端指纹与端口授权记录一致：$($grantedPorts -join ', ')"
}

Invoke-VerificationCheck "固定 SSH 主机密钥" {
    if (-not $script:Config) {
        Add-VerificationWarning "配置不存在，无法检查固定主机密钥。"
        return
    }
    $server = Get-JsonPropertyValue $script:Config "server"
    $pinned = ConvertTo-NormalizedEd25519Key ([string](Get-JsonPropertyValue $server "pinnedHostKey"))
    if (-not $pinned) { throw "config.json 中没有有效的 Ed25519 主机公钥。" }
    $alias = [string](Get-JsonPropertyValue $server "hostKeyAlias")
    if (-not $alias) { throw "config.json 中缺少 hostKeyAlias。" }

    $knownHostsPath = Join-Path $env:LOCALAPPDATA "JKBridge\ssh\known_hosts"
    if (Test-Path -LiteralPath $knownHostsPath -PathType Leaf) {
        $expectedLine = "$alias $pinned"
        $actualLine = (Get-Content -LiteralPath $knownHostsPath -Raw -Encoding UTF8).Trim()
        if ($actualLine -ne $expectedLine) {
            Add-VerificationWarning "应用专用 known_hosts 与 config.json 不一致；它是配置的派生缓存，JK Bridge 下次预检会按已固定公钥安全重建。"
            return
        }
        Write-Host "  known_hosts 与固定公钥一致。"
    } else {
        Add-VerificationWarning "应用尚未生成专用 known_hosts；首次启动时会从已核验的配置生成。"
    }
}

Invoke-VerificationCheck "本地端口" {
    $enabledServices = @($script:Services | Where-Object { (Get-JsonPropertyValue $_ "enabled") -ne $false })
    foreach ($service in $enabledServices) {
        $port = [int](Get-JsonPropertyValue $service "localPort")
        $name = [string](Get-JsonPropertyValue $service "name")
        $listeners = @(Get-TcpListeners $port)
        $exposed = @($listeners | Where-Object { $_.LocalAddress -ne "127.0.0.1" -and $_.LocalAddress -ne "::1" })
        $loopback = @($listeners | Where-Object { $_.LocalAddress -eq "127.0.0.1" })

        if ($exposed.Count -gt 0) {
            $addresses = @($exposed | ForEach-Object { $_.LocalAddress } | Sort-Object -Unique)
            throw "$name 的端口 $port 存在非回环监听 ($($addresses -join ', '))；服务不应暴露到局域网。"
        }
        if ($PortExpectation -eq "Free" -and $listeners.Count -gt 0) {
            throw "$name 的端口 $port 已被占用；启动 JK Bridge 前应为空闲。"
        }
        if ($PortExpectation -eq "Listening" -and $loopback.Count -eq 0) {
            throw "$name 的端口 $port 未在 127.0.0.1 上监听。"
        }

        if ($listeners.Count -eq 0) {
            Write-Host "  $name : 127.0.0.1:$port 空闲"
        } else {
            $pids = @($listeners | ForEach-Object { $_.OwningProcess } | Sort-Object -Unique)
            $listenAddresses = @($listeners | ForEach-Object { $_.LocalAddress } | Sort-Object -Unique)
            Write-Host "  $name : $($listenAddresses -join ', '):$port 正在监听（PID $($pids -join ', ')）"
        }
    }
}

if ($TestServices) {
    Invoke-VerificationCheck "本地服务健康" {
        if ($PortExpectation -ne "Listening") {
            Add-VerificationWarning "建议同时指定 -PortExpectation Listening。"
        }
        foreach ($service in @($script:Services | Where-Object { (Get-JsonPropertyValue $_ "enabled") -ne $false })) {
            Test-ServiceEndpoint $service
        }
    }
}

Write-Host "`n========== 验证摘要 ==========" -ForegroundColor Cyan
Write-Host "失败：$($script:Failures.Count)；警告：$($script:Warnings.Count)"
foreach ($warning in $script:Warnings) { Write-Host "WARN  $warning" -ForegroundColor Yellow }
foreach ($failure in $script:Failures) { Write-Host "FAIL  $failure" -ForegroundColor Red }

if ($script:Failures.Count -gt 0) {
    Write-Host "`n验证未通过。请修复 FAIL 项后重试。" -ForegroundColor Red
    exit 1
}

Write-Host "`nWindows 环境验证通过。" -ForegroundColor Green
exit 0
