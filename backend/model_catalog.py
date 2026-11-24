"""Translation model definitions and the optional local model manifest."""

from __future__ import annotations

import json
import ntpath
import re
import stat
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urlsplit

from language_catalog import GEMMA_LANGUAGES, HYMT_LANGUAGES, INDEX_LANGUAGES


DEFAULT_MODEL_ID = "index2b"
PROMPT_ADAPTERS = frozenset({"index", "hymt", "translategemma", "generic"})
_MODEL_ID = re.compile(r"[a-z][a-z0-9_-]{0,63}\Z")
_FILENAME = re.compile(r"[a-zA-Z0-9][a-zA-Z0-9._-]{0,190}\.gguf\Z", re.IGNORECASE)
_HASH = re.compile(r"[0-9a-f]{64}\Z")
_PINNED_HF_DOWNLOAD = re.compile(
    r"https://huggingface\.co/[a-zA-Z0-9][a-zA-Z0-9_.-]*/[a-zA-Z0-9][a-zA-Z0-9_.-]*/resolve/"
    r"[0-9a-f]{40}/[a-zA-Z0-9][a-zA-Z0-9_.-]*\.gguf\Z",
)


@dataclass(frozen=True)
class GGUFWeight:
    filename: str
    size: int
    sha256: str
    download_url: str = ""


@dataclass(frozen=True)
class TranslationModel:
    id: str
    name: str
    runtime: str
    prompt_adapter: str
    languages: tuple[str, ...]
    license: str
    source_url: str
    weight: GGUFWeight | None = None


_INDEX_REPOSITORY = "https://huggingface.co/IndexTeam/Index-Translate-2B-GGUF"
_HYMT_REPOSITORY = "https://huggingface.co/tencent/Hy-MT2-7B-GGUF"
_INDEX_REVISION = "3a0eca6ce931d88d00614d427ab3ed8ff0743902"
_HYMT_REVISION = "ab8472660ac61fac25f1af43fac2599d52a8a775"
_INDEX_FILENAME = "Index-Translate-2B.Q4_K_M.gguf"
_HYMT_FILENAME = "Hy-MT2-7B-Q4_K_M.gguf"
_INDEX_CODES = tuple(line.split("|", 1)[0] for line in INDEX_LANGUAGES.strip().splitlines())
_BUILTIN_MODELS = (
    TranslationModel(
        "gemma4b", "TranslateGemma 4B", "ollama", "translategemma", tuple(GEMMA_LANGUAGES),
        "Gemma Terms of Use", "https://ollama.com/library/translategemma:4b",
    ),
    TranslationModel(
        DEFAULT_MODEL_ID, "Index-Translate 2B", "llama", "index",
        ("zh", "en", *_INDEX_CODES[2:]),
        "Apache-2.0", f"{_INDEX_REPOSITORY}/tree/{_INDEX_REVISION}", GGUFWeight(
            _INDEX_FILENAME, 1_312_164_352,
            "044b313d29342bd3b2c77cbb64023ca0d209bd9b3247763f9d162767ef2d746a",
            f"{_INDEX_REPOSITORY}/resolve/{_INDEX_REVISION}/{_INDEX_FILENAME}",
        ),
    ),
    TranslationModel(
        "hymt7b", "HY-MT2 7B", "llama", "hymt", tuple(HYMT_LANGUAGES),
        "Apache-2.0", f"{_HYMT_REPOSITORY}/tree/{_HYMT_REVISION}", GGUFWeight(
            _HYMT_FILENAME, 4_624_648_896,
            "9f96256500f3fc1ab4d64336b58f52a949a95ad7516b0c229476eef782f9f77b",
            f"{_HYMT_REPOSITORY}/resolve/{_HYMT_REVISION}/{_HYMT_FILENAME}",
        ),
    ),
)
BUILTIN_MODELS = {model.id: model for model in _BUILTIN_MODELS}


def valid_download_url(url: str) -> bool:
    return bool(_PINNED_HF_DOWNLOAD.fullmatch(url))


