#pragma once
#include <windows.h>
#include <msctf.h>
#include <string>
#include <vector>
#include <memory>
#include "Globals.h"

struct CandidateSnapshot
{
    std::string instance, version;
    ULONGLONG generation = 0;
    bool available = false, composing = false, pending = false;
    std::vector<std::wstring> strings;
    std::vector<UINT> pages;
    UINT page = 0, selection = 0;
};

// Apartment-owned, read-only projection. No getter performs IPC or dispatches
// messages. Hosts retaining this object after End see its last immutable menu.
class CandidateUi final : public ITfCandidateListUIElement
{
    LONG refs_ = 1;
    ITfDocumentMgr* document_;
    std::shared_ptr<const CandidateSnapshot> snapshot_;
    DWORD flags_ = 0x3f;
public:
    CandidateUi(ITfDocumentMgr* document, std::shared_ptr<const CandidateSnapshot> snapshot)
        : document_(document), snapshot_(std::move(snapshot))
    { if (document_) document_->AddRef(); DllAddRef(); }
    ~CandidateUi() { if (document_) document_->Release(); DllRelease(); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** out) override
    {
        if (!out) return E_POINTER;
        *out=nullptr;
        if (id==IID_IUnknown || id==IID_ITfUIElement || id==IID_ITfCandidateListUIElement)
        { *out=static_cast<ITfCandidateListUIElement*>(this); AddRef(); return S_OK; }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs_); }
    ULONG STDMETHODCALLTYPE Release() override
    { auto refs=InterlockedDecrement(&refs_); if (!refs) delete this; return refs; }
    HRESULT STDMETHODCALLTYPE GetDescription(BSTR* out) override
    { if (!out) return E_POINTER; *out=SysAllocString(L"TigerClaw candidates"); return *out ? S_OK : E_OUTOFMEMORY; }
    HRESULT STDMETHODCALLTYPE GetGUID(GUID* out) override
    {
        if (!out) return E_POINTER;
        static const GUID id={0x17222896,0x4264,0x4364,{0xa4,0x20,0x54,0x63,0x61,0x6e,0x64,0x31}};
        *out=id; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Show(BOOL) override { return S_OK; } // Dual-window compatibility.
    HRESULT STDMETHODCALLTYPE IsShown(BOOL* out) override
    { if (!out) return E_POINTER; *out=IsWindowVisible(FindWindowW(nullptr,L"TigerClawCandidate")); return S_OK; }
    HRESULT STDMETHODCALLTYPE GetUpdatedFlags(DWORD* out) override
    { if (!out) return E_POINTER; *out=flags_; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetDocumentMgr(ITfDocumentMgr** out) override
    { if (!out) return E_POINTER; *out=document_; if (*out) (*out)->AddRef(); return S_OK; }
    HRESULT STDMETHODCALLTYPE GetCount(UINT* out) override
    { if (!out) return E_POINTER; *out=static_cast<UINT>(snapshot_->strings.size()); return S_OK; }
    HRESULT STDMETHODCALLTYPE GetSelection(UINT* out) override
    { if (!out) return E_POINTER; *out=snapshot_->selection; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetString(UINT index, BSTR* out) override
    {
        if (!out) return E_POINTER; *out=nullptr;
        if (index>=snapshot_->strings.size()) return E_INVALIDARG;
        const auto& text=snapshot_->strings[index];
        *out=SysAllocStringLen(text.data(),static_cast<UINT>(text.size()));
        return *out ? S_OK : E_OUTOFMEMORY;
    }
    HRESULT STDMETHODCALLTYPE GetPageIndex(UINT* pages, UINT capacity, UINT* count) override
    {
        if (!count || (!pages && capacity)) return E_POINTER;
        *count=static_cast<UINT>(snapshot_->pages.size());
        if (!capacity) return S_OK; // Pain's size query must not throw.
        UINT copied=(capacity<*count ? capacity : *count);
        for (UINT i=0;i<copied;++i) pages[i]=snapshot_->pages[i];
        return capacity<*count ? S_FALSE : S_OK;
    }
    HRESULT STDMETHODCALLTYPE SetPageIndex(UINT*, UINT) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetCurrentPage(UINT* out) override
    { if (!out) return E_POINTER; *out=snapshot_->page; return S_OK; }
    DWORD Update(std::shared_ptr<const CandidateSnapshot> next)
    {
        flags_=0;
        if (next->strings.size()!=snapshot_->strings.size()) flags_|=TF_CLUIE_COUNT;
        if (next->strings!=snapshot_->strings) flags_|=TF_CLUIE_STRING;
        if (next->selection!=snapshot_->selection) flags_|=TF_CLUIE_SELECTION;
        if (next->pages!=snapshot_->pages) flags_|=TF_CLUIE_PAGEINDEX;
        if (next->page!=snapshot_->page) flags_|=TF_CLUIE_CURRENTPAGE;
        snapshot_=std::move(next); return flags_;
    }
};

class CandidateUiPublisher
{
    CandidateUi* element_ = nullptr;
    ITfUIElementMgr* manager_ = nullptr;
    DWORD id_ = TF_INVALID_UIELEMENTID;
    bool notifying_ = false;
    ULONGLONG epoch_ = 0;
    std::string instance_;
public:
    ~CandidateUiPublisher() { End(); }
    void End()
    {
        ++epoch_;
        auto element=element_; auto manager=manager_; auto id=id_;
        element_=nullptr; manager_=nullptr; id_=TF_INVALID_UIELEMENTID; instance_.clear();
        if (manager && id!=TF_INVALID_UIELEMENTID) manager->EndUIElement(id);
        if (element) element->Release();
        if (manager) manager->Release();
    }
    void Publish(ITfUIElementMgr* manager, ITfDocumentMgr* document, std::shared_ptr<const CandidateSnapshot> snapshot)
    {
        if (!snapshot || !snapshot->available || !snapshot->composing) { End(); return; }
        if (notifying_ || !manager) return;
        if (element_ && instance_!=snapshot->instance) End();
        if (!element_)
        {
            auto element=new (std::nothrow) CandidateUi(document,snapshot);
            if (!element) return;
            element_=element; manager_=manager; manager_->AddRef(); instance_=snapshot->instance;
            auto epoch=epoch_;
            element->AddRef(); manager->AddRef();
            BOOL show=TRUE; DWORD id=TF_INVALID_UIELEMENTID;
            notifying_=true;
            auto hr=manager->BeginUIElement(element,&show,&id);
            if (epoch==epoch_ && SUCCEEDED(hr))
            {
                id_=id;
                manager->UpdateUIElement(id); // Even when pbShow == FALSE.
            }
            else
            {
                if (SUCCEEDED(hr)) manager->EndUIElement(id);
                if (element_==element) End();
            }
            notifying_=false;
            element->Release(); manager->Release();
        }
        else if (element_->Update(snapshot))
        {
            manager_->AddRef(); auto active=manager_;
            notifying_=true; active->UpdateUIElement(id_); notifying_=false;
            active->Release();
        }
    }
};
