#include "Renderer.h"
#include <algorithm>
#include <cmath>
#include <filesystem>
#include <vector>

namespace tiger::overlay
{
    static D2D1_COLOR_F Rgba(std::uint32_t argb)
    {
        return {((argb >> 16) & 255) / 255.0f, ((argb >> 8) & 255) / 255.0f,
            (argb & 255) / 255.0f, (argb >> 24) / 255.0f};
    }
    Renderer::Renderer(const std::wstring& directory)
    {
        CheckHr(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, factory_.GetAddressOf()));
        CheckHr(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
            reinterpret_cast<IUnknown**>(textFactory_.GetAddressOf())));
        LoadLocalFonts(directory);
    }
    Renderer::~Renderer() { ReleaseSurface(); }
    void Renderer::ReleaseSurface()
    {
        target_.Reset();
        if (dc_ && original_) SelectObject(dc_, original_);
        if (bitmap_) DeleteObject(bitmap_);
        if (dc_) DeleteDC(dc_);
        dc_ = nullptr; bitmap_ = nullptr; original_ = nullptr; size_ = {};
    }
    void Renderer::Surface(int width, int height)
    {
        if (!dc_)
        {
            dc_ = CreateCompatibleDC(nullptr);
            if (!dc_) throw std::runtime_error("CreateCompatibleDC failed");
        }
        if (!bitmap_ || width != size_.cx || height != size_.cy)
        {
            // Only replace the bitmap. Keep the DC and Direct2D target; Render
            // rebinds the target before drawing. Exact sizing avoids retaining
            // a large high-water allocation after a long composition shrinks.
            BITMAPINFO info{};
            info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
            info.bmiHeader.biWidth = width; info.bmiHeader.biHeight = -height;
            info.bmiHeader.biPlanes = 1; info.bmiHeader.biBitCount = 32; info.bmiHeader.biCompression = BI_RGB;
            void* pixels = nullptr;
            HBITMAP next = CreateDIBSection(dc_, &info, DIB_RGB_COLORS, &pixels, nullptr, 0);
            if (!next) throw std::runtime_error("CreateDIBSection failed");
            HGDIOBJ previous = SelectObject(dc_, next);
            if (!previous || previous == HGDI_ERROR)
            {
                DeleteObject(next);
                throw std::runtime_error("SelectObject failed");
            }
            if (!bitmap_) original_ = previous;
            else DeleteObject(bitmap_);
            bitmap_ = next;
            size_ = {width, height};
#ifdef TIGERCLAW_RENDER_PROBE
            ++creations_[2];
#endif
        }
        if (!target_)
        {
            auto properties = D2D1::RenderTargetProperties(D2D1_RENDER_TARGET_TYPE_SOFTWARE,
                D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
            CheckHr(factory_->CreateDCRenderTarget(&properties, target_.GetAddressOf()));
#ifdef TIGERCLAW_RENDER_PROBE
            ++creations_[3];
#endif
        }
    }
    void Renderer::LoadLocalFonts(const std::wstring& directory)
    {
        // Optional folder fonts; never install them globally.
        try
        {
            ComPtr<IDWriteFactory5> factory;
            if (FAILED(textFactory_.As(&factory))) return;
            ComPtr<IDWriteFontSetBuilder1> builder;
            CheckHr(factory->CreateFontSetBuilder(builder.GetAddressOf()));
            auto root = std::filesystem::path(directory) / L"\u5b57\u4f53";
            if (!std::filesystem::is_directory(root)) return;
            for (const auto& entry : std::filesystem::directory_iterator(root))
            {
                if (!entry.is_regular_file()) continue;
                ComPtr<IDWriteFontFile> file;
                if (SUCCEEDED(factory->CreateFontFileReference(entry.path().c_str(), nullptr, file.GetAddressOf())))
                    builder->AddFontFile(file.Get());
            }
            ComPtr<IDWriteFontSet> set;
            CheckHr(builder->CreateFontSet(set.GetAddressOf()));
            ComPtr<IDWriteFontCollection1> collection;
            CheckHr(factory->CreateFontCollectionFromFontSet(set.Get(), collection.GetAddressOf()));
            CheckHr(collection.As(&localFonts_));
        }
        catch (...) { localFonts_.Reset(); }
    }
    SIZE Renderer::Render(HWND window, const State& state, const Display& display, UINT dpi, bool status)
    {
        auto size = Prepare(state, display, dpi, status);
        Present(window);
        return size;
    }
    SIZE Renderer::Prepare(const State& state, const Display& display, UINT dpi, bool status, const SIZE* frameSize)
    try
    {
        frameReady_ = false;
        auto metrics = MeasureStyle(state, display.mode, display.above);
        auto palette = Theme(state.theme);
        const bool verticalSurface = !status && state.vertical() && display.mode != DisplayMode::CodeOnly;
        surfaceOpacity_ = 255; // Theme alpha controls transparency.
        Text text = display.text;
        std::wstring family = state.font.empty() ? L"Segoe UI" : Wide(state.font);
        IDWriteFontCollection* fonts = nullptr;
        if (!family.empty() && family[0] == L'#') { family.erase(0, 1); fonts = localFonts_.Get(); }
        if (status)
        {
            family = L"\u7b49\u7ebf";
            text = state.isOff ? u"\u7981" : state.status.empty() ? (state.isChinese ? u"\u4e2d" : u"EN") : state.status;
            metrics.fontSize = state.isChinese ? 15 : 14;
            if (state.nativeHook)
                palette = state.isOff ? Palette{0xff505050, 0xfff2f2f2, 0xffa8a8a8, 0, 1} : state.isChinese ?
                    Palette{0xff1f3a5a, 0xffeaf5ff, 0xff2d7dd2, 0, 1} : Palette{0xff4d5b66, 0xfff4f7fa, 0xffa8b6c4, 0, 1};
            else palette = state.isOff ? Palette{0xff6a4a3a, 0xfff3e8e2, 0xffb98a74, 0, 1} : state.isChinese ?
                Palette{0xff3a2a20, 0xfffff8f0, 0xffd96a1b, 0, 1} : Palette{0xff5a4130, 0xfffffdf8, 0xffd9c7b3, 0, 1};
        }
        float fontSize = static_cast<float>(metrics.fontSize);
        float lineHeight = status ? 0.0f : static_cast<float>(metrics.lineHeight);
        if (!format_ || family != formatFamily_ || fonts != formatFonts_ || fontSize != formatSize_ ||
            lineHeight != formatLineHeight_ || status != formatStatus_)
        {
            ComPtr<IDWriteTextFormat> nextFormat;
            CheckHr(textFactory_->CreateTextFormat(family.c_str(), fonts, status ? DWRITE_FONT_WEIGHT_SEMI_BOLD : DWRITE_FONT_WEIGHT_NORMAL,
                DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, fontSize, L"zh-CN", nextFormat.GetAddressOf()));
            CheckHr(nextFormat->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP));
            if (!status && lineHeight > 0) CheckHr(nextFormat->SetLineSpacing(DWRITE_LINE_SPACING_METHOD_UNIFORM,
                lineHeight, static_cast<float>(metrics.lineHeight * 0.8)));
            formatFamily_ = family; formatFonts_ = fonts; formatSize_ = fontSize;
            formatLineHeight_ = lineHeight; formatStatus_ = status;
            format_ = std::move(nextFormat);
            layout_.Reset();
#ifdef TIGERCLAW_RENDER_PROBE
            ++creations_[0];
#endif
        }
        // Keep one layout, not an unbounded cache of typed text. Colors,
        // selection background and target DPI do not change this DIP layout.
        std::vector<std::pair<int, int>> secondaryRanges;
        if (!status && display.candidateRanges.size() > 1)
            secondaryRanges.assign(display.candidateRanges.begin() + 1, display.candidateRanges.end());
        // Include each row's newline so natural line metrics use its own font.
        if (verticalSurface) for (auto& range : secondaryRanges)
            if (range.first + range.second < static_cast<int>(text.size()) && text[range.first + range.second] == u'\n') ++range.second;
        std::pair<int, int> codeRange = !status ? std::make_pair(display.codeStart, display.codeLength) : std::make_pair(-1, 0);
        if (codeRange.first >= 0 && codeRange.first + codeRange.second < static_cast<int>(text.size()) &&
            text[codeRange.first + codeRange.second] == u'\n') ++codeRange.second;
        const auto annotationRanges = !status ? display.annotationRanges : std::vector<std::pair<int, int>>{};
        if (!layout_ || verticalSurface != layoutVertical_ || text != layoutText_ || secondaryRanges != layoutSecondaryRanges_ || codeRange != layoutCodeRange_ || annotationRanges != layoutAnnotationRanges_)
        {
            auto wide = Wide(text);
            ComPtr<IDWriteTextLayout> nextLayout;
            CheckHr(textFactory_->CreateTextLayout(wide.data(), static_cast<UINT32>(wide.size()), format_.Get(),
                300000.0f, 300000.0f, nextLayout.GetAddressOf()));
            for (const auto& range : secondaryRanges)
                CheckHr(nextLayout->SetFontSize(fontSize * 0.7f,
                    {static_cast<UINT32>(range.first), static_cast<UINT32>(range.second)}));
            if (codeRange.first >= 0 && codeRange.second > 0)
                CheckHr(nextLayout->SetFontSize(fontSize * 0.65f,
                    {static_cast<UINT32>(codeRange.first), static_cast<UINT32>(codeRange.second)}));
            for (std::size_t i = 0; i < annotationRanges.size(); ++i)
            {
                const auto& range = annotationRanges[i];
                if (range.first < 0 || range.second <= 0) continue;
                CheckHr(nextLayout->SetFontSize(fontSize * 0.5f,
                    {static_cast<UINT32>(range.first), static_cast<UINT32>(range.second)}));
            }
            codeLayout_.Reset();
            if (!status && display.codeStart >= 0 && display.codeLength > 0)
            {
                auto code = Wide(text.substr(display.codeStart, display.codeLength));
                CheckHr(textFactory_->CreateTextLayout(code.data(), static_cast<UINT32>(code.size()), format_.Get(),
                    300000.f, 300000.f, codeLayout_.GetAddressOf()));
                CheckHr(codeLayout_->SetFontSize(fontSize * 0.65f, {0, static_cast<UINT32>(code.size())}));
            }
            DWRITE_TEXT_METRICS measured{};
            CheckHr(nextLayout->GetMetrics(&measured));
            layoutVertical_ = verticalSurface;
            layoutText_ = text;
            layoutSecondaryRanges_ = std::move(secondaryRanges);
            layoutCodeRange_ = codeRange;
            layoutAnnotationRanges_ = annotationRanges;
            rowLayouts_.clear(); annotationLayouts_.clear();
            if (!status && display.mode != DisplayMode::CodeOnly)
            {
                float maxWord = 0, maxAnnotation = 0;
                for (std::size_t i = 0; i < display.candidateRanges.size(); ++i)
                {
                    auto range = display.candidateRanges[i];
                    auto annotation = i < annotationRanges.size() ? annotationRanges[i] : std::make_pair(-1, 0);
                    int wordLength = annotation.first >= 0 ? annotation.first - range.first : range.second;
                    auto makeLayout = [&](int start, int length, float size) {
                        ComPtr<IDWriteTextLayout> row;
                        auto value = Wide(text.substr(start, length));
                        CheckHr(textFactory_->CreateTextLayout(value.data(), static_cast<UINT32>(value.size()), format_.Get(),
                            300000.f, 300000.f, row.GetAddressOf()));
                        CheckHr(row->SetFontSize(size, {0, static_cast<UINT32>(value.size())}));
                        return row;
                    };
                    auto row = makeLayout(range.first, wordLength, fontSize * (i ? .7f : 1.f));
                    DWRITE_TEXT_METRICS m{}; CheckHr(row->GetMetrics(&m));
                    maxWord = std::max(maxWord, m.widthIncludingTrailingWhitespace);
                    rowLayouts_.push_back(std::move(row));
                    ComPtr<IDWriteTextLayout> note;
                    if (annotation.first >= 0)
                    {
                        note = makeLayout(annotation.first, annotation.second, fontSize * .5f);
                        CheckHr(note->GetMetrics(&m)); maxAnnotation = std::max(maxAnnotation, m.widthIncludingTrailingWhitespace);
                    }
                    annotationLayouts_.push_back(std::move(note));
                }
                // With visible notes, use left-aligned bodies and a separate right column.
                if (verticalSurface) measured.widthIncludingTrailingWhitespace = maxWord + (maxAnnotation > 0 ? maxAnnotation + 4 : 0);
                if (codeLayout_) { DWRITE_TEXT_METRICS m{}; CheckHr(codeLayout_->GetMetrics(&m));
                    measured.widthIncludingTrailingWhitespace = std::max(measured.widthIncludingTrailingWhitespace, m.widthIncludingTrailingWhitespace); }
            }
            layoutMetrics_ = measured;
            layout_ = std::move(nextLayout);
#ifdef TIGERCLAW_RENDER_PROBE
            ++creations_[1];
#endif
        }
        const auto& measured = layoutMetrics_;
        float inset = status ? 0.0f : 4.0f;
        float width = status ? 26.0f : static_cast<float>(std::max(metrics.minWidth,
            measured.widthIncludingTrailingWhitespace + metrics.left + metrics.right) + 2 * palette.borderWidth) + 2 * inset;
        float height = status ? 50.0f : static_cast<float>(measured.height + metrics.top + metrics.bottom + 2 * palette.borderWidth) + 2 * inset;
        float scale = std::max(1u, dpi) / 96.0f;
        if (frameSize)
        {
            width = frameSize->cx / scale; height = frameSize->cy / scale;
            inset = std::min(inset, std::min(width, height) / 4);
        }
        CheckHr(layout_->SetTextAlignment(verticalSurface ? DWRITE_TEXT_ALIGNMENT_CENTER : DWRITE_TEXT_ALIGNMENT_LEADING));
        CheckHr(layout_->SetMaxWidth(verticalSurface ? std::max(1.f,
            width - 2 * inset - static_cast<float>(metrics.left + metrics.right + 2 * palette.borderWidth)) : 300000.f));
        // Bound surfaces to the virtual desktop; untrusted/corrupt MMF cannot allocate unbounded bitmaps.
        int maxWidth = std::max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        int maxHeight = std::max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        Surface(std::clamp(static_cast<int>(std::ceil(width * scale)), 1, maxWidth),
            std::clamp(static_cast<int>(std::ceil(height * scale)), 1, maxHeight));
        RECT bounds{0, 0, size_.cx, size_.cy};
        CheckHr(target_->BindDC(dc_, &bounds));
        target_->SetDpi(static_cast<float>(dpi), static_cast<float>(dpi));
        target_->BeginDraw();
        target_->Clear(D2D1::ColorF(0, 0.0f));
        target_->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        ComPtr<ID2D1SolidColorBrush> brush;
        CheckHr(target_->CreateSolidColorBrush(Rgba(palette.background), brush.GetAddressOf()));
        CheckHr(layout_->SetDrawingEffect(nullptr, {0, static_cast<UINT32>(text.size())}));
        rowHitRects_.clear();
        if (codeRange.first >= 0 && codeRange.second > 0)
        {
            ComPtr<ID2D1SolidColorBrush> codeBrush;
            // The footer is drawn separately at the left edge; centering only
            // affects candidate rows, including their annotations.
            CheckHr(target_->CreateSolidColorBrush(D2D1::ColorF(((palette.foreground >> 16) & 255) / 255.f, ((palette.foreground >> 8) & 255) / 255.f, (palette.foreground & 255) / 255.f, verticalSurface ? 0.f : .5f), codeBrush.GetAddressOf()));
            CheckHr(layout_->SetDrawingEffect(codeBrush.Get(),
                {static_cast<UINT32>(codeRange.first), static_cast<UINT32>(codeRange.second)}));
        }
        if (!annotationRanges.empty())
        {
            ComPtr<ID2D1SolidColorBrush> annotationBrush;
            auto color = Rgba(palette.foreground); color.a *= 0.7f;
            CheckHr(target_->CreateSolidColorBrush(color, annotationBrush.GetAddressOf()));
            for (const auto& range : annotationRanges) if (range.first >= 0 && range.second > 0)
                CheckHr(layout_->SetDrawingEffect(annotationBrush.Get(),
                    {static_cast<UINT32>(range.first), static_cast<UINT32>(range.second)}));
        }
        float border = std::min(static_cast<float>(palette.borderWidth),
            std::max(0.0f, std::min(width, height) - 2 * inset));
        float half = border / 2;
        auto rect = D2D1::RectF(inset + half, inset + half, width - inset - half, height - inset - half);
        auto rounded = D2D1::RoundedRect(rect, status ? 10.0f : 5.0f, status ? 10.0f : 5.0f);
        ComPtr<ID2D1PathGeometry> outline;
        if (!status && display.mode != DisplayMode::CodeOnly)
        {
            CheckHr(factory_->CreatePathGeometry(outline.GetAddressOf()));
            ComPtr<ID2D1GeometrySink> sink;
            CheckHr(outline->Open(sink.GetAddressOf()));
            const float limit = std::max(0.0f, std::min(rect.right - rect.left, rect.bottom - rect.top) / 2);
            const float topLeft = std::min(verticalSurface && display.above ? 10.f : 5.f, limit);
            const float topRight = std::min(!verticalSurface || display.above ? 10.f : 5.f, limit);
            const float bottomRight = std::min(!verticalSurface || !display.above ? 10.f : 5.f, limit);
            const float bottomLeft = std::min(verticalSurface && !display.above ? 10.f : 5.f, limit);
            auto arc = [&](float x, float y, float radius) {
                sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(x, y), D2D1::SizeF(radius, radius),
                    0, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
            };
            sink->BeginFigure(D2D1::Point2F(rect.left + topLeft, rect.top), D2D1_FIGURE_BEGIN_FILLED);
            sink->AddLine(D2D1::Point2F(rect.right - topRight, rect.top)); arc(rect.right, rect.top + topRight, topRight);
            sink->AddLine(D2D1::Point2F(rect.right, rect.bottom - bottomRight)); arc(rect.right - bottomRight, rect.bottom, bottomRight);
            sink->AddLine(D2D1::Point2F(rect.left + bottomLeft, rect.bottom)); arc(rect.left, rect.bottom - bottomLeft, bottomLeft);
            sink->AddLine(D2D1::Point2F(rect.left, rect.top + topLeft)); arc(rect.left + topLeft, rect.top, topLeft);
            sink->EndFigure(D2D1_FIGURE_END_CLOSED);
            CheckHr(sink->Close());
        }
        else if (!status && palette.asymmetricCorners)
        {
            CheckHr(factory_->CreatePathGeometry(outline.GetAddressOf()));
            ComPtr<ID2D1GeometrySink> sink;
            CheckHr(outline->Open(sink.GetAddressOf()));
            float radius = std::min(10.0f, std::min(rect.right - rect.left, rect.bottom - rect.top) / 2);
            sink->BeginFigure(D2D1::Point2F(rect.left + radius, rect.top), D2D1_FIGURE_BEGIN_FILLED);
            sink->AddLine(D2D1::Point2F(rect.right, rect.top));
            sink->AddLine(D2D1::Point2F(rect.right, rect.bottom - radius));
            sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(rect.right - radius, rect.bottom), D2D1::SizeF(radius, radius),
                0, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
            sink->AddLine(D2D1::Point2F(rect.left, rect.bottom));
            sink->AddLine(D2D1::Point2F(rect.left, rect.top + radius));
            sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(rect.left + radius, rect.top), D2D1::SizeF(radius, radius),
                0, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
            sink->EndFigure(D2D1_FIGURE_END_CLOSED);
            CheckHr(sink->Close());
        }
        auto fill = [&]
        {
            if (outline) target_->FillGeometry(outline.Get(), brush.Get());
            else target_->FillRoundedRectangle(rounded, brush.Get());
        };
        auto stroke = [&](float thickness)
        {
            if (outline) target_->DrawGeometry(outline.Get(), brush.Get(), thickness);
            else target_->DrawRoundedRectangle(rounded, brush.Get(), thickness);
        };
        if (!status)
        {
            // A bounded soft outline approximates WPF's 3-DIP drop shadow;
            // no full-window intermediate blur surfaces or animation timers.
            float opacity = std::max(palette.background >> 24, palette.border >> 24) / 255.0f;
            target_->SetTransform(D2D1::Matrix3x2F::Translation(0, 1));
            brush->SetColor(D2D1::ColorF(0, opacity * 0.025f));
            for (float spread : {6.0f, 4.0f, 2.0f}) stroke(spread);
            brush->SetColor(D2D1::ColorF(0, opacity * 0.10f));
            fill();
            target_->SetTransform(D2D1::Matrix3x2F::Identity());
        }
        brush->SetColor(Rgba(palette.background));
        fill();
        brush->SetColor(Rgba(palette.border));
        stroke(border);
        D2D1_POINT_2F origin = status ? D2D1::Point2F((width - measured.width) / 2, 12 + (34 - measured.height) / 2) :
            D2D1::Point2F(inset + static_cast<float>(metrics.left + palette.borderWidth), inset + static_cast<float>(metrics.top + palette.borderWidth));
        textOrigin_ = origin; textScale_ = scale;
        if (status) target_->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(9, 9, 17, 12), 2, 2), brush.Get());
        else if (!verticalSurface && display.selectionStart >= 0 && display.selectionLength > 0)
        {
            UINT32 count = 0;
            layout_->HitTestTextRange(display.selectionStart, display.selectionLength, origin.x, origin.y, nullptr, 0, &count);
            std::vector<DWRITE_HIT_TEST_METRICS> hits(count);
            if (count && SUCCEEDED(layout_->HitTestTextRange(display.selectionStart, display.selectionLength, origin.x, origin.y,
                hits.data(), count, &count)))
            {
                brush->SetColor(Rgba(palette.selection));
                for (const auto& hit : hits) target_->FillRectangle(D2D1::RectF(hit.left, hit.top, hit.left + hit.width, hit.top + hit.height), brush.Get());
            }
        }
        brush->SetColor(Rgba(palette.foreground));
        ComPtr<ID2D1SolidColorBrush> indexBrush, noteBrush;
        auto indexColor = Rgba(palette.foreground); indexColor.a *= .5f;
        CheckHr(target_->CreateSolidColorBrush(indexColor, indexBrush.GetAddressOf()));
        auto noteColor = Rgba(palette.foreground); noteColor.a *= .7f;
        CheckHr(target_->CreateSolidColorBrush(noteColor, noteBrush.GetAddressOf()));
        for (std::size_t i = 0; i < display.candidateRanges.size(); ++i)
        {
            const auto range = display.candidateRanges[i];
            const auto annotation = i < display.annotationRanges.size() ? display.annotationRanges[i] : std::make_pair(-1, 0);
            int length = annotation.first >= 0 ? annotation.first - range.first : range.second;
            auto indexLength = i > 0 && state.showIndex ? static_cast<UINT32>(std::to_string(i + 1).size()) : 0;
            if (verticalSurface)
            {
                auto& row = rowLayouts_[i]; auto& note = annotationLayouts_[i];
                CheckHr(row->SetTextAlignment(std::any_of(annotationLayouts_.begin(), annotationLayouts_.end(),
                    [](const auto& item) { return item.Get() != nullptr; })
                    ? DWRITE_TEXT_ALIGNMENT_LEADING : DWRITE_TEXT_ALIGNMENT_CENTER));
                CheckHr(row->SetMaxWidth(layout_->GetMaxWidth()));
                CheckHr(row->SetDrawingEffect(nullptr, {0, static_cast<UINT32>(length)}));
                if (indexLength) CheckHr(row->SetDrawingEffect(indexBrush.Get(), {static_cast<UINT32>(length) - indexLength, indexLength}));
                FLOAT x = 0, y = 0; DWRITE_HIT_TEST_METRICS hit{};
                CheckHr(layout_->HitTestTextPosition(range.first, FALSE, &x, &y, &hit));
                auto rowBounds = D2D1::RectF(0, y, layout_->GetMaxWidth(), y + hit.height);
                rowHitRects_.push_back(rowBounds);
                if (display.selectionStart == range.first)
                {
                    brush->SetColor(Rgba(palette.selection));
                    target_->FillRectangle(D2D1::RectF(origin.x, origin.y + y, origin.x + rowBounds.right, origin.y + rowBounds.bottom), brush.Get());
                }
                brush->SetColor(Rgba(palette.foreground));
                target_->DrawTextLayout(D2D1::Point2F(origin.x, origin.y + y), row.Get(), brush.Get(), D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
                if (note)
                {
                    CheckHr(note->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_TRAILING));
                    CheckHr(note->SetMaxWidth(layout_->GetMaxWidth()));
                    DWRITE_TEXT_METRICS m{}; CheckHr(note->GetMetrics(&m));
                    target_->DrawTextLayout(D2D1::Point2F(origin.x, origin.y + y + (hit.height - m.height) / 2), note.Get(), noteBrush.Get(), D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
                }
            }
            else
            {
                auto& row = rowLayouts_[i]; auto& note = annotationLayouts_[i];
                CheckHr(row->SetDrawingEffect(nullptr, {0, static_cast<UINT32>(length)}));
                if (indexLength) CheckHr(row->SetDrawingEffect(indexBrush.Get(), {static_cast<UINT32>(length) - indexLength, indexLength}));
                FLOAT x = 0, y = 0; DWRITE_HIT_TEST_METRICS hit{};
                CheckHr(layout_->HitTestTextPosition(range.first, FALSE, &x, &y, &hit));
                DWRITE_TEXT_METRICS m{}; CheckHr(row->GetMetrics(&m));
                target_->DrawTextLayout(D2D1::Point2F(origin.x + x, origin.y + (measured.height - m.height) / 2), row.Get(), brush.Get(), D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
                if (note)
                {
                    CheckHr(layout_->HitTestTextPosition(annotation.first, FALSE, &x, &y, &hit));
                    CheckHr(note->GetMetrics(&m));
                    target_->DrawTextLayout(D2D1::Point2F(origin.x + x, origin.y + (measured.height - m.height) / 2), note.Get(), noteBrush.Get(), D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
                }
            }
        }
        if (status || display.mode == DisplayMode::CodeOnly) target_->DrawTextLayout(origin, layout_.Get(), brush.Get(), D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
        if (codeLayout_)
        {
            FLOAT x = 0, y = 0; DWRITE_HIT_TEST_METRICS hit{};
            CheckHr(layout_->HitTestTextPosition(display.codeStart, FALSE, &x, &y, &hit));
            CheckHr(codeLayout_->SetTextAlignment(verticalSurface ? DWRITE_TEXT_ALIGNMENT_CENTER : DWRITE_TEXT_ALIGNMENT_LEADING));
            CheckHr(codeLayout_->SetMaxWidth(layout_->GetMaxWidth()));
            auto codeColor = Rgba(palette.foreground); codeColor.a *= .5f;
            brush->SetColor(codeColor);
            DWRITE_TEXT_METRICS codeMetrics{}; CheckHr(codeLayout_->GetMetrics(&codeMetrics));
            target_->DrawTextLayout(D2D1::Point2F(origin.x + (verticalSurface ? 0 : x), origin.y + (verticalSurface ? y : (measured.height - codeMetrics.height) / 2)), codeLayout_.Get(), brush.Get(), D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
        }
        HRESULT drawn = target_->EndDraw();
        if (drawn == D2DERR_RECREATE_TARGET) target_.Reset();
        CheckHr(drawn);
        frameReady_ = true;
        return size_;
    }
    catch (...)
    {
        // A failure between BeginDraw and EndDraw must not leave a poisoned
        // target for the next attempt. This never changes the published HWND.
        frameReady_ = false;
        target_.Reset();
        throw;
    }
    void Renderer::Present(HWND window, const POINT* destination)
    {
        if (!frameReady_) throw std::runtime_error("No complete frame to present");
        POINT source{};
        POINT position = destination ? *destination : POINT{};
        BLENDFUNCTION blend{AC_SRC_OVER, 0, surfaceOpacity_, AC_SRC_ALPHA};
        if (!UpdateLayeredWindow(window, nullptr, destination ? &position : nullptr, &size_, dc_, &source, 0, &blend, ULW_ALPHA))
            throw std::runtime_error("UpdateLayeredWindow failed");
    }
    int Renderer::HitCandidate(POINT point, const Display& display) const
    {
        if (!layout_ || !frameReady_ || display.text != layoutText_) return -1;
        if (!rowHitRects_.empty())
        {
            float x = point.x / textScale_ - textOrigin_.x, y = point.y / textScale_ - textOrigin_.y;
            for (std::size_t i = 0; i < rowHitRects_.size(); ++i)
            { const auto& r = rowHitRects_[i]; if (x >= r.left && x < r.right && y >= r.top && y < r.bottom) return static_cast<int>(i); }
            return -1;
        }
        BOOL trailing = FALSE, inside = FALSE;
        DWRITE_HIT_TEST_METRICS hit{};
        if (FAILED(layout_->HitTestPoint(point.x / textScale_ - textOrigin_.x,
            point.y / textScale_ - textOrigin_.y, &trailing, &inside, &hit)) || !inside) return -1;
        for (std::size_t i = 0; i < display.candidateRanges.size(); ++i)
        {
            const auto& range = display.candidateRanges[i];
            if (hit.textPosition >= static_cast<UINT32>(range.first) &&
                hit.textPosition < static_cast<UINT32>(range.first + range.second)) return static_cast<int>(i);
        }
        return -1;
    }

}
