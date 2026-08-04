# 验证 ICC 是否支持白板插件组件注册
param(
    [Parameter(Mandatory=$true)]
    [string]$IccExePath
)

if (-not (Test-Path $IccExePath)) {
    Write-Error "找不到 ICC 主程序: $IccExePath"
    exit 1
}

$dir = Split-Path $IccExePath -Parent
$pluginSdk = Join-Path $dir "InkCanvas.PluginSdk.dll"

if (-not (Test-Path $pluginSdk)) {
    Write-Warning "未找到 InkCanvas.PluginSdk.dll，尝试直接搜索主程序集"
}

# 用字符串匹配查找 RegisterBoardToolbarItem（无需加载依赖程序集）
$bytes = [System.IO.File]::ReadAllBytes($IccExePath)
$text = [System.Text.Encoding]::UTF8.GetString($bytes)

if ($text.Contains('RegisterBoardToolbarItem')) {
    Write-Host '支持白板插件组件注册 (RegisterBoardToolbarItem 已找到)' -ForegroundColor Green
} else {
    Write-Host '不支持白板插件组件注册 (RegisterBoardToolbarItem 未找到)' -ForegroundColor Red
}
