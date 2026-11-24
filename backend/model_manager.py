"""Install and verify app-owned GGUF models without blocking the HTTP server."""

from __future__ import annotations

import hashlib
import http.client
import logging
import os
import shutil
import stat
import threading
import urllib.error
import urllib.request
from collections import Counter
from contextlib import contextmanager
from dataclasses import dataclass, field
from pathlib import Path
from urllib.parse import urlsplit

from model_catalog import DEFAULT_MODEL_ID, valid_download_url


_CHUNK_SIZE = 1024 * 1024
_NETWORK_TIMEOUT = 15
_AUTO_MARKER = ".index-auto-install-attempted"
_WEIGHT_ERRORS = frozenset({"hash_mismatch", "size_mismatch", "invalid_gguf"})
MESSAGES = {
    "missing": "模型文件尚未安装。",
    "verifying": "正在校验模型文件。",
    "downloading": "正在下载模型。",
    "importing": "正在导入模型。",
    "cancelling": "正在取消模型安装。",
    "deleting": "正在删除模型。",
    "cancelled": "模型安装已取消。",
    "busy": "模型正在处理另一项操作，请稍后重试。",
    "model_in_use": "模型正在翻译，请在任务结束后重试。",
    "model_not_found": "未找到此翻译模型。",
    "shared_model": "此模型由外部 Ollama 管理。",
    "download_unavailable": "此模型没有内置下载链接，可导入本地 GGUF。",
    "hash_mismatch": "模型的 SHA-256 校验失败。",
    "size_mismatch": "权重文件大小与清单不符。",
    "invalid_gguf": "文件缺少有效的 GGUF 标记。",
    "invalid_import_path": "请选择本机磁盘上的普通 GGUF 文件。",
    "unsafe_storage": "模型目录和权重须位于应用目录中的普通文件路径。",
    "storage_error": "无法读写模型存储，请检查应用目录的权限及可用空间。",
    "insufficient_space": "模型目录所在磁盘的剩余空间不足。",
    "network_error": "无法下载模型，请检查网络，稍后重试或导入本地 GGUF。",
    "file_changed": "模型文件在校验时被更改，请重试。",
}


class ModelManagerError(Exception):
    def __init__(self, code, status=409):
        super().__init__(MESSAGES[code])
        self.code = code
        self.status = status


class _Cancelled(Exception):
    pass


class _ModelDownloadRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, new_url):
        parts = urlsplit(new_url)
        host = parts.hostname or ""
        if (
            parts.scheme != "https" or parts.port not in (None, 443) or parts.username or parts.password
            or not (
                host == "huggingface.co" or host.endswith(".cdn.hf.co") or host.endswith(".xethub.hf.co")
                or host == "cdn-lfs.huggingface.co" or host.startswith("cdn-lfs-") and host.endswith(".hf.co")
            )
        ):
            raise ModelManagerError("network_error")
        return super().redirect_request(request, fp, code, msg, headers, new_url)


@dataclass
class _Operation:
    status: str
    cancelable: bool
    running: bool = True
    received: int = 0
    error: str = ""
    cancel: threading.Event = field(default_factory=threading.Event)

    def check_cancel(self):
        if self.cancel.is_set():
            raise _Cancelled


def _is_reparse(info):
    return stat.S_ISLNK(info.st_mode) or bool(
        getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT
    )


def _regular_info(path):
    try:
        info = path.lstat()
    except FileNotFoundError:
        return None
    if _is_reparse(info) or not stat.S_ISREG(info.st_mode):
        raise ModelManagerError("unsafe_storage")
    return info


def _file_key(info):
    return (info.st_dev, info.st_ino, info.st_size)


def _identity(info):
    return (*_file_key(info), info.st_mtime_ns, info.st_ctime_ns)


def _partial_path(path):
    return path.with_name(path.name + ".part")


def _import_path(value):
    if not isinstance(value, str) or not value or "\0" in value:
        raise ModelManagerError("invalid_import_path", 400)
    path = Path(value)
    if (
        not path.is_absolute() or path.suffix.lower() != ".gguf" or ".." in path.parts
        or value.replace("/", "\\").startswith("\\\\")
    ):
        raise ModelManagerError("invalid_import_path", 400)
    if os.name == "nt":
        import ctypes
        import ntpath

        if ntpath.isreserved(value) or ctypes.windll.kernel32.GetDriveTypeW(path.anchor) == 4:
            raise ModelManagerError("invalid_import_path", 400)
    return path


