#include "RuntimeFrontendHost.h"
#include "RuntimeStartup.h"
#include <algorithm>
#include <stdexcept>

namespace tiger::core
{
    RuntimeFrontendHost::RuntimeFrontendHost(std::filesystem::path root, std::wstring pipe)
        : _root(std::filesystem::absolute(root)), _pipe(std::move(pipe)),
          _endpoints(_pipe==L"BimeIPC" ? RuntimeEndpoints{} : RuntimeEndpoints::Isolated(_pipe))
    {
        _job=CreateJobObjectW(nullptr,nullptr);
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!_job || !SetInformationJobObject(_job,JobObjectExtendedLimitInformation,&limits,sizeof(limits)))
        { if (_job) CloseHandle(_job); throw std::runtime_error("Cannot create frontend ownership job"); }
        _menu=CreateEventW(nullptr,FALSE,FALSE,_endpoints.menu.c_str());
        if (!_menu) { CloseHandle(_job); throw std::runtime_error("Cannot create isolated menu signal"); }
        if(!_endpoints.isolated) _overlay=FindRuntimeProcess(_root/L"TigerClaw.Overlay.exe");
    }
    void RuntimeFrontendHost::CloseChild(HANDLE& child)
    {
        if (!child) return;
        DWORD pid=GetProcessId(child);
        if (WaitForSingleObject(child,0)==WAIT_TIMEOUT)
        {
            EnumWindows([](HWND window,LPARAM value)->BOOL
            {
                DWORD owner=0; GetWindowThreadProcessId(window,&owner);
                if (owner==static_cast<DWORD>(value)) PostMessageW(window,WM_CLOSE,0,0);
                return TRUE;
            },static_cast<LPARAM>(pid));
            if (WaitForSingleObject(child,1500)==WAIT_TIMEOUT)
            { TerminateProcess(child,1); WaitForSingleObject(child,1500); }
        }
        CloseHandle(child); child=nullptr;
    }
    RuntimeFrontendHost::~RuntimeFrontendHost()
    {
        if(!_endpoints.isolated) SignalHookExit();
        CloseChild(_hook);
        CloseChild(_dialog); CloseChild(_overlay);
        CloseHandle(_menu); CloseHandle(_job);
    }
    bool RuntimeFrontendHost::Start(const wchar_t* name,const wchar_t* arguments,HANDLE& owned)
    {
        auto executable=_root/name;
        if (!std::filesystem::is_regular_file(executable)) return false;
        // Build a child-only environment. Do not mutate the host/global environment.
        std::vector<std::wstring> entries;
        wchar_t* environment=GetEnvironmentStringsW();
        if (!environment) return false;
        for (auto p=environment; *p; p+=wcslen(p)+1)
            if (_wcsnicmp(p,L"TIGERCLAW_TEST_PIPE=",20)!=0) entries.emplace_back(p);
        FreeEnvironmentStringsW(environment);
        if(_endpoints.isolated) entries.emplace_back(L"TIGERCLAW_TEST_PIPE="+_pipe);
        std::sort(entries.begin(),entries.end(),[](const auto& a,const auto& b){return _wcsicmp(a.c_str(),b.c_str())<0;});
        std::vector<wchar_t> block;
        for (const auto& entry:entries) { block.insert(block.end(),entry.begin(),entry.end()); block.push_back(0); }
        block.push_back(0);
        // Windows paths cannot contain quotes; no shell is involved.
        std::wstring command=L"\""+executable.native()+L"\" "+arguments;
        STARTUPINFOW startup{}; startup.cb=sizeof(startup); PROCESS_INFORMATION info{};
        if (!CreateProcessW(executable.c_str(),command.data(),nullptr,nullptr,FALSE,
            CREATE_SUSPENDED|CREATE_UNICODE_ENVIRONMENT,block.data(),_root.c_str(),&startup,&info)) return false;
        bool ok=AssignProcessToJobObject(_job,info.hProcess) && ResumeThread(info.hThread)!=static_cast<DWORD>(-1);
        CloseHandle(info.hThread);
        if (!ok) { TerminateProcess(info.hProcess,1); WaitForSingleObject(info.hProcess,1500); CloseHandle(info.hProcess); return false; }
        owned=info.hProcess;
        return true;
    }
    bool RuntimeFrontendHost::Overlay(bool menu)
    {
        std::lock_guard lock(_mutex);
        if (!_overlay || WaitForSingleObject(_overlay,0)!=WAIT_TIMEOUT)
        { CloseChild(_overlay); if (!Start(L"TigerClaw.Overlay.exe",L"",_overlay)) return false; }
        if (menu) { AllowSetForegroundWindow(GetProcessId(_overlay)); SetEvent(_menu); }
        return true;
    }
    void RuntimeFrontendHost::Tick()
    {
        std::lock_guard lock(_mutex);
        auto now=GetTickCount64();
        if (_lastCheck && now-_lastCheck<3000) return;
        _lastCheck=now;
        bool alive=false;
        const auto& name=_endpoints.overlayHeartbeat;
        HANDLE map=OpenFileMappingW(FILE_MAP_READ,FALSE,name.c_str());
        if (map)
        {
            auto data=static_cast<const ULONGLONG*>(MapViewOfFile(map,FILE_MAP_READ,0,0,16));
            if (data)
            {
                auto seq=data[0],tick=data[1];
                alive=seq>0 && tick>0 && now>=tick && now-tick<=6000;
                UnmapViewOfFile(data);
            }
            CloseHandle(map);
        }
        // Same automatic retry cadence as C# OverlayLaunchSupervisor: a fresh
        // heartbeat resets 10s backoff, failed launches grow by 10s up to 120s.
        if (alive) { _retryDelay=10000; _nextAttempt=now+10000; return; }
        if (now<_nextAttempt) return;
        _nextAttempt=now+_retryDelay; _retryDelay=std::min<DWORD>(120000,_retryDelay+10000);
        if (_overlay && WaitForSingleObject(_overlay,0)==WAIT_TIMEOUT) return;
        CloseChild(_overlay); Start(L"TigerClaw.Overlay.exe",L"",_overlay);
    }
    bool RuntimeFrontendHost::Dialog(bool addCi)
    {
        std::lock_guard lock(_mutex);
        if (_dialog && WaitForSingleObject(_dialog,0)==WAIT_TIMEOUT && _addCi==addCi)
        {
            AllowSetForegroundWindow(GetProcessId(_dialog));
            auto pid=GetProcessId(_dialog);
            EnumWindows([](HWND window,LPARAM value)->BOOL
            { DWORD owner=0; GetWindowThreadProcessId(window,&owner); if(owner==static_cast<DWORD>(value) && IsWindowVisible(window)) SetForegroundWindow(window); return TRUE; },pid);
            return true;
        }
        CloseChild(_dialog); _addCi=addCi;
        return Start(L"TigerClaw.Dialog.exe",addCi ? L"--addci" : L"",_dialog);
    }
    bool RuntimeFrontendHost::Hook()
    {
        std::lock_guard lock(_mutex);
        if(_endpoints.isolated) return false;
        if(!_hook) _hook=FindRuntimeProcess(_root/L"TigerClaw.exe");
        if(_hook && WaitForSingleObject(_hook,0)==WAIT_TIMEOUT) return true;
        CloseChild(_hook); return Start(L"TigerClaw.exe",L"",_hook);
    }
}
