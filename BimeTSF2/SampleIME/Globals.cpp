// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO
// THE IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.
//
// Copyright (c) Microsoft Corporation. All rights reserved

#include "Private.h"
#include "resource.h"
#include "BaseWindow.h"
#include "define.h"
#include "SampleIMEBaseStructure.h"
#include "EmbeddedBuildInfo.h"
#include <stdio.h>
#include <stdarg.h>

#ifndef BIME_EMBED_TEXT_LOG_ENABLED
#define BIME_EMBED_TEXT_LOG_ENABLED 1
#endif

#ifndef BIME_EMBED_VERBOSE_LOG_ENABLED
#define BIME_EMBED_VERBOSE_LOG_ENABLED 0
#endif

namespace Global {
HINSTANCE dllInstanceHandle;

LONG dllRefCount = -1;

CRITICAL_SECTION CS;
HFONT defaultlFontHandle;				// Global font object we use everywhere

//---------------------------------------------------------------------
// SampleIME CLSID
//---------------------------------------------------------------------
// {D2291A80-84D8-4641-9AB2-BDD1472C846B}
extern const CLSID SampleIMECLSID = {
    0x14493d3c,
    0x2059,
    0x41c0,
    { 0x80, 0x5a, 0x1f, 0x78, 0x41, 0xde, 0x20, 0x6b }
};

//---------------------------------------------------------------------
// Profile GUID
//---------------------------------------------------------------------
// {83955C0E-2C09-47a5-BCF3-F2B98E11EE8B}
extern const GUID SampleIMEGuidProfile = {
    0xb5ab471c,
    0x7f90,
    0x49ea,
    { 0xb4, 0x18, 0xdb, 0x80, 0x26, 0x91, 0xfc, 0xc0 }
};

//---------------------------------------------------------------------
// PreserveKey GUID
//---------------------------------------------------------------------
// {4B62B54B-F828-43B5-9095-A96DF9CBDF38}
extern const GUID SampleIMEGuidImeModePreserveKey = {
    0x81b6a813, 
    0x4be9, 
    0x43e8, 
    { 0xa3, 0xa1, 0xdb, 0xd5, 0x96, 0x2b, 0x09, 0x2a } 
};

// {5A08D6C4-4563-4E46-8DDB-65E75C4E73A3}
extern const GUID SampleIMEGuidDoubleSingleBytePreserveKey = {
    0x198a8493, 
    0xe23b, 
    0x4e87, 
    { 0x9a, 0x5b, 0x2f, 0xb0, 0x6b, 0x2a, 0xfd, 0x9d } 
};

// {175F062E-B961-4AED-A3DF-59F78A02862D}
extern const GUID SampleIMEGuidPunctuationPreserveKey = {
    0x015c52a6, 
    0x5db2, 
    0x44fb, 
    { 0x88, 0xdf, 0x95, 0xc3, 0xd7, 0x12, 0x20, 0x2f } 
};

// {A27FBF6D-2D9E-4B5E-9E7D-9C4CFD0A4C19}
extern const GUID BimeGuidCtrlSpacePreserveKey = {
    0xa27fbf6d,
    0x2d9e,
    0x4b5e,
    { 0x9e, 0x7d, 0x9c, 0x4c, 0xfd, 0x0a, 0x4c, 0x19 }
};

//---------------------------------------------------------------------
// Compartments全角/半角
//---------------------------------------------------------------------
// {101011C5-CF72-4F0C-A515-153019593F10}
extern const GUID SampleIMEGuidCompartmentDoubleSingleByte = {
    0x101011c5,
    0xcf72,
    0x4f0c,
    { 0xa5, 0x15, 0x15, 0x30, 0x19, 0x59, 0x3f, 0x10 }
};

// {DD321BCC-A7F8-4561-9B61-9B3508C9BA97}中文标点
extern const GUID SampleIMEGuidCompartmentPunctuation = {
    0xdd321bcc,
    0xa7f8,
    0x4561,
    { 0x9b, 0x61, 0x9b, 0x35, 0x8, 0xc9, 0xba, 0x97 }
};


//---------------------------------------------------------------------
// LanguageBars
//---------------------------------------------------------------------

// {89BE500C-9462-4070-9DB0-B467BB051327}
extern const GUID SampleIMEGuidLangBarIMEMode = {
    0x89be500c,
    0x9462,
    0x4070,
    { 0x9d, 0xb0, 0xb4, 0x67, 0xbb, 0x5, 0x13, 0x27 }
};

// {6A11D9DE-46DB-455B-A257-2EB615746BF4}
extern const GUID SampleIMEGuidLangBarDoubleSingleByte = {
    0x6a11d9de,
    0x46db,
    0x455b,
    { 0xa2, 0x57, 0x2e, 0xb6, 0x15, 0x74, 0x6b, 0xf4 }
};

// {F29C731A-A51E-49FB-8A3C-EE51752912E2}
extern const GUID SampleIMEGuidLangBarPunctuation = {
    0xf29c731a,
    0xa51e,
    0x49fb,
    { 0x8a, 0x3c, 0xee, 0x51, 0x75, 0x29, 0x12, 0xe2 }
};

// {4C802E2C-8140-4436-A5E5-F7C544EBC9CD}
extern const GUID SampleIMEGuidDisplayAttributeInput = {
    0x4c802e2c,
    0x8140,
    0x4436,
    { 0xa5, 0xe5, 0xf7, 0xc5, 0x44, 0xeb, 0xc9, 0xcd }
};

// {9A1CC683-F2A7-4701-9C6E-2DA69A5CD474}
extern const GUID SampleIMEGuidDisplayAttributeConverted = {
    0x9a1cc683,
    0xf2a7,
    0x4701,
    { 0x9c, 0x6e, 0x2d, 0xa6, 0x9a, 0x5c, 0xd4, 0x74 }
};


//---------------------------------------------------------------------
// UI element
//---------------------------------------------------------------------

// {84B0749F-8DE7-4732-907A-3BCB150A01A8}
extern const GUID SampleIMEGuidCandUIElement = {
    0x84b0749f,
    0x8de7,
    0x4732,
    { 0x90, 0x7a, 0x3b, 0xcb, 0x15, 0xa, 0x1, 0xa8 }
};

//---------------------------------------------------------------------
// Unicode byte order mark
//---------------------------------------------------------------------
extern const WCHAR UnicodeByteOrderMark = 0xFEFF;

//---------------------------------------------------------------------
// dictionary table delimiter
//---------------------------------------------------------------------
extern const WCHAR KeywordDelimiter = L'=';
extern const WCHAR StringDelimiter  = L'\"';

//---------------------------------------------------------------------
// defined item in setting file table [PreservedKey] section
//---------------------------------------------------------------------
extern const WCHAR ImeModeDescription[] = L"Chinese/English input (Shift)";
extern const int ImeModeOnIcoIndex = IME_MODE_ON_ICON_INDEX;
extern const int ImeModeOffIcoIndex = IME_MODE_OFF_ICON_INDEX;

extern const WCHAR DoubleSingleByteDescription[] = L"Double/Single byte (Shift+Space)";
extern const int DoubleSingleByteOnIcoIndex = IME_DOUBLE_ON_INDEX;
extern const int DoubleSingleByteOffIcoIndex = IME_DOUBLE_OFF_INDEX;

extern const WCHAR PunctuationDescription[] = L"Chinese/English punctuation (Ctrl+.)";
extern const int PunctuationOnIcoIndex = IME_PUNCTUATION_ON_INDEX;
extern const int PunctuationOffIcoIndex = IME_PUNCTUATION_OFF_INDEX;

//---------------------------------------------------------------------
// defined item in setting file table [LanguageBar] section
//---------------------------------------------------------------------
extern const WCHAR LangbarImeModeDescription[] = L"Conversion mode";
extern const WCHAR LangbarDoubleSingleByteDescription[] = L"Character width";
extern const WCHAR LangbarPunctuationDescription[] = L"Punctuation";

//---------------------------------------------------------------------
// windows class / titile / atom注册候选窗口类
//---------------------------------------------------------------------
extern const WCHAR CandidateClassName[] = L"TigerClaw.CandidateWindow";
ATOM AtomCandidateWindow;

extern const WCHAR ShadowClassName[] = L"TigerClaw.ShadowWindow";
ATOM AtomShadowWindow;

extern const WCHAR ScrollBarClassName[] = L"TigerClaw.ScrollBarWindow";
ATOM AtomScrollBarWindow;

BOOL RegisterWindowClass()
{
    if (!CBaseWindow::_InitWindowClass(CandidateClassName, &AtomCandidateWindow))
    {
        return FALSE;
    }
    if (!CBaseWindow::_InitWindowClass(ShadowClassName, &AtomShadowWindow))
    {
        return FALSE;
    }
    if (!CBaseWindow::_InitWindowClass(ScrollBarClassName, &AtomScrollBarWindow))
    {
        return FALSE;
    }
    return TRUE;
}

//---------------------------------------------------------------------
// defined full width characters for Double/Single byte conversion
//---------------------------------------------------------------------
extern const WCHAR FullWidthCharTable[] = {
    //         !       "       #       $       %       &       '       (    )       *       +       ,       -       .       /
    0x3000, 0xFF01, 0xFF02, 0xFF03, 0xFF04, 0xFF05, 0xFF06, 0xFF07, 0xFF08, 0xFF09, 0xFF0A, 0xFF0B, 0xFF0C, 0xFF0D, 0xFF0E, 0xFF0F,
    // 0       1       2       3       4       5       6       7       8       9       :       ;       <       =       >       ?
    0xFF10, 0xFF11, 0xFF12, 0xFF13, 0xFF14, 0xFF15, 0xFF16, 0xFF17, 0xFF18, 0xFF19, 0xFF1A, 0xFF1B, 0xFF1C, 0xFF1D, 0xFF1E, 0xFF1F,
    // @       A       B       C       D       E       F       G       H       I       J       K       L       M       N       0
    0xFF20, 0xFF21, 0xFF22, 0xFF23, 0xFF24, 0xFF25, 0xFF26, 0xFF27, 0xFF28, 0xFF29, 0xFF2A, 0xFF2B, 0xFF2C, 0xFF2D, 0xFF2E, 0xFF2F,
    // P       Q       R       S       T       U       V       W       X       Y       Z       [       \       ]       ^       _
    0xFF30, 0xFF31, 0xFF32, 0xFF33, 0xFF34, 0xFF35, 0xFF36, 0xFF37, 0xFF38, 0xFF39, 0xFF3A, 0xFF3B, 0xFF3C, 0xFF3D, 0xFF3E, 0xFF3F,
    // '       a       b       c       d       e       f       g       h       i       j       k       l       m       n       o       
    0xFF40, 0xFF41, 0xFF42, 0xFF43, 0xFF44, 0xFF45, 0xFF46, 0xFF47, 0xFF48, 0xFF49, 0xFF4A, 0xFF4B, 0xFF4C, 0xFF4D, 0xFF4E, 0xFF4F,
    // p       q       r       s       t       u       v       w       x       y       z       {       |       }       ~
    0xFF50, 0xFF51, 0xFF52, 0xFF53, 0xFF54, 0xFF55, 0xFF56, 0xFF57, 0xFF58, 0xFF59, 0xFF5A, 0xFF5B, 0xFF5C, 0xFF5D, 0xFF5E
};

//---------------------------------------------------------------------
// defined punctuation characters
//---------------------------------------------------------------------
extern const struct _PUNCTUATION PunctuationTable[14] = {
    {L'!',  0xFF01},
    {L'$',  0xFFE5},
    {L'&',  0x2014},
    {L'(',  0xFF08},
    {L')',  0xFF09},
    {L',',  0xFF0C},
    {L'.',  0x3002},
    {L':',  0xFF1A},
    {L';',  0xFF1B},
    {L'?',  0xFF1F},
    {L'@',  0x00B7},
    {L'\\', 0x3001},
    {L'^',  0x2026},
    {L'_',  0x2014}
};

//+---------------------------------------------------------------------------
//
// CheckModifiers
//
//----------------------------------------------------------------------------

#define TF_MOD_ALLALT     (TF_MOD_RALT | TF_MOD_LALT | TF_MOD_ALT)
#define TF_MOD_ALLCONTROL (TF_MOD_RCONTROL | TF_MOD_LCONTROL | TF_MOD_CONTROL)
#define TF_MOD_ALLSHIFT   (TF_MOD_RSHIFT | TF_MOD_LSHIFT | TF_MOD_SHIFT)
#define TF_MOD_RLALT      (TF_MOD_RALT | TF_MOD_LALT)
#define TF_MOD_RLCONTROL  (TF_MOD_RCONTROL | TF_MOD_LCONTROL)
#define TF_MOD_RLSHIFT    (TF_MOD_RSHIFT | TF_MOD_LSHIFT)

#define CheckMod(m0, m1, mod)        \
    if (m1 & TF_MOD_ ## mod ##)      \
{ \
    if (!(m0 & TF_MOD_ ## mod ##)) \
{      \
    return FALSE;   \
}      \
} \
    else       \
{ \
    if ((m1 ^ m0) & TF_MOD_RL ## mod ##)    \
{      \
    return FALSE;   \
}      \
} \



BOOL CheckModifiers(UINT modCurrent, UINT mod)
{
    mod &= ~TF_MOD_ON_KEYUP;

    if (mod & TF_MOD_IGNORE_ALL_MODIFIER)
    {
        return TRUE;
    }

    if (modCurrent == mod)
    {
        return TRUE;
    }

    if (modCurrent && !mod)
    {
        return FALSE;
    }

    CheckMod(modCurrent, mod, ALT);
    CheckMod(modCurrent, mod, SHIFT);
    CheckMod(modCurrent, mod, CONTROL);

    return TRUE;
}

//+---------------------------------------------------------------------------
//
// UpdateModifiers
//
//    wParam - virtual-key code
//    lParam - [0-15]  Repeat count
//  [16-23] Scan code
//  [24]    Extended key
//  [25-28] Reserved
//  [29]    Context code
//  [30]    Previous key state
//  [31]    Transition state
//----------------------------------------------------------------------------

USHORT ModifiersValue = 0;
BOOL   IsShiftKeyDownOnly = FALSE;
BOOL   IsControlKeyDownOnly = FALSE;
BOOL   IsAltKeyDownOnly = FALSE;

BOOL UpdateModifiers(WPARAM wParam, LPARAM lParam)
{
    // high-order bit : key down
    // low-order bit  : toggled
    SHORT sksMenu = GetKeyState(VK_MENU);
    SHORT sksCtrl = GetKeyState(VK_CONTROL);
    SHORT sksShft = GetKeyState(VK_SHIFT);

    switch (wParam & 0xff)
    {
    case VK_MENU:
        // is VK_MENU down?
        if (sksMenu & 0x8000)
        {
            // is extended key?
            if (lParam & 0x01000000)
            {
                ModifiersValue |= (TF_MOD_RALT | TF_MOD_ALT);
            }
            else
            {
                ModifiersValue |= (TF_MOD_LALT | TF_MOD_ALT);
            }

            // is previous key state up?
            if (!(lParam & 0x40000000))
            {
                // is VK_CONTROL and VK_SHIFT up?
                if (!(sksCtrl & 0x8000) && !(sksShft & 0x8000))
                {
                    IsAltKeyDownOnly = TRUE;
                }
                else
                {
                    IsShiftKeyDownOnly = FALSE;
                    IsControlKeyDownOnly = FALSE;
                    IsAltKeyDownOnly = FALSE;
                }
            }
        }
        break;

    case VK_CONTROL:
        // is VK_CONTROL down?
        if (sksCtrl & 0x8000)    
        {
            // is extended key?
            if (lParam & 0x01000000)
            {
                ModifiersValue |= (TF_MOD_RCONTROL | TF_MOD_CONTROL);
            }
            else
            {
                ModifiersValue |= (TF_MOD_LCONTROL | TF_MOD_CONTROL);
            }

            // is previous key state up?
            if (!(lParam & 0x40000000))
            {
                // is VK_SHIFT and VK_MENU up?
                if (!(sksShft & 0x8000) && !(sksMenu & 0x8000))
                {
                    IsControlKeyDownOnly = TRUE;
                }
                else
                {
                    IsShiftKeyDownOnly = FALSE;
                    IsControlKeyDownOnly = FALSE;
                    IsAltKeyDownOnly = FALSE;
                }
            }
        }
        break;

    case VK_SHIFT:
        // is VK_SHIFT down?
        if (sksShft & 0x8000)    
        {
            // is scan code 0x36(right shift)?
            if (((lParam >> 16) & 0x00ff) == 0x36)
            {
                ModifiersValue |= (TF_MOD_RSHIFT | TF_MOD_SHIFT);
            }
            else
            {
                ModifiersValue |= (TF_MOD_LSHIFT | TF_MOD_SHIFT);
            }

            // is previous key state up?
            if (!(lParam & 0x40000000))
            {
                // is VK_MENU and VK_CONTROL up?
                if (!(sksMenu & 0x8000) && !(sksCtrl & 0x8000))
                {
                    IsShiftKeyDownOnly = TRUE;
                }
                else
                {
                    IsShiftKeyDownOnly = FALSE;
                    IsControlKeyDownOnly = FALSE;
                    IsAltKeyDownOnly = FALSE;
                }
            }
        }
        break;

    default:
        IsShiftKeyDownOnly = FALSE;
        IsControlKeyDownOnly = FALSE;
        IsAltKeyDownOnly = FALSE;
        break;
    }

    if (!(sksMenu & 0x8000))
    {
        ModifiersValue &= ~TF_MOD_ALLALT;
    }
    if (!(sksCtrl & 0x8000))
    {
        ModifiersValue &= ~TF_MOD_ALLCONTROL;
    }
    if (!(sksShft & 0x8000))
    {
        ModifiersValue &= ~TF_MOD_ALLSHIFT;
    }

    return TRUE;
}

//---------------------------------------------------------------------
// override CompareElements
//---------------------------------------------------------------------

static BOOL IsTextLoggingEnabled()
{
#if defined(_DEBUG)
    return TRUE;
#else
    return (BIME_EMBED_TEXT_LOG_ENABLED != 0) ? TRUE : FALSE;
#endif
}

static BOOL IsVerboseLoggingEnabled()
{
#if defined(_DEBUG)
    return TRUE;
#else
    return (BIME_EMBED_VERBOSE_LOG_ENABLED != 0) ? TRUE : FALSE;
#endif
}

BOOL IsTextLoggingEnabledRuntime()
{
    return IsTextLoggingEnabled();
}

BOOL IsVerboseLoggingEnabledRuntime()
{
    return IsVerboseLoggingEnabled();
}

static BOOL BuildLogPath(_Out_writes_(pathSize) char *path, size_t pathSize)
{
    if (path == nullptr || pathSize == 0)
    {
        return FALSE;
    }

    char userProfile[MAX_PATH] = {0};
    DWORD len = GetEnvironmentVariableA("USERPROFILE", userProfile, ARRAYSIZE(userProfile));
    if (len == 0 || len >= ARRAYSIZE(userProfile))
    {
        return FALSE;
    }

    int written = sprintf_s(path, pathSize, "%s\\bime_tsf.log", userProfile);
    return (written > 0) ? TRUE : FALSE;
}

static void WriteLogLineV(const char *format, va_list args)
{
    if (format == nullptr || !IsTextLoggingEnabled())
    {
        return;
    }

    static const char *kBuildStamp = __DATE__ " " __TIME__;

    char logPath[MAX_PATH] = {0};
    if (!BuildLogPath(logPath, ARRAYSIZE(logPath)))
    {
        return;
    }

    FILE *fp = nullptr;
    fopen_s(&fp, logPath, "a");
    if (fp == nullptr)
    {
        return;
    }

    SYSTEMTIME st = {};
    GetLocalTime(&st);
    fprintf(fp, "[%02u:%02u:%02u.%03u][build:%s] ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, kBuildStamp);

    vfprintf(fp, format, args);
    fprintf(fp, "\n");
    fclose(fp);
}

void LogToFile(const char *format, ...)
{
    va_list args;
    va_start(args, format);
    WriteLogLineV(format, args);
    va_end(args);
}

void LogToFileVerbose(const char *format, ...)
{
    if (!IsVerboseLoggingEnabled())
    {
        return;
    }

    va_list args;
    va_start(args, format);
    WriteLogLineV(format, args);
    va_end(args);
}

BOOL CompareElements(LCID locale, const CStringRange* pElement1, const CStringRange* pElement2)
{
    return (CStringRange::Compare(locale, (CStringRange*)pElement1, (CStringRange*)pElement2) == CSTR_EQUAL) ? TRUE : FALSE;
}
}
