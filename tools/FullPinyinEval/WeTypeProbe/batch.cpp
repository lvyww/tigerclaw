#include <windows.h>
#include <msctf.h>
#include <imm.h>
#include <cstdio>
#include <string>
#include <vector>
#include <fstream>
#include <sstream>
#include <cwctype>
#include <share.h>

#ifndef PROBE_NAME
#define PROBE_NAME L"WeType"
#define PROBE_SERVICE L"{86598FB9-66A2-463E-B9C2-AEB906D477AD}"
#define PROBE_PROFILE L"{607FDF85-FCC8-4DBD-A365-41296F980C9C}"
#endif
#ifndef PROBE_OPAQUE_COMPOSITION
#define PROBE_OPAQUE_COMPOSITION 0
#endif

struct Case { std::string id, code, target, initialStage; };
static HWND window,edit;
static ITfInputProcessorProfileMgr* profiles;
static ITfThreadMgr* manager;
static GUID service,profile;
static FILE* output;
static std::vector<Case> cases;
static size_t sample=0,position=0;
static int phase=0,spaces=0,keyDelay=30,finalWait=1000,status=1;
static ULONGLONG due=0,started=0,lastKey=0,commitStart=0,stableSince=0,pausedSince=0;
static std::wstring compositionBefore,textBefore,lastText;
static bool rawVerified=false,interrupted=false;
static bool continuous=false,slowReview=false,stopping=false;
static std::wstring stopPath;

