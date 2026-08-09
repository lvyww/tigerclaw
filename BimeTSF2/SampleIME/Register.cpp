// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO
// THE IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.
//
// Copyright (c) Microsoft Corporation. All rights reserved

#include "Private.h"
#include "Globals.h"

static const WCHAR RegInfo_Prefix_CLSID[] = L"CLSID\\";
static const WCHAR RegInfo_Key_InProSvr32[] = L"InProcServer32";
static const WCHAR RegInfo_Key_LocalServer32[] = L"LocalServer32";
static const WCHAR RegInfo_Key_ThreadModel[] = L"ThreadingModel";
static const WCHAR RegInfo_Prefix_TIP[] = L"SOFTWARE\\Microsoft\\CTF\\TIP\\";
static const WCHAR RegInfo_Key_Enable[] = L"Enable";

static const WCHAR TEXTSERVICE_DESC[] = L"\x864E\x722A\x8F93\x5165\x6CD5";

#if defined(_M_ARM64)
static BOOL ShouldRegisterDirectArm64Dll()
{
    WCHAR value[16] = {'\0'};
    DWORD copied = GetEnvironmentVariableW(L"TIGERCLAW_ARM64_REGISTER_DIRECT", value, ARRAYSIZE(value));
    return (copied > 0 && copied < ARRAYSIZE(value) && (value[0] == L'1' || value[0] == L'y' || value[0] == L'Y')) ? TRUE : FALSE;
}

static BOOL GetArm64WrapperPath(_Out_writes_(pathCount) WCHAR *path, size_t pathCount)
{
    WCHAR modulePath[MAX_PATH] = {'\0'};
    DWORD copied = GetModuleFileNameW(Global::dllInstanceHandle, modulePath, ARRAYSIZE(modulePath));
    if (copied == 0 || copied >= ARRAYSIZE(modulePath))
    {
        return FALSE;
    }

    WCHAR *slash = wcsrchr(modulePath, L'\\');
    if (slash == nullptr)
    {
        return FALSE;
    }

    *(slash + 1) = L'\0';
    return SUCCEEDED(StringCchPrintfW(path, pathCount, L"%sTigerClaw.dll", modulePath)) ? TRUE : FALSE;
}
#endif

//https://learn.microsoft.com/zh-cn/windows/win32/api/msctf/ns-msctf-tf_inputprocessorprofile
static const GUID SupportCategories[] = {
    GUID_TFCAT_TIP_KEYBOARD,//键盘类型项
    GUID_TFCAT_DISPLAYATTRIBUTEPROVIDER,//提供显示属性信息对象
    GUID_TFCAT_TIPCAP_UIELEMENTENABLED, //UI元素
    GUID_TFCAT_TIPCAP_SECUREMODE,//TSF在安全模式下激活。仅激活支持安全模式的文本服务。
    GUID_TFCAT_TIPCAP_COMLESS,//TSF不使用COM。TSF仅激活在GUID_TFCAT_TIPCAP_COMLESS中分类的文本服务。
    GUID_TFCAT_TIPCAP_INPUTMODECOMPARTMENT,//输入模式缓冲池
    GUID_TFCAT_TIPCAP_IMMERSIVESUPPORT, //此文本服务已经过测试，在 Windows 应用商店应用中正常运行。
    GUID_TFCAT_TIPCAP_SYSTRAYSUPPORT,//此文本服务支持包含在系统托盘中。
};
//+---------------------------------------------------------------------------
//注册输入法
//  RegisterProfiles
//注册文本服务和配置文件
//----------------------------------------------------------------------------

