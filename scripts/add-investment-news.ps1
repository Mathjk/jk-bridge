[CmdletBinding()]
param(
    [string]$ConfigPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

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
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    } else {
        $property.Value = $Value
    }
}

if (-not $env:LOCALAPPDATA) {
    throw "LOCALAPPDATA 未设置；请在 Windows 用户会话中运行此脚本。"
}
if (-not $ConfigPath) {
    $ConfigPath = Join-Path $env:LOCALAPPDATA "JKBridge\config.json"
}
$ConfigPath = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($ConfigPath))

if (Get-Process -Name "JKBridge" -ErrorAction SilentlyContinue) {
    throw "请先从托盘退出 JK Bridge，避免它在脚本写入后用旧配置覆盖文件。"
}
if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw "找不到 JK Bridge 配置：$ConfigPath。请先运行 JK Bridge 并完成初次配置。"
}

try {
    $config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
} catch {
    throw "配置不是有效 JSON，未做任何更改：$ConfigPath"
}
if ($null -eq $config) {
    throw "配置内容为空，未做任何更改：$ConfigPath"
}

$schemaVersion = Get-JsonPropertyValue $config "schemaVersion"
if ($null -ne $schemaVersion -and [int]$schemaVersion -notin @(1, 2, 3)) {
    throw "不支持 schemaVersion=$schemaVersion，未做任何更改。"
}

$servicesValue = Get-JsonPropertyValue $config "services"
if ($null -eq $servicesValue) {
    throw "配置缺少 services，未做任何更改。"
}
$services = @($servicesValue)
$sameId = @($services | Where-Object { [string](Get-JsonPropertyValue $_ "id") -eq "investment-news" })
if ($sameId.Count -gt 1) {
    throw "配置中存在多个 investment-news 服务，请先人工消除重复项。"
}

$portConflict = $services | Where-Object {
    [string](Get-JsonPropertyValue $_ "id") -ne "investment-news" -and
    [int](Get-JsonPropertyValue $_ "localPort") -eq 8793
} | Select-Object -First 1
if ($null -ne $portConflict) {
    $conflictName = [string](Get-JsonPropertyValue $portConflict "name")
    throw "Windows 本地端口 8793 已被服务 '$conflictName' 使用，未做任何更改。"
}

$definition = [pscustomobject][ordered]@{
    id = "investment-news"
    name = "Investment News"
    description = "A股产业链资讯与 AI 每日要点"
    icon = "Newspaper"
    enabled = $true
    favorite = $true
    localPort = 8793
    remoteHost = "127.0.0.1"
    remotePort = 8793
    openUrl = "http://127.0.0.1:8793/"
    healthPath = "/"
    healthKind = "httpStatus"
}

if ($sameId.Count -eq 1) {
    $target = $sameId[0]
    foreach ($property in $definition.PSObject.Properties) {
        Set-JsonPropertyValue $target $property.Name $property.Value
    }
    $action = "已更新"
} else {
    Set-JsonPropertyValue $config "services" @($services + $definition)
    $action = "已添加"
}

$directory = Split-Path -Parent $ConfigPath
$temporaryPath = Join-Path $directory (".{0}.{1}.tmp" -f ([IO.Path]::GetFileName($ConfigPath)), [Guid]::NewGuid().ToString("N"))
$backupPath = $ConfigPath + "." + (Get-Date -Format "yyyyMMdd-HHmmss") + ".bak"
$utf8WithoutBom = New-Object Text.UTF8Encoding($false)

try {
    $json = $config | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($temporaryPath, $json + [Environment]::NewLine, $utf8WithoutBom)
    [IO.File]::Replace($temporaryPath, $ConfigPath, $backupPath, $true)
} finally {
    if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}

Write-Host "$action Investment News：Windows 127.0.0.1:8793 -> Linux 127.0.0.1:8793" -ForegroundColor Green
Write-Host "原配置备份：$backupPath"
Write-Host "重新启动 JK Bridge 后即可连接；若 Linux 专用密钥尚未授权 8793，请重新执行一次配对。"
