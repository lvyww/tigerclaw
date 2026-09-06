# Swift/.NET NativeAOT bridge spike

This isolated spike validates the in-process Swift-to-C# C ABI.  It intentionally
uses a dummy engine: `a` returns marked text and `space` returns committed text.

The C# library is NativeAOT and exports a small, ownership-explicit C ABI.  The
Swift executable calls it directly; an InputMethodKit adapter must use the same
API later for real `setMarkedText` and `insertText` lifecycle testing.
