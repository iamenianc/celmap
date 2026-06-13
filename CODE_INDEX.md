# CelMap Codebase Index

**LLMs: Read this file first to understand the project structure.**

## Project Overview
CelMap is an Excel Column Mapper utility. It matches columns between two Excel files and copies values verbatim.

## Structure
- `src/CelMap.Core`: The core mapping engine (Class Library).
  - `ColumnMatcher.cs`: Fuzzy matching logic.
  - `WorkbookReader.cs` & `TargetWriter.cs`: Excel I/O via ClosedXML.
  - `AliasRules.cs` & `QualifiedRules.cs`: Domain-specific rule engines.
  - `synonyms.json` & `qualified_rules.json`: Config files for alias/qualified rules.
- `src/CelMap.App`: The WPF Desktop UI.
  - `MainWindow.xaml` & `MainViewModel.cs`: Core UI and view model.
- `src/CelMap.Cli`: A thin command-line interface.
- `tests/CelMap.Core.Tests`: Unit tests for the engine.

## Guidelines
- Code should be written for LLM token efficiency (maximized density, file-scoped namespaces, expression-bodied members).
- Keep UI (`CelMap.App`) and Core (`CelMap.Core`) strictly separated.
