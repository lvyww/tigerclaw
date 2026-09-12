#pragma once
#include "WinResources.h"
#include <d2d1.h>
#include <dwrite_3.h>

namespace tiger::overlay
{
    // Small per-window premultiplied DIBs preserve per-pixel transparency without
    // a WPF runtime or a continuously running swap chain. Draw only on changes.
    class Renderer
    {
#ifdef TIGERCLAW_RENDER_PROBE
        friend class RendererProbe;
        std::uint64_t creations_[4]{}; // format, layout, DIB, render target
#endif
        ComPtr<ID2D1Factory> factory_;
        ComPtr<IDWriteFactory> textFactory_;
        ComPtr<IDWriteFontCollection> localFonts_;
        ComPtr<ID2D1DCRenderTarget> target_;
        ComPtr<IDWriteTextFormat> format_;
        ComPtr<IDWriteTextLayout> layout_;
        std::wstring formatFamily_;
        IDWriteFontCollection* formatFonts_ = nullptr; // owned by localFonts_
        float formatSize_ = 0, formatLineHeight_ = 0;
        bool formatStatus_ = false;
        Text layoutText_;
        DWRITE_TEXT_METRICS layoutMetrics_{};
        HDC dc_ = nullptr;
        HBITMAP bitmap_ = nullptr;
        HGDIOBJ original_ = nullptr;
        SIZE size_{};
        bool frameReady_ = false;
        void Surface(int width, int height);
        void ReleaseSurface();
        void LoadLocalFonts(const std::wstring& directory);
    public:
        explicit Renderer(const std::wstring& directory);
        ~Renderer();
        Renderer(const Renderer&) = delete;
        Renderer& operator=(const Renderer&) = delete;
        SIZE Render(HWND window, const State& state, const Display& display, UINT dpi, bool status = false);
        SIZE Prepare(const State& state, const Display& display, UINT dpi, bool status = false, const SIZE* frameSize = nullptr);
        void Present(HWND window, const POINT* destination = nullptr);
    };
}
