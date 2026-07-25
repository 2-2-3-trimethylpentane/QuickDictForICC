# 修复 QuickDict 插件加载失败（第二轮）

## 摘要

上一轮已修复 SettingsView 空引用和 NAudio 依赖缺失，但用户反馈"问题依旧"。经深入探索，发现以下残留问题需一并解决：

1. **SettingsView 仍用 XAML 内联事件绑定**：虽然已加 null 检查，但更稳健的做法是改为代码后置挂接（与 DictionaryPopup / ResultView 一致），彻底消除 XAML 初始化时序问题。
2. **插件已被自动禁用**：ICC `PluginErrorRecoveryService` 在 3 次失败后将插件标记为 auto-disabled 并持久化到 `Configs\plugin_error_recovery.json` 和 `Configs\disabled_plugins.json`。即使代码已修复，重新加载时 ICC 仍会跳过已禁用的插件。`build-and-run.ps1` 需在启动前清除这些状态文件。
3. **`TtsService.Dispose()` 在 NAudio.WinMM 缺失时抛异常**：JIT 编译 Dispose 方法时需解析 `WaveOutEvent` 类型，若 NAudio.WinMM.dll 不可用则抛 `FileNotFoundException`。虽然 `Shutdown()` 已有 try-catch，但应让 Dispose 本身更健壮。
4. **`TaskScheduler.FromCurrentSynchronizationContext()` 可能抛异常**：若 `Initialize` 在非 UI 线程调用，`SynchronizationContext.Current` 为 null，`FromCurrentSynchronizationContext()` 抛 `InvalidOperationException`，导致 `InitializeServices()` 崩溃。
5. **`Initialize()` 缺少分步异常隔离**：`InitializeServices`、`InitializeSettingsView`、`RegisterToolbarItem` 三步中任一步抛异常都会导致整个插件加载失败。应分步 try-catch，保证部分初始化失败不影响其他步骤。

## 当前状态分析

### 已确认到位的修复（上一轮）

- `SettingsView.xaml.cs`：`UpdateEnginePanels()` 和 `UpdateRateDisplay()` 已有 null 检查 ✓
- `QuickDictForICC.csproj`：`<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` 已设置 ✓

### 新发现的问题

| # | 问题 | 风险等级 | 位置 |
|---|------|---------|------|
| 1 | SettingsView 用 XAML 内联事件，初始化时序依赖 null 检查 | 高 | `Views/SettingsView.xaml` + `.xaml.cs` |
| 2 | 插件被 auto-disabled，重载时被跳过 | 高 | ICC `Configs/` 目录下的持久化文件 |
| 3 | TtsService.Dispose() JIT 时需 NAudio.WinMM | 中 | `Services/TtsService.cs` |
| 4 | TaskScheduler.FromCurrentSynchronizationContext() 可能抛异常 | 中 | `QuickDictPlugin.cs` InitializeServices |
| 5 | Initialize() 无分步异常隔离 | 中 | `QuickDictPlugin.cs` Initialize |
| 6 | icon.png 缺失（manifest 声明了但文件不存在） | 低 | `manifest.json` + 项目根目录 |

## 拟议改动

### 改动 1：SettingsView 改为代码后置事件挂接（核心修复）

**涉及文件**
- `Views/SettingsView.xaml`
- `Views/SettingsView.xaml.cs`

**修改内容**

1. **XAML**：移除所有内联事件绑定属性
   - `TtsEngineComboBox`：移除 `SelectionChanged="TtsEngineComboBox_SelectionChanged"`
   - `EdgeRateSlider`：移除 `ValueChanged="EdgeRateSlider_ValueChanged"`
   - 6 个 Browse 按钮：移除 `Click="BrowseXxxButton_Click"`
   - `SaveButton`：移除 `Click="SaveButton_Click"`
   - `ClearCacheButton`：移除 `Click="ClearCacheButton_Click"`
   - 保留 `SelectedIndex="0"` 和 `Value="0"` 作为 XAML 中的初始值

