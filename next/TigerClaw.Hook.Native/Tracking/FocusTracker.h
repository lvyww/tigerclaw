#pragma once

#include "..\State\FocusSnapshot.h"

namespace TigerClawHookNative
{
    class FocusTracker
    {
    public:
        bool TryGetSnapshot(FocusSnapshot& focus) const;
    };
}
