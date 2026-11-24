"""Local translation and chat through one on-demand inference process."""

from __future__ import annotations

from contextlib import nullcontext
from dataclasses import dataclass
import json
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import threading
import time
import urllib.error
import urllib.request

from languages import (
    GEMMA_PROMPT_CODES,
    LANGUAGES,
    PLACEHOLDER,
    model_language_metadata,
    resolve_languages,
    validate_languages,
)
from model_catalog import load_models
from model_manager import ModelManager


_CHAT_MODEL_ID = "qwen35_9b"
_CHAT_MODEL_NAME = "Qwen3.5 9B"
_GEMMA_MODEL_BYTES = 3_298_866_368
_MODEL_TAGS = {"gemma4b": ("translategemma", ("4b", "latest")), _CHAT_MODEL_ID: ("qwen3.5", ("9b",))}
_CHAT_CONTEXT = 4096
_CHAT_OUTPUT = 768
_CHAT_INSTRUCTIONS = (
    "你是一位多语种翻译助手。结合对话上下文，直接回答翻译、词义、语法和用法问题。"
    "需要读音时提供文字读音和 IPA，注明美式或英式；不要声称播放了音频。"
    "按用户要求的语言回答，使用简洁的纯文本，避免 Markdown 装饰。"
)


@dataclass(frozen=True)
class _LocalModel:
    path: Path
    ollama_ref: str = ""
    size: int = 0


def _ollama_executables(legacy):
    configured = os.environ.get("LOCAL_TRANSLATE_OLLAMA_EXE")
    if configured:
        return (Path(configured),)
    candidates = [legacy / "ollama.exe"]
    on_path = shutil.which("ollama")
    if on_path:
        candidates.append(Path(on_path))
    local = os.environ.get("LOCALAPPDATA")
    if local:
        candidates.append(Path(local) / "Programs" / "Ollama" / "ollama.exe")
    installed = os.environ.get("ProgramFiles")
    if installed:
        candidates.append(Path(installed) / "Ollama" / "ollama.exe")
    return tuple(candidates)


def _assign_process_job(process):
    """Keep the inference server and any runners in a job owned by this process."""
    if os.name != "nt":
        return None
    import ctypes
    from ctypes import wintypes

    class BasicLimits(ctypes.Structure):
        _fields_ = [
            ("process_time", ctypes.c_int64), ("job_time", ctypes.c_int64),
            ("flags", wintypes.DWORD), ("min_working_set", ctypes.c_size_t),
            ("max_working_set", ctypes.c_size_t), ("active_processes", wintypes.DWORD),
            ("affinity", ctypes.c_size_t), ("priority", wintypes.DWORD), ("scheduling", wintypes.DWORD),
        ]

    class IoCounters(ctypes.Structure):
        _fields_ = [(name, ctypes.c_uint64) for name in (
            "read_operations", "write_operations", "other_operations",
            "read_bytes", "write_bytes", "other_bytes",
        )]

    class ExtendedLimits(ctypes.Structure):
        _fields_ = [
            ("basic", BasicLimits), ("io", IoCounters),
            ("process_memory", ctypes.c_size_t), ("job_memory", ctypes.c_size_t),
            ("peak_process_memory", ctypes.c_size_t), ("peak_job_memory", ctypes.c_size_t),
        ]

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
    kernel.CreateJobObjectW.restype = wintypes.HANDLE
    kernel.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
    kernel.SetInformationJobObject.restype = wintypes.BOOL
    kernel.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
    kernel.AssignProcessToJobObject.restype = wintypes.BOOL
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL
    handle = kernel.CreateJobObjectW(None, None)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    limits = ExtendedLimits()
    limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    if not kernel.SetInformationJobObject(handle, 9, ctypes.byref(limits), ctypes.sizeof(limits)):
        error = ctypes.get_last_error()
        kernel.CloseHandle(handle)
        raise ctypes.WinError(error)
    if not kernel.AssignProcessToJobObject(handle, process._handle):
        error = ctypes.get_last_error()
        kernel.CloseHandle(handle)
        raise ctypes.WinError(error)
    return kernel, handle


