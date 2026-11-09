# Calcpad.Avalonia

Cross-platform desktop front-end for the CalcpadCE engine, built with
[Avalonia UI](https://avaloniaui.net/). It is the path toward a native **macOS** (and Linux)
CalcpadCE app: the engine (`Calcpad.Core`, `Calcpad.OpenXml`) is already portable .NET — only
the UI shell is reimplemented here (the main app, `Calcpad.Wpf`, is Windows-only WPF).

## Features

- **AvaloniaEdit** code editor with Calcpad syntax highlighting (reuses the WPF `.xshd`).
- **Live preview** rendered by the same pipeline as the CLI, shown inline in an embedded
  Chromium web view ([WebViewControl-Avalonia](https://www.nuget.org/packages/WebViewControl-Avalonia/),
  CefGlue — free and cross-platform).
- **Keypad** (Numbers / Operators / Functions / Greek). The `=` key is markdown-aware
  (inserts `:=` in `#md full` mode).
- **Mode-aware snippets** menu, sharing the single catalogue with the WPF app / AI assistant
  (`Calcpad.AI/CalcpadSnippets.cs`).
- **File**: New / Open / Save / Save As (`.cpd`).
- **Export**: HTML, Word (`.docx`, via `Calcpad.OpenXml`), and Print/PDF (opens the styled page
  in the system browser — its Print dialog offers *Save as PDF* on every OS).

## Build & run

Requires the .NET 10 SDK.

```sh
dotnet build Calcpad.Avalonia
dotnet run --project Calcpad.Avalonia
```

The same commands work on Windows, macOS and Linux. On first launch the embedded Chromium
runtime is initialized (CefGlue binaries are restored as NuGet dependencies).

CI builds this project on both `windows-latest` and `macos-latest`
(`.github/workflows/build-avalonia.yml`).

## Status & limitations

- This is a **work in progress** toward feature parity with the WPF app; it is **not yet
  verified running on a Mac** (only Windows-verified so far — CI proves it *compiles* on macOS).
- **PDF** export goes through the browser's print dialog rather than a bundled converter
  (the WPF app ships `wkhtmltopdf.exe`, which is Windows-only).
- The **AI assistant** (`Calcpad.AI`) and the interactive `#input` forms are not ported yet.
- The embedded Chromium adds significant size to the published app.
