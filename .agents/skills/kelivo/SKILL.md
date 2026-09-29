---
name: kelivo
description: Map the Flutter chat client for the F# rewrite.
version: 0.1.0
author: Hermes
metadata:
  hermes:
    tags: [FSharp, Avalonia, Flutter, ChatClient, Rewrite]
---

# Kelivo: Reference Map for the F#/Avalonia Rewrite

This skill maps the `kelivo/` source tree (a Flutter LLM chat client, Material You design inspired by RikkaHub) as the reference for the rewrite mandated by the wanxiang SSOT (`skill://wanxiang`): F# + Avalonia, agent-framework as the Agent layer, UI basically unchanged, multi-platform. It is a read-and-port guide — it inventories screens, data models, services, and platform layers so nothing in the original is lost, and tells you where each piece lives. It does NOT cover agent-framework wiring (see the `agent-framework` skill) or Avalonia itself; it is source-code reading only (Flutter SDK is needed only if you want to run the original).

## When to Use

- Porting any kelivo screen/feature to the F#/Avalonia app ("keep the UI unchanged")
- "What did kelivo do for X?" — search, TTS, MCP tools, backup, message editing, token display, etc.
- Porting the data model (conversations, messages, assistants, provider configs)
- Checking platform behaviors (desktop tray/hotkeys, mobile background generation)
- Deciding what the new app must ship to be feature-parity with kelivo

## Prerequisites

- The repo at `/home/kunweiz/Desktop/vibe/wanxiang/kelivo/` (source of truth; PRODUCT.md states the product brief, `docx/screenshot_*.png` show the reference UI).
- Nothing else strictly: the workflow is `read_file`/`search_files` over the tree. Flutter SDK is only required to run the original (`flutter run -d <linux|windows|macos>`), which is optional.

## How to Run

Invoke through the `terminal` tool only to build/run the original:

```bash
cd /home/kunweiz/Desktop/vibe/wanxiang/kelivo && flutter run -d linux
```

All exploration and porting reads go through `read_file` (single files) and `search_files` (feature/class discovery), e.g. `search_files(pattern='*.dart', target='files', path='kelivo/lib/features/chat')`.

## Quick Reference

Platform targets: Android, iOS, Windows, macOS, Linux (PRODUCT.md also names Harmony — kelivo-ohos is a separate fork, out of scope for .NET). License: AGPL-3.0. UI language: English + Chinese (zh, zh_Hans, zh_Hant) via `lib/l10n/*.arb`.

Feature inventory (`lib/features/`, 17 folders — the parity checklist): assistant, backup, chat, home, instruction_injection, mcp, migration, model, provider, quick_phrase, scan, search, settings, stats, translate, world_book.

lib/ map:
- `core/models/` (19 files) — Hive models: chat_message, conversation, assistant, assistant_memory, assistant_regex, assistant_tag, api_keys, provider_group, model_types, chat_item, chat_input_data, preset_message, quick_phrase, token_usage, world_book, instruction_injection, backup.
- `core/database/` — Hive→SQLite migration in flight: `app_database.dart` (drift), `business_repository.dart`, `business_preferences.dart`, `business_settings_router.dart`, `business_startup_gate.dart`, `chat_database_gateway.dart`, `chat_database_repository.dart`, `chat_database_observer.dart`, `database_installation_gate.dart`, `business_migration_engine.dart`, restore services.
- `core/services/` — `api/` (chat_api_service, builtin_tools, gemini_tool_config, google_service_account_auth, provider_request_headers, providers/), `chat/` (chat_service, prompt_transformer, document_text_extractor), `network/` (dio_http_client, request_logger), plus api_key_manager, model_override_resolver, memory_store, tts/, search/, storage/, backup/, mcp/ (mcp_tool_service), notification_service, provider_balance_service, quick_phrase_store, world_book_store, logging/.
- `core/providers/` (state, provider-package ChangeNotifiers) — assistant, settings, user, model, mcp, tts, tag, update, quick_phrase, instruction_injection(+group), world_book, memory, backup, s3_backup, backup_reminder, hotkey.
- `features/chat/` — pages: chat_history_page, message_edit_page, select_copy_page, html_preview_page, image_viewer_page; widgets: chat_message_widget, chat_suggestion_bubbles, bottom_tools_sheet, citation_sources_sheet, context_management_sheet, image_preview_sheet, message_edit_sheet, message_export_sheet, message_more_sheet, reasoning_budget_sheet, select_copy_sheet, token_detail_popup, token_display_widget.
- `features/home/` — home_page + home_desktop_layout / home_mobile_layout (responsive split), ask_user_interaction_service, tool_approval_service.
- `desktop/` — desktop_home_page, desktop_window_controller, desktop_tray_controller, hotkeys/, setting/, widgets/ (bitsdojo_window + window_manager + tray_manager).
- `theme/` — theme_factory, palettes, dynamic_color (Material You).
- `features/settings/pages/` — settings, display, theme, image, tts (+ tts_services), network_proxy, google_fonts_picker, storage_space, about, debug, log_viewer, more, sponsor.
- Other pages: providers_page + provider_detail/balance/network/groups + multi_key_manager, default_model_page, stats_page, translate_page, world_book_page, scan (mobile_scanner).