2. **xaml.cs 构造函数**：在 `InitializeComponent()` 之后通过 `+=` 挂接所有事件
   ```csharp
   public SettingsView(PluginSettings settings, IPluginHost host = null, Action onSettingsSaved = null)
   {
       InitializeComponent();

       _settings = settings ?? throw new ArgumentNullException(nameof(settings));
       _host = host;
       _onSettingsSaved = onSettingsSaved;

       // 代码后置挂接事件，避免 XAML 初始化阶段触发事件时控件尚未构造。
       TtsEngineComboBox.SelectionChanged += TtsEngineComboBox_SelectionChanged;
       EdgeRateSlider.ValueChanged += EdgeRateSlider_ValueChanged;
       BrowseEcDictButton.Click += BrowseEcDictButton_Click;
       BrowseMDictButton.Click += BrowseMDictButton_Click;
       BrowseMDictResourceButton.Click += BrowseMDictResourceButton_Click;
       BrowsePiperExecutableButton.Click += BrowsePiperExecutableButton_Click;
       BrowsePiperModelButton.Click += BrowsePiperModelButton_Click;
       SaveButton.Click += SaveButton_Click;
       ClearCacheButton.Click += ClearCacheButton_Click;

       LoadSettingsIntoUi();
   }
   ```

3. **保留现有 null 检查**作为双重防御（belt and suspenders）。

**原因**

DictionaryPopup 和 ResultView 已采用此模式，天然规避 XAML 解析时序问题。SettingsView 改为一致风格后，`InitializeComponent()` 期间 `SelectedIndex="0"` 和 `Value="0"` 的设置不会触发任何事件处理程序，从根本上消除空引用风险。

### 改动 2：Initialize() 分步异常隔离

**涉及文件**
- `QuickDictPlugin.cs`

**修改内容**

将 `Initialize()` 中的三个初始化步骤各自用 try-catch 包裹：

```csharp
public override void Initialize(IPluginHost host, IServiceCollection services)
{
    base.Initialize(host, services);
    _host = host;

    try { _settings = SettingsManager.Load(); }
    catch (Exception ex)
    {
        _settings = new PluginSettings();
        host?.LogError("加载 QuickDict 设置失败，已使用默认设置", ex);
    }

    try { InitializeServices(); }
    catch (Exception ex) { host?.LogError("初始化 QuickDict 服务失败", ex); }

    try { InitializeSettingsView(); }
    catch (Exception ex) { host?.LogError("初始化 QuickDict 设置面板失败", ex); }

    try { RegisterToolbarItem(); }
    catch (Exception ex) { host?.LogError("注册 QuickDict 工具栏按钮失败", ex); }

    Log(string.Format("{0} 已初始化", Name));
}
```

同时修改 `GetSettingsView()` 返回前检查 null：
```csharp
public override object GetSettingsView()
{
    return _settingsView;
}
```
（PluginBase 默认返回 null，所以 `_settingsView` 为 null 时是安全的。）

**原因**

即使 SettingsView 创建失败，插件仍可注册工具栏按钮（弹窗创建已有独立 try-catch）。即使 InitializeServices 失败，设置面板仍可显示。分步隔离最大化插件的部分可用性。

### 改动 3：TaskScheduler 回退保护

**涉及文件**
- `QuickDictPlugin.cs`

**修改内容**

在 `InitializeServices()` 中，将 `TaskScheduler.FromCurrentSynchronizationContext()` 改为安全获取：

```csharp
// 获取 UI 线程同步上下文；若当前线程无同步上下文则回退到默认调度器。
System.Threading.Tasks.TaskScheduler uiScheduler;
try
{
    uiScheduler = System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext();
}
catch
{
    uiScheduler = System.Threading.Tasks.TaskScheduler.Default;
}

_ = _dictionaryLoadTask.ContinueWith(_ =>
{
    try { NotifyIfDictionaryUnavailable(); }
    catch (Exception ex) { _host?.LogError("检查 QuickDict 词典可用性时出错", ex); }
}, uiScheduler);
```

**原因**

若 ICC 在非 UI 线程调用 `Initialize`，`SynchronizationContext.Current` 为 null，`FromCurrentSynchronizationContext()` 抛 `InvalidOperationException`。此异常会导致 `InitializeServices()` 崩溃，进而导致整个插件加载失败。

### 改动 4：TtsService.Dispose() 健壮化

**涉及文件**
- `Services/TtsService.cs`

**修改内容**

将 `Dispose()` 方法整体包裹在 try-catch 中：

