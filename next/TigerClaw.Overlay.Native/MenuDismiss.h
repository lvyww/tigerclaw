#pragma once

namespace tiger::overlay
{
    // WPF-equivalent outside-click edges. A button held while the menu opens
    // must not immediately close it, and submenu clicks count as inside.
    struct MenuDismiss
    {
        bool left = false, right = false, escape = false;

        bool Update(bool inside, bool nextLeft, bool nextRight, bool nextEscape)
        {
            bool dismiss = (!inside && ((nextLeft && !left) || (nextRight && !right))) ||
                (nextEscape && !escape);
            left = nextLeft; right = nextRight; escape = nextEscape;
            return dismiss;
        }
    };
}
