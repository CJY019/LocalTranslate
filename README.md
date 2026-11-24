# LocalTranslate · 本地翻译

Windows x64 桌面翻译器。可以粘贴文本、在其他应用里划词、翻译图片，或批量翻译文档和字幕。应用使用本地模型，默认选择 **Index‑Translate 2B**。模型和运行时准备好后，翻译可离线使用。

**[下载 Windows 版](https://github.com/CJY019/LocalTranslate/releases/latest)** · [安装与源码构建](docs/install.md) · [添加模型](docs/models.md) · [制作界面语言包](docs/languages.md) · [English](#localtranslate-for-windows)

## 第一次使用

1. 在 Releases 下载 Windows x64 应用 ZIP，完整解压到自己可写的位置，例如 <code>D:\Translate</code>。双击 <code>install.cmd</code> 创建桌面快捷方式并启动，也可以直接双击 <code>LocalTranslate.exe</code>。
2. 首次启动会下载 **Index‑Translate 2B Q4_K_M**，约 1.31 GB。到设置的模型管理区查看进度，也可以取消、重试、导入事先下载的 GGUF 文件或删除已安装的模型。删除默认模型后，重开应用会保留这个选择。
3. 在翻译工作区选择模型、输入语言和输出语言。输入可选“自动检测”。选择界面语言则在设置里完成，中文和英文随应用提供。选择其他已发布的界面语言时才下载对应的 GitHub 语言包，成功安装后可离线切换。

### 翻译方式

| 入口 | 用法 |
| --- | --- |
| 文本 | 输入或粘贴原文，选择目标语言后开始翻译。流式结果可以停止和复制。 |
| 鼠标划词 | 在首页打开划词开关，在其他应用中选中文字，松开鼠标后点“译”。浮窗中可直接换模型和输入、输出语言，然后点“重新翻译”。在微信等自绘窗口中，程序可以在本机识别高亮文本。不同客户端、主题及文字形状会影响可读性。 |
| 文件和字幕 | 支持 TXT、Markdown、DOCX、有文字层的 PDF、SRT 和 VTT，可拖入文件或文件夹。可输出仅译文或双语。PDF 输出 Word，字幕保留原时间段。默认在原文件旁另存带语言后缀的副本，也可统一选择目录、修改输出名。 |
| 图片 | 选择、拖入或粘贴图片，识别、校对后翻译。Windows OCR 可用语种取决于本机安装的语言组件。 |
| 对话 | 可选组件。需要兼容的 Ollama 运行时及本机 Qwen3.5 9B，安装方式和硬件需求独立于默认翻译模型。 |

### 翻译模型

| 模型 | 权重、来源与部署方式 |
| --- | --- |
| Index‑Translate 2B | 默认模型。下载 [官方 Q4_K_M GGUF](https://huggingface.co/IndexTeam/Index-Translate-2B-GGUF) 后由 llama.cpp 在本机运行。 |
| Hy‑MT2 7B | 可选模型。下载 [官方 Q4_K_M GGUF](https://huggingface.co/tencent/Hy-MT2-7B-GGUF)，约 4.62 GB。 |
| TranslateGemma 4B | 可选模型。复用已有 [Ollama TranslateGemma](https://ollama.com/library/translategemma) 安装，模型许可见 [Google 模型卡](https://huggingface.co/google/translategemma-4b-it)。 |

模型管理使用固定版本、下载大小和 SHA‑256 校验。每个模型有自己的提示模板和支持的输入、输出语言。项目提供本地模型注册方式、管理接口和源码示例。[模型接口与接入指南](docs/models.md)

### 数据位置与联网时机

译文、模型推理、划词和图片识别在本机处理。桌面应用通过 <code>127.0.0.1:47836</code> 调用本地后端。模型下载访问模型发布方，附加界面语言包从此 GitHub 仓库下载。划词兼容复制可能触发 Windows 剪贴板历史或用户已启用的剪贴板同步；微信浮窗在需要替换现有剪贴板时会显示单独的操作按钮。

在设置中可查看权重目录。设置、管理状态和运行日志位于应用目录内；文件翻译的输出位置单独选择。使用 <code>D:\Translate</code> 这样的普通目录，方便读写和迁移。应用遵循 Windows 的系统组件、权限和字体设置。

## LocalTranslate for Windows

LocalTranslate is a WPF desktop translator for 64-bit Windows. It translates text, mouse selections, images, documents and subtitles on your own machine. Download and extract the **Windows x64 application ZIP** from Releases, then run <code>LocalTranslate.exe</code>. First launch downloads the official 1.31 GB Index‑Translate 2B quantization. English and Simplified Chinese are built in. Additional interface packs are downloaded on selection and cached for offline use.

Open **Settings → Model manager** to check progress, retry, import a verified GGUF or remove a model. An explicitly removed default model stays removed. A text selection opens a small translation window with its own model, input-language and output-language selectors. Choose a new pair and use **Translate again** to reuse the original selection.

Sources are organized under <code>frontend/</code> (WPF), <code>backend/</code> (loopback API, inference, file processing and OCR), and <code>langpacks/</code> (interface translations). The installation guide covers the reproducible Windows build and runtime dependencies. Each upstream model retains its own license.

[Windows downloads](https://github.com/CJY019/LocalTranslate/releases/latest) · [Build and installation](docs/install.md) · [Models and API](docs/models.md) · [Language packs](docs/languages.md) · [Third-party notices](THIRD_PARTY_NOTICES.md)

## License

LocalTranslate application code is licensed under **GNU AGPL‑3.0‑or‑later**. Dependencies, bundled language detection data and separately downloaded model weights have their own licenses. [Application license](LICENSE) · [Third-party notices](THIRD_PARTY_NOTICES.md)