```csharp
public void Dispose()
{
    try
    {
        Stop();
        _currentWaveOut?.Dispose();
        _currentWaveOut = null;
        _currentPlaybackCts?.Dispose();
        _currentPlaybackCts = null;
        _playbackSemaphore?.Dispose();
    }
    catch
    {
        // 尽力清理；NAudio 等依赖程序集可能在卸载后不可用。
    }
}
```

**原因**

`Dispose()` 方法体引用 `WaveOutEvent` 类型（`_currentWaveOut` 字段）。JIT 编译时需加载 `NAudio.WinMM.dll`。若该程序集不可用（如插件 AssemblyLoadContext 已卸载），会抛 `FileNotFoundException`。虽然 `Shutdown()` 已有外层 try-catch，但让 Dispose 自身不抛异常更干净。

### 改动 5：build-and-run.ps1 清除插件禁用状态

**涉及文件**
- `build-and-run.ps1`

**修改内容**

在第 6 行（杀进程）之后、第 9 行（编译）之前，插入清除 ICC 插件错误恢复状态的代码：

```powershell
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
```

**原因**

ICC 的 `PluginErrorRecoveryService` 在 3 次失败后自动禁用插件并持久化。即使代码已修复，重新运行时 ICC 仍会读取 `plugin_error_recovery.json` 判断 `IsAutoDisabled`，若为 true 则跳过加载。在开发调试阶段，每次运行前清除这些状态文件可以确保插件被重新加载。

### 改动 6：manifest.json 移除缺失的 Icon 声明（可选）

**涉及文件**
- `manifest.json`

**修改内容**

由于 `icon.png` 文件不存在，将 manifest.json 中的 `"Icon": "icon.png"` 改为 `"Icon": ""`。

**原因**

避免 ICC 在加载插件时尝试读取不存在的图标文件。虽然从日志看这不是崩溃原因，但清理声明可以减少不必要的文件 I/O 和潜在警告。

## 假设与决策

1. **用户此前未成功重新构建**：日志中的行号 (line 62) 对应修复前的代码，说明运行的是旧构建。本计划的所有改动需要用户通过 `build-and-run.ps1` 重新构建后才能生效。
2. **SettingsView 改为代码后置挂接优于 null 检查**：两种方案都能解决问题，但代码后置挂接更彻底（事件根本不会在 XAML 初始化阶段触发），且与项目中其他两个 View 一致。
3. **不修改 ICC PluginManager 代码**：`UnloadPlugin` 中的 null 引用问题位于 ICC 项目中（`community\Ink Canvas\Plugins\PluginManager.cs`），超出当前工作目录范围。且一旦插件加载成功，该路径不会被触发。
4. **build-and-run.ps1 使用 Debug 配置**：脚本中所有 `dotnet build` 均使用 `-c Debug`，插件输出从 `bin\Debug\` 复制到 ICC 的 Plugins 目录。本计划的所有代码改动会在 Debug 构建中生效。
5. **保留 null 检查作为双重防御**：即使改为代码后置挂接，`UpdateEnginePanels()` 和 `UpdateRateDisplay()` 中的 null 检查仍然保留，防止未来如果有人重新在 XAML 中添加事件绑定时回归。

## 验证步骤

1. **编译验证**：
   ```powershell
   dotnet build QuickDictForICC.csproj -c Debug
   ```
   确保 0 错误 0 警告。

2. **全流程验证**：
   ```powershell
   .\build-and-run.ps1
   ```
   脚本应依次：杀进程 → 清除禁用状态 → 编译 ICC → 编译插件 → 复制到 Plugins 目录 → 启动 ICC。

3. **ICC 日志验证**：
   - 确认日志中出现 `Loading plugin: QuickDict 查词`
   - 确认不再出现 `NullReferenceException at SettingsView.UpdateEnginePanels`
   - 确认不再出现 `auto-disabled after 3 failures`
   - 确认出现 `QuickDict 查词 已初始化`
   - 确认出现 `Plugin loading complete. Loaded 1 plugins`（或更多）

4. **功能验证**：
   - ICC 工具栏出现 QuickDict 查词按钮
   - 点击按钮弹出查词弹窗
   - 打开设置面板能正常切换 TTS 引擎（Edge/Piper 面板正确显隐）
   - 设置面板能保存设置

5. **异常场景验证**：
   - 不配置词典路径时，插件仍能正常加载（仅提示未找到词典）
   - 配置不存在的词典路径时，插件不崩溃
