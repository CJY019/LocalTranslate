"""Translate local files with an injected text translator."""

from __future__ import annotations

import codecs
import ntpath
import os
import re
import tempfile
import zipfile
from collections.abc import Callable, Iterable, Iterator
from dataclasses import dataclass
from itertools import tee
from pathlib import Path
from threading import Event
from typing import Any

from languages import LANGUAGES, resolve_languages, validate_languages
from model_catalog import BUILTIN_MODELS


Translator = Callable[[str], str]
BindTranslator = Callable[[Iterable[str]], Translator]
TranslatorFactory = Callable[[str, str], Translator]
Progress = Callable[[dict[str, Any]], None]
Span = tuple[int, int]
SUPPORTED_SUFFIXES = frozenset({".txt", ".md", ".markdown", ".docx", ".pdf", ".srt", ".vtt"})
_MAX_TRANSLATION_LENGTH = 4000
_LANGUAGE_SAMPLE_LENGTH = 12000
_MAX_FILENAME_LENGTH = 255
_NEWLINE = re.compile(r"\r\n|\r|\n")
_BLOCK_SEPARATOR = re.compile(r"(\n[ \t]*\n(?:[ \t]*\n)*)")
_TOKEN = re.compile(r"⟪T\d+⟫")
_TEXT_PART = re.compile(r"^(\s*)(.*?)(\s*)$", re.DOTALL)
_INLINE_TIMESTAMP = re.compile(r"<(?:\d{2,}:)?\d{2}:\d{2}\.\d{3}>")
_VTT_VOICE = re.compile(r"^<v(?:\.[^ >]+)*[ \t][^>\n]+>")
_CUE_MARKUP = re.compile(r"<[^<>\n]+>|\\[Nnh]|\{\\[^{}\n]+\}|&(?:#[xX]?[0-9a-fA-F]+|[a-zA-Z][a-zA-Z0-9]+);|\n")
_SRT_TIMING = re.compile(r"^[ \t]*\d{2,}:\d{2}:\d{2}[,.]\d{3}[ \t]+-->[ \t]+\d{2,}:\d{2}:\d{2}[,.]\d{3}(?:[ \t].*)?$")
_VTT_TIMING = re.compile(r"^[ \t]*(?:\d{2,}:)?\d{2}:\d{2}\.\d{3}[ \t]+-->[ \t]+(?:\d{2,}:)?\d{2}:\d{2}\.\d{3}(?:[ \t].*)?$")
_VTT_HEADER = re.compile(r"^WEBVTT(?:[ \t].*)?$")
_VTT_NOTE = re.compile(r"^NOTE(?:[ \t]|$)")
_MD_MARKER = re.compile(r"(?:>[ \t]?|(?:[-+*]|\d{1,9}[.)])[ \t]+(?:\[[ xX]\][ \t]+)?|#{1,6}(?:[ \t]+|$))")
_MD_INDENT = re.compile(r"[ \t]*")
_MD_ATX_END = re.compile(r"[ \t]+#+[ \t]*$")
_MD_AUTOLINK = re.compile(r"(?:https?://|ftp://|mailto:|www\.)[^\s<>|\[\]]+")
_MD_ENTITY = re.compile(r"&(?:#[xX]?[0-9a-fA-F]+|[a-zA-Z][a-zA-Z0-9]+);")
_MD_TAG = re.compile(r"<!--[\s\S]*?-->|</?[A-Za-z][^>\n]*>")
_MD_BACKTICKS = re.compile(r"(?<!`)(`+)(?!`)")
_MD_PIPE = re.compile(r"\\.|[|]")
_XML_SPACE = "{http://www.w3.org/XML/1998/namespace}space"
_WORD_XML = re.compile(r"word/(?:document|header\d+|footer\d+|footnotes|endnotes)\.xml$")
_WORD_NAMESPACE = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
_WORD_STRICT_NAMESPACE = "http://purl.oclc.org/ooxml/wordprocessingml/main"


class _Cancelled(Exception):
    pass


@dataclass(frozen=True)
class _Job:
    path: Path
    error: str | None = None


@dataclass(frozen=True)
class _TextFormat:
    encoding: str
    bom: bytes
    newline: str


def _check_cancel(cancel: Event) -> None:
    if cancel.is_set():
        raise _Cancelled


def absolute_path(value: str, label: str) -> Path:
    if not isinstance(value, str) or not value or "\0" in value:
        raise ValueError(f"{label}必须是有效的绝对路径。")
    path = Path(value)
    windows_path = value.replace("/", "\\")
    if not path.is_absolute() or windows_path.startswith(("\\\\.\\", "\\\\?\\")) or ntpath.isreserved(value):
        raise ValueError(f"{label}必须是有效的绝对路径。")
    return path


