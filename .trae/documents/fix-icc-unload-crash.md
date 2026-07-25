# 修复 QuickDict 插件被 ICC 卸载导致崩溃的问题

## 摘要

QuickDict 插件在 ICC 宿主中加载时被卸载，最可能的原因是 **Initialize() 方法在主线程上同步加载大型词典文件（ECDICT CSV / MDict .mdx）**，导致 UI 线程长时间阻塞，触发 ICC 的插件加载超时/看门狗机制。本计划通过将词典加载改为后台异步、延迟初始化 WebView2、加固 TTS 服务构造过程等方式修复该问题。

## 当前状态分析

### 已识别的关键风险点

1. **同步阻塞式词典加载（高风险）**
   - 文件：`QuickDictPlugin.cs` 第 78-85 行
   - 问题：`InitializeServices()` 在主线程直接调用 `_dictionaryService.Load()`，内部 `EcDictService.Load()` 和 `MDictService.Load()` 会同步读取并解析可能达到数十 MB 的词典文件。ICC 对插件初始化通常有严格的时间限制，长时间阻塞会导致宿主判定插件无响应并强制卸载。

2. **TTS 服务构造函数未保护（中风险）**
   - 文件：`Services/TtsService.cs` 第 25-32 行
   - 问题：`Directory.CreateDirectory(_cacheDirectory)` 未包裹 try-catch，若临时目录不可写会直接在初始化阶段抛出异常。

3. **WebView2 在弹窗创建时才首次初始化（中风险）**
   - 文件：`Views/ResultView.xaml.cs`（待确认具体实现）
   - 问题：ResultView 首次构造时会初始化 WebView2。若目标机器未安装 WebView2 Runtime，首次打开弹窗时可能抛出 `WebView2RuntimeNotFoundException`。虽然这不影响 ICC 加载阶段，但属于运行时崩溃隐患。

4. **弹窗内容工厂未做异常隔离（中风险）**
   - 文件：`QuickDictPlugin.cs` 第 138-157 行
   - 问题：`CreatePopupContent()` 直接构造 `ResultView`、`DictionaryPopup`、`PopupShellContent`，若其中任何一步抛出异常，会向上传播到 ICC，可能导致工具栏本身崩溃。

### 已确认的非问题点

- `SettingsManager.Load()` 已有 try-catch，安全。
- `NotifyIfDictionaryUnavailable()` 中 `GetServiceOrDefault<INotificationService>()` 已做异常保护。
- `MDictService.Load()` 和 `EcDictService.Load()` 内部已有 try-catch，但阻塞问题无法通过捕获异常解决。

## 拟议改动

### 改动 1：将词典加载改为后台异步（核心修复）

**涉及文件**
- `QuickDictPlugin.cs`
- `Services/IDictionaryService.cs`
- `Services/DictionaryService.cs`
- `Services/EcDictService.cs`
- `Services/MDictService.cs`
- `Views/DictionaryPopup.xaml`
- `Views/DictionaryPopup.xaml.cs`

**修改内容**
1. 在 `IDictionaryService` 中新增异步加载接口：
   ```csharp
   Task LoadAsync(CancellationToken cancellationToken = default);
   ```
2. 为 `EcDictService`、`MDictService`、`DictionaryService` 实现 `LoadAsync`，将同步文件 I/O 和解析逻辑迁移到后台线程（使用 `Task.Run`）。
3. 在 `QuickDictPlugin.Initialize` 中改用 `Task.Run(() => _dictionaryService.LoadAsync(...))` 或 `await` 启动后台加载，确保 `Initialize` 立即返回，不阻塞 ICC。
4. 在 `DictionaryPopup.xaml` 中添加轻量级的"词典加载中..."提示覆盖层。
5. 在 `DictionaryPopup.xaml.cs` 中监听加载完成事件/状态：加载完成前禁用搜索按钮，加载完成后启用。

**原因**
ICC 插件初始化必须在短时间内完成。将耗时操作移到后台线程是避免被看门狗卸载的根本方法。

### 改动 2：加固 TTS 服务构造函数

**涉及文件**
- `Services/TtsService.cs`

