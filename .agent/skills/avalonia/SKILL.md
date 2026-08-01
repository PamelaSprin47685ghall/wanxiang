---
name: avalonia
description: UI framework reference for the F# kelivo rewrite.
version: 0.1.0
author: Hermes
metadata:
  hermes:
    tags: [FSharp, Avalonia, XAML, MVVM, MultiPlatform]
---

# Avalonia in the kelivo Rewrite

This skill maps the vendored `Avalonia/` repo (the Avalonia UI framework source, MIT-licensed, .NET 10 era) as the UI framework for the kelivo rewrite mandated by the wanxiang SSOT (`skill://wanxiang`): F# + Avalonia, UI basically unchanged, multi-platform. It captures the F# project pattern Avalonia itself validates (the in-repo `BuildTests.FSharp` project), the package set, and the sample catalog to crib from. It is NOT an Avalonia tutorial; kelivo's screen inventory lives in the `kelivo` skill and the agent layer in the `agent-framework` skill. The rewrite consumes Avalonia via NuGet; building this repo from source is only needed for framework-level work.

## When to Use

- Creating the F#/Avalonia project skeleton for the kelivo rewrite
- "How do I structure an F# Avalonia app?" (entry point, App/Window/View, XAML loading, MVVM)
- Choosing packages/themes for the app (Fluent vs Simple, desktop backends)
- Finding the right control or pattern (list virtualization, dialogs, rendering) in the samples
- Building or running the vendored framework itself (samples, local NuGet packages)
- Multi-platform build questions (Windows/macOS/Linux; Android/iOS/Browser caveats)

## Prerequisites

- .NET SDK 10.0.201 — the repo pins it in `global.json` (`"version": "10.0.201"`, `rollForward: latestFeature`); older SDKs fail.
- The vendored repo at `/home/kunweiz/Desktop/vibe/wanxiang/Avalonia/` (source of truth; `readme.md` overview, `docs/build.md` build instructions, `docs/nuget.md` local package building).
- For the full solution (mobile/browser): `dotnet workload install android ios tvos maccatalyst wasm-tools` (sudo on Unix). Desktop-only work (`Avalonia.Desktop.slnf`) needs no workloads.
- The rewrite app itself needs no repo build: `dotnet add package Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`.

## How to Run

Invoke through the `terminal` tool. Run the framework's reference sample:

```bash
cd /home/kunweiz/Desktop/vibe/wanxiang/Avalonia/samples/ControlCatalog.Desktop && dotnet run
```

Build the in-repo F# proof that F#+Avalonia works (the pattern to copy):

```bash
cd /home/kunweiz/Desktop/vibe/wanxiang/Avalonia/tests/BuildTests && dotnet build BuildTests.FSharp/BuildTests.FSharp.fsproj -p:AvaloniaVersion=12.0.0
```

For the rewrite project: `dotnet run` from the F# app's own directory.

## Quick Reference

Packages: `Avalonia` (core), `Avalonia.Desktop` (desktop head, pulls platform backends), `Avalonia.Themes.Fluent` / `Avalonia.Themes.Simple` (themes), `Avalonia.Fonts.Inter`, `Avalonia.Diagnostics` (dev tools), `Avalonia.Headless` (tests). Platform backends live in `src/`: `Avalonia.Win32` (Windows), `Avalonia.X11` + `Avalonia.Wayland` + `Avalonia.FreeDesktop` (Linux), `Avalonia.Native` (macOS; see `docs/macos-native.md`), plus `src/Skia`, `src/Browser`, `src/Android`, `src/iOS`.

