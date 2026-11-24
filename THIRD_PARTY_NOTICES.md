# Third-party notices

LocalTranslate's application sources are covered by the repository `LICENSE`. The components listed below are provided under their upstream terms. The Windows application ZIP keeps upstream license files with the runtime and Python wheel metadata. The build inputs, versions, URLs and SHA‑256 digests are recorded in [scripts/dependencies.json](scripts/dependencies.json).

| Component | Version | Terms and upstream source |
| --- | --- | --- |
| CPython Windows embeddable distribution | 3.13.17 | Python Software Foundation license and separate bundled notices in `runtime/python/LICENSE.txt`. [Source](https://github.com/python/cpython/tree/v3.13.17) |
| llama.cpp and ggml | b12633 | MIT. A copy is included in `runtime/licenses/llama-MIT.txt`. [Source and release](https://github.com/ggml-org/llama.cpp/releases/tag/b12633) |
| LLVM OpenMP runtime | Bundled with llama.cpp b12633 | See the `LICENSE-LLVM-OpenMP` included beside `libomp.dll`. [LLVM OpenMP](https://github.com/llvm/llvm-project/tree/main/openmp) |
| Microsoft Visual C++ runtime DLLs | 14.44.35211 | Microsoft redistributable terms in `runtime/licenses/Microsoft-VC-Runtime.rtf`. These application-local binaries are unmodified. Redistribution is subject to the terms of the licensed Visual Studio edition used by a distributor. [Microsoft redistribution list](https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution) |
| PyMuPDF and MuPDF | 1.27.2.3 / 1.27.2 | GNU AGPL v3 distribution. The Windows wheel keeps its upstream licenses. The corresponding-source archive next to the app ZIP carries the exact upstream PyMuPDF and MuPDF source archives and LocalTranslate's build scripts. [PyMuPDF](https://github.com/pymupdf/PyMuPDF) · [MuPDF](https://mupdf.com/downloads/) |
| python-docx | 1.2.0 | MIT. [Source and metadata](https://pypi.org/project/python-docx/1.2.0/) |
| lxml | 6.1.3 | BSD-3-Clause for lxml, with separate notices for components in the Windows wheel. [Source and metadata](https://pypi.org/project/lxml/6.1.3/) |
| markdown-it-py | 4.3.0 | MIT. [Source and metadata](https://pypi.org/project/markdown-it-py/4.3.0/) |
| mdurl | 0.1.2 | MIT. [Source and metadata](https://pypi.org/project/mdurl/0.1.2/) |
| typing-extensions | 4.16.0 | PSF license. [Source and metadata](https://pypi.org/project/typing-extensions/4.16.0/) |
| fasttext-predict | 0.9.2.3 | MIT. [Source and metadata](https://pypi.org/project/fasttext-predict/0.9.2.3/) |
| fastText language identification data `lid.176.ftz` | Upstream published file | Copyright Meta Platforms, Inc. and affiliates. The app redistributes the unmodified data under CC BY‑SA 3.0 Unported. [Original download and acknowledgment](https://fasttext.cc/docs/en/language-identification.html) · [License](https://creativecommons.org/licenses/by-sa/3.0/) |

Weights downloaded from upstream model repositories carry their publishers' licenses. Optional Ollama and NVIDIA CUDA components have their own distribution and use terms. Windows provides the OCR and WPF system components. [Model licenses and source revisions](docs/models.md)
