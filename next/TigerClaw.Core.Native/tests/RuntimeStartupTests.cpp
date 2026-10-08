#include "RuntimeEndpoints.h"
#include "RuntimeStartup.h"
#include <windows.h>
#include <iostream>

int wmain(int argc,wchar_t** argv)
{
    using namespace tiger::core;
    if(argc!=2) return 2;
    RuntimeEndpoints e;
    if(e.pipe!=L"BimeIPC" || e.ui!=L"Local\\TigerClaw.UiState.v1" || e.heartbeat!=L"Local\\TigerClaw.Heartbeat.v1" ||
        e.sentence!=L"TigerClaw.Sentence.v1" || e.menu!=L"Local\\TigerClaw.ShowMenu.v1" || e.isolated) return 3;
    bool rejected=false;
    try { RuntimeEndpoints::Isolated(L"BimeIPC"); } catch(const std::invalid_argument&) { rejected=true; }
    if(!rejected) return 4;
    auto desktopName=L"TigerClawCoreStartupTest"+std::to_wstring(GetCurrentProcessId());
    HDESK desktop=CreateDesktopW(desktopName.c_str(),nullptr,nullptr,0,DESKTOP_CREATEWINDOW|DESKTOP_READOBJECTS|DESKTOP_WRITEOBJECTS|READ_CONTROL,nullptr);
    if(!desktop) { std::cerr<<"Cannot create isolated startup test desktop\n"; return 5; }
    auto fullDesktop=L"WinSta0\\"+desktopName;
    STARTUPINFOW startup{}; startup.cb=sizeof(startup); startup.lpDesktop=fullDesktop.data();
    PROCESS_INFORMATION process{};
    std::wstring command=L"\""+std::wstring(argv[1])+L"\" --legacy-launcher-argument --AuToRuN --silent";
    bool started=CreateProcessW(argv[1],command.data(),nullptr,nullptr,FALSE,0,nullptr,nullptr,&startup,&process)!=FALSE;
    bool ok=false;
    if(started)
    {
        CloseHandle(process.hThread);
        if(WaitForSingleObject(process.hProcess,5000)==WAIT_OBJECT_0)
        { DWORD code=1; GetExitCodeProcess(process.hProcess,&code); ok=code==0; }
        else { TerminateProcess(process.hProcess,1); WaitForSingleObject(process.hProcess,5000); }
        CloseHandle(process.hProcess);
    }
    CloseDesktop(desktop);
    if(!ok) { std::cerr<<"Core failed to reject non-default desktop before startup\n";return 6; }
    std::cout<<"Production endpoints and non-default desktop startup guard passed\n";
    return 0;
}
