# Changelog

## 0.1.0-alpha.3

- Added opt-in personal loot and guild regions using the shared Windows OCR worker.
- Added a separate SQLite profile, manual loot sessions and desktop session summaries.
- Added replay checks for ambiguous feeds, stale commands and saved history.

## 0.1.0-alpha.2

- Added opt-in Windows OCR capture for a configured durability region, with two matching readings required before updating the bridge.
- Added a Windows tray and a separate Avalonia window for craft status and cached recipe quotes.
- Added bounded browser market imports and region-scoped recipe quote lookups.
- Updated Windows packaging to include the OCR worker and desktop window.

## 0.1.0-alpha.1

- Initial Windows x64 Native AOT bridge prototype for the rc.29 browser planner.
- Health, sample durability and acknowledgment endpoints.
