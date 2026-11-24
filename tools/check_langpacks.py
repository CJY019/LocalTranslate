"""Validate shipped UI language packs; refresh their pinned sizes and SHA-256 on request."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import sys


ROOT = Path(__file__).resolve().parents[1] / "langpacks"
MAX_BYTES = 512 * 1024
LOCALE = re.compile(r"[a-z]{2,3}(?:-[A-Za-z0-9]{2,8})*")
PLACEHOLDER = re.compile(r"\{[0-9]\}")
KEY = re.compile(r"[A-Za-z][A-Za-z0-9_.-]{0,95}")


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate key: {key}")
        result[key] = value
    return result


def read(path):
    raw = path.read_bytes()
    if not 0 < len(raw) <= MAX_BYTES:
        raise ValueError(f"{path}: invalid size")
    return raw, json.loads(raw.decode("utf-8"), object_pairs_hook=unique_object)


def schema(obj, keys):
    if not isinstance(obj, dict) or set(obj) != set(keys):
        raise ValueError(f"expected fields {keys}")
    if "schema" in obj and (type(obj["schema"]) is not int or obj["schema"] != 1):
        raise ValueError("expected schema 1")


def pack(path, locale, reference=None):
    raw, data = read(path)
    schema(data, ["schema", "locale", "strings"])
    if data["locale"] != locale or not isinstance(data["strings"], dict):
        raise ValueError(f"{path}: invalid locale or strings")
    signatures = {}
    for key, value in data["strings"].items():
        if not KEY.fullmatch(key) or not isinstance(value, str) or not 0 < len(value.encode("utf-16-le")) // 2 <= 4096:
            raise ValueError(f"{path}: invalid string {key}")
        if any(ord(c) < 32 and c not in "\n\t" for c in value):
            raise ValueError(f"{path}: control character in {key}")
        if any(c in PLACEHOLDER.sub("", value) for c in "{}"):
            raise ValueError(f"{path}: invalid placeholder in {key}")
        signatures[key] = sorted(PLACEHOLDER.findall(value))
    if not signatures:
        raise ValueError(f"{path}: empty pack")
    if reference is not None and signatures != reference:
        differences = sorted(k for k in signatures.keys() | reference.keys() if signatures.get(k) != reference.get(k))
        raise ValueError(f"{path}: keys or placeholders differ: {', '.join(differences)}")
    return raw, signatures


def validate(root, update):
    _, reference = pack(root / "builtin" / "en-US.json", "en-US")
    pack(root / "builtin" / "zh-CN.json", "zh-CN", reference)
    manifest_path = root / "manifest.json"
    _, manifest = read(manifest_path)
    schema(manifest, ["schema", "release", "packs"])
    if not re.fullmatch(r"v[0-9]+\.[0-9]+\.[0-9]+", manifest["release"]) or not isinstance(manifest["packs"], list):
        raise ValueError("expected a release tag and a pack list")
    seen = {"en-us", "zh-cn"}
    registered = set()
    for entry in manifest["packs"]:
        schema(entry, ["locale", "nativeName", "file", "bytes", "sha256"])
        code = entry["locale"]
        if not isinstance(code, str) or not LOCALE.fullmatch(code) or code.lower() in seen:
            raise ValueError(f"invalid or duplicate locale: {code}")
        seen.add(code.lower())
        if not isinstance(entry["nativeName"], str) or not entry["nativeName"].strip() or len(entry["nativeName"]) > 80:
            raise ValueError(f"{code}: invalid nativeName")
        if entry["file"] != f"{code}.json":
            raise ValueError(f"{code}: expected filename {code}.json")
        registered.add(entry["file"])
        raw, _ = pack(root / entry["file"], code, reference)
        digest = hashlib.sha256(raw).hexdigest()
        if update:
            entry.update(bytes=len(raw), sha256=digest)
        elif type(entry["bytes"]) is not int or entry["bytes"] != len(raw) or entry["sha256"] != digest:
            raise ValueError(f"{code}: size or SHA-256 mismatch")
    if registered != {p.name for p in root.glob("*.json") if p != manifest_path}:
        raise ValueError("unregistered or missing language-pack JSON file")
    if update:
        manifest_path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    return len(reference), len(seen)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--update-manifest", action="store_true", help="rewrite external pack hashes and byte counts")
    parser.add_argument("--root", type=Path, default=ROOT, help="language-pack directory")
    args = parser.parse_args()
    try:
        count, locales = validate(args.root, args.update_manifest)
        print(f"Validated {locales} languages, {count} keys each; release hashes verified or updated.")
    except (ValueError, OSError, KeyError, TypeError) as exc:
        sys.exit(str(exc))