BOOL RegisterProfiles()
{
    HRESULT hr = S_FALSE;
//https://github.com/ChineseInputMethod/Interface/blob/master/TSFmanager/ITfInputProcessorProfileMgr.md
    ITfInputProcessorProfileMgr *pITfInputProcessorProfileMgr = nullptr;
    ITfInputProcessorProfiles *pInputProcessorProfiles = nullptr;
    HKEY tipKeyHandle = nullptr;

    hr = CoCreateInstance(CLSID_TF_InputProcessorProfiles, NULL, CLSCTX_INPROC_SERVER,
        IID_ITfInputProcessorProfileMgr, (void**)&pITfInputProcessorProfileMgr);
    if (FAILED(hr))
    {
        Global::LogToFile("RegisterProfiles: CoCreateInstance(ProfileMgr) failed hr=0x%08X", static_cast<unsigned>(hr));
        return FALSE;
    }

    WCHAR achIconFile[MAX_PATH] = {'\0'};
    DWORD cchA = 0;
    cchA = GetModuleFileName(Global::dllInstanceHandle, achIconFile, MAX_PATH);
    cchA = cchA >= MAX_PATH ? (MAX_PATH - 1) : cchA;
    achIconFile[cchA] = '\0';
    Global::LogToFile("RegisterProfiles: icon=%ls", achIconFile);

    size_t lenOfDesc = 0;
    hr = StringCchLength(TEXTSERVICE_DESC, STRSAFE_MAX_CCH, &lenOfDesc);
    if (hr != S_OK)
    {
        Global::LogToFile("RegisterProfiles: desc length failed hr=0x%08X", static_cast<unsigned>(hr));
        goto Exit;
    }//https://learn.microsoft.com/zh-cn/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofilemgr-registerprofile
    hr = pITfInputProcessorProfileMgr->RegisterProfile(Global::SampleIMECLSID,
        TEXTSERVICE_LANGID,
        Global::SampleIMEGuidProfile,
        TEXTSERVICE_DESC,
        static_cast<ULONG>(lenOfDesc),
        achIconFile,
        cchA,
        (UINT)TEXTSERVICE_ICON_INDEX, NULL, 0, TRUE, 0);

    if (FAILED(hr))
    {
        Global::LogToFile("RegisterProfiles: RegisterProfile failed hr=0x%08X", static_cast<unsigned>(hr));
        goto Exit;
    }

    // Match third-party TIP behavior: set root Enable=1 under CTF\TIP\{CLSID}.
    WCHAR tipRootKey[ARRAYSIZE(RegInfo_Prefix_TIP) + CLSID_STRLEN] = {'\0'};
    if (!CLSIDToString(Global::SampleIMECLSID, tipRootKey + ARRAYSIZE(RegInfo_Prefix_TIP) - 1))
    {
        hr = E_FAIL;
        Global::LogToFile("RegisterProfiles: CLSIDToString failed");
        goto Exit;
    }
    memcpy(tipRootKey, RegInfo_Prefix_TIP, sizeof(RegInfo_Prefix_TIP) - sizeof(WCHAR));

    if (RegCreateKeyEx(HKEY_LOCAL_MACHINE, tipRootKey, 0, NULL, REG_OPTION_NON_VOLATILE, KEY_WRITE, NULL, &tipKeyHandle, NULL) != ERROR_SUCCESS)
    {
        Global::LogToFile("RegisterProfiles: RegCreateKeyEx TIP root failed err=%lu key=%ls", GetLastError(), tipRootKey);
        hr = E_FAIL;
        goto Exit;
    }

    static const WCHAR enabledValue[] = L"1";
    if (RegSetValueEx(tipKeyHandle, RegInfo_Key_Enable, 0, REG_SZ, (const BYTE *)enabledValue, sizeof(enabledValue)) != ERROR_SUCCESS)
    {
        Global::LogToFile("RegisterProfiles: RegSetValueEx Enable failed err=%lu", GetLastError());
        hr = E_FAIL;
        goto Exit;
    }

    hr = CoCreateInstance(CLSID_TF_InputProcessorProfiles, NULL, CLSCTX_INPROC_SERVER,
        IID_ITfInputProcessorProfiles, (void**)&pInputProcessorProfiles);
    if (FAILED(hr))
    {
        Global::LogToFile("RegisterProfiles: CoCreateInstance(Profiles) failed hr=0x%08X", static_cast<unsigned>(hr));
        goto Exit;
    }

    hr = pInputProcessorProfiles->EnableLanguageProfile(Global::SampleIMECLSID,
        TEXTSERVICE_LANGID,
        Global::SampleIMEGuidProfile,
        TRUE);
    if (FAILED(hr))
    {
        Global::LogToFile("RegisterProfiles: EnableLanguageProfile failed hr=0x%08X", static_cast<unsigned>(hr));
        goto Exit;
    }

    hr = pInputProcessorProfiles->EnableLanguageProfileByDefault(Global::SampleIMECLSID,
        TEXTSERVICE_LANGID,
        Global::SampleIMEGuidProfile,
        TRUE);
    if (FAILED(hr))
    {
        Global::LogToFile("RegisterProfiles: EnableLanguageProfileByDefault failed hr=0x%08X", static_cast<unsigned>(hr));
        goto Exit;
    }

    hr = pITfInputProcessorProfileMgr->ActivateProfile(
        TF_PROFILETYPE_INPUTPROCESSOR,
        TEXTSERVICE_LANGID,
        Global::SampleIMECLSID,
        Global::SampleIMEGuidProfile,
        static_cast<HKL>(0),
        TF_IPPMF_ENABLEPROFILE | TF_IPPMF_DONTCARECURRENTINPUTLANGUAGE);
    if (FAILED(hr))
    {
        Global::LogToFile("RegisterProfiles: ActivateProfile failed hr=0x%08X", static_cast<unsigned>(hr));
    }
    else
    {
        Global::LogToFile("RegisterProfiles: success");
    }

Exit:
    if (tipKeyHandle)
    {
        RegCloseKey(tipKeyHandle);
        tipKeyHandle = nullptr;
    }

    if (pInputProcessorProfiles)
    {
        pInputProcessorProfiles->Release();
        pInputProcessorProfiles = nullptr;
    }

    if (pITfInputProcessorProfileMgr)
    {
        pITfInputProcessorProfileMgr->Release();
    }

    return SUCCEEDED(hr);
}

