# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

See also `AGENTS.md` for project conventions (comment language/style, task workflow) — those rules apply equally here.

## What this is

LogViewer is a Windows desktop viewer for NLog, log4net, and log4j logs (WPF, .NET Framework 4.8, GPL-3.0). It receives Chainsaw/NLogViewer XML over UDP/TCP, imports text files and archives, and holds a large in-memory buffer that can be filtered, searched, and navigated live. UI is localized in Russian and English.

All source lives under `src/`; `src/LogViewer.sln` is the solution.

## Build, test, run

This is a legacy WPF csproj — `dotnet build`/`dotnet test` cannot compile it. Use MSBuild/vstest (as CI does), typically via Visual Studio, or:

```
nuget restore src/LogViewer.sln
msbuild src/LogViewer.sln -p:Configuration=Release -p:TreatWarningsAsErrors=true
vstest.console.exe src/LogViewer.Core.Tests/bin/Release/net48/LogViewer.Core.Tests.dll src/LogViewer.App.Tests/bin/Release/net48/LogViewer.App.Tests.dll
```

- `LogViewer.Core.Tests`: NUnit, Core-only (parsers, filter, Don't Receive, batch flush). No WPF/Dispatcher.
- `LogViewer.App.Tests`: NUnit, STA, references the WPF host — tests FilterCoordinator and child VMs, some hosting a real (off-screen) `ListView`.
- Run a single test: add `/Tests:FullyQualifiedClassName.MethodName` (or `/TestCaseFilter:"Name~Foo"`) to the `vstest.console.exe` line above.
- Package Reference projects (`LogViewer.Core`, both test projects) restore via `nuget`/`dotnet restore`; the WPF host (`LogViewer.csproj`) uses classic `packages.config`. `nuget restore src/LogViewer.sln` handles both.
- `build/stage.ps1` packages a Release build into a zip-able folder (used by both CI build and release workflows) — copies the whole output folder (English strings are a satellite assembly in `en/`), minus symbols/XML docs.

## Architecture

Two assemblies, enforced by project boundaries (see `AGENTS.md`):

- **`LogViewer.Core`** — framework-agnostic domain/services. No WPF, no `Dispatcher`, no UI types. Log parsing (`XmlLogParser`, `TemplateLogParser`), network sources (`UdpLogSource`, `TcpLogSource`), the in-memory buffer and filtering (`LogSession`, `LogFilter`, `FilterCriteria`), session persistence (`SavedSessionXmlStore`), presets (`FilterPresetXmlStore`), message grouping/highlighting, etc. `LogProcessingService` is the facade over session + parsing + sources.
- **WPF host (`LogViewer.csproj`, root namespace)** — MVVM UI: `MVVM/ViewModels`, `MVVM/Views` (XAML), `MVVM/Models`, `Services/Wpf` (WPF implementations of Core-facing interfaces like `IClipboardService`, `IFileDialogService`), `Services/Updates` (GitHub Releases auto-update), `Adapters/` (bridge Core domain types to UI-facing view models), `Converters/`, `Helpers/`, `Validations/`.

Key seam: **`Adapters/CoreToUiAdapter`** sits between `LogProcessingService`/`LogSession` (Core, background threads) and the UI. It batches high-frequency `EntryProcessed` events (`LogEntryBatcher`) so a UDP storm doesn't flood the UI thread with one `Post` per packet, while keeping `SessionCleared`/`EntriesRemoved`/`FilteredViewUpdated` as ordered one-shot UI-thread events. Refiltering (criteria change, bulk import) runs on the thread pool with a generation counter so a newer refilter request supersedes and cancels a stale in-flight one — don't read `CoreToUiAdapter`'s remarks block, read it before changing anything on this path.

`LogMessage` (`MVVM/Models/LogMessage.cs`) is a thin view over Core's `LogEntry`, not a copy — added to avoid per-row allocation in the UI list (see recent commit "Make LogMessage a view over LogEntry").

Settings persist to `settings.xml` next to the executable; sessions save to `.lvs`/`.lvs.gz` (gzip) via `SavedSessionXmlStore`; filter presets to `filter_presets.xml`. None of these write to Documents implicitly.

## Conventions beyond AGENTS.md

- New domain/service logic belongs in `LogViewer.Core`, with a Core-only NUnit test in `LogViewer.Core.Tests`. Only add to `LogViewer.App.Tests` when the behavior genuinely depends on WPF/Dispatcher/STA.
- `LangVersion` is pinned (7.3 in `LogViewer.Core.Tests`) — match existing language-feature usage rather than reaching for newer C# syntax.
- CI builds with `-p:TreatWarningsAsErrors=true`; keep changes warning-clean.
