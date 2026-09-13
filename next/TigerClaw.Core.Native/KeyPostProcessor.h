#pragma once
#include "SendHistory.h"
#include "OutputState.h"
namespace tiger::core
{
    struct PostProcessResult
    {
        bool handled = false;
        std::u16string text;
        std::u16string inputBuffer;
        bool composing = false;
        OutputAction action = OutputAction::Text;
        bool cancelCompositionBeforePass = false;
    };
    class KeyPostProcessor
    {
    public:
        void Process(int vk, bool keyDown, bool shift, bool ctrl, bool alt, bool win, bool capsLock,
            PostProcessResult& result, OutputContext providers = {});
        std::u16string EmitQuote(bool doubleQuote) { return _history.EmitQuote(doubleQuote); }
        const SendHistory& History() const { return _history; }
        const OutputState& Output() const { return _output; }
        void ClearDecimalArm() { _output.ClearDecimalArm(); }
    private:
        friend class ChineseInputSession;
        SendHistory _history;
        OutputState _output;
    };
}
