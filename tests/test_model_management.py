"""Small GGUF fixtures exercise the model lifecycle without fetching weights."""

from __future__ import annotations

import hashlib
import http.client
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))

from inference import TranslationEngine
from model_catalog import DEFAULT_MODEL_ID, GGUFWeight, TranslationModel
from model_manager import ModelManager, ModelManagerError


WEIGHT = b"GGUF" + b"offline test weights" * 256
WRONG_WEIGHT = WEIGHT[:-1] + b"!"
URL = "https://huggingface.co/test/model/resolve/" + "1" * 40 + "/fixture.gguf"
SHA256 = hashlib.sha256(WEIGHT).hexdigest()
WEIGHT_FILENAME = "fixture.gguf"
DEFAULT_FIXTURE = TranslationModel(
    DEFAULT_MODEL_ID, "Fixture", "llama", "index", ("en", "zh"), "Apache-2.0", "",
    GGUFWeight(WEIGHT_FILENAME, len(WEIGHT), SHA256, URL),
)


class FixtureResponse(io.BytesIO):
    status = 200


class BlockingResponse(FixtureResponse):
    def __init__(self, content):
        super().__init__(content)
        self.proceed = threading.Event()

    def read1(self, size):
        if self.tell() and not self.proceed.wait(4):
            raise TimeoutError("fixture not released")
        return super().read1(size)


class FixtureOpener:
    def __init__(self, *responses):
        self.requests = []
        self._responses = iter(responses)

    def open(self, request, timeout):
        self.requests.append(request.full_url)
        return next(self._responses)