def _output_extension(source: Path) -> str:
    return ".docx" if source.suffix.lower() == ".pdf" else source.suffix


def _valid_name_length(value: str) -> bool:
    return len(value.encode("utf-16-le")) // 2 <= _MAX_FILENAME_LENGTH


def validate_output_options(
    output_dir: str | None, output_names: dict[str, str] | None,
) -> tuple[Path | None, dict[Path, str]]:
    directory = None if output_dir in (None, "") else absolute_path(output_dir, "输出目录")
    if output_names is None:
        return directory, {}
    if not isinstance(output_names, dict):
        raise ValueError("自定义输出文件名格式无效。")
    names = {}
    for key, name in output_names.items():
        source = absolute_path(key, "源文件路径")
        if (
            not isinstance(name, str)
            or not name.strip()
            or name.endswith((".", " "))
            or any(char in name for char in "\\/:\"<>|?*")
            or ntpath.isreserved(name)
            or not _valid_name_length(name + _output_extension(source))
        ):
            raise ValueError(f"自定义输出文件名无效：{name}")
        names[source] = name
    return directory, names


def _language_sample(values: Iterable[str], cancel: Event) -> str:
    excerpts = []
    remaining = _LANGUAGE_SAMPLE_LENGTH
    for value in values:
        _check_cancel(cancel)
        value = value.strip()
        if not value or not _looks_translatable(value[:remaining]):
            continue
        excerpts.append(value[:remaining])
        remaining -= len(excerpts[-1]) + 1
        if remaining <= 0:
            break
    return "\n".join(excerpts)


def _natural_text(text: str, spans: Iterable[Span]) -> str:
    segments = []
    position = 0
    for start, end in spans:
        if start < position:
            continue
        segments.append(text[position:start])
        position = end
    segments.append(text[position:])
    return " ".join(segments)


