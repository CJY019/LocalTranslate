# Install LocalTranslate on Windows

Use 64-bit Windows 11 or Windows 10 version 22H2 with .NET Framework 4.8 or later. This package uses CPU inference by default. A compatible GPU runtime can be configured separately. Windows OCR uses the recognition languages installed on the machine.

## Install and run

1. Download `LocalTranslate-v1.0.0-windows-x64.zip` and `LocalTranslate-v1.0.0-SHA256SUMS.txt` from the release. Compare the ZIP hash with the matching line in the checksum file.
2. Extract the **entire** ZIP to a writable folder such as `D:\Apps\LocalTranslate`. Keep the `runtime`, `backend` and application executable together.
3. Double-click `install.cmd` to add a desktop shortcut and start the app from the extracted folder. You can also open `LocalTranslate.exe` directly. Use the optional `-InstallDirectory` parameter of `install.ps1` to copy the application to a different folder.
4. On the first launch, the local backend starts downloading Index‑Translate 2B Q4_K_M. Open **Settings → Model manager** for progress. Leave roughly 2 GiB free for the unpacked application and default weights. To use the application offline on first launch, put the verified model file into the application’s `models` folder before opening it.

Run this PowerShell command from the download directory to check the ZIP.

~~~~powershell
Get-FileHash .\LocalTranslate-v1.0.0-windows-x64.zip -Algorithm SHA256
~~~~

Language packs and model weights are obtained separately. English and Simplified Chinese work offline. The French interface pack downloads when selected. After model installation, translation works without network access. Settings and cache files live alongside the executable. [Model download and verification details](models.md)

## Build from source

Install Visual Studio 2022 or later, or its Build Tools, with MSBuild, Roslyn and the .NET desktop build tools. Install a Windows 10 or 11 SDK that provides `UnionMetadata\Windows.winmd`. Run these commands from the repository root on the drive where you want its outputs.

~~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\frontend\build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Version v1.0.0 -FrontendExe .\LocalTranslate.exe -OutputDirectory .\dist
~~~~

The release script downloads the dependencies pinned in `scripts/dependencies.json`, checks SHA‑256 before extraction, and verifies the bundled Python modules and llama.cpp executable. Use `-CacheDirectory` to reuse a download cache on the chosen drive. The script creates the app ZIP, a corresponding-source ZIP, and `SHA256SUMS.txt`. Repeat the build with the same compiler, SDK and archived dependencies when comparing artifacts.

Run the backend model-management tests and language pack checks with Python 3.13 from the repository root.

~~~~powershell
python -B -m unittest discover -s tests -v
python -B tools/check_langpacks.py
~~~~

## Optional inference runtimes

- **Ollama.** Use an existing compatible Windows installation for TranslateGemma and the optional Qwen3.5 chat assistant. Set `LOCAL_TRANSLATE_OLLAMA_EXE` for a custom Ollama executable and `OLLAMA_MODELS` for its model store before starting LocalTranslate. [Model setup](models.md)
- **NVIDIA CUDA.** The release contains the CPU version of llama.cpp. To use CUDA, install an appropriate NVIDIA driver and replace `runtime\llama` with a compatible Windows x64 CUDA build of llama.cpp and its required CUDA runtime libraries. Keep those files together. The backend selects GPU layers when the CUDA backend is present. Restore the CPU runtime if your GPU or driver cannot load the quantization.

## 更新、迁移和卸载

- 将新版解压到单独目录，退出旧版后把已有 `models` 和 `settings.json` 移过去，再双击新版。应用管理的权重会重新校验，匹配的文件会直接复用。
- 删除程序目录会删除其中的权重、设置和缓存。需要保留的译文请先另存。桌面快捷方式可单独删除。
- 已通过本机验证的环境是 Windows 11 x64。只含图像的 PDF 需要先用外部工具生成文字层。图片翻译所需的 OCR 语言组件可在 Windows 设置中安装。

[Model integration](models.md) · [Interface translations](languages.md) · [Third-party notices](../THIRD_PARTY_NOTICES.md)