def _read_model(entry, language_codes) -> TranslationModel:
    required = {"id", "name", "runtime", "prompt_adapter", "languages", "license", "filename", "size_bytes", "sha256"}
    if not isinstance(entry, dict) or not required <= entry.keys() or entry.keys() - required - {"source_url", "download_url"}:
        raise ValueError("模型清单条目缺少必填字段或包含未知字段。")
    model_id = entry["id"]
    if not isinstance(model_id, str) or not _MODEL_ID.fullmatch(model_id) or model_id == "qwen35_9b":
        raise ValueError("模型 ID 无效或已保留。")
    if entry["runtime"] != "llama":
        raise ValueError("本地模型须使用 llama 运行时。")
    adapter = entry["prompt_adapter"]
    if not isinstance(adapter, str) or adapter not in PROMPT_ADAPTERS:
        raise ValueError("未知的翻译提示词适配器。")
    languages = entry["languages"]
    if (
        not isinstance(languages, list) or len(languages) < 2
        or any(not isinstance(code, str) or code not in language_codes for code in languages)
        or len(set(languages)) != len(languages)
    ):
        raise ValueError("语言列表须包含至少两个不重复的受支持语言代码。")
    name, license_name = entry["name"], entry["license"]
    for item in (name, license_name):
        if not isinstance(item, str) or not item.strip() or len(item) > 120 or any(ord(c) < 32 for c in item):
            raise ValueError("模型名称或许可无效。")
    filename, size, sha256 = entry["filename"], entry["size_bytes"], entry["sha256"]
    if not isinstance(filename, str) or not _FILENAME.fullmatch(filename) or ntpath.isreserved(filename):
        raise ValueError("权重文件名须为 models 目录下的单个 GGUF 文件名。")
    if type(size) is not int or not 4 <= size <= 2**63 - 1 or not isinstance(sha256, str) or not _HASH.fullmatch(sha256):
        raise ValueError("权重文件大小或 SHA-256 无效。")
    download_url = entry.get("download_url", "")
    if not isinstance(download_url, str) or (download_url and not valid_download_url(download_url)):
        raise ValueError("下载链接须为固定提交版本的 Hugging Face HTTPS GGUF 链接。")
    source_url = entry.get("source_url", "")
    if not isinstance(source_url, str) or len(source_url) > 2048 or any(ord(c) <= 32 for c in source_url):
        raise ValueError("模型来源链接无效。")
    if source_url:
        parsed = urlsplit(source_url)
        if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password:
            raise ValueError("模型来源链接须为公开 HTTPS 网页。")
    return TranslationModel(
        model_id, name.strip(), "llama", adapter, tuple(languages), license_name.strip(), source_url,
        GGUFWeight(filename, size, sha256, download_url),
    )


def load_models(root: Path, language_codes) -> tuple[dict[str, TranslationModel], str]:
    """Load one local catalog when the service starts, retaining built-ins on failure."""
    path = root / "models" / "local-models.json"
    models = BUILTIN_MODELS.copy()
    try:
        for candidate, expected in ((path.parent, stat.S_ISDIR), (path, stat.S_ISREG)):
            info = candidate.lstat()
            if not expected(info.st_mode) or getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                raise ValueError("模型清单须位于应用目录中的普通文件路径。")
        with path.open("rb") as stream:
            raw = stream.read(128 * 1024 + 1)
        if len(raw) > 128 * 1024:
            raise ValueError("模型清单过大。")
        document = json.loads(raw.decode("utf-8-sig"))
        if (
            not isinstance(document, dict) or set(document) != {"version", "models"}
            or type(document["version"]) is not int or document["version"] != 1
            or not isinstance(document["models"], list)
        ):
            raise ValueError("模型清单须包含 version=1 和 models 数组。")
        filenames = {model.weight.filename.casefold() for model in models.values() if model.weight}
        for index, entry in enumerate(document["models"], 1):
            try:
                model = _read_model(entry, language_codes)
                filename = model.weight.filename.casefold()
                if model.id in models or filename in filenames:
                    raise ValueError("模型 ID 或权重文件名已存在。")
            except ValueError as error:
                raise ValueError(f"第 {index} 条模型记录有误。{error}") from error
            models[model.id] = model
            filenames.add(filename)
    except FileNotFoundError:
        return models, ""
    except (OSError, ValueError) as error:
        return BUILTIN_MODELS.copy(), f"无法读取 models/local-models.json。{error}"
    return models, ""
