#include "RuntimeStartup.h"
#include <tlhelp32.h>
#include <vector>
#include <stdexcept>

namespace tiger::core
{
    std::filesystem::path CoreExecutablePath()
    {
        std::wstring path(32768,L'\0');
        auto n=GetModuleFileNameW(nullptr,path.data(),static_cast<DWORD>(path.size()));
        if(!n || n>=path.size()) throw std::runtime_error("Cannot resolve Core executable");
        path.resize(n); return path;
    }
    bool CanStartCore()
    {
        DWORD session=0;
        if(!ProcessIdToSessionId(GetCurrentProcessId(),&session) || !session) return false;
        HANDLE token=nullptr;
        if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token)) return false;
        DWORD size=0; GetTokenInformation(token,TokenUser,nullptr,0,&size);
        std::vector<BYTE> bytes(size);
        bool ok=GetTokenInformation(token,TokenUser,bytes.data(),size,&size)!=FALSE;
        CloseHandle(token); if(!ok) return false;
        auto sid=reinterpret_cast<TOKEN_USER*>(bytes.data())->User.Sid;
        if(IsWellKnownSid(sid,WinLocalSystemSid) || IsWellKnownSid(sid,WinLocalServiceSid) || IsWellKnownSid(sid,WinNetworkServiceSid)) return false;
        auto named=[](HANDLE object,const wchar_t* expected)
        {
            wchar_t text[256]{}; DWORD needed=0;
            return object && GetUserObjectInformationW(object,UOI_NAME,text,sizeof(text),&needed) && !_wcsicmp(text,expected);
        };
        return named(GetProcessWindowStation(),L"WinSta0") && named(GetThreadDesktop(GetCurrentThreadId()),L"Default");
    }
    bool IsTsfRegistered()
    {
        constexpr auto key=L"CLSID\\{14493D3C-2059-41C0-805A-1F7841DE206B}\\InprocServer32";
        for(auto view:{KEY_WOW64_64KEY,KEY_WOW64_32KEY})
        {
            HKEY opened=nullptr;
            if(RegOpenKeyExW(HKEY_CLASSES_ROOT,key,0,KEY_QUERY_VALUE|view,&opened)!=ERROR_SUCCESS) continue;
            wchar_t path[32768]{}; DWORD size=sizeof(path),kind=0;
            auto status=RegQueryValueExW(opened,nullptr,nullptr,&kind,reinterpret_cast<BYTE*>(path),&size); RegCloseKey(opened);
            if(status!=ERROR_SUCCESS || (kind!=REG_SZ && kind!=REG_EXPAND_SZ)) continue;
            path[32767]=0; std::wstring raw(path);
            auto first=raw.find_first_not_of(L" \t\r\n\""); auto last=raw.find_last_not_of(L" \t\r\n\"");
            if(first==raw.npos) continue;
            raw=raw.substr(first,last-first+1); wchar_t expanded[32768]{};
            auto n=ExpandEnvironmentStringsW(raw.c_str(),expanded,32768);
            std::error_code error;
            if(n && n<=32768 && std::filesystem::is_regular_file(expanded,error)) return true;
        }
        return false;
    }
    void SyncCoreAutoStart(bool enabled) noexcept
    {
        try
        {
            auto command=enabled ? L"\""+CoreExecutablePath().native()+L"\" --with-overlay" : std::wstring{};
            HKEY key=nullptr;
            if(RegCreateKeyExW(HKEY_CURRENT_USER,L"Software\\Microsoft\\Windows\\CurrentVersion\\Run",0,nullptr,0,KEY_SET_VALUE,nullptr,&key,nullptr)!=ERROR_SUCCESS) return;
            if(enabled)
            {
                RegSetValueExW(key,L"TigerClawCore",0,REG_SZ,reinterpret_cast<const BYTE*>(command.c_str()),static_cast<DWORD>((command.size()+1)*sizeof(wchar_t)));
            }
            else RegDeleteValueW(key,L"TigerClawCore");
            RegDeleteValueW(key,L"TigerClaw"); RegCloseKey(key);
        }
        catch(...) {}
    }
    void SignalHookExit() noexcept
    {
        HANDLE event=OpenEventW(EVENT_MODIFY_STATE,FALSE,L"Local\\TigerClaw.Hook.Native.Exit.v1");
        if(event) { SetEvent(event); CloseHandle(event); }
    }
    HANDLE FindRuntimeProcess(const std::filesystem::path& executable)
    {
        HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0);
        if(snapshot==INVALID_HANDLE_VALUE) return nullptr;
        DWORD currentSession=0; ProcessIdToSessionId(GetCurrentProcessId(),&currentSession);
        PROCESSENTRY32W entry{}; entry.dwSize=sizeof(entry); HANDLE found=nullptr;
        for(BOOL more=Process32FirstW(snapshot,&entry);more;more=Process32NextW(snapshot,&entry))
        {
            if(entry.th32ProcessID==GetCurrentProcessId() || _wcsicmp(entry.szExeFile,executable.filename().c_str())) continue;
            DWORD session=0;
            if(!ProcessIdToSessionId(entry.th32ProcessID,&session) || session!=currentSession) continue;
            HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE|PROCESS_TERMINATE,FALSE,entry.th32ProcessID);
            if(!process) continue;
            wchar_t path[32768]{}; DWORD size=32768;
            if(QueryFullProcessImageNameW(process,0,path,&size) && !_wcsicmp(path,executable.c_str())) { found=process; break; }
            CloseHandle(process);
        }
        CloseHandle(snapshot); return found;
    }
    void StopRuntimeProcess(HANDLE process)
    {
        if(process && WaitForSingleObject(process,0)==WAIT_TIMEOUT)
        { if(!TerminateProcess(process,0) || WaitForSingleObject(process,5000)!=WAIT_OBJECT_0) throw std::runtime_error("Cannot stop previous runtime process"); }
    }
    CoreInstance::CoreInstance(const std::filesystem::path& executable)
    {
        // Match restart-on-launch for this installation, without killing another
        // user's/session's unrelated executable just because its filename matches.
        if(auto old=FindRuntimeProcess(executable))
        { try { StopRuntimeProcess(old); } catch(...) { CloseHandle(old); throw; } CloseHandle(old); }
        _mutex=CreateMutexW(nullptr,TRUE,L"Local\\TigerClaw.Core.SingleInstance");
        if(!_mutex) throw std::runtime_error("Cannot create Core singleton");
        if(GetLastError()==ERROR_ALREADY_EXISTS) { CloseHandle(_mutex); _mutex=nullptr; }
    }
    CoreInstance::~CoreInstance() { if(_mutex) { ReleaseMutex(_mutex); CloseHandle(_mutex); } }
}
