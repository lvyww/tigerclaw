#include "KeyPostProcessor.h"
namespace tiger::core
{
    void KeyPostProcessor::Process(int vk, bool keyDown, bool shift, bool ctrl, bool alt, bool win, bool capsLock,
        PostProcessResult& result, OutputContext providers)
    {
        if (!result.text.empty())
        {
            auto normalized = _output.Normalize(result.text, std::move(providers));
            result.text = std::move(normalized.text);
            result.action = normalized.action;
            if (result.action != OutputAction::Text) { result.inputBuffer.clear(); result.composing = false; }
        }
        if (keyDown && !result.handled)
        {
            if (vk == 0x08) _history.Backspace();
            _history.AppendPassThrough(vk, shift, ctrl, alt, win, capsLock);
        }
        if (!result.text.empty()) _history.Append(result.text);
        _output.Observe(vk, keyDown, result.handled, result.text, shift, ctrl, alt, win);
    }
}
