# Translation models

The model selected in the app is used for text, image, and file translation. Windows OCR runs separately. TranslateGemma can also read images when its optional Ollama installation is available.

| Model | Weight | License | Installation |
| --- | ---: | --- | --- |
| Index-Translate 2B, Q4_K_M | 1,312,164,352 bytes | Apache-2.0 | Default. The app starts installing it in the background on first launch. [IndexTeam model files](https://huggingface.co/IndexTeam/Index-Translate-2B-GGUF/tree/3a0eca6ce931d88d00614d427ab3ed8ff0743902) |
| HY-MT2 7B, Q4_K_M | 4,624,648,896 bytes | Apache-2.0 | Install from Model Management. [Tencent model files](https://huggingface.co/tencent/Hy-MT2-7B-GGUF/tree/ab8472660ac61fac25f1af43fac2599d52a8a775) · [License at the pinned revision](https://huggingface.co/tencent/Hy-MT2-7B-GGUF/blob/ab8472660ac61fac25f1af43fac2599d52a8a775/LICENSE.txt) |
| TranslateGemma 4B | About 3.30 GB | Gemma Terms of Use | Optional external installation. [Ollama model](https://ollama.com/library/translategemma:4b) · [Gemma terms](https://ai.google.dev/gemma/terms) |

## Install, import, or delete a GGUF

Model Management shows download, import, and verification progress. Cancel ends the current attempt. Install starts a new attempt after a cancellation or error. The one-time Index bootstrap is recorded in `models/.index-auto-install-attempted`, so a cancellation, failed first download, or deletion leaves subsequent installations up to you.

If your computer already has a copy, choose **Import** and select the GGUF. You can also put the file in the app's `models` directory using the exact filename in the table below. The app checks the file size and SHA-256 before using the weight. An already verified file in that directory is reused in place. Import from a different folder creates a verified app copy and preserves the source file. Temporary `.part` files are promoted to the final name only when verification passes. After an interrupted transfer, a new attempt starts from the beginning.

| File in `models` | SHA-256 |
| --- | --- |
| `Index-Translate-2B.Q4_K_M.gguf` | `044b313d29342bd3b2c77cbb64023ca0d209bd9b3247763f9d162767ef2d746a` |
| `Hy-MT2-7B-Q4_K_M.gguf` | `9f96256500f3fc1ab4d64336b58f52a949a95ad7516b0c229476eef782f9f77b` |

For manual installation, download the pinned [Index‑Translate 2B Q4_K_M](https://huggingface.co/IndexTeam/Index-Translate-2B-GGUF/resolve/3a0eca6ce931d88d00614d427ab3ed8ff0743902/Index-Translate-2B.Q4_K_M.gguf) or [HY‑MT2 7B Q4_K_M](https://huggingface.co/tencent/Hy-MT2-7B-GGUF/resolve/ab8472660ac61fac25f1af43fac2599d52a8a775/Hy-MT2-7B-Q4_K_M.gguf).

Delete removes the selected model's file from the app's `models` directory. Finish any translation jobs using that model before deleting it. The app unloads an idle model when needed to release its file.

### TranslateGemma in Ollama

Install Ollama and run `ollama pull translategemma:4b`, then reopen the app. The app also recognizes an existing `translategemma:latest` installation. The Ollama entry shows availability in Model Management. Use Ollama to manage its shared weights. [Ollama Windows installation](https://docs.ollama.com/windows)

The app looks for installed Ollama models in `%OLLAMA_MODELS%` when set. Otherwise it checks `.ollama/models` at the root of the app's drive, then `%USERPROFILE%\.ollama\models`. For a nonstandard Ollama executable, set `LOCAL_TRANSLATE_OLLAMA_EXE` to its full path before starting the app. The executable is normally discovered on the app's drive, in `PATH`, or in the standard Windows installation directories.

## Add a local GGUF model

Create `models/local-models.json` and restart the app. Each entry is added to the model picker and Model Management. This example registers another official Index quantization with English and Simplified Chinese enabled. Add only language codes the model supports. The complete list of codes is in [`backend/language_catalog.py`](../backend/language_catalog.py).

```json
{
  "version": 1,
  "models": [
    {
      "id": "index2b_q5km",
      "name": "Index-Translate 2B Q5_K_M",
      "runtime": "llama",
      "prompt_adapter": "index",
      "languages": ["zh", "en"],
      "license": "Apache-2.0",
      "source_url": "https://huggingface.co/IndexTeam/Index-Translate-2B-GGUF/tree/3a0eca6ce931d88d00614d427ab3ed8ff0743902",
      "filename": "Index-Translate-2B.Q5_K_M.gguf",
      "size_bytes": 1454787072,
      "sha256": "2a869b5e374ff6121ee5191629fba8ff4a081951d56eec90d89771d7971465ee",
      "download_url": "https://huggingface.co/IndexTeam/Index-Translate-2B-GGUF/resolve/3a0eca6ce931d88d00614d427ab3ed8ff0743902/Index-Translate-2B.Q5_K_M.gguf"
    }
  ]
}
```

- Use a unique lowercase `id`, a display `name`, and a unique plain `.gguf` filename. Use `Get-FileHash -Algorithm SHA256` and `Get-Item` to check the hash and exact byte length. Write the hash in lowercase.
- Choose `index`, `hymt`, `translategemma`, or `generic` based on the upstream model's prompt format. `generic` uses a plain translation instruction with the GGUF's chat template. Supply at least two distinct supported `languages` from the application catalog.
- `source_url` is optional metadata. `download_url` is optional and accepts a Hugging Face HTTPS resolve link pinned to a full commit. Leave it out if you intend to import the weights from disk.
- If the model needs a new prompt format or language alias, extend the adapter definitions in [`backend/model_catalog.py`](../backend/model_catalog.py), prompt and generation code in [`backend/inference.py`](../backend/inference.py), and language aliases in [`backend/languages.py`](../backend/languages.py). Built-in GGUFs are defined in `model_catalog.py` using the same metadata and weight fields.

Use models from publishers you trust. The SHA-256 match verifies that the file is the one the manifest describes. The model must also be compatible with the bundled llama.cpp and have a chat template suitable for its adapter.
