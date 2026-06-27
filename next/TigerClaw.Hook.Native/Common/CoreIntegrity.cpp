#include "CoreIntegrity.h"

#include "Logger.h"
#include "NativeHelpers.h"

#include <bcrypt.h>
#include <strsafe.h>

#include <algorithm>
#include <cstdio>
#include <string>

#include "..\..\..\BimeTSF2\SampleIME\EmbeddedBuildInfo.h"

namespace TigerClawHookNative
{
    bool CoreIntegrity::IsTrialExpiredNow(bool& expired, std::wstring& expireUtcText) const
    {
        expired = false;
        expireUtcText.clear();

        char trialExpireUtc[64] = {};
        if (!TryGetEmbeddedTrialExpireUtc(trialExpireUtc, ARRAYSIZE(trialExpireUtc)))
        {
            expireUtcText = L"embedded_invalid";
            return false;
        }

        FILETIME expireUtcFt = {};
        if (!TryParseTrialExpireUtc(trialExpireUtc, &expireUtcFt))
        {
            expireUtcText = WideFromUtf8(trialExpireUtc);
            return false;
        }

        FILETIME nowUtcFt = {};
        GetSystemTimeAsFileTime(&nowUtcFt);
        expired = (CompareFileTime(&nowUtcFt, &expireUtcFt) >= 0);
        expireUtcText = WideFromUtf8(trialExpireUtc);
        return true;
    }

    bool CoreIntegrity::VerifyPipeServerExecutable(HANDLE pipe, std::wstring& serverPath, std::wstring& error)
    {
        serverPath.clear();

        if (!IsVerifyEnabled())
        {
            Logger::Info(L"integrity", L"verify bypassed by embedded config");
            return true;
        }

        if (!TryResolvePipeServerProcessPath(pipe, serverPath, error))
        {
            return false;
        }

        return VerifyExecutableHashCached(serverPath, error);
    }

    bool CoreIntegrity::IsVerifyEnabled() const
    {
#if defined(_DEBUG)
        return false;
#elif (BIME_EMBED_CORE_HASH_VERIFY_ENABLED == 0)
        return false;
#else
        return true;
#endif
    }

    bool CoreIntegrity::TryGetEmbeddedTrialExpireUtc(char* text, size_t textCount) const
    {
        if (text == nullptr || textCount < 2)
        {
            return false;
        }

        if (!_embeddedTrialInitialized)
        {
            _embeddedTrialInitialized = true;
            _embeddedTrialValid = false;
            _embeddedTrialExpireUtc[0] = '\0';

            char decoded[64] = {};
            if (DecodeEmbeddedAscii(
                    BIME_EMBED_TRIAL_EXPIRE_UTC_ENC,
                    static_cast<size_t>(BIME_EMBED_TRIAL_EXPIRE_UTC_LEN),
                    decoded,
                    ARRAYSIZE(decoded)) &&
                TryParseTrialExpireUtc(decoded, nullptr))
            {
                StringCchCopyA(_embeddedTrialExpireUtc, ARRAYSIZE(_embeddedTrialExpireUtc), decoded);
                _embeddedTrialValid = true;
            }
        }

        if (!_embeddedTrialValid)
        {
            return false;
        }

        StringCchCopyA(text, textCount, _embeddedTrialExpireUtc);
        return true;
    }

    bool CoreIntegrity::TryParseTrialExpireUtc(const char* text, FILETIME* fileTime)
    {
        if (text == nullptr)
        {
            return false;
        }

        int year = 0;
        int month = 0;
        int day = 0;
        int hour = 0;
        int minute = 0;
        int second = 0;
        char suffix = '\0';
        if (sscanf_s(text, "%4d-%2d-%2dT%2d:%2d:%2d%c", &year, &month, &day, &hour, &minute, &second, &suffix, 1) != 7 ||
            suffix != 'Z')
        {
            return false;
        }

        if (fileTime == nullptr)
        {
            return true;
        }

        SYSTEMTIME systemTime = {};
        systemTime.wYear = static_cast<WORD>(year);
        systemTime.wMonth = static_cast<WORD>(month);
        systemTime.wDay = static_cast<WORD>(day);
        systemTime.wHour = static_cast<WORD>(hour);
        systemTime.wMinute = static_cast<WORD>(minute);
        systemTime.wSecond = static_cast<WORD>(second);
        systemTime.wMilliseconds = 0;
        return SystemTimeToFileTime(&systemTime, fileTime) != FALSE;
    }

    bool CoreIntegrity::TryResolvePipeServerProcessPath(HANDLE pipe, std::wstring& serverPath, std::wstring& error) const
    {
        ULONG processId = 0;
        if (!GetNamedPipeServerProcessId(pipe, &processId) || processId == 0)
        {
            error = L"Core integrity failed: cannot resolve pipe server process id.";
            return false;
        }

        HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processId);
        if (process == nullptr)
        {
            error = L"Core integrity failed: cannot open pipe server process: " + GetLastErrorMessage(GetLastError());
            return false;
        }

