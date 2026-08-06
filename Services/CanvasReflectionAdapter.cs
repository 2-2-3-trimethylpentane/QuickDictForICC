using Ink_Canvas.Plugins;
using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace QuickDictForICC.Services
{
    /// <summary>
    /// 画布反射适配器：集中封装所有对 ICC 宿主非公开成员的反射访问。
    /// <para>
    /// <b>为什么需要本类</b>：ICC 插件 SDK 未提供「向画布插入任意 WPF 控件」的 API，
    /// <see cref="ICanvasInkService"/> 仅有 <c>InsertBitmap(BitmapSource)</c>。
    /// 而可交互单词卡（插入白板后仍可点击发音、滚动）必须把 UserControl 直接加入
    /// InkCanvas.Children，并复用宿主的元素变换与选中/拖动事件处理器，
    /// 这些成员在宿主中均为 private/internal，只能反射访问。
    /// </para>
    /// <para>
    /// <b>风险提示</b>：本类依赖 ICC-CE 内部实现细节，宿主重构可能导致静默失效。
    /// 所有方法均以「尽力而为」语义实现：失败时返回 false 或安全默认值，
    /// 不抛异常，并通过 <see cref="IPluginHost.Log"/> 输出诊断信息，
    /// 由调用方回退到 SDK 公开 API（<see cref="ICanvasInkService.InsertBitmap"/>）或独立弹窗。
    /// </para>
    /// <para>
    /// <b>依赖的宿主成员清单</b>（以 ICC-CE 1.7.19.x 为准）：
    /// <list type="bullet">
    /// <item><c>PluginHostProxy._manager</c> / <c>PluginManager._services</c> / <c>_serviceCollection</c>（私有字段，用于跨 ALC 定位画布服务）</item>
    /// <item><c>CanvasInkService._mainWindow</c>（私有字段）</item>
    /// <item><c>MainWindow.GetPluginCurrentTool()</c>（internal 方法）</item>
    /// <item><c>MainWindow.inkCanvas</c>（私有字段）</item>
    /// <item><c>MainWindow.InitializeElementTransform(FrameworkElement)</c>（私有方法）</item>
    /// <item><c>MainWindow.SelectElement(FrameworkElement)</c>（私有方法）</item>
    /// <item><c>MainWindow.UpdateCurrentToolMode(string)</c>（私有方法）</item>
    /// <item><c>MainWindow.Element_*</c> 系列鼠标/触摸/操作事件处理器（私有方法）</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>未来清理</b>：当 SDK 发布包含完整 <see cref="ICanvasInkService"/>
    /// （含 <c>CanvasSize</c>、<c>SelectTool</c> 等成员）的版本后，
    /// <see cref="IsSelectionMode"/> 可由 <c>SelectTool</c>/<c>IsPenMode</c> 替代，
    /// 画布尺寸可由 <c>CanvasSize</c> 替代，本类可大幅精简。
    /// </para>
    /// </summary>
    internal sealed class CanvasReflectionAdapter
    {
        private const BindingFlags InstanceNonPublic = BindingFlags.NonPublic | BindingFlags.Instance;

        private readonly IPluginHost _host;
        private readonly Dispatcher _dispatcher;
        private FrameworkElement _activeSelectionElement;

        /// <summary>
        /// 初始化画布反射适配器。
        /// </summary>
        /// <param name="host">插件宿主；可为 <c>null</c>，此时所有能力均不可用。</param>
        /// <param name="dispatcher">UI 线程调度器，用于把画布操作切回 UI 线程。</param>
        public CanvasReflectionAdapter(IPluginHost host, Dispatcher dispatcher)
        {
            _host = host;
            _dispatcher = dispatcher;
        }

        /// <summary>
        /// 获取 ICC 画布墨迹服务实例。
        /// 先走 SDK 公开的泛型 <see cref="IPluginHost.GetService{T}"/>；
        /// 若因跨 AssemblyLoadContext 类型隔离导致解析失败，则回退到反射查找。
        /// </summary>
        /// <returns>画布服务实例；不可用时返回 <c>null</c>。</returns>
        public object GetCanvasInkService()
        {
            // 1. SDK 公开路径：宿主与插件共享 SDK 程序集时可直接命中。
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

            // 2. 反射兜底：PluginHostProxy -> PluginManager -> 服务集合，按类型名匹配。
            try
            {
                if (_host == null)
                    return null;

                object manager = _host;
                var managerField = _host.GetType().GetField("_manager", InstanceNonPublic);
                if (managerField != null)
                    manager = managerField.GetValue(_host);

                if (manager == null)
                    return null;

                var managerType = manager.GetType();

                // 2.1 旧接口注册路径：内部字典。
                var servicesField = managerType.GetField("_services", InstanceNonPublic);
                if (servicesField != null
                    && servicesField.GetValue(manager) is System.Collections.IDictionary services)
                {
                    foreach (var key in services.Keys)
                    {
                        if (key is Type serviceType && serviceType.Name == nameof(ICanvasInkService))
                            return services[key];
                    }
                }

                // 2.2 新接口注册路径：DI 服务描述符集合。
                var serviceCollectionField = managerType.GetField("_serviceCollection", InstanceNonPublic);
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
                            && serviceType.Name == nameof(ICanvasInkService))
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
        /// <param name="canvasInkService">由 <see cref="GetCanvasInkService"/> 返回的服务实例。</param>
        /// <returns>主窗口对象；不可用时返回 <c>null</c>。</returns>
        public object GetMainWindow(object canvasInkService)
        {
            if (canvasInkService == null)
                return null;

            try
            {
                var mainWindowField = canvasInkService.GetType()
                    .GetField("_mainWindow", InstanceNonPublic);
                return mainWindowField?.GetValue(canvasInkService);
            }
            catch (Exception ex)
            {
                LogDiagnostic($"GetMainWindow failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 判断宿主当前是否处于选择（套索）模式。
        /// 选择模式下不应占用画布插入元素，调用方应改用独立弹窗。
        /// </summary>
        /// <param name="mainWindow">由 <see cref="GetMainWindow"/> 返回的主窗口对象。</param>
        /// <returns>是否为选择模式；无法判定时返回 <c>false</c>。</returns>
        public bool IsSelectionMode(object mainWindow)
        {
            if (mainWindow == null)
                return false;

            try
            {
                var method = mainWindow.GetType().GetMethod("GetPluginCurrentTool", InstanceNonPublic);
                var result = method?.Invoke(mainWindow, null);
                if (result == null)
                    return false;

                // 宿主与插件可能处于不同 ALC，enum 类型不可直接比较；
                // PluginInkTool.Select 的名称为 "Select"、值为 0。
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
        /// 尝试把可交互控件插入 ICC 画布，并接入宿主的元素变换与选中/拖动体系，
        /// 使其表现得与插入的图片一致（可选中、拖动、缩放、触摸操作）。
        /// </summary>
        /// <param name="mainWindow">由 <see cref="GetMainWindow"/> 返回的主窗口对象。</param>
        /// <param name="cardView">要插入的单词卡控件。</param>
        /// <param name="defaultSize">控件的期望尺寸，会按画布大小等比缩放。</param>
        /// <returns>是否插入成功；失败时调用方应回退到位图插入。</returns>
        public bool TryInsertInteractiveControl(object mainWindow, Views.WordCardView cardView, Size defaultSize)
        {
            if (mainWindow == null || cardView == null || _dispatcher == null)
                return false;

            try
            {
                return _dispatcher.Invoke(() => InsertControlCore(mainWindow, cardView, defaultSize),
                    DispatcherPriority.Normal);
            }
            catch (Exception ex)
            {
                LogDiagnostic($"TryInsertInteractiveControl dispatch failed: {ex.Message}");
                return false;
            }
        }

        private bool InsertControlCore(object mainWindow, Views.WordCardView cardView, Size defaultSize)
        {
            try
            {
                // 1. 取得宿主 InkCanvas 及其 Children 集合。
                var inkCanvasField = mainWindow.GetType().GetField("inkCanvas", InstanceNonPublic);
                object inkCanvas = inkCanvasField?.GetValue(mainWindow);
                if (inkCanvas == null)
                {
                    LogDiagnostic("inkCanvas field not found on MainWindow.");
                    return false;
                }

                var childrenProperty = inkCanvas.GetType().GetProperty("Children");
                if (!(childrenProperty?.GetValue(inkCanvas) is System.Collections.IList children))
                {
                    LogDiagnostic("InkCanvas.Children is not accessible.");
                    return false;
                }

                cardView.IsHitTestVisible = true;
                cardView.Focusable = false;

                // 2. 按画布尺寸等比缩放并居中。
                var canvasType = inkCanvas.GetType();
                double canvasWidth = (double)(canvasType.GetProperty("ActualWidth")?.GetValue(inkCanvas) ?? 0.0);
                double canvasHeight = (double)(canvasType.GetProperty("ActualHeight")?.GetValue(inkCanvas) ?? 0.0);

                if (canvasWidth <= 0 || canvasHeight <= 0)
                {
                    canvasWidth = SystemParameters.PrimaryScreenWidth;
                    canvasHeight = SystemParameters.PrimaryScreenHeight;
                }

                double scale = Math.Min(
                    canvasWidth * 0.8 / defaultSize.Width,
                    canvasHeight * 0.8 / defaultSize.Height);
                if (scale > 1.0 || double.IsNaN(scale) || scale <= 0)
                    scale = 1.0;

                double newWidth = defaultSize.Width * scale;
                double newHeight = defaultSize.Height * scale;

                cardView.Width = newWidth;
                cardView.Height = newHeight;
                cardView.Measure(new Size(newWidth, newHeight));
                cardView.Arrange(new Rect(0, 0, newWidth, newHeight));

                double left = Math.Max(0, (canvasWidth - newWidth) / 2);
                double top = Math.Max(0, (canvasHeight - newHeight) / 2);
                InkCanvas.SetLeft(cardView, left);
                InkCanvas.SetTop(cardView, top);

                // 3. 加入画布并接入宿主元素体系。
                children.Add(cardView);
                InvokePrivateVoid(mainWindow, "InitializeElementTransform", cardView);
                BindElementEvents(mainWindow, cardView);
                InvokePrivateVoid(mainWindow, "SelectElement", cardView);
                SwitchToSelectToolMode(mainWindow, inkCanvas);
                QueueShowSelectionToolbar(mainWindow, cardView);

                LogDiagnostic($"WordCardView inserted at ({left:F0},{top:F0}) size {newWidth:F0}x{newHeight:F0}.");
                return true;
            }
            catch (Exception ex)
            {
                LogDiagnostic($"InsertControlCore failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 把宿主的元素事件处理器绑定到单词卡控件。
        /// 选中类事件通过 RegisterSelectHandler 模式注册，
        /// 以便控件内部的按钮/滚动条不会误触发白板选中。
        /// </summary>
        private void BindElementEvents(object mainWindow, Views.WordCardView cardView)
        {
            try
            {
                var type = mainWindow.GetType();

                var mouseDown = type.GetMethod("Element_MouseLeftButtonDown", InstanceNonPublic);
                if (mouseDown != null)
                {
                    cardView.RegisterSelectHandler((MouseButtonEventHandler)Delegate.CreateDelegate(
                        typeof(MouseButtonEventHandler), mainWindow, mouseDown));
                }

                var touchDown = type.GetMethod("Element_TouchDown", InstanceNonPublic);
                if (touchDown != null)
                {
                    cardView.RegisterTouchSelectHandler((EventHandler<TouchEventArgs>)Delegate.CreateDelegate(
                        typeof(EventHandler<TouchEventArgs>), mainWindow, touchDown));
                }

                AttachHandler<MouseButtonEventHandler>(mainWindow, type, "Element_MouseLeftButtonUp",
                    h => cardView.MouseLeftButtonUp += h);
                AttachHandler<MouseEventHandler>(mainWindow, type, "Element_MouseMove",
                    h => cardView.MouseMove += h);
                AttachHandler<MouseWheelEventHandler>(mainWindow, type, "Element_MouseWheel",
                    h => cardView.MouseWheel += h);
                AttachHandler<EventHandler<TouchEventArgs>>(mainWindow, type, "Element_TouchUp",
                    h => cardView.TouchUp += h);
                AttachHandler<EventHandler<ManipulationDeltaEventArgs>>(mainWindow, type, "Element_ManipulationDelta",
                    h => cardView.ManipulationDelta += h);
                AttachHandler<EventHandler<ManipulationCompletedEventArgs>>(mainWindow, type, "Element_ManipulationCompleted",
                    h => cardView.ManipulationCompleted += h);

                // 宿主事件处理器先修改元素变换；随后仅由当前卡片在渲染阶段
                // 重新定位工具栏，既能跟随拖动/缩放，也不会让多张卡片相互竞争。
                cardView.MouseMove += (s, args) =>
                {
                    if (args.LeftButton == MouseButtonState.Pressed)
                        QueueSelectionToolbarUpdate(mainWindow, cardView);
                };
                cardView.MouseWheel += (s, args) => QueueSelectionToolbarUpdate(mainWindow, cardView);
                cardView.MouseLeftButtonUp += (s, args) => QueueSelectionToolbarUpdate(mainWindow, cardView);
                cardView.TouchUp += (s, args) => QueueSelectionToolbarUpdate(mainWindow, cardView);
                cardView.ManipulationDelta += (s, args) => QueueSelectionToolbarUpdate(mainWindow, cardView);
                cardView.ManipulationCompleted += (s, args) => QueueSelectionToolbarUpdate(mainWindow, cardView);

                cardView.IsManipulationEnabled = true;
                cardView.Cursor = Cursors.Hand;
                cardView.IsHitTestVisible = true;
                cardView.Focusable = false;

                // 选中后手动显示工具栏（ICC 的 SelectElement 对非图片元素会隐藏工具栏）
                cardView.SelectionRequested += (s, args) =>
                {
                    QueueShowSelectionToolbar(mainWindow, cardView);
                };

                // ShowImageResizeHandles 会由 ICC 宿主自行跟踪已选元素的布局变化。
                // 不再订阅每张卡片的 LayoutUpdated；多张卡片同时触发该事件会争用
                // 宿主唯一的“旋转 / 删除”选择工具栏，造成位置来回跳动、闪烁。
            }
            catch (Exception ex)
            {
                LogDiagnostic($"BindElementEvents failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 反射取出宿主的事件处理器方法并绑定到控件事件。
        /// </summary>
        private void AttachHandler<TDelegate>(object target, Type targetType, string methodName, Action<TDelegate> attach)
            where TDelegate : Delegate
        {
            var method = targetType.GetMethod(methodName, InstanceNonPublic);
            if (method == null)
                return;

            try
            {
                attach((TDelegate)Delegate.CreateDelegate(typeof(TDelegate), target, method));
            }
            catch (Exception ex)
            {
                LogDiagnostic($"AttachHandler({methodName}) failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 把宿主切换到选择模式，与插入图片后的行为保持一致，使单词卡可立即拖动。
        /// </summary>
        private void SwitchToSelectToolMode(object mainWindow, object inkCanvas)
        {
            try
            {
                var editingModeProperty = inkCanvas.GetType().GetProperty("EditingMode");
                if (editingModeProperty != null)
                {
                    var selectValue = Enum.Parse(editingModeProperty.PropertyType, "Select");
                    editingModeProperty.SetValue(inkCanvas, selectValue);
                }

                var updateToolMode = mainWindow.GetType().GetMethod("UpdateCurrentToolMode", InstanceNonPublic);
                updateToolMode?.Invoke(mainWindow, new object[] { "select" });
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
        private void ShowSelectionToolbar(object mainWindow, FrameworkElement element)
        {
            if (mainWindow == null || element == null) return;

            try
            {
                _activeSelectionElement = element;
                var mainWindowType = mainWindow.GetType();

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
        /// 在控件完成首次布局且宿主完成工具切换后，再显示选择工具栏。
        /// 否则 ICC 会基于尚未生效的 Canvas 坐标把工具栏定位到左侧。
        /// </summary>
        private void QueueShowSelectionToolbar(object mainWindow, FrameworkElement element)
        {
            if (_dispatcher == null || mainWindow == null || element == null)
                return;

            _activeSelectionElement = element;
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(_activeSelectionElement, element) || !element.IsLoaded)
                    return;

                ShowSelectionToolbar(mainWindow, element);
            }), DispatcherPriority.Loaded);
        }

        /// <summary>
        /// 等宿主完成当前输入事件中的 RenderTransform 更新后，再定位唯一的选择工具栏。
        /// </summary>
        private void QueueSelectionToolbarUpdate(object mainWindow, FrameworkElement element)
        {
            if (_dispatcher == null || !ReferenceEquals(_activeSelectionElement, element))
                return;

            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(_activeSelectionElement, element) || !element.IsLoaded)
                    return;

                try
                {
                    var method = mainWindow.GetType().GetMethod(
                        "UpdateImageSelectionToolbarPosition",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    method?.Invoke(mainWindow, new object[] { element });
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"UpdateSelectionToolbarPosition failed: {ex.Message}");
                }
            }), DispatcherPriority.Render);
        }

        /// <summary>
        /// 把位图插入画布。优先走 SDK 强类型接口，跨 ALC 类型不匹配时回退到反射调用。
        /// </summary>
        /// <param name="canvasInkService">由 <see cref="GetCanvasInkService"/> 返回的服务实例。</param>
        /// <param name="bitmap">要插入的位图。</param>
        /// <returns>是否插入成功。</returns>
        public bool TryInsertBitmap(object canvasInkService, BitmapSource bitmap)
        {
            if (canvasInkService == null || bitmap == null)
                return false;

            // 1. SDK 公开路径。
            if (canvasInkService is ICanvasInkService typedService)
            {
                try
                {
                    return typedService.InsertBitmap(bitmap);
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"ICanvasInkService.InsertBitmap failed: {ex.Message}");
                }
            }

            // 2. 反射兜底：跨 ALC 时插件侧接口类型与宿主实现类型不一致。
            try
            {
                var serviceType = canvasInkService.GetType();

                var insertMethod = serviceType.GetMethod(
                    "InsertBitmap",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(BitmapSource) },
                    null)
                    ?? serviceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "InsertBitmap" && m.GetParameters().Length == 1);

                if (insertMethod == null)
                {
                    LogDiagnostic("InsertBitmap method not found on canvas service.");
                    return false;
                }

                var result = insertMethod.Invoke(canvasInkService, new object[] { bitmap });
                return result is bool success && success;
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

        /// <summary>
        /// 反射调用宿主的无返回值私有方法。
        /// </summary>
        private void InvokePrivateVoid(object target, string methodName, params object[] args)
        {
            try
            {
                var method = target.GetType().GetMethod(methodName, InstanceNonPublic);
                method?.Invoke(target, args);
            }
            catch (Exception ex)
            {
                LogDiagnostic($"{methodName} failed: {ex.Message}");
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
    }
}
