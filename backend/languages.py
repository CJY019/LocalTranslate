"""Supported translation directions and local source-language detection."""

from __future__ import annotations

from dataclasses import dataclass
from functools import cache
from pathlib import Path
import os
import re
import sys

from language_catalog import ADDITIONAL_LANGUAGES, INDEX_LANGUAGES
from model_catalog import BUILTIN_MODELS


@dataclass(frozen=True)
class Language:
    code: str
    name: str
    english: str


def _catalog():
    languages, aliases = {}, {}
    for line in (INDEX_LANGUAGES.strip() + "\n" + ADDITIONAL_LANGUAGES.strip()).splitlines():
        code, name, english, *alternate_codes = line.split("|")
        languages[code] = Language(code, name, english)
        aliases[code.lower()] = code
        for alternate in " ".join(alternate_codes).split():
            aliases[alternate.lower().replace("_", "-")] = code
    return languages, aliases


LANGUAGES, _ALIASES = _catalog()
_ALIASES.update({"tl": "fil", "zh-cn": "zh", "zh-hans": "zh", "zh-tw": "zh-Hant"})
_MODEL_ALIASES = {
    "translategemma": {
        "ar": "ar-SA", "pt": "pt-BR", "sw": "sw-KE", "nb": "no",
        "bg-bg": "bg", "bn-in": "bn", "ca-es": "ca", "cs-cz": "cs", "da-dk": "da",
        "de-de": "de", "el-gr": "el", "es-mx": "es", "et-ee": "et", "fa-ir": "fa",
        "fi-fi": "fi", "fil-ph": "fil", "fr-fr": "fr", "gu-in": "gu", "he-il": "he",
        "hi-in": "hi", "hr-hr": "hr", "hu-hu": "hu", "id-id": "id", "is-is": "is",
        "it-it": "it", "ja-jp": "ja", "kn-in": "kn", "ko-kr": "ko", "lt-lt": "lt",
        "lv-lv": "lv", "ml-in": "ml", "mr-in": "mr", "nl-nl": "nl", "no-no": "no",
        "pa-in": "pa", "pl-pl": "pl", "ro-ro": "ro", "ru-ru": "ru", "sk-sk": "sk",
        "sl-si": "sl", "sr-rs": "sr", "sv-se": "sv", "ta-in": "ta", "te-in": "te",
        "th-th": "th", "tr-tr": "tr", "uk-ua": "uk", "ur-pk": "ur", "vi-vn": "vi",
        "zu-za": "zu",
    },
    "index": {"no": "nb", "ku": "kmr"},
}
GEMMA_PROMPT_CODES = {"fil": "fil-PH"}


def model_language_metadata(model_id: str, *, models=BUILTIN_MODELS) -> dict:
    options = [{"code": code, "name": LANGUAGES[code].name} for code in models[model_id].languages]
    return {
        "source_languages": [{"code": "auto", "name": "自动检测"}, *options],
        "target_languages": options,
    }


def _model_code(model, code: str) -> str | None:
    normalized = code.lower().replace("_", "-")
    canonical = _ALIASES.get(normalized, code)
    aliases = _MODEL_ALIASES.get(model.prompt_adapter, {}) if model is not None else {}
    canonical = aliases.get(normalized, aliases.get(canonical, canonical))
    if canonical in (LANGUAGES if model is None else model.languages):
        return canonical
    return None


def validate_languages(model_id: str, source: str, target: str, *, models=BUILTIN_MODELS) -> tuple[str, str]:
    if not isinstance(model_id, str) or model_id not in models:
        raise ValueError("请选择可用的翻译模型。")
    model = models[model_id]
    validated = []
    for code, label in ((source, "输入"), (target, "输出")):
        canonical = (code if code == "auto" else _model_code(model, code)) if isinstance(code, str) else None
        if canonical is None:
            raise ValueError(f"所选模型不支持此{label}语言。")
        validated.append(canonical)
    return tuple(validated)


PLACEHOLDER = re.compile(r"⟪T\d{4,}⟫")
_URL = re.compile(r"(?:https?://|www\.)\S+|\S+@\S+\.\S+", re.IGNORECASE)
_HAN = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\U00020000-\U000323af]")
_KANA = re.compile(r"[\u3041-\u3096\u30a1-\u30fa\uff66-\uff9d]")
_HANGUL = re.compile(r"[\u1100-\u11ff\u3131-\u318e\uac00-\ud7a3]")
_CYRILLIC = re.compile(r"[\u0400-\u052f]")
_LATIN = re.compile(r"[A-Za-z\u00c0-\u024f]")
_ENGLISH_WORD = re.compile(r"[A-Za-z]+(?:['’-][A-Za-z]+)*")
_OUTER_PUNCTUATION = re.compile(r"^[\W_]+|[\W_]+$")
_DETECTOR_DIR = Path(__file__).resolve().parent.parent / "runtime" / "language-detection"