//+---------------------------------------------------------------------------
//
//  UnregisterProfiles
//
//----------------------------------------------------------------------------

void UnregisterProfiles()
{
    HRESULT hr = S_OK;

    ITfInputProcessorProfileMgr *pITfInputProcessorProfileMgr = nullptr;
    hr = CoCreateInstance(CLSID_TF_InputProcessorProfiles, NULL, CLSCTX_INPROC_SERVER,
        IID_ITfInputProcessorProfileMgr, (void**)&pITfInputProcessorProfileMgr);
    if (FAILED(hr))
    {
        goto Exit;
    }

    hr = pITfInputProcessorProfileMgr->UnregisterProfile(Global::SampleIMECLSID, TEXTSERVICE_LANGID, Global::SampleIMEGuidProfile, 0);
    if (FAILED(hr))
    {
        goto Exit;
    }

Exit:
    if (pITfInputProcessorProfileMgr)
    {
        pITfInputProcessorProfileMgr->Release();
    }

    return;
}

//+---------------------------------------------------------------------------
//
//  RegisterCategories
//
//----------------------------------------------------------------------------

BOOL RegisterCategories()
{//https://github.com/ChineseInputMethod/Interface/blob/master/TSFmanager/ITfCategoryMgr.md
    ITfCategoryMgr* pCategoryMgr = nullptr;
    HRESULT hr = S_OK;

    hr = CoCreateInstance(CLSID_TF_CategoryMgr, NULL, CLSCTX_INPROC_SERVER, IID_ITfCategoryMgr, (void**)&pCategoryMgr);
    if (FAILED(hr))
    {
        Global::LogToFile("RegisterCategories: CoCreateInstance failed hr=0x%08X", static_cast<unsigned>(hr));
        return FALSE;
    }
//https://learn.microsoft.com/zh-cn/windows/win32/api/msctf/nf-msctf-itfcategorymgr-registercategory
    int categoryIndex = 0;
    for each(GUID guid in SupportCategories)
    {
        hr = pCategoryMgr->RegisterCategory(Global::SampleIMECLSID, guid, Global::SampleIMECLSID);
        Global::LogToFile("RegisterCategories: index=%d hr=0x%08X", categoryIndex, static_cast<unsigned>(hr));
        categoryIndex++;
        if (FAILED(hr))
        {
            break;
        }
    }

    pCategoryMgr->Release();

    return (hr == S_OK);
}

