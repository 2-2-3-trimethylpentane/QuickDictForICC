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
        private EventHandler _generateWordCardRequestedHandler;

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
                MessageBox.Show(
                    Properties.Resources.Message_CanvasServiceNotAvailable,
                    Properties.Resources.MessageBox_Title_Notice,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

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
        /// 反射调用画布服务的 InsertBitmap(BitmapSource) 方法。
        /// 由于服务实例可能来自 ICC 宿主 ALC，无法直接转换为插件侧的 ICanvasInkService 接口。
        /// </summary>
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