**修改内容**
1. 将 `Directory.CreateDirectory(_cacheDirectory)` 包裹在 try-catch 中。
2. 若缓存目录创建失败，将 `_cacheDirectory` 设为 `null` 或回退到临时根目录，并在后续缓存写入时跳过。
3. 构造函数中不立即初始化任何可能失败的网络或外部组件（当前 `EdgeTtsEngine` 和 `PiperTtsEngine` 构造简单，符合要求）。

**原因**
防止因临时目录权限问题导致插件在初始化阶段直接崩溃。

### 改动 3：延迟 ResultView / WebView2 初始化并隔离异常

**涉及文件**
- `QuickDictPlugin.cs`
- `Views/ResultView.xaml.cs`

**修改内容**
1. 在 `CreatePopupContent()` 中，将整个内容创建过程包裹在 try-catch 中。若创建失败，返回一个仅显示错误信息的 `TextBlock` 或 `Border`，而不是让异常向上传播。
2. 在 `ResultView` 构造函数中，将 WebView2 的显式初始化（`EnsureCoreWebView2Async` 等）延迟到 `Loaded` 事件，并捕获 `WebView2RuntimeNotFoundException` 等异常，给出友好提示。
3. 如果 WebView2 初始化失败，回退到纯文本渲染模式（TextBlock），确保弹窗仍可显示 ECDICT 结果。

**原因**
WebView2 Runtime 在某些教学机房可能缺失，延迟初始化并降级可避免弹窗打开时直接崩溃。

### 改动 4：为弹窗创建添加异常隔离

**涉及文件**
- `QuickDictPlugin.cs`

**修改内容**
1. 将 `CreatePopupContent()` 方法体用 try-catch 包裹。
2. 捕获后记录日志（`_host.LogError`）。
3. 返回一个简单错误提示 UI（例如带错误信息的 `TextBlock`），避免 ICC 工具栏崩溃。

**原因**
即使弹窗内部某组件初始化失败，也不应导致整个插件或宿主工具栏崩溃。

### 改动 5：添加初始化超时保护（可选但推荐）

**涉及文件**
- `QuickDictPlugin.cs`

**修改内容**
1. 在 `Initialize` 中使用 `CancellationTokenSource` 设置后台加载任务的取消令牌（例如 30 秒）。
2. 若超时仍未完成，取消加载并通过日志/通知告知用户词典加载未完成。

**原因**
避免极端情况下（损坏的 .mdx 文件导致解析死循环）后台任务无限运行。

## 假设与决策

1. **ICC 插件初始化必须在主线程快速返回**：这是 ICC 插件 SDK 的通用约束，也是本次崩溃的最可能原因。
2. **词典文件加载不可跳过**：用户需要查词功能，因此加载必须发生，但可以在后台进行。
3. **后台加载期间弹窗应给出明确反馈**：通过禁用搜索按钮和显示"加载中"提示，避免用户困惑。
4. **WebView2 不是必需的**：若 WebView2 Runtime 缺失，纯文本模式足以展示 ECDICT 结果。
5. **不修改现有 MDict 解析算法**：当前最小化解析器已实现 v1/v2 支持，本次只改变加载时机和异常处理方式，不动解析逻辑。

## 验证步骤

1. **编译验证**：运行 `dotnet build -c Release`，确保 0 错误 0 警告。
2. **打包验证**：运行 `pack.ps1`，确认 `packages/quickdict.plugin.icpx` 正常生成。
3. **静态代码审查**：
   - 确认 `QuickDictPlugin.Initialize()` 中不再直接调用同步 `_dictionaryService.Load()`。
   - 确认 `IDictionaryService` 包含 `LoadAsync` 方法。
   - 确认 `TtsService` 构造函数对 `Directory.CreateDirectory` 有 try-catch。
   - 确认 `CreatePopupContent()` 有 try-catch 异常隔离。
4. **宿主测试（需要 ICC 环境）**：
   - 将 `.icpx` 放入 ICC 插件目录并启动 ICC。
   - 确认插件成功加载，工具栏出现 QuickDict 按钮。
   - 确认点击按钮后弹窗正常打开。
   - 配置 ECDICT/MDict 路径后，确认后台加载完成并能正常查词。
5. **异常场景测试**：
   - 故意设置一个不存在或损坏的词典路径，确认插件启动不崩溃，仅提示加载失败。
   - 在没有 WebView2 Runtime 的环境中打开弹窗，确认能降级到文本模式。