        wchar_t pathBuffer[MAX_PATH] = {};
        DWORD pathLength = ARRAYSIZE(pathBuffer);
        const BOOL ok = QueryFullProcessImageNameW(process, 0, pathBuffer, &pathLength);
        CloseHandle(process);

        if (!ok || pathLength == 0)
        {
            error = L"Core integrity failed: cannot resolve pipe server path: " + GetLastErrorMessage(GetLastError());
            return false;
        }

        serverPath.assign(pathBuffer, pathLength);
        return true;
    }

    bool CoreIntegrity::VerifyExecutableHashCached(const std::wstring& serverPath, std::wstring& error)
    {
        char expectedSha256[65] = {};
        if (!TryGetExpectedCoreHash(expectedSha256, ARRAYSIZE(expectedSha256)))
        {
#if defined(_DEBUG)
            Logger::Info(L"integrity", L"debug build skips embedded hash enforcement");
            return true;
#else
            error = L"Core integrity failed: embedded hash unavailable.";
            return false;
#endif
        }

        FILETIME lastWrite = {};
        if (!TryGetFileLastWriteTime(serverPath.c_str(), &lastWrite))
        {
            error = L"Core integrity failed: cannot read core last-write time: " + serverPath;
            return false;
        }

        if (_cacheReady &&
            _wcsicmp(_cachedPath.c_str(), serverPath.c_str()) == 0 &&
            CompareFileTime(&_cachedLastWrite, &lastWrite) == 0 &&
            _cachedExpectedHash == expectedSha256)
        {
            if (!_cacheOk)
            {
                error = L"Core integrity failed: cached hash mismatch.";
            }

            return _cacheOk;
        }

        char actualSha256[65] = {};
        if (!ComputeFileSha256Hex(serverPath.c_str(), actualSha256, ARRAYSIZE(actualSha256)))
        {
            error = L"Core integrity failed: hash computation failed: " + serverPath;
            return false;
        }

        const bool matched = strcmp(actualSha256, expectedSha256) == 0;
        _cachedPath = serverPath;
        _cachedLastWrite = lastWrite;
        _cachedExpectedHash = expectedSha256;
        _cachedActualHash = actualSha256;
        _cacheOk = matched;
        _cacheReady = true;

        if (!matched)
        {
            Logger::Info(L"integrity", L"core hash mismatch");
            error = L"Core integrity failed: hash mismatch.";
        }
        else
        {
            Logger::Info(L"integrity", L"core hash verified");
        }

        return matched;
    }

    bool CoreIntegrity::TryGetExpectedCoreHash(char* hashValue, size_t hashCount) const
    {
        if (hashValue == nullptr || hashCount < 65)
        {
            return false;
        }

        hashValue[0] = '\0';

#if defined(_DEBUG)
        return false;
#else
        if (!_embeddedHashInitialized)
        {
            const_cast<CoreIntegrity*>(this)->_embeddedHashInitialized = true;
            const_cast<CoreIntegrity*>(this)->_embeddedHashValid = false;
            const_cast<CoreIntegrity*>(this)->_embeddedHashValue[0] = '\0';

            char decoded[65] = {};
            if (DecodeEmbeddedAscii(
                    BIME_EMBED_CORE_SHA256_ENC,
                    static_cast<size_t>(BIME_EMBED_CORE_SHA256_LEN),
                    decoded,
                    ARRAYSIZE(decoded)) &&
                IsValidSha256Hex(decoded))
            {
                StringCchCopyA(const_cast<CoreIntegrity*>(this)->_embeddedHashValue, ARRAYSIZE(_embeddedHashValue), decoded);
                const_cast<CoreIntegrity*>(this)->_embeddedHashValid = true;
            }
        }

        if (!_embeddedHashValid)
        {
            return false;
        }

        StringCchCopyA(hashValue, hashCount, _embeddedHashValue);
        return true;
#endif
    }

    bool CoreIntegrity::ComputeFileSha256Hex(const wchar_t* filePath, char* outHex, size_t outCount) const
    {
        if (filePath == nullptr || outHex == nullptr || outCount < 65)
        {
            return false;
        }

        outHex[0] = '\0';

        BCRYPT_ALG_HANDLE algorithm = nullptr;
        BCRYPT_HASH_HANDLE hash = nullptr;
        PUCHAR hashObject = nullptr;
        PUCHAR hashBuffer = nullptr;
        ULONG hashObjectSize = 0;
        ULONG hashSize = 0;
        ULONG cbData = 0;
        FILE* file = nullptr;
        bool success = false;

        BYTE readBuffer[64 * 1024] = {};
        const char* hex = "0123456789abcdef";

        NTSTATUS status = BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0);
        if (!BCRYPT_SUCCESS(status))
        {
            goto Exit;
        }

        status = BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&hashObjectSize), sizeof(hashObjectSize), &cbData, 0);
        if (!BCRYPT_SUCCESS(status) || hashObjectSize == 0)
        {
            goto Exit;
        }

        status = BCryptGetProperty(algorithm, BCRYPT_HASH_LENGTH, reinterpret_cast<PUCHAR>(&hashSize), sizeof(hashSize), &cbData, 0);
        if (!BCRYPT_SUCCESS(status) || hashSize == 0)
        {
            goto Exit;
        }

        hashObject = static_cast<PUCHAR>(HeapAlloc(GetProcessHeap(), 0, hashObjectSize));
        hashBuffer = static_cast<PUCHAR>(HeapAlloc(GetProcessHeap(), 0, hashSize));
        if (hashObject == nullptr || hashBuffer == nullptr)
        {
            goto Exit;
        }

        status = BCryptCreateHash(algorithm, &hash, hashObject, hashObjectSize, nullptr, 0, 0);
        if (!BCRYPT_SUCCESS(status))
        {
            goto Exit;
        }

        if (_wfopen_s(&file, filePath, L"rb") != 0 || file == nullptr)
        {
            goto Exit;
        }

        while (true)
        {
            const size_t bytesRead = fread(readBuffer, 1, sizeof(readBuffer), file);
            if (bytesRead == 0)
            {
                break;
            }

            status = BCryptHashData(hash, readBuffer, static_cast<ULONG>(bytesRead), 0);
            if (!BCRYPT_SUCCESS(status))
            {
                goto Exit;
            }
        }

        if (ferror(file) != 0)
        {
            goto Exit;
        }

        status = BCryptFinishHash(hash, hashBuffer, hashSize, 0);
        if (!BCRYPT_SUCCESS(status))
        {
            goto Exit;
        }

        for (ULONG i = 0; i < hashSize; ++i)
        {
            const BYTE value = hashBuffer[i];
            outHex[i * 2] = hex[(value >> 4) & 0x0F];
            outHex[i * 2 + 1] = hex[value & 0x0F];
        }

        outHex[hashSize * 2] = '\0';
        success = true;

    Exit:
        if (file != nullptr)
        {
            fclose(file);
        }

        if (hash != nullptr)
        {
            BCryptDestroyHash(hash);
        }

        if (hashBuffer != nullptr)
        {
            HeapFree(GetProcessHeap(), 0, hashBuffer);
        }

        if (hashObject != nullptr)
        {
            HeapFree(GetProcessHeap(), 0, hashObject);
        }

        if (algorithm != nullptr)
        {
            BCryptCloseAlgorithmProvider(algorithm, 0);
        }

        if (!success)
        {
            outHex[0] = '\0';
        }

        return success;
    }

    bool CoreIntegrity::TryGetFileLastWriteTime(const wchar_t* path, FILETIME* fileTime) const
    {
        if (path == nullptr || fileTime == nullptr)
        {
            return false;
        }

        WIN32_FILE_ATTRIBUTE_DATA data = {};
        if (!GetFileAttributesExW(path, GetFileExInfoStandard, &data))
        {
            return false;
        }

        *fileTime = data.ftLastWriteTime;
        return true;
    }

    bool CoreIntegrity::IsValidSha256Hex(const char* text)
    {
        if (text == nullptr || strlen(text) != 64)
        {
            return false;
        }

        for (size_t i = 0; i < 64; ++i)
        {
            const char c = text[i];
            const bool isHex = (c >= '0' && c <= '9') ||
                               (c >= 'a' && c <= 'f') ||
                               (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    BYTE CoreIntegrity::ComputeEmbeddedMask(size_t index)
    {
        const BYTE keys[] = {BIME_EMBED_XOR_KEY0, BIME_EMBED_XOR_KEY1, BIME_EMBED_XOR_KEY2, BIME_EMBED_XOR_KEY3};
        return static_cast<BYTE>(keys[index % ARRAYSIZE(keys)] ^ ((index * 13 + 0x5A) & 0xFF));
    }

    bool CoreIntegrity::DecodeEmbeddedAscii(const unsigned char* encrypted, size_t encryptedLen, char* output, size_t outputCount)
    {
        if (encrypted == nullptr || output == nullptr)
        {
            return false;
        }

        if (outputCount == 0 || encryptedLen + 1 > outputCount)
        {
            return false;
        }

        for (size_t i = 0; i < encryptedLen; ++i)
        {
            output[i] = static_cast<char>(encrypted[i] ^ ComputeEmbeddedMask(i));
        }

        output[encryptedLen] = '\0';
        return true;
    }
}