class ModelsTest(unittest.TestCase):
    def setUp(self):
        temp = ROOT / "temp"
        temp.mkdir(exist_ok=True)
        folder = tempfile.TemporaryDirectory(prefix="model-manager-test-", dir=temp)
        self.addCleanup(folder.cleanup)
        self.root = Path(folder.name)
        self.directory = self.root / "models"
        self.path = self.directory / WEIGHT_FILENAME
        self.releases = []

    def manager(self, *responses):
        manager = ModelManager(self.root, {DEFAULT_MODEL_ID: DEFAULT_FIXTURE}, self.releases.append)
        manager._opener = FixtureOpener(*responses)
        self.addCleanup(manager.shutdown)
        return manager

    def wait_until(self, observe, predicate):
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            snapshot = observe()
            if predicate(snapshot):
                return snapshot
            time.sleep(0.01)
        self.fail(f"Timed out with status {snapshot}")

    def status(self, manager, expected):
        return self.wait_until(lambda: manager.status(DEFAULT_FIXTURE)[0], lambda row: row["status"] == expected)

    def test_first_install_cancel_retry_delete_without_startup_redownload(self):
        response = BlockingResponse(WEIGHT)
        manager = self.manager(response, FixtureResponse(WEIGHT))
        manager.start()
        partial = self.path.with_name(self.path.name + ".part")
        self.wait_until(lambda: manager.status(DEFAULT_FIXTURE)[0], lambda row: row["received_bytes"] > 0)
        self.assertFalse(self.path.exists())
        self.assertTrue(partial.exists())
        manager.cancel(DEFAULT_MODEL_ID)
        response.proceed.set()
        self.status(manager, "cancelled")
        self.assertFalse(partial.exists())

        manager.install(DEFAULT_MODEL_ID)
        self.status(manager, "installed")
        self.assertEqual(self.path.read_bytes(), WEIGHT)
        self.assertEqual(manager._opener.requests, [URL, URL])
        with manager.use(DEFAULT_MODEL_ID):
            with self.assertRaisesRegex(ModelManagerError, "模型正在翻译"):
                manager.delete(DEFAULT_MODEL_ID)
        manager.delete(DEFAULT_MODEL_ID)
        self.status(manager, "missing")
        self.assertFalse(self.path.exists())
        self.assertEqual(self.releases[-1], DEFAULT_MODEL_ID)
        restarted = self.manager()
        restarted.start()
        self.status(restarted, "missing")
        self.assertEqual(restarted._opener.requests, [])

    def test_existing_valid_copy_is_reused_and_replacement_verified(self):
        self.directory.mkdir()
        self.path.write_bytes(WEIGHT)
        manager = self.manager()
        manager.start()
        self.status(manager, "installed")
        original = self.path.stat()
        manager.install(DEFAULT_MODEL_ID, str(self.path))
        self.assertEqual(self.path.stat().st_mtime_ns, original.st_mtime_ns)
        self.assertEqual(self.path.stat().st_ino, original.st_ino)
        self.assertEqual(manager._opener.requests, [])

        self.path.write_bytes(WRONG_WEIGHT)
        failed = self.status(manager, "failed")
        self.assertEqual(failed["error_code"], "hash_mismatch")
        from_disk = self.root / "existing local weight.gguf"
        from_disk.write_bytes(WEIGHT)
        manager.install(DEFAULT_MODEL_ID, str(from_disk))
        self.status(manager, "installed")
        self.assertEqual(from_disk.read_bytes(), WEIGHT)
        self.assertEqual(self.path.read_bytes(), WEIGHT)

    def test_bad_download_leaves_no_final_or_partial_and_no_auto_retry(self):
        manager = self.manager(FixtureResponse(WRONG_WEIGHT))
        manager.start()
        failed = self.status(manager, "failed")
        self.assertEqual(failed["error_code"], "hash_mismatch")
        self.assertFalse(self.path.exists())
        self.assertFalse(self.path.with_name(self.path.name + ".part").exists())
        next_start = self.manager()
        next_start.start()
        self.status(next_start, "missing")

    def test_translate_gemma_uses_configured_shared_ollama_store(self):
        store = self.root / "shared-ollama"
        manifests = store / "manifests" / "registry.ollama.ai" / "library" / "translategemma"
        manifests.mkdir(parents=True)
        blobs = store / "blobs"
        blobs.mkdir()
        layer = {"mediaType": "application/vnd.ollama.image.model", "digest": "sha256:" + SHA256, "size": len(WEIGHT)}
        (manifests / "4b").write_text(json.dumps({"layers": [layer]}), encoding="utf-8")
        blob = blobs / ("sha256-" + SHA256)
        blob.write_bytes(WEIGHT)
        runtime = self.root / "custom-ollama.exe"
        runtime.touch()
        with patch.dict(os.environ, {"OLLAMA_MODELS": str(store), "LOCAL_TRANSLATE_OLLAMA_EXE": str(runtime)}):
            engine = TranslationEngine(self.root)
        self.addCleanup(engine.shutdown)
        details = engine.model_status("gemma4b")
        self.assertTrue(details["available"])
        self.assertEqual(details["storage"], "ollama")
        self.assertEqual(engine._available_model("gemma4b")[0].ollama_ref, "translategemma:4b")
        with self.assertRaises(ModelManagerError):
            engine.model_manager.delete("gemma4b")
        self.assertEqual(blob.read_bytes(), WEIGHT)

    def test_http_guards_and_custom_model_text_and_files(self):
        import server

        self.directory.mkdir()
        (self.directory / "local-models.json").write_text(json.dumps({"version": 1, "models": [{
            "id": "fixture", "name": "Fixture", "runtime": "llama", "prompt_adapter": "generic",
            "languages": ["fr", "de"], "license": "Apache-2.0",
            "filename": WEIGHT_FILENAME, "size_bytes": len(WEIGHT), "sha256": SHA256,
        }]}), encoding="utf-8")
        model_source = self.root / "manual.gguf"
        model_source.write_bytes(WEIGHT)
        runtime = self.root / "runtime" / "llama"
        runtime.mkdir(parents=True)
        (runtime / "llama-server.exe").touch()
        engine = TranslationEngine(self.root)
        self.addCleanup(engine.shutdown)
        original_engine = server.engine
        self.addCleanup(setattr, server, "engine", original_engine)
        server.engine = engine
        prompts = []

        def fake_inference(model_id, endpoint, data, on_delta, cancel):
            self.assertEqual(model_id, "fixture")
            self.assertEqual(endpoint, "/v1/chat/completions")
            prompts.append(data["messages"][0]["content"])
            if on_delta:
                on_delta("Guten Tag")
            return "Guten Tag"

        engine._run = fake_inference
        service = server.AppServer(("127.0.0.1", 0), server.Handler)
        self.addCleanup(service.server_close)
        threading.Thread(target=service.serve_forever, daemon=True).start()
        self.addCleanup(service.shutdown)

        def request(path, data=None, headers=None):
            headers = (headers or {}).copy()
            body = None if data is None else json.dumps(data).encode("utf-8")
            if body is not None:
                headers.update({"Content-Type": "application/json"})
            connection = http.client.HTTPConnection("127.0.0.1", service.server_port, timeout=3)
            try:
                connection.request("GET" if body is None else "POST", path, body=body, headers=headers)
                response = connection.getresponse()
                content = response.read()
                if response.getheader("Content-Type").startswith("application/x-ndjson"):
                    payload = [json.loads(line) for line in content.splitlines()]
                else:
                    payload = json.loads(content)
                return response.status, payload
            finally:
                connection.close()

        self.assertEqual(request("/api/model-manager", headers={"Host": "untrusted.example"})[0], 403)
        self.assertEqual(request("/api/model-manager", headers={"Sec-Fetch-Site": "same-origin"})[0], 403)
        self.assertEqual(request("/api/model-manager", headers={"Origin": "https://untrusted.example"})[0], 403)
        action = "/api/model-manager/fixture/import"
        self.assertEqual(request(action, {"path": str(model_source)})[0], 403)
        config = request("/api/model-manager")[1]
        headers = {"X-Local-Translate-Model-Token": config["token"]}
        delete = "/api/model-manager/fixture/delete"
        self.assertEqual(request(delete, {"path": str(model_source)}, headers)[0], 400)
        self.assertEqual(request("/api/model-manager/gemma4b/delete", {}, headers)[0], 403)
        self.assertEqual(request(action, {"path": "https://example.test/a.gguf"}, headers)[0], 400)
        self.assertEqual(request(action, {"path": str(model_source)}, headers)[0], 200)

        def fixture_row():
            return next(row for row in request("/api/models")[1]["models"] if row["id"] == "fixture")

        row = self.wait_until(fixture_row, lambda row: row["available"])
        self.assertEqual([language["code"] for language in row["target_languages"]], ["fr", "de"])
        direction = {"model_id": "fixture", "source_lang": "fr", "target_lang": "de"}
        _, events = request("/api/translate", {**direction, "text": "Bonjour"})
        self.assertEqual(events[-1], {"type": "done", "text": "Guten Tag", "model_id": "fixture"})
        source = self.root / "input.txt"
        source.write_text("Bonjour", encoding="utf-8")
        job_id = request("/api/jobs", {**direction, "paths": [str(source)]})[1]["job_id"]
        job = self.wait_until(lambda: request(f"/api/jobs/{job_id}")[1], lambda job: job["status"] == "completed")
        self.assertEqual(job["files"][0]["status"], "success")
        self.assertEqual(Path(job["files"][0]["output"]).read_text(encoding="utf-8-sig"), "Guten Tag")
        self.assertTrue(all("French text into German" in prompt for prompt in prompts))
        self.assertEqual(model_source.read_bytes(), WEIGHT)


if __name__ == "__main__":
    unittest.main()