class TranslationEngine:
    def __init__(self, root: str):
        self.root = Path(root).resolve()
        self.runtime = self.root / "runtime" / "llama" / "llama-server.exe"
        self._legacy_ollama = Path(self.root.anchor) / "Ollama"
        self._ollama_exes = _ollama_executables(self._legacy_ollama)
        self.chat_runtime = self.root / "runtime" / "ollama-chat" / "ollama.exe"
        configured_store = os.environ.get("OLLAMA_MODELS")
        self._ollama_stores = (Path(configured_store),) if configured_store else (
            Path(self.root.anchor) / ".ollama" / "models", Path.home() / ".ollama" / "models",
        )
        self.models, self.model_catalog_error = load_models(self.root, LANGUAGES)
        self.model_manager = ModelManager(self.root, self.models, self._stop_server)
        self.temp = self.root / "temp"
        self.temp.mkdir(parents=True, exist_ok=True)
        self._log_path = self.temp / "llama-server.log"
        self._operation_lock = threading.Lock()
        self._process_lock = threading.Lock()
        self._idle_condition = threading.Condition()
        self._active_requests = 0
        self._idle_since = None
        self._idle_timeout_seconds = 5 * 60
        self._idle_thread = None
        self._closed = threading.Event()
        self._process = None
        self._job = None
        self._model_id = None
        self._local_model = None
        self._port = None
        self._opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def _ollama_exe(self):
        return next((path for path in self._ollama_exes if path.is_absolute() and path.is_file()), None)

    def _chat_ollama_exe(self):
        bundled_runner = self.chat_runtime.parent / "lib" / "ollama" / "llama-server.exe"
        if self.chat_runtime.is_file() and bundled_runner.is_file():
            return self.chat_runtime
        return self._ollama_exe()

    def _ollama_model(self, model_id):
        name, tags = _MODEL_TAGS[model_id]
        for store in dict.fromkeys(self._ollama_stores):
            if not store.is_absolute():
                continue
            for tag in tags:
                manifest = store / "manifests" / "registry.ollama.ai" / "library" / name / tag
                try:
                    data = json.loads(manifest.read_text(encoding="utf-8"))
                    layer = next(item for item in data["layers"] if item.get("mediaType") == "application/vnd.ollama.image.model")
                    digest, size = layer["digest"], layer["size"]
                    if not re.fullmatch(r"sha256:[0-9a-f]{64}", digest) or type(size) is not int or size <= 0:
                        continue
                    path = store / "blobs" / digest.replace(":", "-", 1)
                    if path.is_file() and path.stat().st_size == size:
                        return _LocalModel(path, f"{name}:{tag}", size)
                except (OSError, ValueError, KeyError, StopIteration, TypeError, AttributeError):
                    continue
        return None

    def _model_details(self, model):
        if model.weight:
            status, path = self.model_manager.status(model)
            local = _LocalModel(path) if path is not None else None
            storage = "managed"
            if local is not None and not self.runtime.is_file():
                status.update(reason_code="runtime_missing", reason="缺少 llama.cpp 推理运行时。")
                local = None
        else:
            storage = "ollama"
            local = self._ollama_model(model.id)
            total = local.size if local is not None else _GEMMA_MODEL_BYTES
            status = {
                "status": "installed" if local is not None else "missing",
                "received_bytes": total if local is not None else 0, "total_bytes": total,
                "reason_code": "", "reason": "", "error_code": "", "error": "",
                "can_install": False, "can_import": False, "can_delete": False, "can_cancel": False,
            }
            if self._ollama_exe() is None:
                status.update(reason_code="ollama_missing", reason="缺少 Ollama 推理运行时。")
                local = None
            elif local is None:
                status.update(reason_code="ollama_model_missing", reason="请使用 Ollama 安装 translategemma:4b。")
        return {
            "id": model.id, "name": model.name, "available": local is not None,
            "license": model.license, "source_url": model.source_url, "storage": storage, **status,
        }, local

    def _available_model(self, model_id):
        if model_id == _CHAT_MODEL_ID:
            if self._chat_ollama_exe() is None:
                return None, "缺少 Qwen3.5 对话推理运行时"
            model = self._ollama_model(model_id)
            return model, "" if model is not None else f"未找到完整的 Ollama {_CHAT_MODEL_NAME} 模型"
        details, local = self._model_details(self.models[model_id])
        return local, details["reason"]

    def list_models(self):
        models = []
        for model_id in self.models:
            details = self.model_status(model_id)
            keys = ("id", "name", "available", "reason", "reason_code")
            models.append({**{key: details[key] for key in keys}, **model_language_metadata(model_id, models=self.models)})
        return models

    def model_status(self, model_id):
        return self._model_details(self.models[model_id])[0]

    def list_chat_models(self):
        path, reason = self._available_model(_CHAT_MODEL_ID)
        return [{"id": _CHAT_MODEL_ID, "name": _CHAT_MODEL_NAME, "available": path is not None, "reason": reason}]

    def _check_cancel(self, cancel):
        if cancel is not None and cancel.is_set():
            raise RuntimeError("请求已取消")
        if self._closed.is_set():
            raise RuntimeError("本地服务已关闭")

    def _unload_when_idle(self):
        with self._idle_condition:
            while not self._closed.is_set():
                if self._active_requests or self._idle_since is None:
                    self._idle_condition.wait()
                    continue
                remaining = self._idle_timeout_seconds - (time.monotonic() - self._idle_since)
                if remaining > 0:
                    self._idle_condition.wait(remaining)
                    continue
                self._idle_since = None
                self._stop_server()

    def _stop_server(self, model_id=None):
        with self._process_lock:
            if model_id is not None and self._model_id != model_id:
                return
            process = self._process
            job = self._job
            self._process = None
            self._job = None
            self._model_id = None
            self._local_model = None
            self._port = None
            if job is not None:
                kernel, handle = job
                kernel.CloseHandle(handle)
            if process is not None and process.poll() is None:
                try:
                    if job is None:
                        process.terminate()
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
                except OSError:
                    pass

    def _error_log(self):
        try:
            with self._log_path.open("rb") as log:
                log.seek(0, os.SEEK_END)
                log.seek(max(0, log.tell() - 4000))
                return log.read().decode("utf-8", errors="replace").strip()
        except OSError:
            return ""

    def _start_server(self, model_id, local_model, cancel):
        self._check_cancel(cancel)
        self._stop_server()
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]

        environment = os.environ.copy()
        for key in ("TEMP", "TMP", "TMPDIR", "HF_HOME", "CUDA_CACHE_PATH"):
            environment[key] = str(self.temp / "cuda-cache") if key == "CUDA_CACHE_PATH" else str(self.temp)
        if local_model.ollama_ref:
            is_chat = model_id == _CHAT_MODEL_ID
            ollama_home = self.temp / ("ollama-chat-home" if is_chat else "ollama-home")
            ollama_home.mkdir(parents=True, exist_ok=True)
            environment.update({
                "OLLAMA_HOST": f"127.0.0.1:{port}",
                "OLLAMA_MODELS": str(local_model.path.parent.parent),
                "OLLAMA_NOPRUNE": "1", "OLLAMA_NO_CLOUD": "1", "OLLAMA_KEEP_ALIVE": "-1",
                "USERPROFILE": str(ollama_home), "LOCALAPPDATA": str(ollama_home),
                "APPDATA": str(ollama_home), "HOME": str(ollama_home),
            })
            if is_chat:
                environment.update({
                    "OLLAMA_CONTEXT_LENGTH": str(_CHAT_CONTEXT),
                    "OLLAMA_NUM_PARALLEL": "1", "OLLAMA_MAX_LOADED_MODELS": "1",
                    "OLLAMA_FLASH_ATTENTION": "1", "OLLAMA_GPU_OVERHEAD": str(384 * 1024**2),
                    "OLLAMA_DEBUG": "0", "OLLAMA_DEBUG_LOG_REQUESTS": "0",
                    "OLLAMA_LOG_FILE": "",
                })
            runtime = self._chat_ollama_exe() if is_chat else self._ollama_exe()
            if runtime is None:
                raise RuntimeError("缺少 Ollama 推理运行时。")
            libs = [runtime.parent / "lib" / "ollama"]
            if is_chat and runtime == self.chat_runtime:
                libs.append(self._legacy_ollama / "lib" / "ollama")
            engine_paths = [str(path) for lib in dict.fromkeys(libs) if lib.is_dir() for path in (lib / "cuda_v12", lib)]
            if engine_paths:
                environment["PATH"] = os.pathsep.join([*engine_paths, environment.get("PATH", "")])
            args = [str(runtime), "serve"]
            health_endpoint = "/api/version"
        else:
            args = [
                str(self.runtime), "--model", str(local_model.path), "--alias", "local-translation",
                "--host", "127.0.0.1", "--port", str(port), "--ctx-size", "8192",
                "--parallel", "1", "--jinja", "--no-webui",
            ]
            if (self.runtime.parent / "ggml-cuda.dll").is_file():
                args.extend(("--fit", "on", "--fit-target", "512"))
                ollama_exe = self._ollama_exe()
                if ollama_exe is not None:
                    ollama_lib = ollama_exe.parent / "lib" / "ollama"
                    environment["PATH"] = os.pathsep.join((str(ollama_lib / "cuda_v12"), str(ollama_lib), environment.get("PATH", "")))
            else:
                args.extend(("--n-gpu-layers", "0"))
            health_endpoint = "/health"
        flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        with self._process_lock:
            self._check_cancel(cancel)
            try:
                with self._log_path.open("wb") as log:
                    process = subprocess.Popen(args, stdout=log, stderr=subprocess.STDOUT, env=environment, creationflags=flags)
                try:
                    job = _assign_process_job(process)
                except OSError:
                    process.terminate()
                    process.wait(timeout=5)
                    raise
            except OSError as error:
                raise RuntimeError(f"无法启动本地推理服务：{error}") from error
            self._process = process
            self._job = job
            self._model_id = model_id
            self._local_model = local_model
            self._port = port

        deadline = time.monotonic() + 240
        while time.monotonic() < deadline:
            self._check_cancel(cancel)
            if process.poll() is not None:
                details = self._error_log()
                raise RuntimeError(f"模型加载失败（退出码 {process.returncode}）{': ' + details if details else ''}")
            try:
                with self._opener.open(f"http://127.0.0.1:{port}{health_endpoint}", timeout=1) as response:
                    status = json.load(response)
                if status.get("status") == "ok" or status.get("version"):
                    return
            except (OSError, ValueError, urllib.error.URLError):
                pass
            time.sleep(0.15)
        raise RuntimeError("模型加载超时，请检查可用内存和显存")

    def _ensure_server(self, model_id, path, cancel):
        with self._process_lock:
            ready = (
                self._model_id == model_id and self._local_model == path
                and self._process is not None and self._process.poll() is None
            )
        if not ready:
            self._start_server(model_id, path, cancel)

    def _prompt(self, text, model_id, source, target):
        has_markers = bool(PLACEHOLDER.search(text))
        source, target = validate_languages(model_id, source, target, models=self.models)
        source, target = resolve_languages(text, source, target, model_id, models=self.models)
        adapter = self.models[model_id].prompt_adapter
        if adapter == "translategemma":
            source_name, target_name = LANGUAGES[source].english, LANGUAGES[target].english
            source_code = GEMMA_PROMPT_CODES.get(source, source)
            target_code = GEMMA_PROMPT_CODES.get(target, target)
            preserve = "Preserve every placeholder marker in the source text exactly as written and in its original order. " if has_markers else ""
            return (
                f"You are a professional {source_name} ({source_code}) to {target_name} ({target_code}) translator. "
                f"Your goal is to accurately convey the meaning and nuances of the original {source_name} text "
                f"while adhering to {target_name} grammar, vocabulary, and cultural sensitivities.\n"
                f"Produce only the {target_name} translation, without any additional explanations or commentary. "
                f"{preserve}Please translate the following {source_name} text into {target_name}:\n\n\n{text}"
            )
        target_name = LANGUAGES[target].name
        source_name = LANGUAGES[source].name if source != "auto" else ""
        if source == "zh":
            source_name = "中文"
        if adapter == "index":
            preserve = "请按原有顺序原样保留文中的全部占位标记。" if has_markers else ""
            return f"请将以下{source_name}文本翻译为{target_name}，直接输出翻译结果，不要进行任何解释。{preserve}\n\n{text}"
        if adapter == "hymt" and (source in ("zh", "zh-Hant", "yue") or target in ("zh-Hant", "yue", "bo", "kk", "mn", "ug")):
            preserve = "。请按原有顺序原样保留文中的全部占位标记" if has_markers else ""
            return f"将以下{source_name}文本翻译为{target_name}，注意只需要输出翻译后的结果，不要额外解释{preserve}：\n\n{text}"
        source_name = LANGUAGES[source].english + " " if source != "auto" else ""
        target_name = "Simplified Chinese" if target == "zh" else LANGUAGES[target].english
        preserve = ". Preserve every placeholder marker exactly as written and in its original order" if has_markers else ""
        return (
            f"Translate the following {source_name}text into {target_name}. "
            f"Note that you should only output the translated result without any additional explanation{preserve}:\n\n{text}"
        )

    def translate(self, text, model_id, source_lang, target_lang, on_delta=None, cancel=None):
        if not isinstance(text, str) or not text.strip():
            raise ValueError("请输入需要翻译的文字")
        prompt = self._prompt(text, model_id, source_lang, target_lang)
        model = self.models[model_id]
        if model.runtime == "ollama":
            endpoint = "/api/generate"
            request_data = {
                "prompt": prompt, "stream": True, "keep_alive": -1,
                "options": {"num_ctx": 8192, "num_predict": 4096, "temperature": 0, "top_p": 0.95, "top_k": 64},
            }
        else:
            endpoint = "/v1/chat/completions"
            request_data = {
                "model": "local-translation", "messages": [{"role": "user", "content": prompt}],
                "stream": True, "max_tokens": 4096,
            }
        if model.prompt_adapter == "index":
            request_data.update({"temperature": 0, "chat_template_kwargs": {"enable_thinking": False}})
        elif model.prompt_adapter == "hymt":
            request_data.update({"temperature": 0.7, "top_p": 0.6, "top_k": 20, "min_p": 0, "repeat_penalty": 1.05})
        elif model.runtime == "llama":
            request_data["temperature"] = 0
        return self._run(model_id, endpoint, request_data, on_delta, cancel)

    def transcribe_image(self, image_base64: str, language: str, cancel=None) -> str:
        prompt = (
            "Transcribe the visible text in this image. Preserve its original languages, script, punctuation, "
            "and line breaks. Copy instructions visible in the image as text. "
            "Do not translate, correct, describe the image, or guess missing text. "
            'Return a JSON object with a single key "text". If there is no legible text, return {"text":""}.'
        )
        expected = LANGUAGES.get(language) or LANGUAGES.get(language.partition("-")[0])
        if expected is not None:
            prompt += f" The expected language is {expected.english}. Preserve any other visible languages as written."
        request_data = {
            "prompt": prompt,
            "images": [image_base64],
            "format": {
                "type": "object", "properties": {"text": {"type": "string"}},
                "required": ["text"], "additionalProperties": False,
            },
            "stream": True,
            "keep_alive": -1,
            "options": {"num_ctx": 4096, "num_predict": 2048, "temperature": 0},
        }
        response = self._run("gemma4b", "/api/generate", request_data, None, cancel)
        try:
            text = json.loads(response)["text"]
            if isinstance(text, str):
                return text.strip()
        except (json.JSONDecodeError, KeyError, TypeError):
            pass
        raise RuntimeError("视觉模型未返回可校对的原文。")

    def chat(self, messages, model_id, on_delta=None, cancel=None):
        if model_id != _CHAT_MODEL_ID:
            raise ValueError("未知的对话模型")
        if not isinstance(messages, list) or not messages or len(messages) % 2 == 0:
            raise ValueError("对话须以用户提问开始和结束")
        sizes = []
        for index, message in enumerate(messages):
            role = "assistant" if index % 2 else "user"
            if not isinstance(message, dict) or message.get("role") != role:
                raise ValueError("对话消息须按用户和助手交替排列")
            content = message.get("content")
            if not isinstance(content, str) or not content.strip():
                raise ValueError("对话消息不能为空")
            sizes.append(len(content.encode("utf-8")) + 16)

        # Qwen's byte-based tokenizer uses at most one token per UTF-8 byte.
        # Reserve space for the answer, role delimiters and the system prompt.
        remaining = _CHAT_CONTEXT - _CHAT_OUTPUT - len(_CHAT_INSTRUCTIONS.encode("utf-8")) - 64
        if sizes[-1] > remaining:
            raise ValueError("本次提问过长，请缩短后重试")
        remaining -= sizes[-1]
        first = len(messages) - 1
        while first > 0:
            turn_size = sizes[first - 2] + sizes[first - 1]
            if turn_size > remaining:
                break
            remaining -= turn_size
            first -= 2

        history = [{"role": "system", "content": _CHAT_INSTRUCTIONS}]
        history.extend({"role": item["role"], "content": item["content"]} for item in messages[first:])
        request_data = {
            "messages": history, "stream": True, "think": False,
            "keep_alive": -1, "options": {
                "num_ctx": _CHAT_CONTEXT, "num_predict": _CHAT_OUTPUT, "temperature": 0.25, "num_batch": 256,
            },
        }
        return self._run(model_id, "/api/chat", request_data, on_delta, cancel)

    def _run(self, model_id, endpoint, request_data, on_delta, cancel):
        lease = self.model_manager.use(model_id) if model_id in self.models else nullcontext()
        with lease:
            return self._run_request(model_id, endpoint, request_data, on_delta, cancel)

    def _run_request(self, model_id, endpoint, request_data, on_delta, cancel):
        with self._idle_condition:
            self._check_cancel(cancel)
            self._active_requests += 1
            self._idle_since = None
            self._idle_condition.notify()
        try:
            return self._generate(model_id, endpoint, request_data, on_delta, cancel)
        finally:
            with self._idle_condition:
                self._active_requests -= 1
                if self._active_requests == 0 and not self._closed.is_set():
                    with self._process_lock:
                        has_process = self._process is not None
                    if has_process:
                        self._idle_since = time.monotonic()
                        if self._idle_thread is None:
                            self._idle_thread = threading.Thread(target=self._unload_when_idle, daemon=True)
                            self._idle_thread.start()
                self._idle_condition.notify()

    def _generate(self, model_id, endpoint, request_data, on_delta, cancel):
        self._check_cancel(cancel)
        while not self._operation_lock.acquire(timeout=0.15):
            self._check_cancel(cancel)
        finished = threading.Event()
        watcher = None
        generation_started = False
        try:
            self._check_cancel(cancel)
            local, reason = self._available_model(model_id)
            if local is None:
                raise RuntimeError(reason)
            generation_started = True
            if cancel is not None:
                def watch_cancellation():
                    while not finished.wait(0.1):
                        if cancel.is_set():
                            self._stop_server()
                            return
                watcher = threading.Thread(target=watch_cancellation, daemon=True)
                watcher.start()
            self._ensure_server(model_id, local, cancel)
            self._check_cancel(cancel)
            with self._process_lock:
                port = self._port
            self._check_cancel(cancel)
            is_ollama = bool(local.ollama_ref)
            if is_ollama:
                request_data = {**request_data, "model": local.ollama_ref}
            request = urllib.request.Request(
                f"http://127.0.0.1:{port}{endpoint}",
                data=json.dumps(request_data, ensure_ascii=False).encode("utf-8"),
                headers={"Content-Type": "application/json"},
            )
            output = []
            ended = False
            is_chat = model_id == _CHAT_MODEL_ID
            task = "对话" if is_chat else "翻译"
            with self._opener.open(request, timeout=600) as response:
                for raw_line in response:
                    self._check_cancel(cancel)
                    line = raw_line.decode("utf-8").strip()
                    if is_ollama:
                        if not line:
                            continue
                        payload = line
                    else:
                        if not line.startswith("data:"):
                            continue
                        payload = line[5:].strip()
                        if payload == "[DONE]":
                            ended = True
                            break
                    data = json.loads(payload)
                    if data.get("error"):
                        error = data["error"]
                        raise RuntimeError(f"推理失败：{error.get('message', error) if isinstance(error, dict) else error}")
                    if is_ollama:
                        content = data.get("message", {}).get("content") if is_chat else data.get("response")
                        choices = [{"delta": {"content": content}, "finish_reason": data.get("done_reason")}]
                        ended = bool(data.get("done"))
                    else:
                        choices = data.get("choices", [])
                    for choice in choices:
                        if choice.get("finish_reason") == "length":
                            raise RuntimeError(f"{task}达到模型的长度限制，请缩短输入后重试")
                        delta = choice.get("delta", {}).get("content")
                        if delta:
                            output.append(delta)
                            if on_delta is not None:
                                on_delta(delta)
                    if ended:
                        break
            self._check_cancel(cancel)
            if not ended:
                raise RuntimeError(f"推理服务提前断开，{task}未完成")
            if not output:
                raise RuntimeError(f"模型没有返回{task}结果")
            return "".join(output)
        except urllib.error.HTTPError as error:
            self._stop_server()
            self._check_cancel(cancel)
            detail = error.read().decode("utf-8", errors="replace")
            try:
                parsed = json.loads(detail)
                detail = parsed.get("error", {}).get("message", detail)
            except (ValueError, AttributeError):
                pass
            raise RuntimeError(f"本地推理失败：{detail}") from error
        except Exception:
            if generation_started:
                self._stop_server()
            self._check_cancel(cancel)
            raise
        finally:
            finished.set()
            if watcher is not None:
                watcher.join(timeout=6)
            self._operation_lock.release()

    def shutdown(self):
        self.model_manager.shutdown()
        self._closed.set()
        with self._idle_condition:
            self._idle_since = None
            self._idle_condition.notify_all()
        self._stop_server()
