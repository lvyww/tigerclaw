#pragma once
#include "../State/CoreResponse.h"

namespace TigerClawHookNative
{
    // Replay success means OS submission, not proof that the application inserted
    // the text. Both foreground checks are required before a positive receipt.
    template<class FocusCheck, class Replay, class Acknowledge>
    void ApplyCommitWithLearning(const CoreResponse& response, bool suppressed,
        FocusCheck sameFocus, Replay replay, Acknowledge acknowledge)
    {
        bool applied = false;
        if (response.Success && response.Handled && !response.CommitText.empty() &&
            !suppressed && sameFocus())
            applied = replay(response.CommitText) && sameFocus();
        if (!response.LearningReceipt.empty())
            acknowledge(response.LearningReceipt, applied);
    }
}
