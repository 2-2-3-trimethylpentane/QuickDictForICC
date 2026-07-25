# 获取脚本所在目录，基于此计算相对路径
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

# 查杀 InkCanvasForClass 进程
Get-Process -Name "InkCanvasForClass" -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host "已终止 InkCanvasForClass 进程"

# 清除插件自动禁用状态，确保每次测试都是干净状态
$iccConfigDir = "$repoRoot\community\Ink Canvas\bin\Debug\AnyCPU\net6.0-windows10.0.19041.0\Configs"
$filesToClean = @("disabled_plugins.json", "plugin_error_recovery.json")
foreach ($file in $filesToClean) {
    $filePath = Join-Path $iccConfigDir $file
    if (Test-Path $filePath) {
        Remove-Item $filePath -Force
        Write-Host "已清除 $file"
    }
}

# 编译主程序
Write-Host "正在编译主程序..."
dotnet build "$repoRoot\community\Ink Canvas\InkCanvasForClass.csproj" -c Debug
if ($LASTEXITCODE -ne 0) { Write-Host "主程序编译失败！" -ForegroundColor Red; exit 1 }

# 编译子项目（控件库和插件SDK）
Write-Host "正在编译控件库 InkCanvas.Controls..."
dotnet build "$repoRoot\community\InkCanvas.Controls\InkCanvas.Controls.csproj" -c Debug
if ($LASTEXITCODE -ne 0) { Write-Host "控件库编译失败！" -ForegroundColor Red; exit 1 }

Write-Host "正在编译插件SDK InkCanvas.PluginSdk..."
dotnet build "$repoRoot\community\InkCanvas.PluginSdk\InkCanvas.PluginSdk.csproj" -c Debug
if ($LASTEXITCODE -ne 0) { Write-Host "插件SDK编译失败！" -ForegroundColor Red; exit 1 }

# 将子项目输出的 dll 更新到示例插件的 lib 目录
$subProjectsOutput = "$repoRoot\community"
$libDir = "$scriptDir\lib"

if (-not (Test-Path $libDir)) {
    New-Item -ItemType Directory -Path $libDir -Force | Out-Null
    Write-Host "已创建 lib 目录"
}

$framework = "net6.0-windows10.0.19041.0"
$dllsToUpdate = @(
    @{ Name = "InkCanvas.Controls"; Path = "$subProjectsOutput\InkCanvas.Controls\bin\Debug\$framework\InkCanvas.Controls.dll" },
    @{ Name = "InkCanvas.PluginSdk"; Path = "$subProjectsOutput\InkCanvas.PluginSdk\bin\Debug\$framework\InkCanvas.PluginSdk.dll" }
)

foreach ($dll in $dllsToUpdate) {
    if (Test-Path $dll.Path) {
        Copy-Item -Path $dll.Path -Destination $libDir -Force
        Write-Host "已更新 $($dll.Name).dll 到 lib 目录"
    } else {
        Write-Host "警告：未找到 $($dll.Name).dll ($($dll.Path))" -ForegroundColor Yellow
    }
}

# 编译 QuickDict 插件
Write-Host "正在编译 QuickDict 插件..."
dotnet build "$scriptDir\QuickDictForICC.csproj" -c Debug
if ($LASTEXITCODE -ne 0) { Write-Host "QuickDict 插件编译失败！" -ForegroundColor Red; exit 1 }

# 复制插件输出到 Plugins/<插件Id> 目录（新目录结构，支持 manifest.json）
$pluginSource = "$scriptDir\bin\Debug\net6.0-windows10.0.19041.0"
$pluginsDir = "$repoRoot\community\Ink Canvas\bin\Debug\AnyCPU\net6.0-windows10.0.19041.0\Plugins\quickdict.plugin"

if (-not (Test-Path $pluginsDir)) {
    New-Item -ItemType Directory -Path $pluginsDir -Force | Out-Null
    Write-Host "已创建 Plugins 目录"
}

# 清空旧文件后复制
Get-ChildItem $pluginsDir -Recurse | Remove-Item -Recurse -Force
Copy-Item -Path "$pluginSource\*" -Destination $pluginsDir -Recurse -Force
Write-Host "已复制插件到 Plugins\quickdict.plugin 目录"

# 启动主程序
Write-Host "正在启动 InkCanvasForClass..."
Start-Process "$repoRoot\community\Ink Canvas\bin\Debug\AnyCPU\net6.0-windows10.0.19041.0\InkCanvasForClass.exe"
