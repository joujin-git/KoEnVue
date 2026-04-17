# CLAUDE.md — KoEnVue Project Guide

Windows Korean/English IME state indicator. Draggable floating overlay showing **한** / **En** / **EN** labels, with optional CAPS LOCK bars on the label edges.

C# 14 / .NET 10 + NativeAOT single exe (~4.7 MB). Zero external NuGet packages.

## Tech stack

- **Target**: `net10.0-windows`, `PublishAot`, `AllowUnsafeBlocks`, `InvariantGlobalization`
- **Source generators only**: `[LibraryImport]`, `[JsonSerializable]` (`JsonSerializerIsReflectionEnabledByDefault=false`)
- **P/Invoke**: User32, Imm32, Shell32, Gdi32, Kernel32, Shcore, Ole32, OleAut32, Dwmapi, WinHttp
- **`[DllImport]` is banned** — always use `[LibraryImport]`

## Hard constraints (P1–P6)

| Rule | Description |
|------|-------------|
| **P1** | Zero external NuGet packages. .NET 10 BCL + Windows API only |
| **P2** | UI text defaults to Korean. Log messages and config keys in English |
| **P3** | No magic numbers → `const`/`enum`/config. No string comparisons → `enum` |
| **P4** | No duplicate implementations — always reach for shared `Core/` modules |
| **P5** | `app.manifest` UAC `requireAdministrator` |
| **P6** | One-way layer dependency: `App/` may import `Core/`, never the reverse |

Verification invariants — all must return **0 matches**:

```bash
git grep "KoEnVue\.App"      Core/   # P6 namespace gate
git grep "ImeState"          Core/   # Risk 4 enum gate
git grep "NonKoreanImeMode"  Core/   # Risk 4 enum gate
git grep "DllImport"                 # banned, use [LibraryImport]
```

## Architecture

Two-thread model:

```
Main thread (UI):       Message loop + rendering + tray + WM_TIMER animation + CAPS LOCK poll (200 ms)
Detection thread (BG):  80 ms polling → PostMessageW to main
```

Two-layer source split (one-way dependency, `App/` → `Core/`):

- **[Core/](Core/)** (`namespace KoEnVue.Core.*`) — reusable Win32/.NET infrastructure. Designed to lift into another Windows desktop project as-is. Zero KoEnVue-specific symbols, zero `AppConfig`, zero IME enums
- **[App/](App/)** (`namespace KoEnVue.App.*`) — KoEnVue-specific application layer (IME detection, dialogs, tray, themes, update checker)
- **[Program.cs](Program.cs) + [Program.Bootstrap.cs](Program.Bootstrap.cs)** — entry point + main message loop + bootstrap helpers. Composes both layers, lives at the repo root as `namespace KoEnVue`

Full module breakdown and reuse contract: **[docs/architecture.md](docs/architecture.md)**.

## Build & run

```bash
dotnet build                          # debug build
dotnet publish -r win-x64 -c Release  # NativeAOT single exe
```

**Always run both debug and release builds** — a debug-only build leaves the release exe outdated. [KoEnVue.csproj](KoEnVue.csproj) has `NoWarn=SYSLIB1051` for the .NET 10 `LibraryImport` + `IntPtr` diagnostic.

## Documentation map

| File | Purpose |
|------|---------|
| **[docs/KoEnVue_PRD.md](docs/KoEnVue_PRD.md)** | Product requirements (feature spec, behavior, config) |
| **[docs/User_Guide.md](docs/User_Guide.md)** | End-user manual (Korean) |
| **[README.md](README.md)** | Download, build, release procedure, config.json keys |
| **[docs/architecture.md](docs/architecture.md)** | Core/App module list, reuse contract, facade pattern |
| **[docs/implementation-notes.md](docs/implementation-notes.md)** | Render pipeline, drag/snap, animation, CAPS LOCK, hot reload, dialogs, update check |
| **[docs/conventions.md](docs/conventions.md)** | P1–P6 enforcement, silent catch policy, .NET 10 quirks |
| **[CHANGELOG.md](CHANGELOG.md)** | Release history (Keep a Changelog format) |
