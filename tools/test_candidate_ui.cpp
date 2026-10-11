// Isolated COM contract tests and factory for the unmodified Pain presenter.
#include "../BimeTSF2/SampleIME/CandidateUi.h"
#include <cstdio>
#include <cstdlib>
#include <functional>
static LONG dllRefs=0;
#ifdef _M_IX86
#pragma comment(linker, "/export:CreateTestCandidate=_CreateTestCandidate")
#endif
void DllAddRef() { ++dllRefs; }
void DllRelease() { --dllRefs; }
static void Check(bool ok,const char* why) { if(!ok) { std::printf("FAIL %s\n",why); std::exit(1); } }
static std::shared_ptr<CandidateSnapshot> Menu(UINT page)
{
    auto menu=std::make_shared<CandidateSnapshot>();
    menu->instance="abc-1"; menu->version="1"; menu->available=menu->composing=true;
    menu->strings={L"first",L"second",L"third",L"fourth",L"fifth",L"remaining",L"\xd840\xdc00" L"e\x0301"};
    menu->pages={0,5}; menu->page=page; menu->selection=page ? 6 : 0;
    return menu;
}
extern "C" __declspec(dllexport) HRESULT __cdecl CreateTestCandidate(UINT page, IUnknown** out)
{ if (!out || page>1) return E_INVALIDARG; *out=new CandidateUi(nullptr,Menu(page)); return S_OK; }

class Manager final : public ITfUIElementMgr
{
public:
    ULONG refs=1;
    ITfUIElement* element=nullptr;
    std::vector<char> events;
    std::function<void()> duringBegin, duringUpdate;
    ~Manager() { if (element) element->Release(); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** out) override
    { if(!out)return E_POINTER; *out=nullptr; if(id!=IID_IUnknown && id!=IID_ITfUIElementMgr)return E_NOINTERFACE; *out=this; AddRef();return S_OK; }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { return --refs; }
    HRESULT STDMETHODCALLTYPE BeginUIElement(ITfUIElement* value,BOOL* show,DWORD* id) override
    { events.push_back('B'); if(element)element->Release(); element=value; element->AddRef(); *show=FALSE; *id=42; if(duringBegin)duringBegin();return S_OK; }
    HRESULT STDMETHODCALLTYPE UpdateUIElement(DWORD id) override
    { Check(id==42,"update id"); events.push_back('U'); if(duringUpdate)duringUpdate();return S_OK; }
    HRESULT STDMETHODCALLTYPE EndUIElement(DWORD id) override
    { Check(id==42,"end id"); events.push_back('E');return S_OK; }
    HRESULT STDMETHODCALLTYPE GetUIElement(DWORD,ITfUIElement** out) override
    { *out=element; if(element)element->AddRef(); return S_OK; }
    HRESULT STDMETHODCALLTYPE EnumUIElements(IEnumTfUIElements**) override { return E_NOTIMPL; }
};

class Document final : public ITfDocumentMgr
{
public:
    ULONG refs=1;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** out) override
    { if(!out)return E_POINTER; *out=nullptr; if(id!=IID_IUnknown && id!=IID_ITfDocumentMgr)return E_NOINTERFACE; *out=this;AddRef();return S_OK; }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { return --refs; }
    HRESULT STDMETHODCALLTYPE CreateContext(TfClientId,DWORD,IUnknown*,ITfContext**,TfEditCookie*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE Push(ITfContext*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE Pop(DWORD) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetTop(ITfContext**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetBase(ITfContext**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE EnumContexts(IEnumTfContexts**) override { return E_NOTIMPL; }
};

int main()
{
    CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
    {
        Document doc;
        auto element=new CandidateUi(&doc,Menu(0));
        Check(doc.refs==2,"document retained by UIElement");
        ITfDocumentMgr* returned=nullptr;
        element->GetDocumentMgr(&returned);
        Check(returned==&doc && doc.refs==3,"document getter AddRef");
        returned->Release(); element->Release(); Check(doc.refs==1,"document reference released");
    }
    {
        Manager manager; CandidateUiPublisher publisher;
        publisher.Publish(&manager,nullptr,Menu(1));
        Check(manager.events==std::vector<char>({'B','U'}),"pbShow false still updates");
        ITfCandidateListUIElement* element=nullptr;
        Check(SUCCEEDED(manager.element->QueryInterface(IID_ITfCandidateListUIElement,reinterpret_cast<void**>(&element))),"candidate QI");
        DWORD flags=0; element->GetUpdatedFlags(&flags); Check(flags==0x3f,"initial complete flags");
        void* behavior=nullptr; Check(element->QueryInterface(IID_ITfCandidateListUIElementBehavior,&behavior)==E_NOINTERFACE,"no mouse behavior");
        UINT count,selection,page,pages;
        element->GetCount(&count); element->GetSelection(&selection); element->GetCurrentPage(&page);
        Check(element->GetPageIndex(nullptr,0,&pages)==S_OK && pages==2,"Pain size query");
        UINT starts[2]; Check(element->GetPageIndex(starts,2,&pages)==S_OK,"Pain second page query");
        Check(count==7 && selection==6 && page==1 && starts[1]==5,"page boundary/selection");
        BSTR text=nullptr; Check(element->GetString(starts[page],&text)==S_OK && std::wstring(text)==L"remaining","current page text"); SysFreeString(text);
        element->GetString(6,&text); Check(SysStringLen(text)==4,"supplementary plus combining UTF16"); SysFreeString(text);
        Check(element->GetString(7,&text)==E_INVALIDARG && text==nullptr,"index bounds");
        Check(element->GetCount(nullptr)==E_POINTER,"null getter");
        Check(element->GetPageIndex(starts,1,&pages)==S_FALSE && pages==2,"small page buffer");
        publisher.Publish(&manager,nullptr,Menu(1)); Check(manager.events.size()==2,"unchanged no update");
        auto next=Menu(0); publisher.Publish(&manager,nullptr,next);
        element->GetUpdatedFlags(&flags); Check(flags==(TF_CLUIE_SELECTION|TF_CLUIE_CURRENTPAGE),"precise changed flags");
        publisher.End(); publisher.End(); Check(manager.events==std::vector<char>({'B','U','U','E'}),"single end");
        element->GetCount(&count); Check(count==7,"retained COM snapshot survives end"); element->Release();
        manager.duringBegin=[&] { publisher.End(); };
        publisher.Publish(&manager,nullptr,Menu(0));
        Check(manager.events.back()=='E' && manager.events[manager.events.size()-2]=='B',"focus reentry during begin ends without update");
        manager.duringBegin={}; manager.duringUpdate=[&] { publisher.End(); };
        publisher.Publish(&manager,nullptr,Menu(0)); Check(manager.events.back()=='E',"update reentry lifetime");
        manager.duringUpdate={};
        auto empty=Menu(0); empty->strings.clear(); empty->pages.clear(); empty->selection=0;
        publisher.Publish(&manager,nullptr,empty); Check(manager.events.back()=='U',"empty composing placeholder");
        empty=std::make_shared<CandidateSnapshot>(*empty); empty->composing=false;
        publisher.Publish(&manager,nullptr,empty); Check(manager.events.back()=='E',"commit ends");
    }
    Check(dllRefs==0,"no DLL lifetime leak");
    CoUninitialize(); std::puts("Candidate UI COM, Pain getter order, notification and reentry checks passed");
}
