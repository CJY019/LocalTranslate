"""Compare the three installed translation models on the same local examples."""

import json
import statistics
import subprocess
import threading
import time
import urllib.request
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BASE = "http://127.0.0.1:47836"

# Twelve English and twelve Chinese examples cover the user's agreed categories.
SAMPLES = [
    ("日常短句", "Hello, how are you today?"),
    ("时间", "The meeting starts at 9:30 a.m. on Tuesday."),
    ("工作邮件", "Please send me the updated report before Friday."),
    ("否定", "We have not approved the proposal yet."),
    ("数字与单位", "The temperature dropped from 25°C to 18°C."),
    ("金额", "The total cost is $125.50, including tax."),
    ("界面", "Click Save to apply your changes."),
    ("技术", "The server returned an error because the connection timed out."),
    ("否定与条件", "Do not delete the original file before you verify the backup."),
    ("长句", "Although the prototype is fast, we need to check whether it produces accurate translations for technical documents."),
    ("显存", "An 8 GB graphics card may need to offload part of a large model to system memory."),
    ("条件", "If the data is incomplete, record the missing fields and retry only after the source has been corrected."),
    ("日常短句", "你好，今天怎么样？"),
    ("时间", "会议将于周二上午九点半开始。"),
    ("工作邮件", "请在周五之前把更新后的报告发给我。"),
    ("否定", "我们尚未批准这项提案。"),
    ("数字与单位", "温度从25摄氏度降到了18摄氏度。"),
    ("金额", "含税总费用为125.50美元。"),
    ("界面", "点击保存以应用更改。"),
    ("技术", "由于连接超时，服务器返回了错误。"),
    ("否定与条件", "验证备份之前，不要删除原始文件。"),
    ("长句", "虽然原型运行得很快，我们仍需检查它能否准确翻译技术文档中的长句和专业术语。"),
    ("显存", "8 GB显存可能不足以完全加载大型模型，需要将部分计算放在系统内存中。"),
    ("条件", "如果数据不完整，请记录缺失字段，并在来源修正后重试。"),
]

MODELS = [
    ("gemma4b", "TranslateGemma 4B"),
    ("index2b", "Index-Translate 2B"),
    ("hymt7b", "Hy-MT2 7B"),
]


class GPUTracker:
    def __init__(self):
        self.maximum = 0
        self.stop_event = threading.Event()
        self.thread = threading.Thread(target=self.poll, daemon=True)

    def poll(self):
        while not self.stop_event.is_set():
            try:
                result = subprocess.run(
                    ["nvidia-smi", "--query-gpu=memory.used",
                     "--format=csv,noheader,nounits"],
                    capture_output=True, text=True, timeout=5,
                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                )
                if result.returncode == 0:
                    values = [int(x.strip()) for x in result.stdout.splitlines()]
                    self.maximum = max([self.maximum] + values)
            except (OSError, ValueError, subprocess.TimeoutExpired):
                pass
            self.stop_event.wait(0.7)

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *exc):
        self.stop_event.set()
        self.thread.join(timeout=6)


def translate(model_id, sample, number):
    body = json.dumps(
        {"id": f"benchmark-{model_id}-{number}",
         "model_id": model_id, "text": sample,
         "source_lang": "auto", "target_lang": "auto"},
        ensure_ascii=False,
    ).encode("utf-8")
    request = urllib.request.Request(
        BASE + "/api/translate", body,
        headers={"Content-Type": "application/json"}, method="POST",
    )
    start = time.perf_counter()
    first = None
    output = ""
    with urllib.request.urlopen(request, timeout=600) as response:
        for raw in response:
            event = json.loads(raw.decode("utf-8"))
            if event["type"] == "delta" and event.get("text"):
                if first is None:
                    first = time.perf_counter() - start
                output += event["text"]
            elif event["type"] == "done":
                output = event.get("text", output)
            elif event["type"] == "error":
                raise RuntimeError(event.get("message", "翻译失败"))
    total = time.perf_counter() - start
    return output.strip(), first, total


def cell(text):
    return text.replace("|", r"\|").replace("\r", " ").replace("\n", "<br>")


def main():
    model_results = []
    all_rows = []
    print("Starting local benchmark.", flush=True)
    for model_id, name in MODELS:
        rows = []
        print(f"\n{name}", flush=True)
        with GPUTracker() as gpu:
            for number, (category, source) in enumerate(SAMPLES, 1):
                try:
                    output, first, elapsed = translate(model_id, source, number)
                    error = ""
                except Exception as exc:
                    output, first, elapsed, error = "", None, None, str(exc)
                rows.append((category, source, output, first, elapsed, error))
                first_text = "-" if first is None else f"{first:.2f}s"
                elapsed_text = "-" if elapsed is None else f"{elapsed:.2f}s"
                print(f"{number:02d}/24 first={first_text} total={elapsed_text} {error}", flush=True)
        model_results.append((name, rows, gpu.maximum))
        all_rows.append((name, rows))
    stamp = datetime.now().strftime("%Y-%m-%d %H:%M")
    lines = [
        "# 本机翻译模型实测",
        "",
        f"测试时间：{stamp}（本机时间）",
        "",
        "测试素材为同一组 24 条自拟中英短句、工作邮件、否定、数字、技术和长句。"
        "冷启动首字包含模型切换及加载，热启动首字为后续样本的中位数。"
        "显存数值为测试期间整张 GPU 的峰值占用，包含其他程序。",
        "",
        "| 模型 | 冷启动首字 | 热启动首字中位数 | 热启动整句中位数 | GPU 总显存峰值 | 完成 |",
        "| --- | ---: | ---: | ---: | ---: | ---: |",
    ]
    for name, rows, peak in model_results:
        first_cold = rows[0][3]
        hot_first = [row[3] for row in rows[1:] if row[3] is not None]
        hot_total = [row[4] for row in rows[1:] if row[4] is not None]
        fmt = lambda value: f"{value:.2f} 秒" if value is not None else "失败"
        lines.append(
            f"| {name} | {fmt(first_cold)} | "
            f"{fmt(statistics.median(hot_first) if hot_first else None)} | "
            f"{fmt(statistics.median(hot_total) if hot_total else None)} | "
            f"{peak} MiB | {sum(not row[5] for row in rows)}/24 |"
        )
    lines += ["", "## 逐条输出", ""]
    for name, rows in all_rows:
        lines += [f"### {name}", "", "| 类别 | 原文 | 译文／错误 |", "| --- | --- | --- |"]
        for category, source, output, first, elapsed, error in rows:
            lines.append(f"| {cell(category)} | {cell(source)} | {cell(error or output)} |")
        lines.append("")
    (ROOT / "模型实测报告.md").write_text("\n".join(lines), encoding="utf-8")
    print("\nReport written to 模型实测报告.md", flush=True)


if __name__ == "__main__":
    main()