class ModelManager:
    def __init__(self, root, models, stop_loaded):
        self.directory = root / "models"
        self.models = models
        self._stop_loaded = stop_loaded
        self._lock = threading.RLock()
        self._started = False
        self._closed = False
        self._operations = {}
        self._users = Counter()
        self._verified = {}
        self._rejected = {}
        self._opener = urllib.request.build_opener(_ModelDownloadRedirect())

    def _check_directory(self, create=False):
        if create:
            self.directory.mkdir(exist_ok=True)
        try:
            info = self.directory.lstat()
        except FileNotFoundError:
            return
        if _is_reparse(info) or not stat.S_ISDIR(info.st_mode):
            raise ModelManagerError("unsafe_storage")

    def _mark_default_attempt(self):
        self._check_directory(create=True)
        marker = self.directory / _AUTO_MARKER
        try:
            with marker.open("xb") as stream:
                stream.write(b"1\n")
                stream.flush()
                os.fsync(stream.fileno())
            return True
        except FileExistsError:
            _regular_info(marker)
            return False

    def start(self):
        """Record the startup attempt before starting the default download worker."""
        with self._lock:
            if self._started or self._closed:
                return
            self._started = True
            model = self.models[DEFAULT_MODEL_ID]
            try:
                fresh = self._mark_default_attempt()
                path = self.directory / model.weight.filename
                if fresh and _regular_info(path) is None:
                    self._begin(model, "downloading", self._install, True)
            except (ModelManagerError, OSError) as error:
                code = error.code if isinstance(error, ModelManagerError) else "storage_error"
                self._operations[model.id] = _Operation("failed", False, running=False, error=code)

    def _owned_model(self, model_id):
        model = self.models.get(model_id)
        if model is None:
            raise ModelManagerError("model_not_found", 404)
        if model.weight is None:
            raise ModelManagerError("shared_model", 403)
        return model

    def _check_idle_operation(self, model_id):
        operation = self._operations.get(model_id)
        if self._closed or (operation is not None and operation.running):
            raise ModelManagerError("busy")

    def _check_unused(self, model_id):
        if self._users[model_id]:
            raise ModelManagerError("model_in_use")

    @contextmanager
    def use(self, model_id):
        with self._lock:
            operation = self._operations.get(model_id)
            if operation is not None and operation.running:
                raise ModelManagerError(operation.status)
            self._users[model_id] += 1
        try:
            yield
        finally:
            with self._lock:
                self._users[model_id] -= 1

    def _begin(self, model, status, action, cancelable, *args):
        operation = _Operation(status, cancelable)
        self._operations[model.id] = operation
        threading.Thread(
            target=self._run, args=(model, operation, action, args), daemon=True,
            name=f"model-{status}-{model.id}",
        ).start()
        return operation

    def _run(self, model, operation, action, args):
        try:
            final_status = action(model, operation, *args)
            error = ""
        except _Cancelled:
            final_status, error = "cancelled", ""
        except ModelManagerError as exc:
            final_status, error = "failed", exc.code
        except OSError:
            final_status, error = "failed", "storage_error"
        except Exception:
            logging.getLogger(__name__).exception("Model operation failed for %s", model.id)
            final_status, error = "failed", "storage_error"
        with self._lock:
            operation.status = final_status
            operation.running = False
            operation.error = error

    def install(self, model_id, source_path=None):
        model = self._owned_model(model_id)
        source = _import_path(source_path) if source_path is not None else None
        if source is None and not model.weight.download_url:
            raise ModelManagerError("download_unavailable", 400)
        with self._lock:
            self._check_idle_operation(model_id)
            self._check_directory(create=True)
            path = self.directory / model.weight.filename
            info = _regular_info(path)
            if info is not None and self._verified.get(model_id) == _identity(info):
                return
            self._check_unused(model_id)
            if model_id == DEFAULT_MODEL_ID:
                self._mark_default_attempt()
            status = "importing" if source is not None else "downloading"
            self._begin(model, "verifying" if info is not None else status, self._install, True, source)

    def cancel(self, model_id):
        self._owned_model(model_id)
        with self._lock:
            operation = self._operations.get(model_id)
            if operation is not None and operation.running and operation.cancelable:
                operation.status = "cancelling"
                operation.cancel.set()

    def delete(self, model_id):
        model = self._owned_model(model_id)
        with self._lock:
            self._check_idle_operation(model_id)
            self._check_unused(model_id)
            self._check_directory()
            if model_id == DEFAULT_MODEL_ID:
                self._mark_default_attempt()
            self._begin(model, "deleting", self._delete, False)

    def _delete(self, model, operation):
        path = self.directory / model.weight.filename
        stage = _partial_path(path)
        self._stop_loaded(model.id)
        self._check_directory()
        for candidate in (stage, path):
            if _regular_info(candidate) is not None:
                candidate.unlink(missing_ok=True)
        with self._lock:
            self._verified.pop(model.id, None)
            self._rejected.pop(model.id, None)
        return "missing"

    def _progress(self, operation, received):
        with self._lock:
            operation.check_cancel()
            operation.received = received

    def _check_stream(self, read, model, operation, output=None):
        weight = model.weight
        digest = hashlib.sha256()
        received = 0
        magic = bytearray()
        while True:
            operation.check_cancel()
            data = read(_CHUNK_SIZE)
            if not data:
                break
            received += len(data)
            if received > weight.size:
                raise ModelManagerError("size_mismatch")
            if len(magic) < 4:
                magic.extend(data[:4 - len(magic)])
                if len(magic) == 4 and magic != b"GGUF":
                    raise ModelManagerError("invalid_gguf")
            digest.update(data)
            if output is not None:
                output.write(data)
            self._progress(operation, received)
        if received != weight.size:
            raise ModelManagerError("size_mismatch")
        if digest.hexdigest() != weight.sha256:
            raise ModelManagerError("hash_mismatch")

    def _verify(self, model, operation, path, info):
        identity = _identity(info)
        try:
            if info.st_size != model.weight.size:
                raise ModelManagerError("size_mismatch")
            with path.open("rb") as stream:
                if _file_key(os.fstat(stream.fileno())) != _file_key(info):
                    raise ModelManagerError("file_changed")
                self._check_stream(stream.read, model, operation)
            checked = _regular_info(path)
            if checked is None or _identity(checked) != identity:
                raise ModelManagerError("file_changed")
        except (ModelManagerError, OSError) as error:
            code = error.code if isinstance(error, ModelManagerError) else "storage_error"
            if code in _WEIGHT_ERRORS or code == "storage_error":
                with self._lock:
                    self._rejected[model.id] = (identity, code)
            raise
        with self._lock:
            operation.check_cancel()
            self._verified[model.id] = identity
            self._rejected.pop(model.id, None)
            operation.cancelable = False
        return "installed"

    def _download(self, model, operation, target):
        url = model.weight.download_url
        if not valid_download_url(url):
            raise ModelManagerError("download_unavailable")
        request = urllib.request.Request(url, headers={"User-Agent": "LocalTranslate/1", "Accept-Encoding": "identity"})
        try:
            response = self._opener.open(request, timeout=_NETWORK_TIMEOUT)
        except (OSError, ValueError, http.client.HTTPException) as error:
            raise ModelManagerError("network_error") from error
        with response:
            def read(size):
                try:
                    return response.read1(size)
                except (OSError, http.client.HTTPException) as error:
                    raise ModelManagerError("network_error") from error

            if response.status != 200:
                raise ModelManagerError("network_error")
            self._check_stream(read, model, operation, target)

    def _copy(self, source, model, operation, target):
        try:
            for parent in source.parents:
                if _is_reparse(parent.lstat()):
                    raise ModelManagerError("invalid_import_path", 400)
            info = _regular_info(source)
            if info is None:
                raise ModelManagerError("invalid_import_path", 400)
            stream = source.open("rb")
        except (OSError, ModelManagerError) as error:
            raise ModelManagerError("invalid_import_path", 400) from error
        with stream:
            if _file_key(os.fstat(stream.fileno())) != _file_key(info):
                raise ModelManagerError("file_changed")
            if info.st_size != model.weight.size:
                raise ModelManagerError("size_mismatch")
            self._check_stream(stream.read, model, operation, target)

    def _install(self, model, operation, source=None):
        self._check_directory(create=True)
        path = self.directory / model.weight.filename
        info = _regular_info(path)
        if info is not None:
            try:
                return self._verify(model, operation, path, info)
            except ModelManagerError as error:
                if error.code not in _WEIGHT_ERRORS:
                    raise
        with self._lock:
            operation.check_cancel()
            operation.received = 0
            operation.status = "importing" if source is not None else "downloading"
        stage = _partial_path(path)
        if _regular_info(stage) is not None:
            stage.unlink(missing_ok=True)
        if shutil.disk_usage(self.directory).free < model.weight.size:
            raise ModelManagerError("insufficient_space")
        owns_stage = False
        try:
            with stage.open("xb") as target:
                owns_stage = True
                if source is None:
                    self._download(model, operation, target)
                else:
                    self._copy(source, model, operation, target)
                target.flush()
                os.fsync(target.fileno())
                file_key = _file_key(os.fstat(target.fileno()))
            checked = _regular_info(stage)
            if checked is None or _file_key(checked) != file_key:
                raise ModelManagerError("file_changed")
            identity = _identity(checked)
            with self._lock:
                operation.check_cancel()
                self._check_unused(model.id)
                self._stop_loaded(model.id)
                self._check_directory()
                staged = _regular_info(stage)
                if staged is None or _identity(staged) != identity:
                    raise ModelManagerError("file_changed")
                existing = _regular_info(path)
                if (info is None) != (existing is None) or (info is not None and _identity(info) != _identity(existing)):
                    raise ModelManagerError("file_changed")
                stage.replace(path)
                self._verified[model.id] = _identity(path.stat())
                self._rejected.pop(model.id, None)
                operation.cancelable = False
                owns_stage = False
        finally:
            if owns_stage:
                try:
                    self._check_directory()
                    if _regular_info(stage) is not None:
                        stage.unlink(missing_ok=True)
                except (OSError, ModelManagerError):
                    pass
        return "installed"

    def status(self, model):
        """Return the current disk and operation state, starting lazy verification if needed."""
        weight = model.weight
        path = self.directory / weight.filename
        with self._lock:
            operation = self._operations.get(model.id)
            if operation is not None and operation.running:
                return self._pending_status(weight.size, operation), None
            try:
                self._check_directory()
                info = _regular_info(path)
                stage = _regular_info(_partial_path(path))
            except (ModelManagerError, OSError) as error:
                code = error.code if isinstance(error, ModelManagerError) else "storage_error"
                return self._status("failed", 0, weight.size, code, code), None
            current_error = operation.error if operation else ""
            can_import = not self._closed
            can_install = can_import and bool(weight.download_url)
            can_delete = can_import and (info is not None or stage is not None)
            if info is not None:
                identity = _identity(info)
                if self._verified.get(model.id) == identity:
                    return self._status("installed", weight.size, weight.size, "", current_error, can_delete=can_delete), path
                rejection = self._rejected.get(model.id)
                if info.st_size != weight.size:
                    code = "size_mismatch"
                elif rejection is not None and rejection[0] == identity:
                    code = rejection[1]
                elif not self._closed:
                    operation = self._begin(model, "verifying", self._verify, False, path, info)
                    return self._pending_status(weight.size, operation), None
                else:
                    code = "missing"
                status, reason = "failed", code
                current_error = current_error or code
            elif operation is not None and operation.status in ("failed", "cancelled"):
                status, reason = operation.status, current_error or operation.status
            else:
                status = reason = "missing"
            return self._status(
                status, 0, weight.size, reason, current_error,
                can_install=can_install, can_import=can_import, can_delete=can_delete,
            ), None

    def _pending_status(self, size, operation):
        return self._status(
            operation.status, operation.received, size, operation.status, "",
            can_cancel=operation.cancelable and not operation.cancel.is_set(),
        )

    @staticmethod
    def _status(status, received, size, reason, error, **actions):
        return {
            "status": status, "received_bytes": received, "total_bytes": size,
            "reason_code": reason, "reason": MESSAGES.get(reason, ""),
            "error_code": error, "error": MESSAGES.get(error, ""),
            "can_install": False, "can_import": False, "can_delete": False, "can_cancel": False,
            **actions,
        }

    def shutdown(self):
        with self._lock:
            self._closed = True
            for operation in self._operations.values():
                if operation.cancelable:
                    operation.cancel.set()
