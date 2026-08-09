// Minimal out-of-proc COM host for the TigerClaw TSF text service.

#include "Private.h"
#include "Globals.h"

BOOL RegisterLocalServer();
void UnregisterServer();
BOOL RegisterProfiles();
void UnregisterProfiles();
BOOL RegisterCategories();
void UnregisterCategories();

STDAPI DllGetClassObject(_In_ REFCLSID rclsid, _In_ REFIID riid, _Outptr_ void** ppv);

static HRESULT RegisterComClassObject(_Out_ DWORD *cookie)
{
    if (cookie == nullptr)
    {
        return E_POINTER;
    }

    *cookie = 0;

    IUnknown *factory = nullptr;
    HRESULT hr = DllGetClassObject(Global::SampleIMECLSID, IID_IUnknown, reinterpret_cast<void **>(&factory));
    if (FAILED(hr))
    {
        Global::LogToFile("LocalServer: DllGetClassObject failed hr=0x%08X", static_cast<unsigned>(hr));
        return hr;
    }

    hr = CoRegisterClassObject(
        Global::SampleIMECLSID,
        factory,
        CLSCTX_LOCAL_SERVER,
        REGCLS_MULTIPLEUSE | REGCLS_SUSPENDED,
        cookie);
    factory->Release();

    if (FAILED(hr))
    {
        Global::LogToFile("LocalServer: CoRegisterClassObject failed hr=0x%08X", static_cast<unsigned>(hr));
        return hr;
    }

    hr = CoResumeClassObjects();
    if (FAILED(hr))
    {
        Global::LogToFile("LocalServer: CoResumeClassObjects failed hr=0x%08X", static_cast<unsigned>(hr));
        CoRevokeClassObject(*cookie);
        *cookie = 0;
        return hr;
    }

    Global::LogToFile("LocalServer: class object registered cookie=%lu", *cookie);
    return S_OK;
}

static HRESULT InitializeComSecurity()
{
    HRESULT hr = CoInitializeSecurity(
        nullptr,
        -1,
        nullptr,
        nullptr,
        RPC_C_AUTHN_LEVEL_DEFAULT,
        RPC_C_IMP_LEVEL_IDENTIFY,
        nullptr,
        EOAC_NONE,
        nullptr);

    if (FAILED(hr) && hr != RPC_E_TOO_LATE)
    {
        Global::LogToFile("LocalServer: CoInitializeSecurity failed hr=0x%08X", static_cast<unsigned>(hr));
        return hr;
    }

    Global::LogToFile("LocalServer: CoInitializeSecurity hr=0x%08X", static_cast<unsigned>(hr));
    return S_OK;
}

static BOOL InitializeLocalServerGlobals()
{
    if (!InitializeCriticalSectionAndSpinCount(&Global::CS, 0))
    {
        Global::LogToFile("LocalServer: InitializeCriticalSectionAndSpinCount failed");
        return FALSE;
    }

    if (!Global::RegisterWindowClass())
    {
        Global::LogToFile("LocalServer: RegisterWindowClass failed");
        DeleteCriticalSection(&Global::CS);
        return FALSE;
    }

    return TRUE;
}

static void UninitializeLocalServerGlobals()
{
    DeleteCriticalSection(&Global::CS);
}

static HRESULT RegisterTextService()
{
    if ((!RegisterLocalServer()) || (!RegisterProfiles()) || (!RegisterCategories()))
    {
        UnregisterProfiles();
        UnregisterCategories();
        UnregisterServer();
        return E_FAIL;
    }

    return S_OK;
}

static void UnregisterTextService()
{
    UnregisterProfiles();
    UnregisterCategories();
    UnregisterServer();
}

int WINAPI wWinMain(_In_ HINSTANCE hInstance, _In_opt_ HINSTANCE, _In_ PWSTR commandLine, _In_ int)
{
    Global::dllInstanceHandle = hInstance;
    Global::LogToFile("LocalServer: start command_line=%ls", (commandLine != nullptr) ? commandLine : L"");
    if (!InitializeLocalServerGlobals())
    {
        return 1;
    }

    if (commandLine != nullptr)
    {
        if (wcsstr(commandLine, L"--register-localserver") != nullptr)
        {
            HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
            if (FAILED(hr))
            {
                return static_cast<int>(hr);
            }

            hr = RegisterTextService();
            CoUninitialize();
            UninitializeLocalServerGlobals();
            return SUCCEEDED(hr) ? 0 : 1;
        }

        if (wcsstr(commandLine, L"--unregister-localserver") != nullptr)
        {
            HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
            if (SUCCEEDED(hr))
            {
                UnregisterTextService();
                CoUninitialize();
            }
            UninitializeLocalServerGlobals();
            return SUCCEEDED(hr) ? 0 : 1;
        }
    }

    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(hr))
    {
        Global::LogToFile("LocalServer: CoInitializeEx failed hr=0x%08X", static_cast<unsigned>(hr));
        UninitializeLocalServerGlobals();
        return static_cast<int>(hr);
    }

    hr = InitializeComSecurity();
    if (FAILED(hr))
    {
        CoUninitialize();
        UninitializeLocalServerGlobals();
        return static_cast<int>(hr);
    }

    DWORD cookie = 0;
    hr = RegisterComClassObject(&cookie);
    if (FAILED(hr))
    {
        CoUninitialize();
        return static_cast<int>(hr);
    }

    Global::LogToFile("LocalServer: message loop start");

    MSG msg = {};
    while (GetMessageW(&msg, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    Global::LogToFile("LocalServer: message loop exit");

    if (cookie != 0)
    {
        CoRevokeClassObject(cookie);
    }

    CoUninitialize();
    UninitializeLocalServerGlobals();
    return 0;
}
