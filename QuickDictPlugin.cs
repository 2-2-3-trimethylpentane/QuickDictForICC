using Ink_Canvas.Controls;
using Ink_Canvas.Plugins;
using Microsoft.Extensions.DependencyInjection;
using QuickDictForICC.Properties;
using QuickDictForICC.Services;
using QuickDictForICC.Views;
using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern.Controls;

namespace QuickDictForICC
{
    /// <summary>
    /// QuickDict 查词插件入口。
    /// </summary>
    [PluginEntrance]
    public class QuickDictPlugin : PluginBase
    {
        private IPluginHost _host;
        private PluginSettings _settings;
        private DictionaryService _dictionaryService;
        private System.Threading.CancellationTokenSource _dictionaryLoadCts;
        private System.Threading.Tasks.Task _dictionaryLoadTask;
        private TtsService _ttsService;
        private WordCardService _wordCardService;
        private SettingsView _settingsView;
        private DictionaryWindow _dictionaryWindow;
        private ResolveEventHandler _assemblyResolveHandler;
        private static XamlControlsResources _xamlControlsResources;

        // 元数据（Id/Name/Version/Description/Author）从 manifest.json 自动读取，无需在代码中重复定义

        public override void Initialize(IPluginHost host, IServiceCollection services)
        {
            base.Initialize(host, services);
            _host = host;

            _assemblyResolveHandler = (sender, args) => ResolvePluginAssembly(args);
            AppDomain.CurrentDomain.AssemblyResolve += _assemblyResolveHandler;

            try
            {
                InitializeThemeResources();
            }
            catch (Exception ex)
            {
                host?.LogError(Resources.Message_InitializeThemeResourcesFailed, ex);
            }

            try
            {
                _settings = SettingsManager.Load();
            }
            catch (Exception ex)
            {
                _settings = new PluginSettings();
                host?.LogError(Resources.Message_LoadSettingsFailed, ex);
            }

            try
            {
                InitializeServices();
            }
            catch (Exception ex)
            {
                host?.LogError(Resources.Message_InitializeServicesFailed, ex);
            }

            try
            {
                InitializeSettingsView();
            }
            catch (Exception ex)
            {
                host?.LogError(Resources.Message_InitializeSettingsViewFailed, ex);
            }

            try
            {
                RegisterToolbarItem();
            }
            catch (Exception ex)
            {
                host?.LogError(Resources.Message_RegisterToolbarItemFailed, ex);
            }

            try
            {
                RegisterBoardToolbarItem();
            }
            catch (Exception ex)
            {
                host?.LogError(Resources.Message_RegisterBoardToolbarItemFailed, ex);
            }

            Log(string.Format(Resources.Message_PluginInitialized_Format, Name));
        }

        public override void Shutdown()
        {
            if (_assemblyResolveHandler != null)
            {
                AppDomain.CurrentDomain.AssemblyResolve -= _assemblyResolveHandler;
                _assemblyResolveHandler = null;
            }

            try
            {
                _dictionaryLoadCts?.Cancel();
                _dictionaryLoadCts?.Dispose();
                _dictionaryLoadCts = null;
            }
            catch (Exception ex)
            {
                _host?.LogError(Resources.Message_CancelDictionaryLoadFailed, ex);
            }

            try
            {
                _ttsService?.Dispose();
            }
            catch (Exception ex)
            {
                _host?.LogError(Resources.Message_DisposeTtsServiceFailed, ex);
            }

            try
            {
                if (_xamlControlsResources != null && Application.Current != null)
                {
                    Application.Current.Resources.MergedDictionaries.Remove(_xamlControlsResources);
                }
            }
            catch (Exception ex)
            {
                _host?.LogError(Resources.Message_ShutdownThemeResourcesFailed, ex);
            }

            Log(string.Format(Resources.Message_PluginShutdown_Format, Name));
        }

        public override object GetMainView()
        {
            return null;
        }

        public override object GetSettingsView()
        {
            return _settingsView;
        }

