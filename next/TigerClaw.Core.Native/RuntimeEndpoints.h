#pragma once
#include <string>
#include <stdexcept>

namespace tiger::core
{
    // Production values intentionally match TigerClaw.Shared.RuntimeConstants.
    struct RuntimeEndpoints
    {
        bool isolated=false;
        std::wstring pipe=L"BimeIPC";
        std::wstring ui=L"Local\\TigerClaw.UiState.v1";
        std::wstring heartbeat=L"Local\\TigerClaw.Heartbeat.v1";
        std::wstring overlayHeartbeat=L"Local\\TigerClaw.OverlayHeartbeat.v1";
        std::wstring menu=L"Local\\TigerClaw.ShowMenu.v1";
        std::wstring sentence=L"TigerClaw.Sentence.v1";
        static RuntimeEndpoints Isolated(std::wstring name)
        {
            if (!name.starts_with(L"TigerClaw.Core.Native.Test.") || name.size()>180 ||
                name.find_first_not_of(L"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-")!=name.npos)
                throw std::invalid_argument("Explicit isolated Core namespace required");
            RuntimeEndpoints e; e.isolated=true; e.pipe=std::move(name);
            e.ui=L"Local\\"+e.pipe+L".UiState.v1";
            e.heartbeat=L"Local\\"+e.pipe+L".Heartbeat.v1";
            e.overlayHeartbeat=L"Local\\"+e.pipe+L".OverlayHeartbeat.v1";
            e.menu=L"Local\\"+e.pipe+L".ShowMenu.v1";
            e.sentence=e.pipe+L".Sentence";
            return e;
        }
    };
}