//+---------------------------------------------------------------------------
//
//  UnregisterCategories
//
//----------------------------------------------------------------------------

void UnregisterCategories()
{
    ITfCategoryMgr* pCategoryMgr = S_OK;
    HRESULT hr = S_OK;

    hr = CoCreateInstance(CLSID_TF_CategoryMgr, NULL, CLSCTX_INPROC_SERVER, IID_ITfCategoryMgr, (void**)&pCategoryMgr);
    if (FAILED(hr))
    {
        return;
    }

    for each(GUID guid in SupportCategories)
    {
        pCategoryMgr->UnregisterCategory(Global::SampleIMECLSID, guid, Global::SampleIMECLSID);
    }
  
    pCategoryMgr->Release();

    return;
}

//+---------------------------------------------------------------------------
//删除子健
// RecurseDeleteKey
//
// RecurseDeleteKey is necessary because on NT RegDeleteKey doesn't work if the
// specified key has subkeys
//----------------------------------------------------------------------------

LONG RecurseDeleteKey(_In_ HKEY hParentKey, _In_ LPCTSTR lpszKey)
{
    HKEY regKeyHandle = nullptr;
    LONG res = 0;
    FILETIME time;
    WCHAR stringBuffer[256] = {'\0'};
    DWORD size = ARRAYSIZE(stringBuffer);

    if (RegOpenKey(hParentKey, lpszKey, &regKeyHandle) != ERROR_SUCCESS)
    {
        return ERROR_SUCCESS;
    }

    res = ERROR_SUCCESS;
    while (RegEnumKeyEx(regKeyHandle, 0, stringBuffer, &size, NULL, NULL, NULL, &time) == ERROR_SUCCESS)
    {
        stringBuffer[ARRAYSIZE(stringBuffer)-1] = '\0';
        res = RecurseDeleteKey(regKeyHandle, stringBuffer);
        if (res != ERROR_SUCCESS)
        {
            break;
        }
        size = ARRAYSIZE(stringBuffer);
    }
    RegCloseKey(regKeyHandle);

    return res == ERROR_SUCCESS ? RegDeleteKey(hParentKey, lpszKey) : res;
}


static BOOL BuildCurrentModuleCommandLine(_Out_writes_(pathCount) WCHAR *path, size_t pathCount)
{
    WCHAR modulePath[MAX_PATH] = {'\0'};
    DWORD copied = GetModuleFileNameW(Global::dllInstanceHandle, modulePath, ARRAYSIZE(modulePath));
    if (copied == 0 || copied >= ARRAYSIZE(modulePath))
    {
        Global::LogToFile("RegisterLocalServer: module path failed err=%lu", GetLastError());
        return FALSE;
    }

    return SUCCEEDED(StringCchPrintfW(path, pathCount, L"\"%s\" -Embedding", modulePath)) ? TRUE : FALSE;
}

