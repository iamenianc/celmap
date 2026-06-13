# Repository Helper Scripts

This directory contains helper scripts for managing synonyms and configurations.

## Synonyms Exporter

Exports the synonym groups from `src/CelMap.Core/synonyms.json` to a clean, human-readable Markdown table (`synonyms_review.md` in the repository root) for manual verification.

### How to Run

Run the native PowerShell script:
```powershell
.\scripts\export_synonyms.ps1
```
No external runtime or dependency (such as Node.js or Python) is required.
