// Explicitly invoked real-key test. Sends only to its own foreground RichEdit;
// a companion copy of the production renderer observes the live UI channel.
// No registration/default IME/config/model/production Overlay changes.
#include "Model.h"
#include <windows.h>
#include <msctf.h>
#include <dwmapi.h>
#include <cstdio>
#include <share.h>
#include <string>
#include <vector>
#include <nlohmann/json.hpp>

namespace latency_probe {
HWND window=nullptr,edit=nullptr;
ITfInputProcessorProfileMgr* profiles=nullptr;
GUID service,profile;
FILE* output=nullptr;
LARGE_INTEGER frequency{};
struct KeyRecord {std::u16string raw;LONGLONG tick;bool shown=false;};
std::vector<KeyRecord> keys;
const wchar_t* cases[]={L"shiyixia",L"nihaoma",L"changyongzi",L"yaoyouhuahaojiu",L"jintianxiawuwomenyiqiquchifan",L"woxiangceshiyixiapinyinshurufa",L"nh",L"windowsbanben"};
int intervals[]={100,60,30};
int sample=0,round=0,position=0,phase=0,status=1;
ULONGLONG due=0;
bool recording=false;
std::u16string expected;
LONGLONG Tick(){LARGE_INTEGER value;QueryPerformanceCounter(&value);return value.QuadPart;}
void Log(nlohmann::json value){auto line=value.dump();fprintf(output,"%s\n",line.c_str());fflush(output);}
bool Ready(){
    if(GetForegroundWindow()!=window||GetFocus()!=edit)return false;
    for(int vk:{VK_SHIFT,VK_CONTROL,VK_MENU,VK_LWIN,VK_RWIN})if(GetAsyncKeyState(vk)&0x8000)return false;
    TF_INPUTPROCESSORPROFILE active{};
    return SUCCEEDED(profiles->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD,&active))&&
        IsEqualGUID(active.clsid,service)&&IsEqualGUID(active.guidProfile,profile);
}
void Stop(const char* reason){recording=false;Log({{"stop",reason},{"status",status}});KillTimer(window,1);DestroyWindow(window);}
bool Key(WORD vk){
    if(!Ready()){Stop("focus/modifier/profile changed; no key sent");return false;}
    INPUT input[2]{};input[0].type=input[1].type=INPUT_KEYBOARD;
    input[0].ki.wVk=input[1].ki.wVk=vk;input[1].ki.dwFlags=KEYEVENTF_KEYUP;
    if(SendInput(2,input,sizeof(INPUT))!=2){Stop("SendInput failed");return false;}return true;
}
void Presented(const tiger::overlay::State& state,HWND candidate){
    if(!recording||!Ready()||!IsWindowVisible(candidate)||state.candidateHoldWhilePending||state.candidateSelectionToken.empty()||state.candidates.empty())return;
    for(auto it=keys.rbegin();it!=keys.rend();++it){
        if(it->shown||state.input!=it->raw)continue;
        // This measures successful layered-window presentation followed by a
        // compositor flush, not a photodiode measurement of the physical panel.
        LONGLONG submitted=Tick();HRESULT flush=DwmFlush();LONGLONG now=Tick();it->shown=true;
        if(state.input==expected)expected=state.candidates.front();
        Log({{"event","present"},{"round",round},{"case",sample},{"raw",tiger::overlay::ToUtf8(it->raw)},
            {"submit_ms",(submitted-it->tick)*1000.0/frequency.QuadPart},{"ms",(now-it->tick)*1000.0/frequency.QuadPart},{"dwm_flush",flush},{"top",tiger::overlay::ToUtf8(state.candidates.front())}});break;
    }
}
LRESULT CALLBACK Proc(HWND hwnd,UINT message,WPARAM wp,LPARAM lp){
    if(message==WM_TIMER){
        if(GetTickCount64()<due)return 0;
        if(!Ready()){Stop("foreground ownership lost");return 0;}
        if(phase==0){
            if(cases[sample][position]){
                std::u16string raw;for(int i=0;i<=position;i++)raw.push_back(static_cast<char16_t>(cases[sample][i]));
                if(!cases[sample][position+1])expected=raw;
                keys.push_back({raw,Tick()});recording=true;
                Log({{"event","key"},{"round",round},{"case",sample},{"raw",tiger::overlay::ToUtf8(raw)},{"interval",intervals[round]}});
                if(Key(static_cast<WORD>(towupper(cases[sample][position]))))position++;
                due=GetTickCount64()+intervals[round];
            }else{phase=1;due=GetTickCount64()+700;}
        }else if(phase==1){
            if(keys.empty()||!keys.back().shown){Stop("final candidate frame was not observed");return 0;}
            recording=false;if(Key(VK_SPACE)){phase=2;due=GetTickCount64()+600;}
        }else if(phase==2){
            wchar_t text[4096]{};GetWindowTextW(edit,text,4096);
            std::u16string committed(reinterpret_cast<const char16_t*>(text));
            Log({{"event","commit"},{"round",round},{"case",sample},{"text",tiger::overlay::ToUtf8(committed)},
                {"matches_presented_first",committed==expected}});
            if(committed!=expected||committed.empty()){Stop("commit did not match presented first candidate");return 0;}
            for(auto& key:keys)if(!key.shown)Log({{"event","unpresented"},{"round",round},{"case",sample},{"raw",tiger::overlay::ToUtf8(key.raw)}});
            if(++sample==8){sample=0;if(++round==3){status=0;Stop("requested real-key commits and presentation traces completed");return 0;}}
            SetWindowTextW(edit,L"");keys.clear();expected.clear();position=0;phase=0;due=GetTickCount64()+250;
        }return 0;
    }
    if(message==WM_DESTROY){PostQuitMessage(0);return 0;}
    return DefWindowProcW(hwnd,message,wp,lp);
}
}
#define wWinMain unusedOverlayEntry
#include "latency-overlay-main.cpp"
#undef wWinMain

