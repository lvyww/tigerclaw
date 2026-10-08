#pragma once
#include "RuntimeInput.h"
#include "KeyRequestReplayCache.h"
#include "OutputServices.h"

namespace tiger::core
{
    enum class RuntimeUiCommand { ShowConfig=1, ShowAddCi=2, ExitCore=3, ShowMenu=4 };
    // Serialized protocol kernel. Transport/UI publication belong to the caller;
    // this adapter never opens production pipes, maps or application windows.
    class RuntimeProtocol
    {
        RuntimeLexicons& _runtime;
        RuntimeInput& _input;
        std::filesystem::path _config;
        std::function<void(RuntimeUiCommand)> _command;
        std::function<bool(std::u16string_view,bool)> _openTarget;
        std::mutex _mutex;
        KeyRequestReplayCache _replay;
        bool _pendingFrontendCompositionReset = false;
        bool _hookNative = false, _hookDisabled = false, _imeActive = false, _holdBlocked = false;
        bool _anchorPending = false;
        int _caretX = 0, _caretY = 0, _caretWidth = 2, _caretHeight = 20;
        std::uint64_t _anchorRevision = 0, _soundSeq = 0;
        std::uint64_t _backgroundUntil = 0;
        OutputRandom _random;
        int _soundVk = 0, _soundVolume = 0;
        std::chrono::steady_clock::time_point _freshCaretDeadline{};
        std::string _focusIdentity = "[0,0,\"\",\"\",\"\"]";
        std::string _frameSession;
        void UpdateCaret(int x, int y, int width, int height)
        {
            _caretX=x; _caretY=y; _caretWidth=width; _caretHeight=height;
            if (_anchorPending) { _anchorPending=false; ++_anchorRevision; }
        }
    public:
        RuntimeProtocol(RuntimeLexicons& runtime, RuntimeInput& input, std::filesystem::path config,
            std::function<void(RuntimeUiCommand)> command = {}, std::function<bool(std::u16string_view,bool)> openTarget = {})
            : _runtime(runtime), _input(input), _config(std::move(config)), _command(std::move(command)), _openTarget(std::move(openTarget)), _frameSession(learningId()) {}
        std::optional<std::string> Handle(std::string_view json, CtrlSpaceState::Time now);
        std::string CaptureUiState();
        RuntimeInputSnapshot CaptureSnapshot()
        { std::lock_guard lock(_mutex); return _input.CaptureSnapshot(); }
    };
}
