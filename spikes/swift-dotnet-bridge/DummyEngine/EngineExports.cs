using System;
using System.Runtime.InteropServices;

public static class EngineExports
{
    private sealed class EngineState
    {
        public string Preedit = string.Empty;
        public string Commit = string.Empty;
    }

    [UnmanagedCallersOnly(EntryPoint = "engine_create")]
    public static IntPtr Create()
    {
        try
        {
            return GCHandle.ToIntPtr(GCHandle.Alloc(new EngineState()));
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "engine_destroy")]
    public static void Destroy(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            GCHandle.FromIntPtr(handle).Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "engine_process_key")]
    public static int ProcessKey(IntPtr handle, IntPtr keyUtf8)
    {
        try
        {
            if (handle == IntPtr.Zero || keyUtf8 == IntPtr.Zero)
            {
                return 0;
            }

            var state = (EngineState)GCHandle.FromIntPtr(handle).Target;
            string key = Marshal.PtrToStringUTF8(keyUtf8) ?? string.Empty;
            state.Commit = string.Empty;

            if (key == "a")
            {
                state.Preedit = "TigerClaw Test";
                return 1;
            }

            if (key == "space" && state.Preedit.Length > 0)
            {
                state.Commit = state.Preedit;
                state.Preedit = string.Empty;
                return 1;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "engine_get_preedit")]
    public static IntPtr GetPreedit(IntPtr handle)
    {
        return CopyStateText(handle, false);
    }

    [UnmanagedCallersOnly(EntryPoint = "engine_get_commit")]
    public static IntPtr GetCommit(IntPtr handle)
    {
        return CopyStateText(handle, true);
    }

    [UnmanagedCallersOnly(EntryPoint = "engine_free")]
    public static void Free(IntPtr text)
    {
        if (text != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    private static IntPtr CopyStateText(IntPtr handle, bool commit)
    {
        try
        {
            if (handle == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var state = (EngineState)GCHandle.FromIntPtr(handle).Target;
            return Marshal.StringToCoTaskMemUTF8(commit ? state.Commit : state.Preedit);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}
