# TigerClaw macOS .NET Hybrid Spike Environment

Date: 2026-08-27

## Host

- Machine architecture: `arm64`
- macOS: `26.5` (`25F71`)
- Xcode path: `/Applications/Xcode.app/Contents/Developer`
- Xcode: `26.6` (`17F113`)
- Swift: Apple Swift `6.3.3`, target `arm64-apple-macosx26.0`
- Clang: Apple clang `21.0.0`
- Rust: `rustc 1.98.0`, `cargo 1.98.0`

## .NET

- Install root: `/Users/wuzz/.dotnet`
- SDK: `.NET SDK 10.0.400`
- Host runtime: `.NET 10.0.11`, `osx-arm64`
- Workload set: `10.0.400.1`
- Installed workloads: `macos` (`26.5.10315/10.0.100`)

## Setup Actions

- Installed a user-local `.NET SDK 10.0.400` at `/Users/wuzz/.dotnet` from the
  official ARM64 SDK archive after verifying its published MD5.
- Installed user-local Avalonia templates with `dotnet new install Avalonia.Templates`.
- Installed template version: `Avalonia.Templates@12.1.1`.
- Ran `dotnet workload update` after MSBuild reported a missing workload-set manifest. This repaired the user-local workload state and left the `macos` workload visible to `dotnet workload list`.

No system-wide SDK install or sudo operation was used.