        private void InitializeThemeResources()
        {
            if (Application.Current == null)
                return;

            foreach (var dictionary in Application.Current.Resources.MergedDictionaries)
            {
                if (dictionary is XamlControlsResources)
                    return;
            }

            var resources = new XamlControlsResources();
            (resources as ISupportInitialize)?.BeginInit();
            (resources as ISupportInitialize)?.EndInit();
            _xamlControlsResources = resources;
            Application.Current.Resources.MergedDictionaries.Add(resources);
        }

        private void InitializeServices()
        {
            if (string.IsNullOrWhiteSpace(_settings.EcDictPath) || !File.Exists(_settings.EcDictPath))
            {
                string builtInEcDict = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "ecdict", "ecdict.csv");
                if (File.Exists(builtInEcDict))
                {
                    _settings.EcDictPath = builtInEcDict;
                }
            }

            var ecDictService = new EcDictService(_settings.EcDictPath);
            var mdictService = new MDictService(_settings.MDictPath, _settings.MDictResourcePath);
            _dictionaryService = new DictionaryService(mdictService, ecDictService);

            // 在后台线程异步加载词典，避免阻塞 ICC 初始化。
            // 设置 30 秒超时保护，防止损坏的词典文件导致解析死循环。
            _dictionaryLoadCts = new System.Threading.CancellationTokenSource();
            _dictionaryLoadCts.CancelAfter(TimeSpan.FromSeconds(30));

