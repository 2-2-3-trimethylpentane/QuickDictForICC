using Ink_Canvas.Controls;
using Ink_Canvas.Plugins;
using Microsoft.Extensions.DependencyInjection;
using QuickDictForICC.Services;
using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace QuickDictForICC.Views
{
    /// <summary>
    /// 英汉字典独立窗口。
    /// 内部承载 <see cref="DictionaryPopup"/> 作为实际 UI。
    /// </summary>
    public partial class DictionaryWindow : Window
    {
        private DictionaryPopup _popup;
        private ResultView _resultView;
        private WordCardService _wordCardService;
        private IPluginHost _host;
        private TtsService _ttsService;
        private TtsOptions _ttsOptions;
        private EventHandler _generateWordCardRequestedHandler;

        private const double DefaultCardWidth = 400;
        private const double DefaultCardHeight = 600;

        /// <summary>
        /// 初始化 <see cref="DictionaryWindow"/>。
        /// </summary>
        public DictionaryWindow(
            IDictionaryService dictionaryService,
            TtsService ttsService,
            Task loadingTask,
            PluginSettings settings,
            WordCardService wordCardService,
            IPluginHost host)
        {
            InitializeComponent();

            _wordCardService = wordCardService;
            _host = host;
            _ttsService = ttsService;
            _ttsOptions = settings?.ToTtsOptions();

            _resultView = new ResultView();
            _resultView.SetTtsService(ttsService);
            if (settings != null)
            {
                _resultView.SetTtsOptions(settings.ToTtsOptions());
            }

            _popup = new DictionaryPopup();
            _popup.SetDictionaryService(dictionaryService);
            _popup.SetTtsService(ttsService);
            _popup.SetResultView(_resultView);
            _popup.SetLoadingTask(loadingTask);
            _popup.SetTitleBarVisible(true);

            _generateWordCardRequestedHandler = async (s, e) => await OnGenerateWordCardRequestedAsync();
            _resultView.GenerateWordCardRequested += _generateWordCardRequestedHandler;

            MainContentHost.Content = _popup;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            Cleanup();
            base.OnClosing(e);
        }

        private async Task OnGenerateWordCardRequestedAsync()
        {
            var entry = _popup?.LastResult;
            if (entry == null)
            {
                MessageBox.Show(
                    Properties.Resources.Message_NoWordToGenerateCard,
                    Properties.Resources.MessageBox_Title_Notice,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            object canvasInkService = GetCanvasInkService();
            if (canvasInkService == null)
            {
                // 画布服务不可用时，直接以弹窗形式展示，保证功能可用。
                ShowWordCardPopup(entry);
                return;
            }

            object mainWindow = GetMainWindowFromCanvasService(canvasInkService);
            bool isSelectionMode = IsSelectionMode(mainWindow);

            if (isSelectionMode)
            {
                // 选择模式下不占用画布，使用独立弹窗。
                ShowWordCardPopup(entry);
                return;
            }

            // 非选择模式：优先尝试插入可交互控件。
            bool controlInserted = await TryInsertWordCardControlAsync(mainWindow, entry);
            if (controlInserted)
                return;

            // 控件插入失败时回退到位图插入。
            try
            {
                BitmapSource bitmap = await _wordCardService.GenerateBitmapAsync(entry);
                if (bitmap == null)
                {
                    MessageBox.Show(
                        string.Format(Properties.Resources.Message_WordCardRenderFailed_Format, "位图为空"),
                        Properties.Resources.MessageBox_Title_Notice,
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                bool inserted = TryInsertBitmap(canvasInkService, bitmap);
                if (!inserted)
                {
                    MessageBox.Show(
                        string.Format(Properties.Resources.Message_WordCardInsertFailed_Format, string.Empty),
                        Properties.Resources.MessageBox_Title_Notice,
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format(Properties.Resources.Message_WordCardRenderFailed_Format, ex.Message),
                    Properties.Resources.MessageBox_Title_Notice,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 显示单词卡独立弹窗。
        /// </summary>
        private void ShowWordCardPopup(IWordEntry entry)
        {
            try
            {
                var window = new WordCardWindow(entry, _ttsService, _ttsOptions)
                {
                    Owner = this
                };
                window.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format(Properties.Resources.Message_WordCardRenderFailed_Format, ex.Message),
                    Properties.Resources.MessageBox_Title_Notice,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 获取 ICC 画布墨迹服务实例。
        /// 先尝试泛型 GetService，失败时通过反射绕过插件 ALC 与宿主 ALC 的类型隔离。
        /// </summary>
        private object GetCanvasInkService()
        {
            // 1. 正常泛型路径：类型一致时直接返回。
            try
            {
                var service = _host?.GetService<ICanvasInkService>();
                if (service != null)
                    return service;
            }
            catch (Exception ex)
            {
                LogDiagnostic($"GetService<ICanvasInkService> failed: {ex.Message}");
            }

            // 2. 反射路径：从 PluginHostProxy -> PluginManager -> _services 字典
            //    按类型名 "ICanvasInkService" 匹配，避免跨 ALC 的 Type 对象不一致。
            try
            {
                var host = _host;
                if (host == null)
                    return null;

                // 解开可能的 PluginHostProxy 包装
                object manager = host;
                var hostType = host.GetType();
                var managerField = hostType.GetField("_manager", BindingFlags.NonPublic | BindingFlags.Instance);
                if (managerField != null)
                    manager = managerField.GetValue(host);

                if (manager == null)
                    return null;

                var managerType = manager.GetType();

                // 2.1 优先查内部字典（旧接口注册路径）
                var servicesField = managerType.GetField("_services", BindingFlags.NonPublic | BindingFlags.Instance);
                if (servicesField != null
                    && servicesField.GetValue(manager) is System.Collections.IDictionary services)
                {
                    foreach (var key in services.Keys)
                    {
                        if (key is Type serviceType && serviceType.Name == "ICanvasInkService")
                            return services[key];
                    }
                }

                // 2.2 再查 DI 服务集合（新接口注册路径）
                var serviceCollectionField = managerType.GetField("_serviceCollection", BindingFlags.NonPublic | BindingFlags.Instance);
                if (serviceCollectionField?.GetValue(manager) is System.Collections.IEnumerable serviceCollection)
                {
                    foreach (var descriptor in serviceCollection)
                    {
                        if (descriptor == null)
                            continue;

                        var descriptorType = descriptor.GetType();
                        var serviceTypeProp = descriptorType.GetProperty("ServiceType");
                        var implementationInstanceProp = descriptorType.GetProperty("ImplementationInstance");

                        if (serviceTypeProp?.GetValue(descriptor) is Type serviceType
                            && serviceType.Name == "ICanvasInkService")
                        {
                            return implementationInstanceProp?.GetValue(descriptor);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogDiagnostic($"Reflection lookup for ICanvasInkService failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 从画布服务实例中提取 ICC 主窗口对象。
        /// </summary>
        private object GetMainWindowFromCanvasService(object canvasInkService)
        {
            if (canvasInkService == null)
                return null;

            try
            {
                var mainWindowField = canvasInkService.GetType()
                    .GetField("_mainWindow", BindingFlags.NonPublic | BindingFlags.Instance);
                return mainWindowField?.GetValue(canvasInkService);
            }
            catch (Exception ex)
            {
                LogDiagnostic($"GetMainWindowFromCanvasService failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 反射调用主窗口的 GetPluginCurrentTool，判断当前是否为选择模式。
        /// </summary>
        private bool IsSelectionMode(object mainWindow)
        {
            if (mainWindow == null)
                return false;

            try
            {
                var method = mainWindow.GetType().GetMethod(
                    "GetPluginCurrentTool",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var result = method?.Invoke(mainWindow, null);
                if (result == null)
                    return false;

                // 宿主与插件可能处于不同 ALC，enum 类型不可直接比较。
                // PluginInkTool.Select 的值为 0，名称也为 "Select"。
                if (string.Equals(result.ToString(), "Select", StringComparison.OrdinalIgnoreCase))
                    return true;

                return Convert.ToInt32(result) == 0;
            }
            catch (Exception ex)
            {
                LogDiagnostic($"IsSelectionMode failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 尝试将 <see cref="WordCardView"/> 作为可交互控件插入 ICC 画布。
        /// </summary>
        private async Task<bool> TryInsertWordCardControlAsync(object mainWindow, IWordEntry entry)
        {
            if (mainWindow == null || entry == null)
                return false;

            try
            {
                return await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // 1. 通过反射拿到 inkCanvas 字段与 Children 集合。
                        var mainWindowType = mainWindow.GetType();
                        var inkCanvasField = mainWindowType.GetField(
                            "inkCanvas",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        object inkCanvas = inkCanvasField?.GetValue(mainWindow);
                        if (inkCanvas == null)
                        {
                            LogDiagnostic("inkCanvas field not found on MainWindow.");
                            return false;
                        }

                        var childrenProperty = inkCanvas.GetType().GetProperty("Children");
                        var children = childrenProperty?.GetValue(inkCanvas) as System.Collections.IList;
                        if (children == null)
                        {
                            LogDiagnostic("InkCanvas.Children is not accessible.");
                            return false;
                        }

                        // 2. 创建单词卡控件。
                        var cardView = new WordCardView();
                        cardView.SetTtsService(_ttsService);
                        cardView.SetTtsOptions(_ttsOptions);
                        cardView.ShowEntry(entry);
                        cardView.IsHitTestVisible = true;
                        cardView.Focusable = false;

                        // 3. 按画布尺寸居中缩放。
                        double cardWidth = DefaultCardWidth;
                        double cardHeight = DefaultCardHeight;

                        var actualWidthProp = inkCanvas.GetType().GetProperty("ActualWidth");
                        var actualHeightProp = inkCanvas.GetType().GetProperty("ActualHeight");
                        double canvasWidth = (double)(actualWidthProp?.GetValue(inkCanvas) ?? 0.0);
                        double canvasHeight = (double)(actualHeightProp?.GetValue(inkCanvas) ?? 0.0);

                        if (canvasWidth <= 0 || canvasHeight <= 0)
                        {
                            canvasWidth = SystemParameters.PrimaryScreenWidth;
                            canvasHeight = SystemParameters.PrimaryScreenHeight;
                        }

                        double maxWidth = canvasWidth * 0.8;
                        double maxHeight = canvasHeight * 0.8;
                        double scale = Math.Min(maxWidth / cardWidth, maxHeight / cardHeight);
                        if (scale > 1.0) scale = 1.0;
                        if (double.IsNaN(scale) || scale <= 0) scale = 1.0;

                        double newWidth = cardWidth * scale;
                        double newHeight = cardHeight * scale;

                        cardView.Width = newWidth;
                        cardView.Height = newHeight;

                        cardView.Measure(new Size(newWidth, newHeight));
                        cardView.Arrange(new Rect(0, 0, newWidth, newHeight));

                        double left = Math.Max(0, (canvasWidth - newWidth) / 2);
                        double top = Math.Max(0, (canvasHeight - newHeight) / 2);

                        InkCanvas.SetLeft(cardView, left);
                        InkCanvas.SetTop(cardView, top);

                        // 4. 加入画布，初始化 ICC 元素变换与事件绑定，使其像图片一样被选中/拖动。
                        children.Add(cardView);
                        InitializeElementTransformViaReflection(mainWindow, cardView);
                        BindElementEventsViaReflection(mainWindow, cardView);
                        SelectWordCardInIcc(mainWindow, cardView);
                        ShowSelectionToolbarViaReflection(mainWindow, cardView);
                        SwitchToSelectToolModeViaReflection(mainWindow, inkCanvas);

                        LogDiagnostic($"WordCardView inserted at ({left:F0},{top:F0}) size {newWidth:F0}x{newHeight:F0}.");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        LogDiagnostic($"TryInsertWordCardControl inner failed: {ex.Message}");
                        return false;
                    }
                }, System.Windows.Threading.DispatcherPriority.Normal);
            }
            catch (Exception ex)
            {
                LogDiagnostic($"TryInsertWordCardControl async dispatch failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 通过反射调用 ICC 主窗口的 SelectElement，使单词卡显示选中框。
        /// </summary>
        private void SelectWordCardInIcc(object mainWindow, FrameworkElement element)
        {
            if (mainWindow == null || element == null) return;

            try
            {
                var method = mainWindow.GetType().GetMethod(
                    "SelectElement",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                method?.Invoke(mainWindow, new object[] { element });
            }
            catch (Exception ex)
            {
                LogDiagnostic($"SelectElement failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 通过反射调用 ICC 主窗口的 InitializeElementTransform，为单词卡创建 Scale/Translate/Rotate 变换组。
        /// </summary>
        private void InitializeElementTransformViaReflection(object mainWindow, FrameworkElement element)
        {
            if (mainWindow == null || element == null) return;

            try
            {
                var method = mainWindow.GetType().GetMethod(
                    "InitializeElementTransform",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                method?.Invoke(mainWindow, new object[] { element });
            }
            catch (Exception ex)
            {
                LogDiagnostic($"InitializeElementTransform failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 通过反射把 ICC 白板元素事件绑定到单词卡控件，使用 RegisterSelectHandler 模式避免内部交互子控件触发选择。
        /// </summary>
        private void BindElementEventsViaReflection(object mainWindow, WordCardView cardView)
        {
            if (mainWindow == null || cardView == null) return;

            try
            {
                var mainWindowType = mainWindow.GetType();

                var mouseLeftButtonDown = mainWindowType.GetMethod(
                    "Element_MouseLeftButtonDown",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var touchDown = mainWindowType.GetMethod(
                    "Element_TouchDown",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var mouseLeftButtonUp = mainWindowType.GetMethod(
                    "Element_MouseLeftButtonUp",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var mouseMove = mainWindowType.GetMethod(
                    "Element_MouseMove",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var mouseWheel = mainWindowType.GetMethod(
                    "Element_MouseWheel",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var touchUp = mainWindowType.GetMethod(
                    "Element_TouchUp",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var manipulationDelta = mainWindowType.GetMethod(
                    "Element_ManipulationDelta",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var manipulationCompleted = mainWindowType.GetMethod(
                    "Element_ManipulationCompleted",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (mouseLeftButtonDown != null)
                {
                    cardView.RegisterSelectHandler(
                        (MouseButtonEventHandler)Delegate.CreateDelegate(
                            typeof(MouseButtonEventHandler), mainWindow, mouseLeftButtonDown));
                }

                if (touchDown != null)
                {
                    cardView.RegisterTouchSelectHandler(
                        (EventHandler<TouchEventArgs>)Delegate.CreateDelegate(
                            typeof(EventHandler<TouchEventArgs>), mainWindow, touchDown));
                }

                if (mouseLeftButtonUp != null)
                {
                    cardView.MouseLeftButtonUp += (MouseButtonEventHandler)Delegate.CreateDelegate(
                        typeof(MouseButtonEventHandler), mainWindow, mouseLeftButtonUp);
                }

                if (mouseMove != null)
                {
                    cardView.MouseMove += (MouseEventHandler)Delegate.CreateDelegate(
                        typeof(MouseEventHandler), mainWindow, mouseMove);
                }

                if (mouseWheel != null)
                {
                    cardView.MouseWheel += (MouseWheelEventHandler)Delegate.CreateDelegate(
                        typeof(MouseWheelEventHandler), mainWindow, mouseWheel);
                }

                if (touchUp != null)
                {
                    cardView.TouchUp += (EventHandler<TouchEventArgs>)Delegate.CreateDelegate(
                        typeof(EventHandler<TouchEventArgs>), mainWindow, touchUp);
                }

                if (manipulationDelta != null)
                {
                    cardView.ManipulationDelta += (EventHandler<ManipulationDeltaEventArgs>)Delegate.CreateDelegate(
                        typeof(EventHandler<ManipulationDeltaEventArgs>), mainWindow, manipulationDelta);
                }

                if (manipulationCompleted != null)
                {
                    cardView.ManipulationCompleted += (EventHandler<ManipulationCompletedEventArgs>)Delegate.CreateDelegate(
                        typeof(EventHandler<ManipulationCompletedEventArgs>), mainWindow, manipulationCompleted);
                }

                cardView.IsManipulationEnabled = true;
                cardView.Cursor = Cursors.Hand;
                cardView.IsHitTestVisible = true;
                cardView.Focusable = false;

                // 选中后手动显示工具栏（ICC 的 SelectElement 对非图片元素会隐藏工具栏）
                cardView.SelectionRequested += (s, args) =>
                    ShowSelectionToolbarViaReflection(mainWindow, cardView);

                // 拖动/缩放/旋转时持续更新工具栏位置
                cardView.LayoutUpdated += (s, args) =>
                    UpdateSelectionToolbarPositionViaReflection(mainWindow, cardView);
            }
            catch (Exception ex)
            {
                LogDiagnostic($"BindElementEvents failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 通过反射将 ICC 切换到选择模式，与插入图片后的行为一致，使单词卡可立即拖动。
        /// </summary>
        private void SwitchToSelectToolModeViaReflection(object mainWindow, object inkCanvas)
        {
            if (mainWindow == null || inkCanvas == null) return;

            try
            {
                var inkCanvasType = inkCanvas.GetType();
                var editingModeProperty = inkCanvasType.GetProperty("EditingMode");
                if (editingModeProperty != null)
                {
                    var selectValue = Enum.Parse(editingModeProperty.PropertyType, "Select");
                    editingModeProperty.SetValue(inkCanvas, selectValue);
                }

                var updateToolModeMethod = mainWindow.GetType().GetMethod(
                    "UpdateCurrentToolMode",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                updateToolModeMethod?.Invoke(mainWindow, new object[] { "select" });
            }
            catch (Exception ex)
            {
                LogDiagnostic($"SwitchToSelectToolMode failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 通过反射显示 ICC 的图片选择工具栏和缩放控制点。
        /// ICC 的 SelectElement 对非图片元素会隐藏工具栏，此方法在 SelectElement 之后补救。
        /// </summary>
        private void ShowSelectionToolbarViaReflection(object mainWindow, FrameworkElement element)
        {
            if (mainWindow == null || element == null) return;

            try
            {
                var mainWindowType = mainWindow.GetType();

                // 显示图片选择工具栏
                var toolbarField = mainWindowType.GetField(
                    "BorderImageSelectionControl",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var toolbar = toolbarField?.GetValue(mainWindow) as FrameworkElement;
                if (toolbar != null)
                {
                    var updatePosMethod = mainWindowType.GetMethod(
                        "UpdateImageSelectionToolbarPosition",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    updatePosMethod?.Invoke(mainWindow, new object[] { element });

                    toolbar.Visibility = Visibility.Visible;
                }

                // 显示缩放控制点（内部会设置 LayoutUpdated 跟踪）
                var showHandlesMethod = mainWindowType.GetMethod(
                    "ShowImageResizeHandles",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                showHandlesMethod?.Invoke(mainWindow, new object[] { element });
            }
            catch (Exception ex)
            {
                LogDiagnostic($"ShowSelectionToolbar failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 通过反射更新工具栏位置（拖动/缩放过程中调用）。
        /// </summary>
        private void UpdateSelectionToolbarPositionViaReflection(object mainWindow, FrameworkElement element)
        {
            if (mainWindow == null || element == null) return;

            try
            {
                var mainWindowType = mainWindow.GetType();

                var toolbarField = mainWindowType.GetField(
                    "BorderImageSelectionControl",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var toolbar = toolbarField?.GetValue(mainWindow) as FrameworkElement;

                if (toolbar?.Visibility == Visibility.Visible)
                {
                    var updatePosMethod = mainWindowType.GetMethod(
                        "UpdateImageSelectionToolbarPosition",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    updatePosMethod?.Invoke(mainWindow, new object[] { element });
                }
            }
            catch (Exception ex)
            {
                LogDiagnostic($"UpdateSelectionToolbarPosition failed: {ex.Message}");
            }
        }

        private bool TryInsertBitmap(object canvasInkService, BitmapSource bitmap)
        {
            if (canvasInkService == null || bitmap == null)
                return false;

            try
            {
                var serviceType = canvasInkService.GetType();

                // 先尝试精确匹配（参数类型为 BitmapSource）
                var insertMethod = serviceType.GetMethod(
                    "InsertBitmap",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(BitmapSource) },
                    null);

                // 精确匹配失败时，按名称+单参数兜底（兼容跨 ALC 类型不一致）
                if (insertMethod == null)
                {
                    insertMethod = serviceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "InsertBitmap"
                                             && m.GetParameters().Length == 1);
                }

                if (insertMethod == null)
                {
                    LogDiagnostic("InsertBitmap method not found on canvas service.");
                    return false;
                }

                var result = insertMethod.Invoke(canvasInkService, new object[] { bitmap });
                return result is bool b && b;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                LogDiagnostic($"InsertBitmap invocation failed: {tie.InnerException.Message}");
                return false;
            }
            catch (Exception ex)
            {
                LogDiagnostic($"InsertBitmap reflection failed: {ex.Message}");
                return false;
            }
        }

        private void LogDiagnostic(string message)
        {
            try
            {
                _host?.Log($"[QuickDict WordCard] {message}");
            }
            catch
            {
                // 日志失败不影响主流程。
            }
        }

        private void Cleanup()
        {
            if (_resultView != null && _generateWordCardRequestedHandler != null)
            {
                _resultView.GenerateWordCardRequested -= _generateWordCardRequestedHandler;
            }

            if (_popup != null)
            {
                _popup.SetResultView(null);
                _popup.SetDictionaryService(null);
                _popup.SetTtsService(null);
            }

            MainContentHost.Content = null;
            _popup = null;
            _resultView = null;
            _generateWordCardRequestedHandler = null;
        }
    }
}
