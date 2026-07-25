# 修复 QuickDict 工具栏按钮不显示问题

## 一、问题摘要

ICC 日志显示 `Plugin registered toolbar item: quickdict.button` 成功注册，但主工具栏中**未显示** QuickDict 按钮。根因：插件调用 `RegisterToolbarItem()` 时只设置了 `PopupContentFactory`，**未设置必需的 `ViewFactory` 字段**，导致 `PluginToolbarItemWrapper.BuildView()` 在 [ToolbarRegistry.cs:1202](file:///c:/Users/Lenovo/Desktop/project/community/Ink%20Canvas/Controls/Toolbar/FloatingToolbar/ToolbarRegistry.cs#L1202) 处返回 `null`，按钮在 `FlattenEntries()` 中被静默跳过。

> 关键认知：日志中的 "registered toolbar item" 仅表示插件项被加入到 `ToolbarRegistry._pluginItems` 列表，不代表按钮被渲染到 UI。`IconGeometry`（字符串）和 `DisplayName` 只是元数据，wrapper **不会**基于它们自动构造按钮，插件必须在 `ViewFactory` 中自行构造 `ToolbarImageButton`。

## 二、当前状态分析

### 当前 [QuickDictPlugin.cs](file:///c:/Users/Lenovo/Desktop/project/QuickDictForICC/QuickDictPlugin.cs) 的注册代码（第 196-206 行）

```csharp
private void RegisterToolbarItem()
{
    _host?.RegisterToolbarItem(new PluginToolbarItemInfo
    {
        Id = "quickdict.button",
        DisplayName = "QuickDict 查词",
        Description = "打开 QuickDict 英语单词查询弹窗",
        IconGeometry = "M15.5 14h-.79l-.28-.27C15.41 12.59 ...",
        PopupContentFactory = CreatePopupContent
        // ❌ 缺少 ViewFactory
        // ❌ 缺少 ApplyOrientation
    });
}
```

### ICC 主程序期望（[IPluginHost.cs:85-101](file:///c:/Users/Lenovo/Desktop/project/community/InkCanvas.PluginSdk/IPluginHost.cs#L85-L101)）

```csharp
public class PluginToolbarItemInfo
{
    public string Id { get; set; }
    public string DisplayName { get; set; }
    public string Description { get; set; }
    public string IconGeometry { get; set; }
    public Func<FrameworkElement> ViewFactory { get; set; }              // ← 必需！
    public Action<FrameworkElement, Orientation> ApplyOrientation { get; set; }
    public Action<FrameworkElement, Dictionary<string, object>> ApplySettings { get; set; }
    public List<PluginToolbarSettingInfo> CustomSettings { get; set; } = new List<PluginToolbarSettingInfo>();
    public Func<FrameworkElement> PopupContentFactory { get; set; }      // 仅当 ViewFactory 返回 ToolbarImageButton 时生效
}
```

### `ToolbarImageButton` 控件关键属性（[ToolbarImageButton.xaml.cs](file:///c:/Users/Lenovo/Desktop/project/community/InkCanvas.Controls/ToolbarImageButton.xaml.cs)）

- `Label`（string）：按钮文字标签
- `IconGeometryDrawing`（`GeometryDrawing`）：图标几何图形（**不是** path 字符串，需要转换）
- `ApplyOrientation(bool isVertical)`：应用横/纵向布局
- `ApplyCompactMode(bool compact)`：紧凑模式

## 三、修复方案

### 修改文件：[QuickDictPlugin.cs](file:///c:/Users/Lenovo/Desktop/project/QuickDictForICC/QuickDictPlugin.cs)

#### 1. 补充 using 引用

在文件头部添加 `System.Windows.Media` 命名空间引用（用于 `GeometryDrawing`、`Geometry`、`SolidColorBrush` 等）。

```csharp
using System.Windows.Media;
```

> 注：当前已有 `using System.Windows;`，但未导入 `System.Windows.Media`。

#### 2. 重写 `RegisterToolbarItem()` 方法

在 `ViewFactory` 中构造 `ToolbarImageButton` 实例，将 `IconGeometry` 字符串解析为 `GeometryDrawing`，设置 `Label`，并提供 `ApplyOrientation` 委托适配 `ToolbarImageButton.ApplyOrientation(bool isVertical)` 签名。

```csharp
private void RegisterToolbarItem()
{
    const string searchIconPath =
        "M15.5 14h-.79l-.28-.27C15.41 12.59 16 11.11 16 9.5 16 5.91 13.09 3 9.5 3S3 5.91 3 9.5 5.91 16 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zM9.5 14C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z";

    _host?.RegisterToolbarItem(new PluginToolbarItemInfo
    {
        Id = "quickdict.button",
        DisplayName = "QuickDict 查词",
        Description = "打开 QuickDict 英语单词查询弹窗",
        IconGeometry = searchIconPath,
        ViewFactory = () => CreateToolbarButton(searchIconPath),
        ApplyOrientation = (view, orientation) =>
        {
            if (view is ToolbarImageButton btn)
                btn.ApplyOrientation(orientation == Orientation.Vertical);
        },
        PopupContentFactory = CreatePopupContent
    });
}

/// <summary>
/// 构造工具栏按钮视图。将 path 字符串解析为 GeometryDrawing 并赋给 ToolbarImageButton。
/// </summary>
private FrameworkElement CreateToolbarButton(string iconPath)
{
    try
    {
        var geometry = Geometry.Parse(iconPath);
        var drawing = new GeometryDrawing
        {
            Geometry = geometry,
            Brush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33))
        };

        var button = new ToolbarImageButton
        {
            Label = "查词",
            IconGeometryDrawing = drawing
        };

        return button;
    }
    catch (Exception ex)
    {
        _host?.LogError("构造 QuickDict 工具栏按钮视图失败", ex);
        // 失败时返回一个退化的 TextBlock，至少保证 BuildView 不返回 null
        return new System.Windows.Controls.TextBlock
        {
            Text = "QuickDict",
            Margin = new Thickness(4)
        };
    }
}
```

### 关键修复点说明

| 项目 | 修复前 | 修复后 |
|------|--------|--------|
| `ViewFactory` | null（按钮被静默跳过） | 返回 `ToolbarImageButton` 实例 |
| `ApplyOrientation` | 未设置（按钮无法适配横/纵向） | 委托适配 `ToolbarImageButton.ApplyOrientation(bool)` |
| `IconGeometry` 字符串→图形 | 仅作为元数据保存，未被使用 | 在 `ViewFactory` 中解析为 `GeometryDrawing` 并赋给按钮 |
| 异常容错 | 无 | 构造失败时返回退化 `TextBlock`，避免 `BuildView` 返回 null |

## 四、假设与决策

1. **图标颜色**：使用深灰色 `#333333`（与 ICC 默认工具栏图标风格一致）。如需调整可后续修改。
2. **按钮文字标签**：使用 "查词"（简短），与 ICC 其他工具栏按钮（如"墨迹"、"选择"）风格一致，避免过长。
3. **不修改 `PopupContentFactory` 和 `CreatePopupContent()`**：弹窗内容构造逻辑已正常工作，无需改动。
4. **不修改 manifest.json**：工具栏注册通过代码完成，manifest 不需要声明 `ToolbarItems`。
5. **不修改 SDK/主程序**：仅修复插件侧。研究报告建议的"在 wrapper 中自动构造默认按钮"属于主程序改进，超出本次修复范围。
6. **保留 `IconGeometry` 字段**：作为元数据保留，便于后续可能的配置 UI 或日志引用。

## 五、验证步骤

1. **编译验证**
   - 执行 `build-and-run.ps1`，确认编译通过、`quickdict.plugin.icpx` 包生成成功。
   - 脚本会自动清除 `disabled_plugins.json` 和 `plugin_error_recovery.json`，确保干净测试状态。

2. **运行时验证**
   - 启动 ICC，查看日志：
     - 应看到 `Plugin registered toolbar item: quickdict.button`（已存在）。
     - **不应**再出现因 `ViewFactory` 为 null 导致的静默跳过（wrapper 当前不写日志，但按钮应可见）。
   - 在 ICC 主工具栏中应**可见**一个带搜索图标和"查词"文字的按钮。

3. **交互验证**
   - 点击"查词"按钮，应弹出 `PopupShellContent`（标题"QuickDict 查词"），内含 `DictionaryPopup` 用户控件。
   - 弹窗应显示搜索框、候选词区、虚拟键盘等 UI。
   - 点击弹窗关闭按钮（`PopupShellContent.CloseButtonControl`），弹窗应关闭。

4. **方向适配验证**
   - 切换 ICC 工具栏方向（横向/纵向），QuickDict 按钮应正确适配布局（通过 `ApplyOrientation` 委托调用 `ToolbarImageButton.ApplyOrientation(bool)`）。

5. **回归验证**
   - 确认设置面板仍可正常打开、保存设置。
   - 确认 TTS 服务关闭时不再抛出 `NAudio.WinMM` 异常（已有的 try-catch 修复仍生效）。
   - 确认词典后台加载、超时取消等逻辑未受影响。
