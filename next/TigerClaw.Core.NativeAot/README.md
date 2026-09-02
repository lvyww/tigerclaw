# TigerClaw.Core Native AOT

This is an experimental parallel host for the maintained C# Core. It compiles
the same Core source files for `net10.0-windows` and does not replace or fork the
default .NET Framework project in `../TigerClaw.Core/`.

Publish an ARM64 executable without touching the live release directory:

```batch
dotnet publish TigerClaw.Core.NativeAot.csproj -c Release -r win-arm64 --self-contained true -o ..\_run\NativeAotArm64
```

From the repository root, `publish_aot_core_arm64.bat` performs that publish and
then directly replaces only `release_arm64\TigerClaw.Core.exe`. It deliberately
leaves all runtime configuration, code tables, UI processes, Sentence files and
the framework `TigerClaw.Shared.dll` untouched. The AOT executable contains its
own compiled Shared code.

Pass `--build-only` to validate and stage the AOT output under
`next\_run\NativeAotArm64` without stopping Core or changing `release_arm64`.

The Core/TSF wire protocol and MMF JSON remain compatible with the framework
build. The modern target uses source-generated `System.Text.Json` metadata for
the MMF state and Sentence pipe so the AOT binary does not depend on runtime
reflection.

The Core-only script does not rebuild TSF DLLs. A deployed TSF compiled with
Core hash verification enabled will reject the newly built executable until a
full matching release is built.
