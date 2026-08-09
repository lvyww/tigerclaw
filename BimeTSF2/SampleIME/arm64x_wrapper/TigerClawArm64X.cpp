#include <windows.h>
#include <strsafe.h>

#include "../EmbeddedBuildInfo.h"

extern "C" IMAGE_DOS_HEADER __ImageBase;

typedef HRESULT (STDAPICALLTYPE *DllGetClassObjectFn)(REFCLSID, REFIID, LPVOID *);
typedef HRESULT (STDAPICALLTYPE *DllCanUnloadNowFn)();
typedef HRESULT (STDAPICALLTYPE *DllRegisterServerFn)();
typedef HRESULT (STDAPICALLTYPE *DllUnregisterServerFn)();

static HMODULE g_sidecarModule = nullptr;
static INIT_ONCE g_loadOnce = INIT_ONCE_STATIC_INIT;
static HRESULT g_loadResult = E_UNEXPECTED;

#if defined(_M_ARM64EC)
static const wchar_t kSidecarName[] = L"TigerClawx64.dll";
static const char kWrapperArch[] = "ARM64EC/x64";
#elif defined(_M_ARM64)
static const wchar_t kSidecarName[] = L"TigerClawARM64.dll";
static const char kWrapperArch[] = "ARM64";
#else
static const wchar_t kSidecarName[] = L"TigerClawx64.dll";
static const char kWrapperArch[] = "unknown";
#endif

#if BIME_EMBED_TEXT_LOG_ENABLED
static void Log(const char *message, HRESULT hr = S_OK)
{
    char path[MAX_PATH] = {'\0'};
    DWORD copied = GetEnvironmentVariableA("USERPROFILE", path, ARRAYSIZE(path));
    if (copied == 0 || copied >= ARRAYSIZE(path))
    {
        return;
    }

    if (FAILED(StringCchCatA(path, ARRAYSIZE(path), "\\bime_tsf.log")))
    {
        return;
    }

    SYSTEMTIME st = {};
    GetLocalTime(&st);

    char line[512] = {'\0'};
    if (FAILED(StringCchPrintfA(
            line,
            ARRAYSIZE(line),
            "[%02u:%02u:%02u.%03u][arm64x:%s] %s hr=0x%08X sidecar=%S\r\n",
            st.wHour,
            st.wMinute,
            st.wSecond,
            st.wMilliseconds,
            kWrapperArch,
            message,
            static_cast<unsigned>(hr),
            kSidecarName)))
    {
        return;
    }

    HANDLE file = CreateFileA(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return;
    }

    DWORD written = 0;
    WriteFile(file, line, static_cast<DWORD>(lstrlenA(line)), &written, nullptr);
    CloseHandle(file);
}
#else
static void Log(const char *, HRESULT = S_OK)
{
}
#endif

static BOOL CALLBACK LoadSidecarOnce(PINIT_ONCE, PVOID, PVOID *)
{
    wchar_t path[MAX_PATH] = {'\0'};
    DWORD copied = GetModuleFileNameW(reinterpret_cast<HMODULE>(&__ImageBase), path, ARRAYSIZE(path));
    if (copied == 0 || copied >= ARRAYSIZE(path))
    {
        g_loadResult = HRESULT_FROM_WIN32(GetLastError());
        Log("GetModuleFileNameW failed", g_loadResult);
        return TRUE;
    }

    wchar_t *slash = wcsrchr(path, L'\\');
    if (slash == nullptr)
    {
        g_loadResult = HRESULT_FROM_WIN32(ERROR_BAD_PATHNAME);
        Log("module path missing slash", g_loadResult);
        return TRUE;
    }

    *(slash + 1) = L'\0';
    if (FAILED(StringCchCatW(path, ARRAYSIZE(path), kSidecarName)))
    {
        g_loadResult = HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        Log("sidecar path too long", g_loadResult);
        return TRUE;
    }

    g_sidecarModule = LoadLibraryExW(path, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (g_sidecarModule == nullptr)
    {
        g_loadResult = HRESULT_FROM_WIN32(GetLastError());
        Log("LoadLibraryExW sidecar failed", g_loadResult);
        return TRUE;
    }

    g_loadResult = S_OK;
    Log("sidecar loaded", g_loadResult);
    return TRUE;
}

static HRESULT EnsureSidecarLoaded()
{
    if (!InitOnceExecuteOnce(&g_loadOnce, LoadSidecarOnce, nullptr, nullptr))
    {
        return HRESULT_FROM_WIN32(GetLastError());
    }

    return g_loadResult;
}

template <typename T>
static HRESULT GetSidecarExport(const char *name, T *fn)
{
    HRESULT hr = EnsureSidecarLoaded();
    if (FAILED(hr))
    {
        return hr;
    }

    FARPROC proc = GetProcAddress(g_sidecarModule, name);
    if (proc == nullptr)
    {
        hr = HRESULT_FROM_WIN32(GetLastError());
        Log("GetProcAddress failed", hr);
        return hr;
    }

    *fn = reinterpret_cast<T>(proc);
    return S_OK;
}

extern "C" HRESULT STDAPICALLTYPE DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID *ppv)
{
    DllGetClassObjectFn fn = nullptr;
    HRESULT hr = GetSidecarExport("DllGetClassObject", &fn);
    if (FAILED(hr))
    {
        return hr;
    }

    return fn(rclsid, riid, ppv);
}

extern "C" HRESULT STDAPICALLTYPE DllCanUnloadNow()
{
    DllCanUnloadNowFn fn = nullptr;
    HRESULT hr = GetSidecarExport("DllCanUnloadNow", &fn);
    if (FAILED(hr))
    {
        return S_FALSE;
    }

    return fn();
}

extern "C" HRESULT STDAPICALLTYPE DllRegisterServer()
{
    DllRegisterServerFn fn = nullptr;
    HRESULT hr = GetSidecarExport("DllRegisterServer", &fn);
    if (FAILED(hr))
    {
        return hr;
    }

    return fn();
}

extern "C" HRESULT STDAPICALLTYPE DllUnregisterServer()
{
    DllUnregisterServerFn fn = nullptr;
    HRESULT hr = GetSidecarExport("DllUnregisterServer", &fn);
    if (FAILED(hr))
    {
        return hr;
    }

    return fn();
}

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID)
{
    return TRUE;
}
