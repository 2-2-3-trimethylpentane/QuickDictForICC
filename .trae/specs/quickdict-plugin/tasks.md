# 任务列表

- [x] 任务 1：搭建插件项目结构
  - [x] 将示例插件清单替换为 QuickDict 插件元数据
  - [x] 清理示例演示工具栏项和设置视图
  - [x] 添加所需 NuGet 包：`Microsoft.Web.WebView2`、`NAudio`、`System.Text.Json`、`iNKORE.UI.WPF.Modern`
  - [x] 验证项目能针对 ICC SDK 正常编译

- [x] 任务 2：实现词典服务层
  - [x] 定义 `IWordEntry` 和 `IDictionaryService` 抽象接口
  - [x] 实现 ECDICT 解析/读取（加载 CSV/JSON 到内存或 SQLite）
  - [x] 实现用户导入 `.mdx`/`.mdd` 的 MDict 解析集成
  - [x] 实现查词回退逻辑：已加载 MDict 时优先使用，否则回退到 ECDICT

- [x] 任务 3：实现 TTS 服务层
  - [x] 定义 `ITtsEngine` 抽象接口
  - [x] 实现 Edge TTS 引擎（在线、WebSocket/HTTP、缓存 MP3）
  - [x] 实现 Piper TTS 引擎（本地进程、ONNX 模型）
  - [x] 实现引擎选择器，并通过 NAudio 播放音频

- [x] 任务 4：构建虚拟键盘弹窗界面
  - [x] 创建 `DictionaryPopup.xaml`，包含搜索框、候选词条、虚拟键盘、搜索按钮
  - [x] 为触摸优化按键样式（大点击区域、圆角）
  - [x] 将按键点击绑定到输入框和查询命令
  - [x] 添加退格、清空、空格键功能

- [x] 任务 5：构建结果展示界面
  - [x] 创建结果面板，显示单词、音标和发音按钮
  - [x] 添加标签页控件：释义、词组、例句、近义词
  - [x] MDict 结果通过 WebView2 渲染 HTML，ECDICT 结果以文本展示
  - [x] 添加底部"生成单词卡"按钮占位

- [x] 任务 6：注册工具栏按钮和设置视图
  - [x] 在插件 `Initialize` 中注册单个查词工具栏项
  - [x] 创建新的 `SettingsView.xaml`，配置 MDict 路径、TTS 选择、音色语速、Piper 配置
  - [x] 将设置持久化到本地，启动时自动加载
  - [x] 对需要手动下载的依赖给出友好提示

- [x] 任务 7：构建与冒烟测试
  - [x] 编译插件 DLL 并生成 `.icpx` 插件包
  - [x] 在 ICC 宿主中运行（如有）或使用说明方式验证
  - [x] 验证编译与打包功能正常

# 任务依赖关系
- 任务 5 依赖任务 2
- 任务 4 依赖任务 2
- 任务 7 依赖任务 1、2、3、4、5、6

- [x] 修复任务：补全 `IWordEntry` 抽象接口
  - [x] 在 `Services` 中新增 `IWordEntry` 接口（包含 `Word`、`Phonetic`、`Definition`、`Translation`、`Pos`、`Exchange`、`HtmlDefinition`、`Source` 等属性）
  - [x] 让现有 `WordEntry` 类实现 `IWordEntry`
  - [x] 将 `IDictionaryService.Lookup` 的返回类型改为 `IWordEntry`
  - [x] 更新 `DictionaryService`、`EcDictService`、`MDictService`、`ResultView` 中对 `WordEntry` 的引用，保持接口兼容
  - 原因：验收清单要求词典服务抽象包含 `IWordEntry`，但当前实现只有 `WordEntry` 具体类，缺少对应接口抽象。