BOOL RegisterLocalServer()
{
    DWORD copiedStringLen = 0;
    HKEY regKeyHandle = nullptr;
    HKEY regSubkeyHandle = nullptr;
    BOOL ret = FALSE;
    WCHAR achIMEKey[ARRAYSIZE(RegInfo_Prefix_CLSID) + CLSID_STRLEN] = {'\0'};
    WCHAR commandLine[MAX_PATH * 2] = {'\0'};

    if (!CLSIDToString(Global::SampleIMECLSID, achIMEKey + ARRAYSIZE(RegInfo_Prefix_CLSID) - 1))
    {
        return FALSE;
    }

    memcpy(achIMEKey, RegInfo_Prefix_CLSID, sizeof(RegInfo_Prefix_CLSID) - sizeof(WCHAR));

    if (RegCreateKeyEx(HKEY_CLASSES_ROOT, achIMEKey, 0, NULL, REG_OPTION_NON_VOLATILE, KEY_WRITE, NULL, &regKeyHandle, &copiedStringLen) == ERROR_SUCCESS)
    {
        if (RegSetValueEx(regKeyHandle, NULL, 0, REG_SZ, (const BYTE *)TEXTSERVICE_DESC, (_countof(TEXTSERVICE_DESC))*sizeof(WCHAR)) != ERROR_SUCCESS)
        {
            Global::LogToFile("RegisterLocalServer: set CLSID description failed err=%lu", GetLastError());
            goto Exit;
        }

        RegDeleteTreeW(regKeyHandle, RegInfo_Key_InProSvr32);

        if (RegCreateKeyEx(regKeyHandle, RegInfo_Key_LocalServer32, 0, NULL, REG_OPTION_NON_VOLATILE, KEY_WRITE, NULL, &regSubkeyHandle, &copiedStringLen) == ERROR_SUCCESS)
        {
            if (!BuildCurrentModuleCommandLine(commandLine, ARRAYSIZE(commandLine)))
            {
                goto Exit;
            }

            size_t commandLineLen = 0;
            if (StringCchLengthW(commandLine, ARRAYSIZE(commandLine), &commandLineLen) != S_OK)
            {
                Global::LogToFile("RegisterLocalServer: command line length failed");
                goto Exit;
            }

            copiedStringLen = static_cast<DWORD>(commandLineLen + 1);
            Global::LogToFile("RegisterLocalServer: LocalServer32=%ls", commandLine);
            if (RegSetValueEx(regSubkeyHandle, NULL, 0, REG_SZ, (const BYTE *)commandLine, (copiedStringLen)*sizeof(WCHAR)) != ERROR_SUCCESS)
            {
                Global::LogToFile("RegisterLocalServer: set LocalServer32 failed err=%lu", GetLastError());
                goto Exit;
            }

            ret = TRUE;
        }
        else
        {
            Global::LogToFile("RegisterLocalServer: create LocalServer32 failed err=%lu", GetLastError());
        }
    }
    else
    {
        Global::LogToFile("RegisterLocalServer: create CLSID key failed err=%lu", GetLastError());
    }

Exit:
    if (regSubkeyHandle)
    {
        RegCloseKey(regSubkeyHandle);
        regSubkeyHandle = nullptr;
    }
    if (regKeyHandle)
    {
        RegCloseKey(regKeyHandle);
        regKeyHandle = nullptr;
    }

    return ret;
}

//+---------------------------------------------------------------------------
//注册COM组件
//  RegisterServer
//
//----------------------------------------------------------------------------

