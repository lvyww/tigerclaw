#include <windows.h>
#include <msctf.h>
#include <imm.h>
#include <cstdio>
#include <string>

static HWND window, edit;
static ITfInputProcessorProfileMgr* profiles;
static ITfThreadMgr* threadManager;
static GUID service, profile;
static FILE* logFile;
static int phase=0, sample=0, position=0, exitStatus=1;
static ULONGLONG due=0;
static const wchar_t* cases[]={L"nihao",L"yinhang",L"xingzou"};
static void Log(const char* name,const std::wstring& text) {
    int n=WideCharToMultiByte(CP_UTF8,0,text.c_str(),-1,nullptr,0,nullptr,nullptr);
    std::string utf8(n,'\0');WideCharToMultiByte(CP_UTF8,0,text.c_str(),-1,utf8.data(),n,nullptr,nullptr);
    fprintf(logFile,"%s\t%s\n",name,utf8.c_str());fflush(logFile);
}
static void Stop(const wchar_t* reason) {
    Log("stop",reason);KillTimer(window,1);DestroyWindow(window);
}
static bool TargetReady() {
    if(GetForegroundWindow()!=window||GetFocus()!=edit)return false;
    for(int vk:{VK_SHIFT,VK_CONTROL,VK_MENU,VK_LWIN,VK_RWIN})
        if(GetAsyncKeyState(vk)&0x8000)return false;
    TF_INPUTPROCESSORPROFILE active{};
    return SUCCEEDED(profiles->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD,&active)) &&
        IsEqualGUID(active.clsid,service)&&IsEqualGUID(active.guidProfile,profile);
}
static bool Key(WORD vk) {
    if(!TargetReady()){Stop(L"focus/modifier/profile changed; no key sent");return false;}
    INPUT keys[2]{};
    keys[0].type=keys[1].type=INPUT_KEYBOARD;
    keys[0].ki.wVk=keys[1].ki.wVk=vk;
    keys[1].ki.dwFlags=KEYEVENTF_KEYUP;
    if(SendInput(2,keys,sizeof(INPUT))!=2){Stop(L"SendInput failed");return false;}
    return true;
}
static std::wstring Composition() {
    HIMC context=ImmGetContext(edit);
    if(!context)return L"";
    LONG bytes=ImmGetCompositionStringW(context,GCS_COMPSTR,nullptr,0);
    std::wstring value(bytes>0?bytes/sizeof(wchar_t):0,L'\0');
    if(bytes>0)ImmGetCompositionStringW(context,GCS_COMPSTR,value.data(),bytes);
    ImmReleaseContext(edit,context);return value;
}
static LRESULT CALLBACK Proc(HWND hwnd,UINT msg,WPARAM wp,LPARAM lp) {
    if(msg==WM_TIMER) {
        if(GetTickCount64()<due)return 0;
        if(phase==0) {
            if(!TargetReady()){Stop(L"test window or WeType is not active");return 0;}
            HIMC context=ImmGetContext(edit);
            if(context){ImmSetOpenStatus(context,TRUE);ImmReleaseContext(edit,context);}
            phase=1;due=GetTickCount64()+500;return 0;
        }
        if(phase==1) {
            if(cases[sample][position]) {
                if(Key(static_cast<WORD>(towupper(cases[sample][position]))))++position;
                due=GetTickCount64()+100;
            }else{phase=2;due=GetTickCount64()+1000;}
        }else if(phase==2) {
            Log("input",cases[sample]);Log("composition",Composition());
            if(Key(VK_SPACE)){phase=3;due=GetTickCount64()+600;}
        }else if(phase==3) {
            if(!TargetReady()){Stop(L"focus changed while waiting for commit");return 0;}
            wchar_t text[4096]{};GetWindowTextW(edit,text,4096);Log("committed",text);
            bool chinese=false;for(auto ch:std::wstring(text))if(ch>=0x4e00&&ch<=0x9fff)chinese=true;
            if(!chinese){Stop(L"no Han commit; probe not validated");return 0;}
            if(++sample==3){exitStatus=0;Stop(L"three real key-input commits captured");return 0;}
            SetWindowTextW(edit,L"");position=0;phase=1;due=GetTickCount64()+500;
        }
        return 0;
    }
    if(msg==WM_DESTROY){PostQuitMessage(0);return 0;}
    return DefWindowProcW(hwnd,msg,wp,lp);
}
int wmain(int argc,wchar_t** argv) {
    if(argc!=2)return 2;
    if(_wfopen_s(&logFile,argv[1],L"wb")||!logFile)return 3;
    CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
    CLSIDFromString(L"{86598FB9-66A2-463E-B9C2-AEB906D477AD}",&service);
    CLSIDFromString(L"{607FDF85-FCC8-4DBD-A365-41296F980C9C}",&profile);
    HRESULT hr=CoCreateInstance(CLSID_TF_InputProcessorProfiles,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&profiles));
    if(FAILED(hr)){fprintf(logFile,"profile manager failed %08lx\n",hr);return 4;}
    hr=CoCreateInstance(CLSID_TF_ThreadMgr,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&threadManager));
    TfClientId client=0;
    if(FAILED(hr)||FAILED(threadManager->Activate(&client)))return 5;
    LoadLibraryW(L"Msftedit.dll");
    WNDCLASSW wc{};wc.lpfnWndProc=Proc;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"WeTypeOfflineProbe";
    wc.hbrBackground=(HBRUSH)(COLOR_WINDOW+1);RegisterClassW(&wc);
    window=CreateWindowW(wc.lpszClassName,L"WeType isolated input probe (auto closes)",WS_OVERLAPPEDWINDOW,
        100,100,760,240,nullptr,nullptr,wc.hInstance,nullptr);
    edit=CreateWindowExW(WS_EX_CLIENTEDGE,L"RICHEDIT50W",L"",WS_CHILD|WS_VISIBLE|WS_TABSTOP|ES_MULTILINE,
        15,20,700,140,window,nullptr,wc.hInstance,nullptr);
    ShowWindow(window,SW_SHOW);SetForegroundWindow(window);SetFocus(edit);
    HKL language=LoadKeyboardLayoutW(L"00000804",0);
    if(language)ActivateKeyboardLayout(language,0);
    hr=profiles->ActivateProfile(TF_PROFILETYPE_INPUTPROCESSOR,0x804,service,profile,nullptr,
        TF_IPPMF_FORPROCESS|TF_IPPMF_DONTCARECURRENTINPUTLANGUAGE);
    fprintf(logFile,"activate\t%08lx\n",hr);fflush(logFile);
    if(FAILED(hr)){DestroyWindow(window);}else{due=GetTickCount64()+1500;SetTimer(window,1,50,nullptr);}
    MSG msg{};while(GetMessageW(&msg,nullptr,0,0)>0){TranslateMessage(&msg);DispatchMessageW(&msg);}
    threadManager->Deactivate();threadManager->Release();profiles->Release();CoUninitialize();
    fclose(logFile);return exitStatus;
}
