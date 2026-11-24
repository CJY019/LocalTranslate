# Interface languages

Open **Settings → Interface language** to change the desktop app immediately. **Use system language** selects Simplified Chinese for a Chinese Windows display language and English for other display languages. Both are built into the executable. The translation source and target selectors are separate settings.

Choose **Français** to download the French interface from GitHub. After a successful download it works offline, including after restarting the app. A failed download leaves the active interface and the saved preference in place. If a saved pack is missing or damaged at startup, the app uses its system-language default and shows the reason in Settings.

On a portable installation in `D:\Translate`, the preference is in `D:\Translate\settings.json` and the verified French cache for v1.0.1 is in `D:\Translate\cache\langpacks\v1.0.1\fr-FR.json`. These locations follow the application directory. The language request downloads the selected JSON and sends no text, documents, chat messages, model settings, or file names to GitHub.

## Create a language pack

1. Copy `langpacks/builtin/en-US.json` to `langpacks/<locale>.json`, for example `langpacks/it-IT.json`. Change the top-level `locale` to the same language tag. Keep `schema` at `1`.
2. Translate every value in `strings` and retain every key. Entries include tabs, accessibility names, model-management actions, statuses, backend diagnostics, and translation-language display names under `lang.*`. Brand names, model IDs and file formats can retain their established spelling.
3. Preserve each numbered placeholder, including its number of occurrences. A sentence may reorder `{0}` and `{1}` to fit its grammar. Placeholders accept only decimal indices from `{0}` through `{9}`. Use ordinary Unicode text and punctuation. Save as UTF-8 without a BOM with LF line endings.
4. Keep the pack within 512 KiB. Every value must be a nonempty string of at most 4,096 UTF-16 code units. Tabs and line breaks are supported. Extra keys, missing keys, duplicate keys, nested objects, arrays, unmatched braces and additional fields are rejected.

The following excerpt illustrates one translated entry. The validator compares the complete pack against the English built-in. [English source](../langpacks/builtin/en-US.json) · [Complete French example](../langpacks/fr-FR.json)

```json
{
  "schema": 1,
  "locale": "it-IT",
  "strings": {
    "status.translated": "Traduzione completata · {0}"
  }
}
```

## Register and verify

Add an entry to the `packs` array in `langpacks/manifest.json`. Use the language’s native name for the selector, a unique locale, and the exact filename `<locale>.json`. A new entry may start with `"bytes": 0` and `"sha256": ""`. The validator fills both from the saved bytes. [Pack manifest](../langpacks/manifest.json)

```powershell
python tools/check_langpacks.py --update-manifest
python tools/check_langpacks.py
./frontend/build.ps1
```

The first command updates the manifest, the second performs a read-only check, and the last embeds the two built-ins and the verified manifest into `LocalTranslate.exe`. External translations stay in the repository. The desktop app always rechecks a downloaded or cached pack’s length, SHA-256, locale, schema, keys and placeholders before using it. It displays resource values as strings. The pack loader never interprets a value as XAML, a binding, a URL to fetch, or executable code.

To exercise an external language offline before publishing, copy its verified JSON into `cache/langpacks/<release>/` next to a **test copy** of the executable. Select that language in Settings, switch back to a built-in and restart. Enter sample text first to check that switching preserves the original, translation and file names.

## Publish with a release

Set `release` in the manifest to the release’s exact version tag, for example `v1.0.0`. Commit the packs and generated manifest together, build the executable from that commit, and publish the matching GitHub tag. The executable constructs the official URL from its embedded catalog.

```text
https://raw.githubusercontent.com/CJY019/LocalTranslate/<release>/langpacks/<locale>.json
```

The first selection of an uncached language downloads that one file. A new or revised translation requires a new tag and a build that embeds its manifest. Preserve previously published tags so older builds keep retrieving the bytes they expect. The tagged file must have the same SHA-256 as its manifest entry.

## Add interface text in the app

Give each new label a stable key in **all** packs. XAML uses `{DynamicResource your.key}`. In C#, `Ui.T("your.key", args)` renders the current language, and `App.Say(status, "your.key", args)` keeps live status text in sync during a language change. Programmatically drawn views can listen to `Ui.Changed`. Pass dynamic values such as file paths as arguments, keeping original text, translated text, and chat content outside the resource dictionary.