static std::string Utf8(const std::wstring& s) {
    if(s.empty())return {};
    int n=WideCharToMultiByte(CP_UTF8,0,s.data(),(int)s.size(),nullptr,0,nullptr,nullptr);
    std::string out(n,'\0');WideCharToMultiByte(CP_UTF8,0,s.data(),(int)s.size(),out.data(),n,nullptr,nullptr);return out;
}
static std::string Quote(const std::string& s) {
    std::string out="\"";
    for(unsigned char c:s) {
        if(c=='"'||c=='\\'){out+='\\';out+=c;}
        else if(c<32){char b[8];sprintf_s(b,"\\u%04x",c);out+=b;}
        else out+=c;
    }return out+'"';
}
static void Event(const char* name,const char* detail) {
    fprintf(output,"{\"event\":%s,\"sample\":%zu,\"detail\":%s}\n",Quote(name).c_str(),sample,Quote(detail).c_str());fflush(output);
}
static void Stop(const char* why) {
    if(stopping)return;stopping=true;
    Event("stop",why);KillTimer(window,1);
    HIMC context=ImmGetContext(edit);
    if(context){ImmNotifyIME(context,NI_COMPOSITIONSTR,CPS_CANCEL,0);ImmReleaseContext(edit,context);}
    DestroyWindow(window);
}
static bool Ready() {
    if(GetForegroundWindow()!=window||GetFocus()!=edit)return false;
    for(int vk:{VK_SHIFT,VK_CONTROL,VK_MENU,VK_LWIN,VK_RWIN})if(GetAsyncKeyState(vk)&0x8000)return false;
    TF_INPUTPROCESSORPROFILE active{};
    return SUCCEEDED(profiles->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD,&active)) &&
        IsEqualGUID(active.clsid,service)&&IsEqualGUID(active.guidProfile,profile);
}
static bool Key(WORD vk) {
    if(!Ready())return false;
    INPUT keys[2]{};keys[0].type=keys[1].type=INPUT_KEYBOARD;
    keys[0].ki.wVk=keys[1].ki.wVk=vk;keys[1].ki.dwFlags=KEYEVENTF_KEYUP;
    if(SendInput(2,keys,sizeof(INPUT))!=2){Stop("SendInput failed");return false;}return true;
}
class CompositionReader final : public ITfEditSession {
    LONG refs=1;
    ITfContext* context;
public:
    std::wstring text;
    explicit CompositionReader(ITfContext* value):context(value){context->AddRef();}
    ~CompositionReader(){context->Release();}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** result) override {
        if(!result)return E_POINTER;
        *result=nullptr;
        if(iid==IID_IUnknown||iid==IID_ITfEditSession){*result=static_cast<ITfEditSession*>(this);AddRef();return S_OK;}
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override{return InterlockedIncrement(&refs);}
    ULONG STDMETHODCALLTYPE Release() override{auto n=InterlockedDecrement(&refs);if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE DoEditSession(TfEditCookie cookie) override {
        ITfContextComposition* compositions=nullptr;
        if(FAILED(context->QueryInterface(IID_PPV_ARGS(&compositions))))return S_OK;
        IEnumITfCompositionView* enumerator=nullptr;
        if(SUCCEEDED(compositions->EnumCompositions(&enumerator))){
            ITfCompositionView* view=nullptr;ULONG fetched=0;
            while(enumerator->Next(1,&view,&fetched)==S_OK){
                ITfRange* range=nullptr;
                if(SUCCEEDED(view->GetRange(&range))){
                    WCHAR buffer[512];ULONG count=0;
                    if(SUCCEEDED(range->GetText(cookie,0,buffer,512,&count)))text.append(buffer,count);
                    range->Release();
                }
                view->Release();
            }
            enumerator->Release();
        }
        compositions->Release();return S_OK;
    }
};
static TfClientId probeClient=0;
static std::wstring TsfComposition() {
    ITfDocumentMgr* document=nullptr;ITfContext* context=nullptr;
    std::wstring text;
    if(SUCCEEDED(manager->GetFocus(&document))&&document){
        if(SUCCEEDED(document->GetTop(&context))&&context){
            auto reader=new CompositionReader(context);HRESULT session=E_FAIL;
            if(SUCCEEDED(context->RequestEditSession(probeClient,reader,TF_ES_SYNC|TF_ES_READ,&session))&&SUCCEEDED(session))text=reader->text;
            reader->Release();context->Release();
        }
        document->Release();
    }
    return text;
}
static std::wstring Composition() {
    HIMC context=ImmGetContext(edit);if(!context)return TsfComposition();
    LONG bytes=ImmGetCompositionStringW(context,GCS_COMPSTR,nullptr,0);
    std::wstring s(bytes>0?bytes/sizeof(wchar_t):0,L'\0');
    if(bytes>0)ImmGetCompositionStringW(context,GCS_COMPSTR,s.data(),bytes);
    ImmReleaseContext(edit,context);return s.empty()?TsfComposition():s;
}
static std::wstring Text() {
    int n=GetWindowTextLengthW(edit);std::wstring s(n+1,L'\0');
    s.resize(GetWindowTextW(edit,s.data(),n+1));return s;
}
static std::string Letters(const std::wstring& s) {
    std::string r;for(wchar_t c:s)if(c>=L'a'&&c<=L'z')r+=(char)c;return r;
}
static void Begin() {
    if(continuous){keyDelay=slowReview?100:30;finalWait=slowReview?3000:1000;}
    if(PROBE_OPAQUE_COMPOSITION&&!Key(VK_ESCAPE))return;
    // Cancel only this control's composition; reset surrounding text between cases.
    HIMC context=ImmGetContext(edit);
    if(context){ImmNotifyIME(context,NI_COMPOSITIONSTR,CPS_CANCEL,0);ImmSetOpenStatus(context,TRUE);ImmReleaseContext(edit,context);}
    SetWindowTextW(edit,L"");position=0;spaces=0;interrupted=false;rawVerified=false;
    compositionBefore.clear();textBefore.clear();lastText.clear();phase=1;started=GetTickCount64();lastKey=started;due=started+200;
    auto title=std::wstring(PROBE_NAME)+L" black-box test "+std::to_wstring(sample+1)+L" / "+std::to_wstring(cases.size())+L" (focus loss pauses input)";
    if(continuous)title+=slowReview?L" [SLOW REVIEW; close to stop]":L" [FAST; close to stop]";
    SetWindowTextW(window,title.c_str());
}
static void Finish(const char* resultStatus) {
    auto now=GetTickCount64();auto text=Text();
    bool valid=std::string(resultStatus)=="ok"&&!interrupted&&Composition().empty()&&
        (rawVerified||(PROBE_OPAQUE_COMPOSITION&&position==cases[sample].code.size()&&!text.empty()));
    bool correct=Utf8(text)==cases[sample].target;
    fprintf(output,"{\"event\":\"result\",\"id\":%s,\"code\":%s,\"text\":%s,\"compositionBefore\":%s,\"textBefore\":%s,\"compositionAfter\":%s,\"status\":%s,\"rawVerified\":%s,\"interrupted\":%s,\"spaces\":%d,\"keyDelayMs\":%d,\"typingWallMs\":%llu,\"caseWallMs\":%llu,\"postInputWaitMs\":%llu}\n",
        Quote(cases[sample].id).c_str(),Quote(cases[sample].code).c_str(),Quote(Utf8(text)).c_str(),Quote(Utf8(compositionBefore)).c_str(),Quote(Utf8(textBefore)).c_str(),Quote(Utf8(Composition())).c_str(),Quote(resultStatus).c_str(),rawVerified?"true":"false",interrupted?"true":"false",spaces,keyDelay,lastKey-started,now-started,now-lastKey);
    fflush(output);
    if(continuous) {
        fprintf(output,"{\"event\":\"decision\",\"id\":%s,\"stage\":%s,\"valid\":%s,\"correct\":%s,\"finalWaitMs\":%d}\n",
            Quote(cases[sample].id).c_str(),Quote(slowReview?"slow":"fast").c_str(),valid?"true":"false",correct?"true":"false",finalWait);fflush(output);
        if(interrupted){Event("retry","interrupted attempt excluded; restart same stage");Begin();return;}
        if(!slowReview&&(!valid||!correct)){slowReview=true;Event("review","fast attempt failed; retry slowly");Begin();return;}
    }
    if(++sample==cases.size()){
        if(continuous){phase=4;Event("exhausted","all queued unique cases collected; no repeated learning runs");if(PROBE_OPAQUE_COMPOSITION){status=0;Stop("completed");}else SetWindowTextW(window,L"WeType collection complete - close to stop");return;}
        status=0;Stop("completed");return;
    }
    if(continuous)slowReview=cases[sample].initialStage=="slow";
    Begin();
}
static LRESULT CALLBACK Proc(HWND hwnd,UINT msg,WPARAM wp,LPARAM lp) {
    if(msg==WM_TIMER) {
        auto now=GetTickCount64();
        if(!stopPath.empty()&&GetFileAttributesW(stopPath.c_str())!=INVALID_FILE_ATTRIBUTES){status=0;Stop("manual stop file");return 0;}
        if(phase==4)return 0;
        if(!Ready()) {
            if(!pausedSince){pausedSince=now;Event("pause","focus, modifier or input profile changed");}
            if(!continuous&&now-pausedSince>120000)Stop("focus unavailable for 120 seconds");
            return 0;
        }
        if(pausedSince) {
            Event("resume","target ready; interrupted attempt will be recorded");pausedSince=0;
            if(phase>0){interrupted=true;Finish("interrupted");return 0;}
        }
        if(now<due)return 0;
        if(phase==0){Begin();return 0;}
        if(phase==1) {
            if(position<cases[sample].code.size()) {
                char c=cases[sample].code[position];WORD vk=c=='\''?VK_OEM_7:static_cast<WORD>(c-'a'+'A');
                if(Key(vk)){++position;lastKey=GetTickCount64();due=lastKey+keyDelay;}
            }else{phase=2;due=now+finalWait;}
        }else if(phase==2) {
            compositionBefore=Composition();textBefore=Text();rawVerified=Letters(compositionBefore)==cases[sample].code;
            commitStart=now;stableSince=now;lastText=Text();phase=3;
            if(PROBE_OPAQUE_COMPOSITION||!compositionBefore.empty()){if(Key(VK_SPACE))++spaces;}
            due=now+200;
        }else if(phase==3) {
            auto text=Text();auto composition=Composition();
            if(text!=lastText){lastText=text;stableSince=now;}
            if(composition.empty()&&now-stableSince>=400){Finish("ok");return 0;}
            if(now-commitStart>8000||spaces>=20){Finish("commit_timeout");return 0;}
            if(!composition.empty()&&now>=due){if(Key(VK_SPACE))++spaces;due=now+400;}
        }
        return 0;
    }
    if(msg==WM_CLOSE){status=0;Stop("manual window close");return 0;}
    if(msg==WM_DESTROY){PostQuitMessage(0);return 0;}
    return DefWindowProcW(hwnd,msg,wp,lp);
}
int wmain(int argc,wchar_t** argv) {
    if(argc!=4&&argc!=5)return 2;
    continuous=std::wstring(argv[3])==L"--continuous";
    if(!continuous){keyDelay=_wtoi(argv[3]);if(keyDelay<20||keyDelay>500)return 2;}
    if(argc==5)finalWait=_wtoi(argv[4]);if(finalWait<1000||finalWait>15000)return 2;
    std::ifstream input(argv[1]);std::string line;
    while(std::getline(input,line)) {
        if(!line.empty()&&line.back()=='\r')line.pop_back();
        auto tab=line.find('\t');if(tab==std::string::npos)return 3;
        Case c{line.substr(0,tab),line.substr(tab+1),{}, {}};
        if(continuous){
            auto second=c.code.find('\t');if(second==std::string::npos)return 3;
            auto rest=c.code.substr(second+1);c.code.resize(second);
            auto third=rest.find('\t');if(third==std::string::npos)return 3;
            c.target=rest.substr(0,third);c.initialStage=rest.substr(third+1);
            if(c.target.empty()||(c.initialStage!="fast"&&c.initialStage!="slow"))return 3;
        }
        if(c.id.empty()||c.code.empty()||c.code.find_first_not_of("abcdefghijklmnopqrstuvwxyz'")!=std::string::npos)return 3;
        cases.push_back(c);
    }
    if(cases.empty()||GetFileAttributesW(argv[2])!=INVALID_FILE_ATTRIBUTES)return 3;
    if(continuous){slowReview=cases[0].initialStage=="slow";stopPath=std::wstring(argv[2])+L".stop";}
    output=_wfsopen(argv[2],L"wb",_SH_DENYNO);if(!output)return 3;
    CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
    CLSIDFromString(PROBE_SERVICE,&service);
    CLSIDFromString(PROBE_PROFILE,&profile);
    HRESULT hr=CoCreateInstance(CLSID_TF_InputProcessorProfiles,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&profiles));
    if(FAILED(hr)){Event("error","profile manager creation");return 4;}
    hr=CoCreateInstance(CLSID_TF_ThreadMgr,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&manager));
    if(FAILED(hr)||FAILED(manager->Activate(&probeClient)))return 5;
    LoadLibraryW(L"Msftedit.dll");
    WNDCLASSW wc{};wc.lpfnWndProc=Proc;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"WeTypeBatchProbe";
    wc.hbrBackground=(HBRUSH)(COLOR_WINDOW+1);RegisterClassW(&wc);
    window=CreateWindowW(wc.lpszClassName,L"WeType black-box input test",WS_OVERLAPPEDWINDOW,100,100,960,300,nullptr,nullptr,wc.hInstance,nullptr);
    edit=CreateWindowExW(WS_EX_CLIENTEDGE,L"RICHEDIT50W",L"",WS_CHILD|WS_VISIBLE|WS_TABSTOP|ES_MULTILINE,15,20,900,200,window,nullptr,wc.hInstance,nullptr);
    ShowWindow(window,SW_SHOW);SetForegroundWindow(window);SetFocus(edit);
    HKL language=LoadKeyboardLayoutW(L"00000804",0);if(language)ActivateKeyboardLayout(language,0);
    hr=profiles->ActivateProfile(TF_PROFILETYPE_INPUTPROCESSOR,0x804,service,profile,nullptr,TF_IPPMF_FORPROCESS|TF_IPPMF_DONTCARECURRENTINPUTLANGUAGE);
    if(FAILED(hr)){Event("error","profile activation");DestroyWindow(window);}
    else {Event("start",Utf8(std::wstring(PROBE_NAME)+L"; actual virtual keys; current user settings").c_str());due=GetTickCount64()+1500;SetTimer(window,1,15,nullptr);}
    MSG msg{};while(GetMessageW(&msg,nullptr,0,0)>0){TranslateMessage(&msg);DispatchMessageW(&msg);}
    manager->Deactivate();manager->Release();profiles->Release();CoUninitialize();fclose(output);return status;
}