Core API names (from the F# build test, verbatim): `AppBuilder.Configure<App>()`, `.UseSkia()`, `.UseWin32()`, `.LogToTrace(areas = ...)`, `.StartWithClassicDesktopLifetime(argv)`; `AvaloniaXamlLoader.Load(this)`; `IClassicDesktopStyleApplicationLifetime` with `desktop.MainWindow <- MainWindow()`; XAML root `<Window xmlns="https://github.com/avaloniaui" x:Class="...">`, `<Application.Styles><FluentTheme /></Application.Styles>`, `RequestedThemeVariant="Default"`.

Samples (`samples/`): `ControlCatalog` (control gallery + `ControlCatalog.Desktop` runner), `MiniMvvm` (minimal MVVM: ViewModelBase + MiniCommand), `VirtualizationDemo`, `TextTestApp`, `RenderDemo`, `GpuInterop`, `SingleProjectSandbox`, `AppWithoutLifetime`.

Source layout: `src/Avalonia.Base`, `src/Avalonia.Controls`, `src/Avalonia.Desktop`, `src/Markup` (XAML), `src/Skia`, `src/Themes.*`, platform dirs `src/Windows`, `src/Linux`, `src/Android`, `src/iOS`, `src/Browser`, `src/Headless`. Build tooling: `build.sh`/`build.ps1`/`build.cmd` (Nuke), `nuke CreateNugetPackages`, `nuke --target BuildToNuGetCache` (pushes `9999.0.0-localbuild` into `~/.nuget/packages`).

## Procedure

1. Locate the framework: `Avalonia/readme.md` (overview, license = MIT via `licence.md`), `docs/build.md` (build/run), `docs/nuget.md` (local package builds), `src/` (framework source), `samples/` (reference apps). Use `search_files`/`read_file` before writing new UI code.
2. Scaffold the F# app project. Copy the repo's own `tests/BuildTests/BuildTests.FSharp/BuildTests.FSharp.fsproj` shape: `OutputType=WinExe`, `TargetFramework=net10.0`, `AvaloniaUseCompiledBindingsByDefault=true`, packages `Avalonia`, `Avalonia.Themes.Fluent`, `FSharp.Core` (+ `Avalonia.Win32`/`Avalonia.X11` for desktop backends). Compile order matters: MainView, MainViewModel, MainWindow, App, then Program.
3. Write the entry point (verbatim pattern from `Program.fs`): `[<EntryPoint; STAThread>] let main argv = AppBuilder.Configure<App>().UseSkia().UseWin32().LogToTrace(areas = Array.empty).StartWithClassicDesktopLifetime(argv)`.
4. Define `App` inheriting `Application()`: `Initialize()` calls `AvaloniaXamlLoader.Load(this)`; `OnFrameworkInitializationCompleted()` matches `IClassicDesktopStyleApplicationLifetime` and sets `desktop.MainWindow <- MainWindow()`. Ship an `App.axaml` with `<Application.Styles><FluentTheme /></Application.Styles>` (see `BuildTests/App.axaml`).
5. Views: `Window`/`UserControl` F# subclasses with `do this.InitializeComponent()` → `AvaloniaXamlLoader.Load(this)`; companion `.axaml` declares `x:Class` matching the F# type. Register axaml files as `<AvaloniaXaml Include=...>` and assets as `<AvaloniaResource Include=...>` in the project file (pattern: `tests/BuildTests/IncludeBuildTestsAvaloniaItems.props`).
6. ViewModels: plain F# classes with `member val X = ... with get, set` (see `MainViewModel.fs`) or INotifyPropertyChanged base; `MiniMvvm` sample shows the minimal MVVM base. Bind in XAML with `{Binding ...}` (compiled bindings are on by default).
7. Theming: kelivo's Material You contract (from the `kelivo` skill: light/dark + dynamic color) maps to `FluentTheme` + `RequestedThemeVariant` (`Default`/`Light`/`Dark`); dynamic color needs manual palette work — Avalonia has no Material You equivalent.
8. Multi-platform: `Avalonia.Desktop` covers Windows/macOS/Linux. Mobile (Android/iOS) and Browser targets exist in `src/` and `samples/ControlCatalog.{Android,iOS,Browser,MacCatalyst,tvOS}` but need the extra .NET workloads and have platform caveats — treat desktop as the solid target, verify mobile before committing to it.
9. Reference `ControlCatalog` (run it, then read its `Pages/` + `Views/` + `ViewModels/` via `read_file`) for any control you need to port a kelivo screen with.

## Pitfalls

- SDK pin: `global.json` requires SDK 10.0.201; the repo hardcodes a known-compatible SDK because new SDK releases can break the build. Check `dotnet --version` first.
- Full-solution builds need workloads (`dotnet workload install android ios tvos maccatalyst wasm-tools`); use `Avalonia.Desktop.slnf` for desktop-only, which needs none.
- Error MSB4062 (GenerateAvaloniaResourcesTask): build `src/Avalonia.Build.Tasks` once (or run a Nuke build) before IDE builds.
- Keep submodules updated when working against the repo: `git submodule update --init --recursive` (from `docs/build.md`).
- Building local NuGet packages: `CreateNugetPackages` does NOT build `Avalonia.Native` on non-macOS, which breaks `Avalonia.Desktop` consumption; use `nuke --target BuildToNuGetCache` instead (installs `9999.0.0-localbuild` to `~/.nuget/packages`).
- Avalonia 12 has breaking changes vs 11 — the docs reference an `avalonia12-breaking-changes` page; don't assume 11-era APIs.
- Compiled bindings (`AvaloniaUseCompiledBindingsByDefault=true`) surface binding errors at build time — keep it on and treat build errors as real bugs.
- F# note: the build-test F# project is WinExe/desktop-only; the `.UseWin32()` call in `Program.fs` is the Windows backend — swap to the platform's backend (X11/Wayland/Native) per target, or reference `Avalonia.Desktop` for auto-selection.
- This skill covers the framework only; kelivo's UI inventory and agent-framework integration are in their respective skills.

## Verification

Build the F# proof project (no workloads needed):

```bash
cd /home/kunweiz/Desktop/vibe/wanxiang/Avalonia/tests/BuildTests && dotnet build BuildTests.FSharp/BuildTests.FSharp.fsproj -p:AvaloniaVersion=12.0.0
```

For the rewrite itself, `dotnet build` the F# app project and `dotnet run` it to see the window; run `ControlCatalog.Desktop` to verify any control you port from it.
