# QuickDictForICC

QuickDictForICC 是 [InkCanvasForClass Community Edition](https://github.com/InkCanvasForClass/community)（ICC-CE）的英语单词查询插件，支持 ECDICT 离线词典与用户自行导入的 MDict 词典，并内置 Edge TTS 与 Piper 本地 TTS 发音功能。

>[!warning]
>经过评估，mdict支持属于伪需求且实现周期过于漫长，后续将不再优化mdict解析，如果你想做mdict相关内容请自行pr。   

>[!note]
>本readme文档内容一部分为AI自动撰写，部分内容可能有误，欢迎勘误。   


## 功能简介

- **单词查询**：在 ICC 工具栏点击“QuickDict 查词”按钮，输入英文单词即可查看释义。
- **多词典支持**：
  - [ECDICT](https://github.com/skywind3000/ECDICT) 离线 CSV 词典。
  - 用户导入的 MDict 词典（`.mdx`）及可选资源包（`.mdd`）。
- **发音朗读**：
  - **Edge TTS**：在线微软神经网络语音，无需额外安装。
  - **Piper TTS**：本地离线神经网络语音，需自行下载 Piper 可执行文件与语音模型。
- **设置持久化**：词典路径、TTS 引擎、音色等设置会自动保存到本地配置文件。

## 依赖准备

插件本体仅包含程序文件，以下数据/程序需要用户手动准备并配置路径。

### 1. ECDICT 数据（推荐）

- 下载地址：<https://github.com/skywind3000/ECDICT>
- 需要文件：`ecdict.csv`（完整版或精简版均可）。
- 在插件设置中选择该 CSV 文件路径。

### 2. MDict 词典（可选）

- 支持标准 MDict 格式：
  - 词库文件：`.mdx`
  - 资源文件：`.mdd`（部分词典含图片/发音资源时可选）
- 在插件设置中分别选择 `.mdx` 与 `.mdd` 路径。

### 3. Piper TTS（可选，用于离线发音）

- 下载 Piper 可执行文件（`piper.exe`）：<https://github.com/rhasspy/piper>
- 下载所需的 `.onnx` 语音模型及对应 `.onnx.json` 配置文件。
- 在插件设置中配置：
  - Piper 可执行文件路径
  - Piper 语音模型路径（`.onnx`）

### 4. Edge TTS

- 无需手动下载，依赖本机网络连接访问微软在线 TTS 服务。
- 可在设置中切换音色（如 `en-US-AriaNeural`）与语速。

## 安装方式

1. 从 Release 页面下载 `quickdict.plugin.icpx`。
2. 将 `.icpx` 文件放入 ICC 插件目录：

   ```text
   %LocalAppData%\InkCanvasForClass\Plugins\
   ```

   或 ICC 安装目录下的 `Plugins` 文件夹（具体以 ICC 版本为准）。
3. 重启 ICC，在工具栏即可看到“QuickDict 查词”按钮。

## 设置项说明

| 设置项 | 说明 |
| --- | --- |
| **ECDICT 路径** | `ecdict.csv` 文件完整路径，作为默认离线词库。 |
| **MDict 词库路径** | `.mdx` 文件路径，可与 ECDICT 同时使用或单独使用。 |
| **MDict 资源路径** | `.mdd` 文件路径，可选，用于加载词典内的媒体资源。 |
| **TTS 引擎** | 选择 `Edge`（在线）或 `Piper`（本地离线）。 |
| **Edge 音色** | Edge TTS 的说话人，如 `en-US-AriaNeural`。 |
| **Edge 语速** | 语速偏移百分比，范围 `-50` ~ `+50`，默认 `0`。 |
| **Piper 可执行文件** | `piper.exe` 的完整路径。 |
| **Piper 语音模型** | Piper `.onnx` 模型文件路径。 |

设置保存后，词典/TTS 路径变更需重启插件后生效。

## 构建与打包

本地构建请使用 Release 配置：

```powershell
dotnet build QuickDictForICC.csproj -c Release
```

生成 `.icpx` 插件包：

```powershell
.\pack.ps1
```

脚本默认读取 `bin\Release\net6.0-windows10.0.19041.0\publish` 目录下的全部发布文件（含依赖 DLL）并打包为 `packages\quickdict.plugin.icpx`。也可通过环境变量指定配置：

```powershell
$env:BuildConfiguration = "Debug"
.\pack.ps1
```

## 目录结构

```text
QuickDictForICC/
├── QuickDictPlugin.cs          # 插件入口
├── manifest.json               # 插件元数据
├── QuickDictForICC.csproj
├── Services/                   # 词典、TTS、设置服务
├── Views/                      # DictionaryPopup、ResultView、SettingsView
├── lib/                        # InkCanvas 插件 SDK 引用
├── pack.ps1                    # 打包脚本
└── packages/                   # 生成的 .icpx 包
```

## 已知限制

- 首次使用必须至少配置 ECDICT 或 MDict 中的一个，否则插件会提示“未找到可用的词典文件”。
- Edge TTS 需要联网；Piper TTS 需要正确配置可执行文件与模型。
- 未提供 ICC 宿主环境时，仅完成编译与打包验证，无法在实际 ICC 中运行测试。

## License

本项目采用 GPL-3.0 许可证，详见 [LICENSE](LICENSE)。
