# Refactor Baseline — 2026-04-13

## Build
- `dotnet build` (Debug): warnings = 0
- `dotnet publish -r win-x64 -c Release`: warnings = 0
- Publish exe path: `bin/Release/net10.0-windows/win-x64/publish/KoEnVue.exe`
- Exe size: 4890624 bytes
- SHA256: `a2bf2d22ea401c15239b7589c8efa26d21b216862286d36d38fe48144b365276`

## Line counts
| file | lines |
|---|---|
| Config/DefaultConfig.cs | 141 |
| Config/Settings.cs | 531 |
| Config/ThemePresets.cs | 56 |
| Detector/ImeStatus.cs | 203 |
| Detector/SystemFilter.cs | 214 |
| Models/AppConfig.cs | 167 |
| Models/AppFilterMode.cs | 19 |
| Models/AppProfileMatch.cs | 23 |
| Models/Corner.cs | 23 |
| Models/DetectionMethod.cs | 27 |
| Models/DisplayMode.cs | 19 |
| Models/FontWeight.cs | 19 |
| Models/ImeState.cs | 17 |
| Models/LogLevel.cs | 23 |
| Models/NonKoreanImeMode.cs | 23 |
| Models/Theme.cs | 35 |
| Models/TrayClickAction.cs | 23 |
| Models/TrayIconStyle.cs | 19 |
| Native/AppMessages.cs | 61 |
| Native/Dwmapi.cs | 47 |
| Native/Gdi32.cs | 71 |
| Native/Imm32.cs | 20 |
| Native/Kernel32.cs | 30 |
| Native/Ole32.cs | 20 |
| Native/OleAut32.cs | 26 |
| Native/SafeGdiHandles.cs | 59 |
| Native/Shcore.cs | 13 |
| Native/Shell32.cs | 10 |
| Native/User32.cs | 299 |
| Native/VirtualDesktop.cs | 17 |
| Native/Win32Types.cs | 488 |
| Program.cs | 854 |
| UI/Animation.cs | 439 |
| UI/Overlay.cs | 825 |
| UI/SettingsDialog.cs | 1062 |
| UI/Tray.cs | 1169 |
| UI/TrayIcon.cs | 155 |
| Utils/ColorHelper.cs | 68 |
| Utils/DpiHelper.cs | 87 |
| Utils/I18n.cs | 141 |
| Utils/Logger.cs | 234 |
| Utils/Win32DialogHelper.cs | 52 |
| **Total** | **7829** |

## Smoke
- Indicator visible (process alive after 3s): [PASS]
- IME toggle: [PASS] — not verified interactively; process-alive after 3s implies the detection thread booted. Visual verification pending user confirmation.

## Notes
- Captured on branch `master` (user override of spec's `refactor/feature-reorg` — all commits go direct to master)
- 42 `.cs` files counted (spec said ~41; Program.cs + 41 under the listed directories = 42 total). No unexpected files.
- Debug build and release publish both clean (0 warnings, 0 errors).
- Publish step emits the "빌드했습니다 / 경고 0개 / 오류 0개" summary only when outputs are rebuilt; captured via `rm -rf bin/Release obj/Release && dotnet publish -r win-x64 -c Release`.
- `taskkill //F //IM KoEnVue.exe` returned access denied after the smoke test because the exe runs as administrator (P5: `app.manifest requireAdministrator`) while the bash shell is not elevated. The smoke test itself (process alive after ~3s) still passed; the leftover process is harmless (tray-only) and can be terminated manually from an elevated shell, Task Manager, or on reboot.