BOOL RegisterServer()
{
    DWORD copiedStringLen = 0;
    HKEY regKeyHandle = nullptr;
    HKEY regSubkeyHandle = nullptr;
    BOOL ret = FALSE;
    WCHAR achIMEKey[ARRAYSIZE(RegInfo_Prefix_CLSID) + CLSID_STRLEN] = {'\0'};
    WCHAR achFileName[MAX_PATH] = {'\0'};

    if (!CLSIDToString(Global::SampleIMECLSID, achIMEKey + ARRAYSIZE(RegInfo_Prefix_CLSID) - 1))
    {
        return FALSE;
    }

    memcpy(achIMEKey, RegInfo_Prefix_CLSID, sizeof(RegInfo_Prefix_CLSID) - sizeof(WCHAR));

    if (RegCreateKeyEx(HKEY_CLASSES_ROOT, achIMEKey, 0, NULL, REG_OPTION_NON_VOLATILE, KEY_WRITE, NULL, &regKeyHandle, &copiedStringLen) == ERROR_SUCCESS)
    {
        if (RegSetValueEx(regKeyHandle, NULL, 0, REG_SZ, (const BYTE *)TEXTSERVICE_DESC, (_countof(TEXTSERVICE_DESC))*sizeof(WCHAR)) != ERROR_SUCCESS)
        {
            goto Exit;
        }

        RegDeleteTreeW(regKeyHandle, RegInfo_Key_LocalServer32);

        if (RegCreateKeyEx(regKeyHandle, RegInfo_Key_InProSvr32, 0, NULL, REG_OPTION_NON_VOLATILE, KEY_WRITE, NULL, &regSubkeyHandle, &copiedStringLen) == ERROR_SUCCESS)
        {
#if defined(_M_ARM64)
            if (ShouldRegisterDirectArm64Dll())
            {
                copiedStringLen = GetModuleFileNameW(Global::dllInstanceHandle, achFileName, ARRAYSIZE(achFileName));
                if (copiedStringLen == 0 || copiedStringLen >= ARRAYSIZE(achFileName))
                {
                    Global::LogToFile("RegisterServer: ARM64 direct module path failed err=%lu", GetLastError());
                    goto Exit;
                }
                Global::LogToFile("RegisterServer: ARM64 direct diagnostic registration enabled");
            }
            else if (!GetArm64WrapperPath(achFileName, ARRAYSIZE(achFileName)))
            {
                Global::LogToFile("RegisterServer: ARM64 wrapper path failed");
                goto Exit;
            }
#else
            copiedStringLen = GetModuleFileNameW(Global::dllInstanceHandle, achFileName, ARRAYSIZE(achFileName));
            if (copiedStringLen == 0 || copiedStringLen >= ARRAYSIZE(achFileName))
            {
                Global::LogToFile("RegisterServer: module path failed err=%lu", GetLastError());
                goto Exit;
            }
#endif
            size_t fileNameLen = 0;
            if (StringCchLengthW(achFileName, ARRAYSIZE(achFileName), &fileNameLen) != S_OK)
            {
                Global::LogToFile("RegisterServer: path length failed");
                goto Exit;
            }
            copiedStringLen = static_cast<DWORD>(fileNameLen + 1);
            Global::LogToFile("RegisterServer: InProcServer32=%ls", achFileName);
            if (RegSetValueEx(regSubkeyHandle, NULL, 0, REG_SZ, (const BYTE *)achFileName, (copiedStringLen)*sizeof(WCHAR)) != ERROR_SUCCESS)
            {
                Global::LogToFile("RegisterServer: set InProcServer32 failed err=%lu", GetLastError());
                goto Exit;
            }
            if (RegSetValueEx(regSubkeyHandle, RegInfo_Key_ThreadModel, 0, REG_SZ, (const BYTE *)TEXTSERVICE_MODEL, (_countof(TEXTSERVICE_MODEL)) * sizeof(WCHAR)) != ERROR_SUCCESS)
            {
                Global::LogToFile("RegisterServer: set ThreadingModel failed err=%lu", GetLastError());
                goto Exit;
            }

            ret = TRUE;
        }
    }

Exit:
    if (regSubkeyHandle)
    {
        RegCloseKey(regSubkeyHandle);
        regSubkeyHandle = nullptr;
    }
    if (regKeyHandle)
    {
        RegCloseKey(regKeyHandle);
        regKeyHandle = nullptr;
    }

    return ret;
}

//+---------------------------------------------------------------------------
//
//  UnregisterServer
//
//----------------------------------------------------------------------------

void UnregisterServer()
{
    WCHAR achIMEKey[ARRAYSIZE(RegInfo_Prefix_CLSID) + CLSID_STRLEN] = {'\0'};

    if (!CLSIDToString(Global::SampleIMECLSID, achIMEKey + ARRAYSIZE(RegInfo_Prefix_CLSID) - 1))
    {
        return;
    }

    memcpy(achIMEKey, RegInfo_Prefix_CLSID, sizeof(RegInfo_Prefix_CLSID) - sizeof(WCHAR));

    RecurseDeleteKey(HKEY_CLASSES_ROOT, achIMEKey);
}