            _dictionaryLoadTask = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await _dictionaryService.LoadAsync(_dictionaryLoadCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _host?.Log(Resources.Message_DictionaryLoadCanceled);
                }
                catch (Exception ex)
                {
                    _host?.LogError(Resources.Message_LoadDictionaryFailed, ex);
                }
            });

            _ttsService = new TtsService();
            _wordCardService = new WordCardService();

            // 延迟到加载任务结束后再提示词典不可用。
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
                try
                {
                    NotifyIfDictionaryUnavailable();
                }
                catch (Exception ex)
                {
                    _host?.LogError(Resources.Message_CheckDictionaryAvailabilityFailed, ex);
                }
            }, uiScheduler);
        }

        private void NotifyIfDictionaryUnavailable()
        {
            if (_host == null)
                return;

            bool hasEcDict = !string.IsNullOrWhiteSpace(_settings.EcDictPath) && File.Exists(_settings.EcDictPath);
            bool hasMdict = !string.IsNullOrWhiteSpace(_settings.MDictPath) && File.Exists(_settings.MDictPath);

            if (hasEcDict || hasMdict)
                return;

            string message = Resources.Message_DictionaryUnavailable;

            try
            {
                var notificationService = GetServiceOrDefault<INotificationService>();
                notificationService?.Show(Resources.Plugin_DisplayName, message, NotificationLevel.Warning);
            }
            catch
            {
                // 通知服务不是必需依赖，失败时仅记录日志。
            }

            _host.Log(message);
        }

        private void InitializeSettingsView()
        {
            _settingsView = new SettingsView(_settings, _host, () =>
            {
                _host?.Log(Resources.Message_SettingsSavedRestartRequired);
            }, _ttsService);
        }

        private void RegisterToolbarItem()
        {
            const string searchIconPath =
                "M15.5 14h-.79l-.28-.27C15.41 12.59 16 11.11 16 9.5 16 5.91 13.09 3 9.5 3S3 5.91 3 9.5 5.91 16 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zM9.5 14C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z";

            _host?.RegisterToolbarItem(new PluginToolbarItemInfo
            {
                Id = "quickdict.button",
                DisplayName = Resources.Plugin_DisplayName,
                Description = Resources.Plugin_Description,
                IconGeometry = searchIconPath,
                ViewFactory = () => CreateToolbarButton(searchIconPath),
                ApplyOrientation = (view, orientation) =>
                {
                    if (view is ToolbarImageButton btn)
                        btn.ApplyOrientation(orientation == Orientation.Vertical);
                }
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

                // 保留 ToolbarImageButton 默认的动态主题笔刷（IconForeground / FloatBarForeground），
                // 不再用硬编码颜色覆盖，避免深色模式下图标/文字看不见。
                var button = new ToolbarImageButton
                {
                    Label = Resources.ToolbarButton_Label
                };
                button.Icon.Geometry = geometry;

                button.ButtonMouseUp += (s, e) => ShowOrActivateDictionaryWindow();

                return button;
            }
            catch (Exception ex)
            {
                _host?.LogError(Resources.Message_CreateToolbarButtonFailed, ex);
                // 失败时返回一个退化的 TextBlock，至少保证 BuildView 不返回 null
                return new System.Windows.Controls.TextBlock
                {
                    Text = Resources.ToolbarButton_FallbackText,
                    Margin = new Thickness(4)
                };
            }
        }

        private void RegisterBoardToolbarItem()
        {
            const string searchIconPath =
                "M15.5 14h-.79l-.28-.27C15.41 12.59 16 11.11 16 9.5 16 5.91 13.09 3 9.5 3S3 5.91 3 9.5 5.91 16 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zM9.5 14C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z";

            var itemInfo = new PluginToolbarItemInfo
            {
                Id = "quickdict.board.button",
                DisplayName = Resources.Plugin_DisplayName,
                Description = Resources.Plugin_Description,
                IconGeometry = searchIconPath,
                ViewFactory = () => CreateBoardToolbarButton(searchIconPath)
            };

            _host?.RegisterBoardToolbarItem(itemInfo);
        }

        /// <summary>
        /// 构造白板工具栏按钮视图。
        /// </summary>
        private FrameworkElement CreateBoardToolbarButton(string iconPath)
        {
            try
            {
                var button = new BoardToolbarButton
                {
                    Label = Resources.ToolbarButton_Label,
                    IconGeometry = iconPath
                };

                button.ButtonMouseUp += (s, e) => ShowOrActivateDictionaryWindow();

                return button;
            }
            catch (Exception ex)
            {
                _host?.LogError(Resources.Message_CreateToolbarButtonFailed, ex);
                // 失败时返回一个退化的 TextBlock，至少保证 BuildView 不返回 null
                return new System.Windows.Controls.TextBlock
                {
                    Text = Resources.ToolbarButton_FallbackText,
                    Margin = new Thickness(4)
                };
            }
        }

        private void ShowOrActivateDictionaryWindow()
        {
            try
            {
                if (_dictionaryWindow == null || !_dictionaryWindow.IsVisible)
                {
                    _dictionaryWindow = new DictionaryWindow(
                        _dictionaryService,
                        _ttsService,
                        _dictionaryLoadTask,
                        _settings,
                        _wordCardService,
                        _host);

                    _dictionaryWindow.Closed += (s, e) => _dictionaryWindow = null;
                    _dictionaryWindow.Show();
                }
                else
                {
                    _dictionaryWindow.Activate();
                    _dictionaryWindow.Focus();
                }
            }
            catch (Exception ex)
            {
                _host?.LogError(Resources.Message_OpenDictionaryWindowFailed, ex);
            }
        }

        private Assembly ResolvePluginAssembly(ResolveEventArgs args)
        {
            if (string.IsNullOrWhiteSpace(args?.Name))
                return null;

            string assemblyName = new AssemblyName(args.Name).Name;
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrWhiteSpace(pluginDir))
                return null;

            // Only resolve NAudio-related assemblies from the plugin directory.
            if (!assemblyName.StartsWith("NAudio", StringComparison.OrdinalIgnoreCase))
                return null;

            string assemblyPath = Path.Combine(pluginDir, assemblyName + ".dll");
            if (File.Exists(assemblyPath))
            {
                try
                {
                    return Assembly.LoadFrom(assemblyPath);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private T GetServiceOrDefault<T>() where T : class
        {
            try
            {
                return GetService<T>();
            }
            catch
            {
                return null;
            }
        }
    }
}