def _chunk_text(value: str) -> Iterator[str]:
    while len(value) > _MAX_TRANSLATION_LENGTH:
        boundary = _MAX_TRANSLATION_LENGTH
        preferred = max(value.rfind(" ", boundary // 2, boundary), value.rfind("\n", boundary // 2, boundary))
        if preferred != -1:
            boundary = preferred + 1
        yield value[:boundary]
        value = value[boundary:]
    yield value


def _looks_translatable(text: str) -> bool:
    return any(char.isalpha() for char in text)


def _translate_text(value: str, translate: Translator, cancel: Event) -> str:
    output = []
    for chunk in _chunk_text(value):
        _check_cancel(cancel)
        match = _TEXT_PART.fullmatch(chunk)
        prefix, body, suffix = match.groups()
        if not _looks_translatable(body):
            output.append(chunk)
            continue
        transformed = translate(body)
        _check_cancel(cancel)
        if not isinstance(transformed, str) or not transformed.strip():
            raise ValueError("翻译服务返回了空文本或无效结果。")
        transformed = _NEWLINE.sub(" ", transformed)
        output.append(prefix + transformed.strip() + suffix)
    return "".join(output)


def _protected_translation(
    text: str, spans: list[Span], translate: Translator, cancel: Event
) -> str:
    if not spans:
        return _translate_text(text, translate, cancel)

    values, original_parts, protected_parts, tokens = {}, [], [], []
    end = 0
    for start, stop in spans:
        if start < end:
            continue
        token = f"⟪T{len(tokens):04}⟫"
        original_parts.append(text[end:start])
        protected_parts.extend((text[end:start], token))
        values[token] = text[start:stop]
        tokens.append(token)
        end = stop
    original_parts.append(text[end:])
    protected_parts.append(text[end:])
    if not _looks_translatable("".join(original_parts)):
        return text

    protected = "".join(protected_parts)
    if len(protected) <= _MAX_TRANSLATION_LENGTH and not _TOKEN.search(text):
        transformed = _translate_text(protected, translate, cancel)
        if _TOKEN.findall(transformed) == tokens and _TOKEN.sub("", transformed).strip():
            return _TOKEN.sub(lambda found: values[found.group()], transformed)

    output = []
    for natural_text, token in zip(original_parts, tokens):
        output.extend((_translate_text(natural_text, translate, cancel), values[token]))
    output.append(_translate_text(original_parts[-1], translate, cancel))
    return "".join(output)


def _read_text(path: Path) -> tuple[str, _TextFormat]:
    data = path.read_bytes()
    bom = b""
    for marker, encoding in (
        (codecs.BOM_UTF32_LE, "utf-32-le"),
        (codecs.BOM_UTF32_BE, "utf-32-be"),
        (codecs.BOM_UTF16_LE, "utf-16-le"),
        (codecs.BOM_UTF16_BE, "utf-16-be"),
        (codecs.BOM_UTF8, "utf-8"),
    ):
        if data.startswith(marker):
            bom = marker
            value = data[len(marker):].decode(encoding)
            break
    else:
        for encoding in ("utf-8", "gb18030", "cp1252"):
            try:
                value = data.decode(encoding)
                break
            except UnicodeDecodeError:
                continue
        else:
            raise ValueError("无法识别文件的文本编码。")
    if "\0" in value:
        raise ValueError("文件包含空字符，请先将其保存为带字节顺序标记的 Unicode 文本。")
    newline = _NEWLINE.search(value)
    newline = newline[0] if newline else "\n"
    return _NEWLINE.sub("\n", value), _TextFormat(encoding, bom, newline)


def _write_text(path: Path, value: str, original: _TextFormat, *, vtt: bool = False) -> None:
    value = value.replace("\n", original.newline)
    if vtt:
        path.write_bytes(value.encode("utf-8"))
        return
    try:
        data = original.bom + value.encode(original.encoding)
    except UnicodeEncodeError:
        data = value.encode("utf-8-sig")
    path.write_bytes(data)


def _translate_plain_text(
    text: str, translate: Translator, cancel: Event, bilingual: bool, report: Callable[[int, int], None]
) -> str:
    lines = text.split("\n")
    output = []
    for number, line in enumerate(lines, 1):
        transformed = _translate_text(line, translate, cancel)
        if bilingual and transformed != line:
            output.append(line)
        output.append(transformed)
        report(number, len(lines))
    return "\n".join(output)


def _split_markdown_prefix(line: str) -> tuple[str, str, str, bool]:
    position = 0
    continuation = ""
    heading = False
    while position < len(line):
        indent = _MD_INDENT.match(line, position).group()
        marker = _MD_MARKER.match(line, position + len(indent))
        if not marker:
            continuation += indent
            position += len(indent)
            break
        token = marker.group()
        if token.startswith(">"):
            continuation += indent + token
        else:
            continuation += indent + " " * len(token.expandtabs(4))
        position = marker.end()
        if token.startswith("#"):
            heading = True
            break
    return line[:position], line[position:], continuation, heading


def _markdown_blocks(lines: list[str]) -> tuple[set[int], set[int], set[int], set[str]]:
    from markdown_it import MarkdownIt

    environment: dict[str, Any] = {}
    tokens = MarkdownIt("commonmark").enable("table").parse("\n".join(lines), environment)
    frozen, tables, setext = set(), set(), set()
    for token in tokens:
        if token.map is None:
            continue
        first, end = token.map
        if token.type in {"fence", "code_block", "html_block", "hr"}:
            frozen.update(range(first, end))
        elif token.type == "table_open":
            tables.update(range(first, end))
            frozen.add(first + 1)
        elif token.type == "heading_open" and token.markup in {"=", "-"}:
            setext.update(range(first, end - 1))
            frozen.add(end - 1)
    for reference in environment.get("references", {}).values():
        frozen.update(range(*reference["map"]))
    return frozen, tables, setext, set(environment.get("references", {}))


def _normalize_reference(label: str) -> str:
    return " ".join(label.strip().split()).lower().upper()


def _markdown_code_spans(text: str) -> list[Span]:
    markers = list(_MD_BACKTICKS.finditer(text))
    spans = []
    index = 0
    while index < len(markers):
        opener = markers[index]
        backslashes = 0
        while opener.start() > backslashes and text[opener.start() - backslashes - 1] == "\\":
            backslashes += 1
        if backslashes % 2:
            index += 1
            continue
        for next_index in range(index + 1, len(markers)):
            closer = markers[next_index]
            if closer[0] == opener[0]:
                spans.append((opener.start(), closer.end()))
                index = next_index
                break
        index += 1
    return spans


def _markdown_link_end(text: str, start: int) -> int | None:
    depth = 1
    quote = ""
    index = start + 1
    while index < len(text):
        char = text[index]
        if char == "\\":
            index += 2
            continue
        if quote:
            if char == quote:
                quote = ""
        elif char == "<" and (index == start + 1 or text[index - 1].isspace()):
            quote = ">"
        elif char in "\"'" and text[index - 1].isspace():
            quote = char
        elif char == "(":
            depth += 1
        elif char == ")":
            depth -= 1
            if depth == 0:
                return index + 1
        index += 1
    return None


def _markdown_inline(
    text: str, references: set[str], *, closing_heading: bool = False, compound: list[Span] | None = None
) -> tuple[str, list[Span]]:
    code = dict(_markdown_code_spans(text))
    syntax = []
    opens = []
    index = 0
    while index < len(text):
        char = text[index]
        stop = code.get(index)
        if stop is None and char == "\\" and index + 1 < len(text) and not text[index + 1].isalnum():
            stop = index + 2
        if stop is None:
            match = _MD_TAG.match(text, index) or _MD_AUTOLINK.match(text, index) or _MD_ENTITY.match(text, index)
            if match:
                stop = match.end()
        if stop is None and char == "[":
            opens.append(index)
            stop = index + 1
        if stop is None and char == "]" and opens:
            opener = opens.pop()
            label = text[opener + 1:index]
            stop = index + 1
            explicit = re.match(r"\[((?:\\.|[^\]\\])*)\]", text[stop:])
            has_destination = False
            if text[stop:stop + 1] == "(":
                destination_end = _markdown_link_end(text, stop)
                if destination_end is not None:
                    has_destination = True
                    stop = destination_end
            elif (explicit is None or not explicit[1]) and _normalize_reference(label) in references:
                old_end = stop + (explicit.end() if explicit else 0)
                reference = "[" + " ".join(label.split()) + "]"
                text = text[:stop] + reference + text[old_end:]
                shift = stop + len(reference) - old_end
                code = {start + (shift if start >= old_end else 0): end + (shift if end >= old_end else 0)
                        for start, end in code.items()}
                has_destination = True
                stop += len(reference)
            elif explicit:
                has_destination = True
                stop += explicit.end()
            if compound is not None and has_destination:
                compound.append((opener, stop))
        if stop is None and char in "*_~[]|!":
            stop = index + 1
            while stop < len(text) and text[stop] == char:
                stop += 1
        if stop is not None:
            syntax.append((index, stop))
            if compound is not None and "\n" in text[index:stop]:
                compound.append((index, stop))
            index = stop
        else:
            index += 1
    if closing_heading:
        trailing = _MD_ATX_END.search(text)
        if trailing:
            syntax.append(trailing.span())
    return text, sorted(syntax)


def _translate_markdown_line(
    line: str, references: set[str], translate: Translator, cancel: Event
) -> str:
    prefix, body, _, heading = _split_markdown_prefix(line)
    prepared, spans = _markdown_inline(body, references, closing_heading=heading)
    return prefix + _protected_translation(prepared, spans, translate, cancel)


def _translate_markdown_table_row(
    line: str, references: set[str], translate: Translator, cancel: Event, bilingual: bool
) -> str:
    prefix, body, _, _ = _split_markdown_prefix(line)
    code = _markdown_code_spans(body)
    pipes = (match.start() for match in _MD_PIPE.finditer(body)
             if match[0] == "|" and not any(start < match.start() < end for start, end in code))
    output = []
    start = 0
    for end in [*pipes, len(body)]:
        cell = body[start:end]
        prepared, spans = _markdown_inline(cell, references)
        transformed = _protected_translation(prepared, spans, translate, cancel)
        if bilingual and transformed != cell:
            transformed = cell.rstrip() + "<br>" + transformed.strip() + cell[len(cell.rstrip()):]
        output.append(transformed)
        start = end + 1
    return prefix + "|".join(output)


def _markdown_multiline_groups(
    lines: list[str], skipped: set[int], references: set[str], cancel: Event
) -> dict[int, int]:
    result = {}
    start = 0
    for stop in range(len(lines) + 1):
        _check_cancel(cancel)
        if stop < len(lines) and stop not in skipped and lines[stop].strip():
            continue
        if stop > start + 1:
            block = "\n".join(_split_markdown_prefix(line)[1] for line in lines[start:stop])
            spans = []
            prepared, _ = _markdown_inline(block, references, compound=spans)
            groups = []
            for left, right in sorted(spans):
                first = start + prepared.count("\n", 0, left)
                last = first + prepared.count("\n", left, right)
                if first == last:
                    continue
                if groups and first <= groups[-1][1]:
                    groups[-1] = (groups[-1][0], max(last, groups[-1][1]))
                else:
                    groups.append((first, last))
            result.update(groups)
        start = stop + 1
    return result


def _translate_markdown_group(
    lines: list[str], references: set[str], translate: Translator, cancel: Event, bilingual: bool
) -> list[str]:
    parts = [_split_markdown_prefix(line) for line in lines]
    block = "\n".join(body for _, body, _, _ in parts)
    prepared, spans = _markdown_inline(block, references)
    spans.extend(match.span() for match in re.finditer("\n", prepared))
    transformed = _protected_translation(prepared, sorted(spans), translate, cancel)
    if transformed == block:
        return lines
    rows = transformed.split("\n")
    continuation = parts[0][2]
    translated = [prefix + row for (prefix, *_), row in zip(parts, rows, strict=True)]
    if not bilingual:
        return translated
    translated[0] = continuation + rows[0]
    tail = lines[-1]
    if not tail.endswith(("\\", "  ")):
        tail += "  "
    return [*lines[:-1], tail, *translated]


def _markdown_language_samples(
    lines: list[str], frozen: set[int], groups: dict[int, int], references: set[str], cancel: Event,
) -> Iterator[str]:
    last_group_end = -1
    for index, line in enumerate(lines):
        _check_cancel(cancel)
        if index in frozen or index <= last_group_end:
            continue
        if index in groups:
            last_group_end = groups[index]
            body = "\n".join(_split_markdown_prefix(row)[1] for row in lines[index:last_group_end + 1])
            heading = False
        else:
            _, body, _, heading = _split_markdown_prefix(line)
        prepared, spans = _markdown_inline(body, references, closing_heading=heading)
        yield _natural_text(prepared, spans)


def _translate_markdown(
    text: str, bind_translate: BindTranslator, cancel: Event, bilingual: bool, report: Callable[[int, int], None]
) -> str:
    lines = text.split("\n")
    frozen, tables, setext, references = _markdown_blocks(lines)
    groups = _markdown_multiline_groups(lines, frozen | tables, references, cancel)
    translate = bind_translate(_markdown_language_samples(lines, frozen, groups, references, cancel))
    output = []
    last_group_end = -1
    for index, line in enumerate(lines):
        _check_cancel(cancel)
        if index <= last_group_end:
            continue
        if index in groups:
            last_group_end = groups[index]
            output.extend(_translate_markdown_group(lines[index:last_group_end + 1], references, translate, cancel, bilingual))
            report(last_group_end + 1, len(lines))
            continue
        prefix, body, continuation, heading = _split_markdown_prefix(line)
        if index in frozen or not body.strip():
            output.append(line)
        elif index in tables:
            output.append(_translate_markdown_table_row(line, references, translate, cancel, bilingual))
        else:
            transformed = _translate_markdown_line(line, references, translate, cancel)
            if bilingual and transformed != line:
                if heading:
                    output.append(line)
                elif index in setext:
                    transformed = line.rstrip() + "<br>" + transformed[len(prefix):].lstrip()
                else:
                    output.append(line if line.endswith("\\") or line.endswith("  ") else line + "  ")
                    transformed = continuation + transformed[len(prefix):]
            output.append(transformed)
        report(index + 1, len(lines))
    return "\n".join(output)


def _subtitle_blocks(
    blocks: list[str], vtt: bool, cancel: Event,
) -> Iterator[tuple[int, list[str], int | None]]:
    timing_pattern = _VTT_TIMING if vtt else _SRT_TIMING
    for index, block in enumerate(blocks):
        _check_cancel(cancel)
        if not block.strip() or (vtt and index == 0):
            continue
        lines = block.split("\n")
        if vtt and (lines[0].strip() in {"STYLE", "REGION"} or _VTT_NOTE.match(lines[0])):
            continue
        timing = next((number for number, row in enumerate(lines[:2]) if timing_pattern.fullmatch(row)), None)
        yield index, lines, timing


def _subtitle_language_samples(blocks: list[str], vtt: bool, cancel: Event) -> Iterator[str]:
    for _, lines, timing in _subtitle_blocks(blocks, vtt, cancel):
        if timing is not None:
            yield _CUE_MARKUP.sub(" ", "\n".join(lines[timing + 1:]))


def _translate_subtitle(
    text: str,
    bind_translate: BindTranslator,
    cancel: Event,
    bilingual: bool,
    vtt: bool,
    report: Callable[[int, int], None],
) -> tuple[str, list[str]]:
    chunks = _BLOCK_SEPARATOR.split(text)
    blocks = chunks[::2]
    if vtt and not _VTT_HEADER.fullmatch(blocks[0].split("\n", 1)[0]):
        raise ValueError("VTT 文件缺少 WEBVTT 文件头。")
    translate = bind_translate(_subtitle_language_samples(blocks, vtt, cancel))
    cue_count = 0
    malformed = []
    for index, lines, timing in _subtitle_blocks(blocks, vtt, cancel):
        report(index, len(blocks))
        if timing is None:
            malformed.append(index + 1)
            continue
        cue_count += 1
        if len(lines) <= timing + 1:
            continue
        cue = "\n".join(lines[timing + 1:])
        trailing = "\n" if cue.endswith("\n") else ""
        if trailing:
            cue = cue[:-1]
        spans = [match.span() for match in _CUE_MARKUP.finditer(cue)]
        transformed = _protected_translation(cue, spans, translate, cancel)
        if bilingual and transformed != cue:
            if vtt:
                transformed = _INLINE_TIMESTAMP.sub("", transformed)
                voice = _VTT_VOICE.match(cue)
                if voice and "</v>" not in cue:
                    transformed = transformed.removeprefix(voice[0])
            translated_lines = [line for line in transformed.split("\n") if line.strip()]
            transformed = "\n".join([cue, *translated_lines])
        elif transformed != cue:
            transformed = "\n".join(line for line in transformed.split("\n") if line.strip())
        chunks[index * 2] = "\n".join(lines[:timing + 1]) + "\n" + transformed + trailing

    report(len(blocks), len(blocks))
    if cue_count == 0:
        raise ValueError("文件中没有可识别的字幕时间段。")
    warning = f"第 {'、'.join(map(str, malformed))} 个字幕块没有可识别的时间轴，已按原样保留。"
    return "".join(chunks), [warning] if malformed else []


def _word_text_nodes(paragraph: Any, qname: Callable[[str], str]) -> Iterator[Any]:
    atomic = {qname(name) for name in ("tab", "br", "cr", "drawing", "object", "fldChar", "instrText")}
    paragraph_tag, text_tag = qname("p"), qname("t")
    for child in paragraph:
        if child.tag == text_tag:
            yield child
        elif child.tag in atomic:
            yield None
        elif child.tag != paragraph_tag:
            yield from _word_text_nodes(child, qname)


def _word_groups(paragraph: Any, qname: Callable[[str], str]) -> list[list[Any]]:
    groups = []
    current = []
    for node in _word_text_nodes(paragraph, qname):
        if node is None and current:
            groups.append(current)
            current = []
        elif node is not None and node.text:
            current.append(node)
    if current:
        groups.append(current)
    return groups


def _set_word_text(node: Any, value: str) -> None:
    node.text = value
    if value != value.strip():
        node.set(_XML_SPACE, "preserve")


def _replace_word_group(nodes: list[Any], original: str, value: str) -> None:
    if len(nodes) == 1:
        _set_word_text(nodes[0], value)
        return
    boundaries = [index for index in range(1, len(value))
                  if value[index - 1].isspace() or value[index].isspace()]
    position, original_position = 0, 0
    for node in nodes[:-1]:
        original_position += len(node.text)
        proportional = round(original_position * len(value) / len(original))
        nearby = [index for index in boundaries if index >= position and abs(index - proportional) <= 8]
        stop = min(nearby, key=lambda index: abs(index - proportional)) if nearby else max(position, proportional)
        _set_word_text(node, value[position:stop])
        position = stop
    _set_word_text(nodes[-1], value[position:])


def _word_language_samples(
    paragraphs: list[Any], namespace: str, cancel: Event,
) -> Iterator[str]:
    def qname(name: str) -> str:
        return f"{{{namespace}}}{name}"

    for paragraph in paragraphs:
        _check_cancel(cancel)
        for group in _word_groups(paragraph, qname):
            yield "".join(node.text for node in group)


def _translate_docx(
    path: Path, output: Path, bind_translate: BindTranslator, cancel: Event, bilingual: bool,
    report: Callable[[int, int], None],
) -> None:
    from lxml import etree

    parser = etree.XMLParser(resolve_entities=False, no_network=True)
    with zipfile.ZipFile(path) as source:
        parts = {}
        total = 0
        for item in source.infolist():
            _check_cancel(cancel)
            if _WORD_XML.fullmatch(item.filename):
                element = etree.fromstring(source.read(item), parser)
                namespace = etree.QName(element).namespace
                if namespace not in {_WORD_NAMESPACE, _WORD_STRICT_NAMESPACE}:
                    continue
                paragraphs = list(element.iter(f"{{{namespace}}}p"))
                parts[item.filename] = (element, namespace, paragraphs)
                total += len(paragraphs)
        if "word/document.xml" not in parts:
            raise ValueError("DOCX 文件缺少可识别的 Word 正文。")
        _, namespace, paragraphs = parts["word/document.xml"]
        translate = bind_translate(_word_language_samples(paragraphs, namespace, cancel))
        current = 0
        with zipfile.ZipFile(output, "w") as target:
            for item in source.infolist():
                _check_cancel(cancel)
                if item.filename not in parts:
                    target.writestr(item, source.read(item))
                    continue
                element, namespace, paragraphs = parts[item.filename]

                def qname(name: str) -> str:
                    return f"{{{namespace}}}{name}"

                for paragraph in paragraphs:
                    _check_cancel(cancel)
                    groups = _word_groups(paragraph, qname)
                    originals = ["".join(node.text for node in group) for group in groups]
                    translated = [_translate_text(value, translate, cancel) for value in originals]
                    if bilingual and originals != translated:
                        run = etree.SubElement(paragraph, qname("r"))
                        for value in translated:
                            etree.SubElement(run, qname("br"))
                            _set_word_text(etree.SubElement(run, qname("t")), value)
                    elif not bilingual:
                        for group, original, value in zip(groups, originals, translated):
                            if value != original:
                                _replace_word_group(group, original, value)
                    current += 1
                    report(current, total)
                target.writestr(item, etree.tostring(element, encoding="UTF-8", xml_declaration=True, standalone=True))


def _pdf_paragraph(text: str) -> str:
    lines = [line.strip() for line in text.splitlines() if line.strip()]
    paragraph = ""
    for line in lines:
        if paragraph and not re.search(r"[\u3000-\u9fff]$", paragraph) and not re.match(r"[\u3000-\u9fff]", line):
            paragraph += " "
        paragraph += line
    return paragraph


def _pdf_page_paragraphs(source: Any, cancel: Event) -> Iterator[list[str]]:
    for page in source:
        _check_cancel(cancel)
        paragraphs = (_pdf_paragraph(block[4]) for block in page.get_text("blocks", sort=True) if block[6] == 0)
        yield [paragraph for paragraph in paragraphs if paragraph]


def _translate_pdf(
    path: Path, output: Path, bind_translate: BindTranslator, cancel: Event, bilingual: bool,
    report: Callable[[int, int], None],
) -> list[str]:
    import fitz
    from docx import Document

    document = Document()
    empty_pages = []
    with fitz.open(path) as source:
        if not source.is_pdf:
            raise ValueError("该文件无法识别为 PDF。")
        if source.needs_pass:
            raise ValueError("PDF 受密码保护。请先在 PDF 阅读器中另存一份解锁版本。")
        sample_pages, pages = tee(_pdf_page_paragraphs(source, cancel))
        translate = bind_translate(paragraph for page in sample_pages for paragraph in page)
        del sample_pages
        for page_number, paragraphs in enumerate(pages, 1):
            _check_cancel(cancel)
            if page_number > 1:
                document.add_page_break()
            if not paragraphs:
                empty_pages.append(page_number)
                document.add_paragraph(f"第 {page_number} 页无可提取文字。")
            for paragraph in paragraphs:
                transformed = _translate_text(paragraph, translate, cancel)
                if bilingual and paragraph != transformed:
                    document.add_paragraph(paragraph)
                document.add_paragraph(transformed)
            report(page_number, len(source))
        if empty_pages and len(empty_pages) == len(source):
            raise ValueError(f"PDF 的第 {'、'.join(map(str, empty_pages))} 页均无可提取文字。")
    document.save(output)
    if empty_pages:
        return [f"PDF 第 {'、'.join(map(str, empty_pages))} 页无可提取文字，Word 文档中已标记。"]
    return []


def _collect_jobs(paths: list[str], output_root: Path | None, cancel: Event) -> list[_Job]:
    jobs, seen = [], set()

    def add(path: Path, error: str | None = None) -> None:
        if path not in seen:
            seen.add(path)
            jobs.append(_Job(path, error))

    for selected in paths:
        _check_cancel(cancel)
        path = Path(selected).expanduser()
        try:
            path = path.resolve()
            if not path.is_dir():
                add(path)
                continue
            def on_error(error: OSError) -> None:
                add(Path(error.filename) if error.filename else path, str(error))

            for parent, directories, files in os.walk(path, onerror=on_error):
                _check_cancel(cancel)
                directory = Path(parent)
                directories[:] = sorted(
                    item.name for item in (directory / entry for entry in directories)
                    if not item.is_symlink() and not item.is_junction()
                )
                for file in sorted(files):
                    _check_cancel(cancel)
                    item = directory / file
                    if item.suffix.lower() in SUPPORTED_SUFFIXES and not item.is_symlink():
                        add(item.resolve())
        except (OSError, RuntimeError, ValueError) as error:
            add(path, str(error))
    return jobs


def _publish_output(
    temporary: Path, source: Path, output: Path, output_name: str | None, target_lang: str,
    inputs: set[Path], cancel: Event,
) -> Path:
    suffix = _output_extension(source)
    stem = source.stem if output_name is None else output_name
    language = ""
    if output_name is None:
        code = "cn" if target_lang == "zh" else LANGUAGES[target_lang].code
        language = "_" + code
    increment = 1
    while True:
        _check_cancel(cancel)
        number = f"_{increment}" if increment > 1 else ""
        ending = number + language + suffix
        available = 2 * _MAX_FILENAME_LENGTH - len(ending.encode("utf-16-le"))
        shortened = stem.encode("utf-16-le")[:available].decode("utf-16-le", errors="ignore")
        destination = output / (shortened + ending)
        if destination in inputs or os.path.lexists(destination):
            increment += 1
            continue
        try:
            if os.name == "nt":
                temporary.rename(destination)
            else:
                os.link(temporary, destination)
            return destination
        except FileExistsError:
            increment += 1
        except PermissionError:
            # Windows can report access denied when the competing entry is a directory.
            if not os.path.lexists(destination):
                raise
            increment += 1


def _process_file(
    path: Path,
    output: Path,
    bind_translate: BindTranslator,
    cancel: Event,
    bilingual: bool,
    report: Callable[[int, int], None],
) -> list[str]:
    suffix = path.suffix.lower()
    if suffix == ".docx":
        _translate_docx(path, output, bind_translate, cancel, bilingual, report)
        return []
    if suffix == ".pdf":
        return _translate_pdf(path, output, bind_translate, cancel, bilingual, report)

    text, original = _read_text(path)
    if suffix in {".md", ".markdown"}:
        value = _translate_markdown(text, bind_translate, cancel, bilingual, report)
    elif suffix == ".txt":
        translate = bind_translate(text.split("\n"))
        value = _translate_plain_text(text, translate, cancel, bilingual, report)
    else:
        value, warnings = _translate_subtitle(text, bind_translate, cancel, bilingual, suffix == ".vtt", report)
        _write_text(output, value, original, vtt=suffix == ".vtt")
        return warnings
    _write_text(output, value, original)
    return []


def run_batch(
    paths: list[str],
    model_id: str,
    source_lang: str,
    target_lang: str,
    bilingual: bool,
    output_dir: str | None,
    translate: Translator | None,
    cancel: Event,
    progress: Progress,
    *,
    output_names: dict[str, str] | None = None,
    translator_factory: TranslatorFactory | None = None,
    models=BUILTIN_MODELS,
) -> None:
    """Process files in order and emit progress events with absolute paths."""
    if translate is None and translator_factory is None:
        raise ValueError("缺少文件翻译服务。")
    language_model = model_id if model_id in models else None
    if language_model is not None:
        source_lang, target_lang = validate_languages(language_model, source_lang, target_lang, models=models)
    output_root, names = validate_output_options(output_dir or None, output_names)
    if output_root is not None:
        output_root = output_root.resolve()
    names = {path.resolve(): name for path, name in names.items()}
    total = completed = failed = 0
    try:
        entries = _collect_jobs(paths, output_root, cancel)
        total = len(entries)
        inputs = {entry.path for entry in entries}
        progress({"type": "start", "total": total})
        for entry in entries:
            _check_cancel(cancel)
            path = entry.path
            progress({"type": "file_start", "path": str(path)})
            last_percent = -1
            effective_target = target_lang

            def bind_translate(samples: Iterable[str]) -> Translator:
                nonlocal effective_target
                sample = _language_sample(samples, cancel) if source_lang == "auto" else ""
                if source_lang == "auto" and target_lang != "auto" and not sample:
                    return lambda text: text
                effective_source, effective_target = resolve_languages(
                    sample, source_lang, target_lang, language_model, models=models,
                )
                if translator_factory is not None:
                    return translator_factory(effective_source, effective_target)
                assert translate is not None
                return translate

            def report(current: int, total: int) -> None:
                nonlocal last_percent
                _check_cancel(cancel)
                percent = current * 100 // max(total, 1)
                if percent > last_percent:
                    last_percent = percent
                    progress({"type": "file_progress", "path": str(path), "current": current, "total": total})

            try:
                if entry.error:
                    raise ValueError(entry.error)
                if not path.is_file():
                    raise ValueError("无法读取该文件，请检查文件是否仍在原位置。")
                if path.suffix.lower() not in SUPPORTED_SUFFIXES:
                    raise ValueError(f"不支持 {path.suffix or '此'} 文件格式。")
                directory = output_root if output_root is not None else path.parent
                directory.mkdir(parents=True, exist_ok=True)
                descriptor, temporary_name = tempfile.mkstemp(prefix=".translate-", suffix=".tmp", dir=directory)
                os.close(descriptor)
                temporary = Path(temporary_name)
                try:
                    warnings = _process_file(path, temporary, bind_translate, cancel, bilingual, report)
                    output = _publish_output(temporary, path, directory, names.get(path), effective_target, inputs, cancel)
                finally:
                    temporary.unlink(missing_ok=True)
            except _Cancelled:
                raise
            except Exception as error:
                _check_cancel(cancel)
                failed += 1
                progress({"type": "file_error", "path": str(path), "error": str(error) or type(error).__name__})
            else:
                completed += 1
                progress({"type": "file_done", "path": str(path), "output": str(output), "warnings": warnings})
    except _Cancelled:
        pass
    progress({"type": "finish", "total": total, "completed": completed, "failed": failed, "cancelled": cancel.is_set()})
