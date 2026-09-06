# Avalonia Settings Spike Result

## Result

`PASS` for an Avalonia Settings control client on macOS arm64.

This validates Avalonia as a plausible non-hot-path settings app candidate. It
does not validate candidate UI or IME hot-path behavior.

## Implemented UI

`MainWindow.axaml` and `TigerClawHostCli.cs` provide:

- Schema selection
- Mixed Input toggle
- Candidate-count, page-size and maximum-code-length controls
- Save and refresh operations
- User dictionary add/remove/list operations
- A diagnostics view showing the effective host settings

The app invokes the installed, sandboxed IMK host's validated command interface.
It does not read or write the host's sandbox files directly. The host is still
the owner of configuration and user-dictionary persistence.

## Verification

- Template install: `dotnet new install Avalonia.Templates` -> installed `Avalonia.Templates@12.1.1`.
- Project create: `dotnet new avalonia.app -o spikes/avalonia-settings -n TigerClawSettingsSpike --framework net10.0` -> created and restored.
- Build: `dotnet build spikes/avalonia-settings/TigerClawSettingsSpike.csproj -c Release` -> `0` warnings, `0` errors.
- Publish: `dotnet publish spikes/avalonia-settings/TigerClawSettingsSpike.csproj -c Release -r osx-arm64 --self-contained true` -> passed.
- Bundle check: the packaged `TigerClawSettingsSpike.app` has a valid `Info.plist` and an arm64 executable.
- UI smoke: the packaged app read the installed host's current schema (`虎整句`), configuration and empty user dictionary; all three tabs rendered correctly.
- End-to-end control smoke: saving the current settings succeeded; a temporary `zz -> 设置界面校验词` entry added through the UI appeared in the list and was removed through the UI.

## Artifacts

- Publish directory: `spikes/avalonia-settings/bin/Release/net10.0/osx-arm64/publish`
- Package command: `zsh spikes/avalonia-settings/scripts/build_package.sh`
- App bundle: `spikes/avalonia-settings/dist/TigerClawSettingsSpike.app`
- Publish/app size: about `112M`

## Notes

The first NuGet restore hit transient EOF errors while downloading SkiaSharp native assets, then completed successfully. A later build exposed a user-local .NET workload manifest mismatch; `dotnet workload update` repaired the state and left the `macos` workload installed.

Avalonia should remain outside the IME key hot path.
