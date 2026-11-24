"""Local HTTP bridge between the desktop UI and the inference services."""

import json
import os
import re
import secrets
import sys
import threading
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(Path(__file__).resolve().parent))
TEMP = ROOT / "temp"
TEMP.mkdir(parents=True, exist_ok=True)
os.environ["TEMP"] = str(TEMP)
os.environ["TMP"] = str(TEMP)
os.environ["PYTHONPYCACHEPREFIX"] = str(TEMP / "pycache")
os.environ["PYTHONNOUSERSITE"] = "1"

from inference import TranslationEngine
from file_jobs import absolute_path, run_batch, validate_output_options
from image_ocr import ImageOcrError, ocr_capabilities, recognize_image
from languages import LANGUAGES, detect_language, validate_languages
from model_catalog import DEFAULT_MODEL_ID
from model_manager import ModelManagerError

engine = TranslationEngine(str(ROOT))
model_manager_token = secrets.token_urlsafe(32)
MODEL_ACTION = re.compile(r"/api/model-manager/([a-z][a-z0-9_-]{0,63})/(install|cancel|delete|import)")
state_lock = threading.RLock()
requests = {}
jobs = {}


def parse_translation(data):
    model_id = data.get("model_id", DEFAULT_MODEL_ID)
    source = data.get("source_lang", "auto")
    target = data.get("target_lang", "auto")
    source, target = validate_languages(model_id, source, target, models=engine.models)
    return model_id, source, target


class AppServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, format, *args):
        # The server never logs source text or file contents.
        return

    def send_json(self, value, status=200):
        payload = json.dumps(value, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        self.wfile.write(payload)

    def body(self, maximum=20 * 1024 * 1024):
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError as exc:
            raise ValueError("请求长度无效。") from exc
        if length <= 0 or length > maximum:
            raise ValueError("请求内容为空或过大。")
        value = json.loads(self.rfile.read(length).decode("utf-8"))
        if not isinstance(value, dict):
            raise ValueError("请求格式无效。")
        return value

    def desktop_model_request(self):
        host = self.headers.get("Host", "").lower()
        port = self.server.server_port
        return host in (f"127.0.0.1:{port}", f"localhost:{port}") and not any(
            name.lower() == "origin" or name.lower().startswith("sec-fetch-") for name in self.headers
        )

    def do_GET(self):
        path = urlsplit(self.path).path
        if path == "/api/model-manager":
            if not self.desktop_model_request():
                return self.send_json({"error": "此功能仅供桌面程序访问。", "code": "desktop_required"}, 403)
            return self.send_json({
                "default_model_id": DEFAULT_MODEL_ID, "token": model_manager_token,
                "catalog_error": engine.model_catalog_error,
                "models": [engine.model_status(model_id) for model_id in engine.models],
            })
        if self.headers.get("Origin"):
            return self.send_json({"error": "此服务仅供桌面程序访问。"}, 403)
        if path == "/api/health":
            return self.send_json({"ready": True, "service": "local-translator-v1", "pid": os.getpid()})
        if path == "/api/ocr/languages":
            try:
                return self.send_json(ocr_capabilities())
            except ImageOcrError as exc:
                return self.send_json({"error": str(exc), "code": exc.code}, 503)
        if path == "/api/models":
            try:
                return self.send_json({"models": engine.list_models()})
            except Exception as exc:
                return self.send_json({"error": str(exc)}, 500)
        if path == "/api/chat/models":
            try:
                return self.send_json({"models": engine.list_chat_models()})
            except Exception as exc:
                return self.send_json({"error": str(exc)}, 500)
        if path.startswith("/api/jobs/"):
            job_id = path[len("/api/jobs/"):]
            with state_lock:
                job = jobs.get(job_id)
                if job is None:
                    snapshot = None
                else:
                    snapshot = {
                        "status": job["status"],
                        "total": job["total"],
                        "completed": job["completed"],
                        "current_file": job["current_file"],
                        "current_done": job["current_done"],
                        "current_total": job["current_total"],
                        "files": [dict(item) for item in job["files"]],
                    }
            if snapshot is None:
                return self.send_json({"error": "找不到该任务。"}, 404)
            return self.send_json(snapshot)
        return self.send_json({"error": "找不到请求的功能。"}, 404)

    def do_POST(self):
        path = urlsplit(self.path).path
        model_action = MODEL_ACTION.fullmatch(path)
        if model_action is not None:
            return self.manage_model(*model_action.groups())
        if self.headers.get("Origin"):
            return self.send_json({"error": "此服务仅供桌面程序访问。"}, 403)
        try:
            data = self.body()
            if path == "/api/translate":
                return self.translate(data)
            if path == "/api/detect-language":
                content = data.get("text")
                if not isinstance(content, str) or not content.strip():
                    raise ValueError("请输入需要检测语言的文字。")
                code = detect_language(content)
                language = LANGUAGES.get(code)
                return self.send_json({
                    "code": code if language else None,
                    "name": language.name if language else None,
                })
            if path == "/api/ocr":
                return self.ocr(data)
            if path == "/api/chat":
                return self.chat(data)
            if path == "/api/cancel":
                request_id = str(data.get("id", ""))
                with state_lock:
                    event = requests.get(request_id)
                if event is not None:
                    event.set()
                return self.send_json({"cancelled": event is not None})
            if path == "/api/jobs":
                return self.start_job(data)
            if path.startswith("/api/jobs/") and path.endswith("/cancel"):
                job_id = path[len("/api/jobs/"):-len("/cancel")]
                with state_lock:
                    job = jobs.get(job_id)
                    if job is not None:
                        job["cancel"].set()
                return self.send_json({"cancelled": job is not None})
            if path == "/api/shutdown":
                with state_lock:
                    for event in requests.values():
                        event.set()
                    for job in jobs.values():
                        job["cancel"].set()
                self.send_json({"stopping": True})
                threading.Thread(target=self.server.shutdown, daemon=True).start()
                return
            return self.send_json({"error": "找不到请求的功能。"}, 404)
        except ImageOcrError as exc:
            return self.send_json({"error": str(exc), "code": exc.code}, 400)
        except (ValueError, TypeError, json.JSONDecodeError) as exc:
            return self.send_json({"error": str(exc)}, 400)
        except (BrokenPipeError, ConnectionResetError):
            return
        except Exception as exc:
            return self.send_json({"error": str(exc)}, 500)

    def manage_model(self, model_id, action):
        token = self.headers.get("X-Local-Translate-Model-Token", "")
        if not (self.desktop_model_request() and token.isascii() and secrets.compare_digest(token, model_manager_token)):
            self.close_connection = True
            return self.send_json({"error": "此功能仅供桌面程序访问。", "code": "desktop_required"}, 403)
        if self.headers.get_content_type() != "application/json":
            self.close_connection = True
            return self.send_json({"error": "请求格式无效。", "code": "invalid_request"}, 415)
        try:
            data = self.body(128 * 1024)
            manager = engine.model_manager
            if action == "import":
                if data.keys() != {"path"} or not isinstance(data["path"], str) or not data["path"]:
                    raise ModelManagerError("invalid_import_path", 400)
                manager.install(model_id, source_path=data["path"])
            else:
                if data:
                    raise ValueError("此操作只接受模型 ID。")
                {"install": manager.install, "cancel": manager.cancel, "delete": manager.delete}[action](model_id)
            return self.send_json(engine.model_status(model_id))
        except ModelManagerError as error:
            return self.send_json({"error": str(error), "code": error.code}, error.status)
        except (ValueError, TypeError) as error:
            self.close_connection = True
            return self.send_json({"error": str(error), "code": "invalid_request"}, 400)
        except (BrokenPipeError, ConnectionResetError):
            return
        except OSError:
            error = ModelManagerError("storage_error", 500)
            return self.send_json({"error": str(error), "code": error.code}, error.status)

    def ocr(self, data):
        request_id = str(data.get("id") or uuid.uuid4().hex)
        cancel = threading.Event()
        with state_lock:
            if request_id in requests:
                raise ValueError("请求标识重复。")
            requests[request_id] = cancel
        try:
            result = recognize_image(
                data.get("path"), data.get("language", "auto"), cancel=cancel,
                visual_reader=engine.transcribe_image,
            )
            return self.send_json(result)
        finally:
            with state_lock:
                requests.pop(request_id, None)

    def translate(self, data):
        model_id, source, target = parse_translation(data)
        content = data.get("text")
        if not isinstance(content, str) or not content.strip():
            raise ValueError("请输入需要翻译的文字。")

        def generate(on_delta, cancel):
            return engine.translate(content, model_id, source, target, on_delta=on_delta, cancel=cancel)

        return self.stream(data, model_id, generate)

    def chat(self, data):
        model_id = data.get("model_id")
        messages = data.get("messages")

        def generate(on_delta, cancel):
            return engine.chat(messages, model_id, on_delta=on_delta, cancel=cancel)

        return self.stream(data, model_id, generate)

    def stream(self, data, model_id, generate):
        request_id = str(data.get("id") or uuid.uuid4().hex)
        cancel = threading.Event()
        with state_lock:
            if request_id in requests:
                raise ValueError("请求标识重复。")
            requests[request_id] = cancel
        self.close_connection = True

        def write_event(event):
            encoded = (json.dumps(event, ensure_ascii=False) + "\n").encode("utf-8")
            self.wfile.write(encoded)
            self.wfile.flush()

        def emit(event):
            if cancel.is_set():
                raise InterruptedError("请求已取消。")
            try:
                write_event(event)
            except OSError:
                cancel.set()
                raise

        try:
            self.send_response(200)
            self.send_header("Content-Type", "application/x-ndjson; charset=utf-8")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Connection", "close")
            self.end_headers()
            try:
                text = generate(lambda part: emit({"type": "delta", "text": part}), cancel)
                emit({"type": "done", "text": text, "model_id": model_id})
            except Exception as exc:
                write_event({"type": "error", "message": str(exc) or "请求已停止。"})
        except OSError:
            cancel.set()
        finally:
            with state_lock:
                requests.pop(request_id, None)

    def start_job(self, data):
        model_id, source, target = parse_translation(data)
        paths = data.get("paths")
        if not isinstance(paths, list) or not paths or any(
            not isinstance(p, str) or not p for p in paths
        ):
            raise ValueError("请先添加需要翻译的文件或文件夹。")
        bilingual = data.get("bilingual", False)
        if not isinstance(bilingual, bool):
            raise ValueError("输出格式无效。")
        for path in paths:
            absolute_path(path, "源文件路径")
        output_dir = data.get("output_dir")
        output_names = data.get("output_names")
        validate_output_options(output_dir, output_names)
        job_id = uuid.uuid4().hex
        cancel = threading.Event()
        with state_lock:
            jobs[job_id] = {
                "status": "running",
                "total": 0,
                "completed": 0,
                "current_file": "",
                "current_done": 0,
                "current_total": 0,
                "files": [],
                "cancel": cancel,
            }

        def progress(event):
            with state_lock:
                job = jobs[job_id]
                kind = event.get("type")
                if kind == "start":
                    job["total"] = event.get("total", 0)
                elif kind == "file_start":
                    job["current_file"] = event.get("path", "")
                    job["current_done"] = job["current_total"] = 0
                elif kind == "file_progress" and event.get("path") == job["current_file"]:
                    job["current_done"] = event.get("current", 0)
                    job["current_total"] = event.get("total", 0)
                elif kind in ("file_done", "file_error"):
                    result = {
                        "path": event.get("path", ""),
                        "status": "success" if kind == "file_done" else "failed",
                        "output": event.get("output", ""),
                        "error": event.get("error", ""),
                        "warnings": event.get("warnings", []),
                    }
                    job["files"].append(result)
                    job["completed"] += 1
                    job["current_done"] = job["current_total"] = 0
                elif kind == "finish":
                    job["status"] = "cancelled" if event.get("cancelled") else "completed"
                    job["current_file"] = ""

        def bind_file_translator(file_source, file_target):
            def translate_file_part(text):
                if cancel.is_set():
                    raise InterruptedError("任务已取消。")
                return engine.translate(text, model_id, file_source, file_target, cancel=cancel)

            return translate_file_part

        def work():
            try:
                with engine.model_manager.use(model_id):
                    run_batch(
                        paths, model_id, source, target, bilingual,
                        output_dir, None, cancel, progress,
                        output_names=output_names, translator_factory=bind_file_translator, models=engine.models,
                    )
            except Exception as exc:
                with state_lock:
                    job = jobs[job_id]
                    job["files"].append({
                        "path": job["current_file"],
                        "status": "failed",
                        "output": "",
                        "error": str(exc),
                        "warnings": [],
                    })
            finally:
                with state_lock:
                    job = jobs[job_id]
                    if job["status"] == "running":
                        job["status"] = "cancelled" if cancel.is_set() else "completed"
                    job["current_file"] = ""

        threading.Thread(target=work, daemon=True, name="file-job-" + job_id).start()
        return self.send_json({"job_id": job_id})

def main():
    server = AppServer(("127.0.0.1", 47836), Handler)
    try:
        engine.model_manager.start()
        server.serve_forever(poll_interval=0.2)
    finally:
        server.server_close()
        engine.shutdown()


if __name__ == "__main__":
    main()
