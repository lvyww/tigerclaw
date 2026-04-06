#pragma once

#include <windows.h>

#include <string>

namespace TigerClawHookNative
{
    class CoreIntegrity
    {
    public:
        bool IsTrialExpiredNow(bool& expired, std::wstring& expireUtcText) const;
        bool VerifyPipeServerExecutable(HANDLE pipe, std::wstring& serverPath, std::wstring& error);

    private:
        bool IsVerifyEnabled() const;
        bool TryGetEmbeddedTrialExpireUtc(char* text, size_t textCount) const;
        static bool TryParseTrialExpireUtc(const char* text, FILETIME* fileTime);
        bool TryResolvePipeServerProcessPath(HANDLE pipe, std::wstring& serverPath, std::wstring& error) const;
        bool VerifyExecutableHashCached(const std::wstring& serverPath, std::wstring& error);
        bool TryGetExpectedCoreHash(char* hashValue, size_t hashCount) const;
        bool ComputeFileSha256Hex(const wchar_t* filePath, char* outHex, size_t outCount) const;
        bool TryGetFileLastWriteTime(const wchar_t* path, FILETIME* fileTime) const;
        static bool IsValidSha256Hex(const char* text);
        static BYTE ComputeEmbeddedMask(size_t index);
        static bool DecodeEmbeddedAscii(const unsigned char* encrypted, size_t encryptedLen, char* output, size_t outputCount);

        bool _cacheReady = false;
        bool _cacheOk = false;
        std::wstring _cachedPath;
        FILETIME _cachedLastWrite = {};
        std::string _cachedExpectedHash;
        std::string _cachedActualHash;

        bool _embeddedHashInitialized = false;
        bool _embeddedHashValid = false;
        char _embeddedHashValue[65] = {};
        mutable bool _embeddedTrialInitialized = false;
        mutable bool _embeddedTrialValid = false;
        mutable char _embeddedTrialExpireUtc[64] = {};
    };
}