@cache
def _language_detector():
    sys.path.insert(0, str(_DETECTOR_DIR))
    try:
        from fasttext import load_model
        return load_model(str(_DETECTOR_DIR / "lid.176.ftz"))
    except (ImportError, OSError, ValueError) as error:
        raise RuntimeError("无法加载本地语言检测，请手动选择输入语言。") from error
    finally:
        sys.path.remove(str(_DETECTOR_DIR))


def _traditional_chinese(text: str) -> bool:
    if os.name != "nt":
        return False
    from ctypes import WinDLL, create_unicode_buffer

    to_simplified = 0x02000000  # LCMAP_SIMPLIFIED_CHINESE
    mapped = create_unicode_buffer(len(text.encode("utf-16-le")) // 2 + 1)
    length = WinDLL("kernel32").LCMapStringEx("zh-CN", to_simplified, text, -1, mapped, len(mapped), None, None, 0)
    return bool(length and mapped.value != text)


_FASTTEXT_CODES = {"als": "sq", "iw": "he", "no": "nb", "tl": "fil", "zh-yue": "yue"}
_SHORT_PHRASES = {
    "hello": "en",
    "hi": "en",
    "yes": "en",
    "thanks": "en",
    "hola": "es",
    "gracias": "es",
    "bonjour": "fr",
    "merci": "fr",
    "ciao": "it",
}
_AMBIGUOUS_WORDS = {"no", "ja", "gift", "chat"}


def detect_language(text: str) -> str | None:
    sample = " ".join(_URL.sub(" ", PLACEHOLDER.sub(" ", text[:12_000])).replace("\0", " ").split())
    phrase = _OUTER_PUNCTUATION.sub("", sample).casefold()
    if phrase in _SHORT_PHRASES:
        return _SHORT_PHRASES[phrase]
    if phrase in _AMBIGUOUS_WORDS:
        return None
    letters = sum(char.isalpha() for char in sample)
    if not letters:
        return None

    han, kana, hangul = (len(pattern.findall(sample)) for pattern in (_HAN, _KANA, _HANGUL))
    if (han + kana) * 2 >= letters and kana:
        return "ja"
    if hangul * 2 >= letters:
        return "ko"
    if han and len(_LATIN.findall(sample)) >= han * 3:
        # A few quoted ideographs should not skew the classifier for otherwise Latin-script text.
        sample = _HAN.sub(" ", sample)
    labels, scores = _language_detector().predict(sample, k=2)
    if not scores:
        return None
    confidence, runner_up = scores[0], scores[1] if len(scores) > 1 else 0
    if confidence < (0.8 if 0 < han == letters <= 2 else 0.28) or confidence - runner_up < 0.12:
        return None
    code = labels[0].removeprefix("__label__")
    code = _FASTTEXT_CODES.get(code, code)
    if code == "zh" and _traditional_chinese(sample):
        return "zh-Hant"
    if code == "sr" and not _CYRILLIC.search(sample):
        return "sr-Latn"
    if code == "uz" and _CYRILLIC.search(sample):
        return "uz-Cyrl"
    return code


def resolve_languages(
    text: str, source: str, target: str, model_id: str | None = None, *, models=BUILTIN_MODELS,
) -> tuple[str, str]:
    model = models.get(model_id)
    adapter = model.prompt_adapter if model is not None else None
    target_only = adapter in ("index", "hymt", "generic")
    if source == "auto":
        detected = detect_language(text)
        if detected is None:
            if target != "auto":
                if target_only:
                    return source, target
                raise ValueError("无法确定输入语言。请补充文字或手动选择输入语言。")
            # Older clients sent both fields as auto to request the original Chinese/English direction.
            sample = PLACEHOLDER.sub("", text)
            source = "zh" if len(_HAN.findall(sample)) > len(_ENGLISH_WORD.findall(sample)) else "en"
            if model is not None and source not in model.languages:
                raise ValueError("无法确定输入语言。请补充文字或手动选择输入语言。")
        else:
            source = _model_code(model, detected)
            if source is None:
                if adapter == "index" and detected == "zh-Hant" and "zh" in model.languages:
                    source = "zh"  # Index lists a single Mandarin entry and uses a target-only prompt.
                elif target_only and target != "auto" and detected not in LANGUAGES:
                    return "auto", target
            if source is None:
                language = LANGUAGES.get(detected)
                label = language.name if language else detected
                raise ValueError(f"检测到输入语言为 {label}，所选模型不支持。请切换模型或手动选择输入语言。")
    if target == "auto":
        target = "en" if source in ("zh", "zh-Hant", "yue") else "zh"
        if model is not None and target not in model.languages:
            raise ValueError("所选模型需要手动选择输出语言。")
    return source, target