API providers (`core/services/api/providers/`): openai_chat_completions, openai_responses, openai_images, claude_official, google_gemini, google_vertex, google_common, openai_common. Web search engines: Bing, DuckDuckGo, Exa, Tavily, Zhipu, LinkUp, Brave, Metaso, SearXNG, Ollama, Jina, Perplexity, Bocha, Serper, Grok.

## Procedure

1. Orient: read `PRODUCT.md` (product brief + design principles: conversation-primary UI, Material 3 vocabulary, light/dark + dynamic color, localization, reduced motion), `README.md` (feature list), and `docx/screenshot_*.png` for the reference look. SSOT constraint (`skill://wanxiang`): UI stays basically unchanged, so screenshots + feature folders are the contract.
2. Inventory screens for parity: run `search_files(pattern='*.dart', target='files', path='kelivo/lib/features')`, then for each feature folder read its `pages/` (top-level screens) and `widgets/` (components) — these map 1:1 to Avalonia views/controls.
3. Port the data model first — it is the contract between UI and agent layer. Read `core/models/chat_message.dart` and `conversation.dart` fully; port every field (see Pitfalls for the subtle ones). Other models in `core/models/` follow the same Hive-annotation shape (typeId, HiveField N).
4. Port persistence: the F# app should target the drift SQLite schema (`core/database/app_database.dart`, `drift_schemas/app_database/`), NOT the legacy Hive boxes — the Flutter app itself is mid-migration from Hive to SQLite (`features/migration/hive_to_sqlite_migration_*.dart`), so SQLite is the forward-compatible shape. Keep backup/restore (`services/backup/`, restore receipt/lease/pruner) since data portability is a PRODUCT.md principle.
5. Port the service layer: `services/api/` (provider adapters + request headers + API key manager), `services/chat/chat_service.dart` (generation loop — this is where agent-framework replaces direct provider calls), `services/mcp/mcp_tool_service.dart` + builtin tools, `services/search/` (web search engines), `services/tts/`, `services/storage/`. Map `core/providers/` ChangeNotifiers to whatever state mechanism the F# app uses; they are view-model-equivalent.
6. Port platform layers: `desktop/` (window controls, tray, hotkeys, background behaviors) for Windows/macOS/Linux; `services/android_background.dart` / `ios_background_generation.dart` / `notification_service.dart` for mobile. Avalonia covers the desktop trio; mobile per PRODUCT.md targets Android/iOS.
7. Port theming + l10n: `theme/theme_factory.dart` + `palettes.dart` + dynamic_color give the Material You theming contract (Avalonia has no Material You — recreate light/dark palettes and dynamic-color behavior manually); `lib/l10n/app_*.arb` are the string tables (en, zh, zh_Hans, zh_Hant).

## Pitfalls

- Message versioning is a distinctive kelivo feature, easy to lose: `ChatMessage.groupId` + `version` (0-based, increments on regeneration) and `Conversation.versionSelections` (groupId -> chosen version) — regenerate/rollback UI depends on it. Port it.
- `Conversation.messageIds` is an ordered list (display order); `truncateIndex` (-1 = none) marks context truncation; `assistantId` null = global/default conversation; `mcpServerIds` is per-conversation MCP enablement. All must survive the port.
- `ChatMessage.role` is the string `'user'` / `'assistant'`; token accounting is split across `totalTokens`, `promptTokens`, `completionTokens`, `cachedTokens`; reasoning has `reasoningText`, `reasoningSegmentsJson`, `reasoningStartAt/FinishedAt`; `translation` holds translated content. The token/reasoning/translation UI sheets in `features/chat/widgets/` depend on these fields.
- The data layer is mid-migration: Hive models and drift tables coexist (`app_database.g.dart`, `chat_message.g.dart`, `conversation.g.dart` are generated). For the rewrite, model the drift SQLite schema, not the Hive annotations.
- Don't copy Flutter-only packages 1:1 — syncfusion (PDF), mobile_scanner, flutter_math_fork (LaTeX), webview_flutter, easy_image_viewer, gpt_markdown, highlight all need Avalonia/.NET equivalents; keep the behavior, not the dependency.
- `docx/` also contains document-generation files (docx/ dir name is coincidental, screenshots live there); `assets/` has app_icon.png.
- Harmony OS support is a separate fork (kelivo-ohos) — do not treat it as part of this repo's contract.
- License is AGPL-3.0 — the rewrite must keep compatible licensing.
- This skill covers the kelivo reference only; agent-framework integration and Avalonia UI work are covered by their own skills.

## Verification

Confirm parity: enumerate kelivo's 17 feature folders with `search_files(pattern='*', target='files', path='kelivo/lib/features')` and check each has a counterpart screen/module in the F# app; for the data model, verify every field of `ChatMessage` and `Conversation` (from `read_file` on `core/models/chat_message.dart` and `conversation.dart`) exists in the ported F# records/classes.