int wmain(int argc,wchar_t** argv){
    using namespace latency_probe;
    if((argc!=2&&argc!=4)||GetFileAttributesW(argv[1])!=INVALID_FILE_ATTRIBUTES)return 2;
    if(argc==4){latency_probe::round=_wtoi(argv[2]);latency_probe::sample=_wtoi(argv[3]);if(latency_probe::round<0||latency_probe::round>=3||latency_probe::sample<0||latency_probe::sample>=8)return 2;}
    output=_wfsopen(argv[1],L"wb",_SH_DENYWR);if(!output)return 3;
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);QueryPerformanceFrequency(&frequency);
    CLSIDFromString(L"{14493D3C-2059-41C0-805A-1F7841DE206B}",&service);
    CLSIDFromString(L"{B5AB471C-7F90-49EA-B418-DB802691FCC0}",&profile);
    if(FAILED(CoCreateInstance(CLSID_TF_InputProcessorProfiles,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&profiles))))return 4;
    ITfThreadMgr* manager=nullptr;TfClientId client=0;
    if(FAILED(CoCreateInstance(CLSID_TF_ThreadMgr,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&manager)))||FAILED(manager->Activate(&client)))return 5;
    LoadLibraryW(L"Msftedit.dll");
    WNDCLASSW type{};type.lpfnWndProc=Proc;type.hInstance=GetModuleHandleW(nullptr);type.lpszClassName=L"TigerClaw.Performance.InputProbe";
    type.hbrBackground=(HBRUSH)(COLOR_WINDOW+1);RegisterClassW(&type);
    window=CreateWindowW(type.lpszClassName,L"TigerClaw isolated typing performance test (auto closes)",WS_OVERLAPPEDWINDOW,100,100,900,260,nullptr,nullptr,type.hInstance,nullptr);
    edit=CreateWindowExW(WS_EX_CLIENTEDGE,L"RICHEDIT50W",L"",WS_CHILD|WS_VISIBLE|WS_TABSTOP|ES_MULTILINE,15,20,850,170,window,nullptr,type.hInstance,nullptr);
    ShowWindow(window,SW_SHOW);SetForegroundWindow(window);SetFocus(edit);
    HKL language=LoadKeyboardLayoutW(L"00000804",0);if(language)ActivateKeyboardLayout(language,0);
    HRESULT activated=profiles->ActivateProfile(TF_PROFILETYPE_INPUTPROCESSOR,0x804,service,profile,nullptr,TF_IPPMF_FORPROCESS|TF_IPPMF_DONTCARECURRENTINPUTLANGUAGE);
    Log({{"activate",activated},{"observer","test-owned production-renderer copy; live UI channel read-only"}});
    if(SUCCEEDED(activated)){
        tiger::overlay::Endpoints endpoints;
        endpoints.pipe=L"\\\\.\\pipe\\TigerClaw.PerformanceProbe.Disabled";
        endpoints.overlayHeartbeat=L"Local\\TigerClaw.PerformanceProbe.Heartbeat";
        endpoints.menu=L"Local\\TigerClaw.PerformanceProbe.Menu";
        due=GetTickCount64()+2000;SetTimer(window,1,5,nullptr);
        tiger::overlay::Application app(false,endpoints,true);app.Run();
    }
    manager->Deactivate();manager->Release();profiles->Release();CoUninitialize();fclose(output);return status;
}
