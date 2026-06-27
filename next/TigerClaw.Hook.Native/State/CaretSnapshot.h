#pragma once

#include <windows.h>

namespace TigerClawHookNative
{
    struct CaretSnapshot
    {
        bool IsValid = false;
        LONG X = 0;
        LONG Y = 0;
        LONG Width = 2;
        LONG Height = 20;
        bool IsPrecise = false;

        bool Equals(const CaretSnapshot& other) const
        {
            return IsValid == other.IsValid &&
                   X == other.X &&
                   Y == other.Y &&
                   Width == other.Width &&
                   Height == other.Height &&
                   IsPrecise == other.IsPrecise;
        }
    };
}
