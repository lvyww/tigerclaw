# Pure .NET IMK Probe Result

## Result

`NOT_SUPPORTED_BY_CURRENT_BINDINGS` for the direct C# InputMethodKit path.

## Evidence

The user-local .NET 10 SDK (`10.0.400`) and the `macos` workload
(`26.5.10315/10.0.100`) were installed successfully. A `net10.0-macos` spike
project reached macOS bundle validation after adding its temporary
`ApplicationId`, then failed at the intended binding probe:

```text
Program.cs(2,7): error CS0246: The type or namespace name 'InputMethodKit' could not be found
```

The installed `Microsoft.macOS.Ref.net10.0_26.5` reference documentation and
assembly were also searched for `IMKServer`, `IMKInputController`, and
`InputMethodKit`; none are present.

## Decision Impact

This does not block the Hybrid route. It removes direct C# InputMethodKit from
the current primary path and leaves the already compiled Swift IMK adapter plus
NativeAOT C ABI as the supported experiment path.
