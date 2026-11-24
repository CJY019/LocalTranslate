"""Offline Windows OCR with an optional local visual transcription fallback."""

from collections.abc import Callable
import json
import ntpath
import os
from pathlib import Path
import re
import subprocess
from threading import Event
import time


_ROOT = Path(__file__).resolve().parent.parent
_WORKER = Path(__file__).with_suffix(".ps1")
_EXTENSIONS = (".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp")
_CHINESE_TEXT = "\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\U00020000-\U000323af，。！？、：；（）《》【】〈〉「」『』"
_CHINESE_WORD_GAP = re.compile(rf"(?<=[{_CHINESE_TEXT}]) +(?=[{_CHINESE_TEXT}])")
_LANGUAGE_TAG = re.compile(r"[a-z]{2,8}(?:-[a-z0-9]{1,8})*", re.IGNORECASE | re.ASCII)
_VISUAL_FALLBACK_ERRORS = {"language_unavailable", "no_languages", "no_text"}
_ERRORS = {
    "unavailable": "无法启动 Windows 图片文字识别。",
    "no_languages": "Windows 未安装文字识别语言包。请在 Windows 语言设置中添加所需语言的光学字符识别组件。",
    "language_unavailable": "Windows 未安装所选的文字识别语言。请选择已安装的语言，或在 Windows 语言设置中添加相应组件。",
    "decode_failed": "无法读取图片。请确认文件可打开，或另存为 PNG 或 JPG 后重试。",
    "recognition_failed": "Windows 无法识别这张图片。请尝试裁剪文字区域后重试。",
    "no_text": "图片中未识别到可读文字。请确认文字清晰，或选择与图片匹配的识别语言。",
    "timeout": "图片识别超时。请尝试缩小图片或裁剪文字区域。",
    "cancelled": "图片识别已停止。",
}


class ImageOcrError(ValueError):
    def __init__(self, code: str, message: str | None = None):
        self.code = code
        super().__init__(message or _ERRORS[code])


def _collect(worker: subprocess.Popen, data: bytes | None, cancel: Event | None) -> bytes:
    deadline = time.monotonic() + 60
    while True:
        if cancel is not None and cancel.is_set():
            raise ImageOcrError("cancelled")
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise ImageOcrError("timeout")
        try:
            stdout, _ = worker.communicate(data, timeout=remaining if cancel is None else min(remaining, 0.15))
        except subprocess.TimeoutExpired:
            data = None  # communicate already sent the input to the worker.
            continue
        if cancel is not None and cancel.is_set():
            raise ImageOcrError("cancelled")
        return stdout


def _request(data: dict, cancel: Event | None = None) -> dict:
    if os.name != "nt":
        raise ImageOcrError("unavailable")
    if cancel is not None and cancel.is_set():
        raise ImageOcrError("cancelled")
    powershell = Path(os.environ.get("SystemRoot", r"C:\Windows")) / (
        r"System32\WindowsPowerShell\v1.0\powershell.exe"
    )
    temp = _ROOT / "temp"
    try:
        temp.mkdir(exist_ok=True)
        with subprocess.Popen(
            [str(powershell), "-NoLogo", "-NoProfile", "-NonInteractive", "-Sta", "-ExecutionPolicy", "Bypass",
             "-File", str(_WORKER)],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            creationflags=subprocess.CREATE_NO_WINDOW,
            env={**os.environ, "TEMP": str(temp), "TMP": str(temp)},
        ) as worker:
            try:
                stdout = _collect(worker, json.dumps(data, ensure_ascii=False).encode("utf-8"), cancel)
            finally:
                if worker.poll() is None:
                    worker.kill()
                    worker.communicate()
            if worker.returncode != 0:
                raise ImageOcrError("unavailable")
        payload = json.loads(stdout)
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ImageOcrError("unavailable") from exc
    if not isinstance(payload, dict):
        raise ImageOcrError("unavailable")
    if payload.get("error") in _ERRORS:
        raise ImageOcrError(payload["error"])
    return payload


def _language(tag: str) -> str:
    return tag.partition("-")[0].lower()


def ocr_capabilities() -> dict:
    result = _request({"action": "capabilities"})
    for language in result["languages"]:
        language["language"] = _language(language["tag"])
    result["extensions"] = list(_EXTENSIONS if result.pop("webp") else _EXTENSIONS[:-1])
    result["provider"] = "windows"
    return result


def recognize_image(
    path: str | Path,
    language: str = "auto",
    cancel: Event | None = None,
    visual_reader: Callable[[str, str, Event | None], str] | None = None,
) -> dict:
    """Return recognized text, the OCR engine language, and line boxes in image pixels."""
    if not isinstance(path, (str, Path)) or not str(path) or "\0" in str(path):
        raise ImageOcrError("invalid_path", "图片路径无效。")
    path = Path(path)
    if not path.is_absolute() or path.drive.startswith("\\\\") or ntpath.isreserved(str(path)):
        raise ImageOcrError("invalid_path", "请选择本机上的图片文件。")
    if path.suffix.lower() not in _EXTENSIONS:
        raise ImageOcrError("unsupported_format", "请选择 PNG、JPG、BMP、GIF、TIFF 或 WebP 图片。")
    if not path.is_file():
        raise ImageOcrError("invalid_path", "找不到所选图片文件。")
    if not isinstance(language, str) or len(language) > 80 or not _LANGUAGE_TAG.fullmatch(language.strip()):
        raise ImageOcrError("invalid_language", "图片识别语言无效。")
    language = language.strip()
    try:
        result = _request({"action": "recognize", "path": str(path), "language": language}, cancel)
    except ImageOcrError as error:
        if visual_reader is None or error.code not in _VISUAL_FALLBACK_ERRORS:
            raise
        image = _request({"action": "prepare_visual", "path": str(path)}, cancel)
        try:
            text = visual_reader(image["base64"], language, cancel).strip()
        except (RuntimeError, OSError) as exc:
            if cancel is not None and cancel.is_set():
                raise ImageOcrError("cancelled") from exc
            raise ImageOcrError("visual_failed", f"本地视觉识别失败：{exc}") from exc
        if cancel is not None and cancel.is_set():
            raise ImageOcrError("cancelled")
        if not text:
            raise ImageOcrError("no_text")
        return {
            "text": text, "engine": "translategemma", "language": None, "language_tag": None, "lines": [],
            "width": image["width"], "height": image["height"],
        }
    result["engine"] = "windows"
    result["language"] = _language(result["language_tag"])
    if result["language"] == "zh":
        for line in result["lines"]:
            line["text"] = _CHINESE_WORD_GAP.sub("", line["text"])
        result["text"] = "\n".join(line["text"] for line in result["lines"])
    return result
