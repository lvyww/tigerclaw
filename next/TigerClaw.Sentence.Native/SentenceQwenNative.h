#pragma once

#include <cstddef>
#include <cstdint>

#if defined(_WIN32)
#if defined(TIGERCLAW_SENTENCE_NATIVE_STATIC)
#define TCS_API extern "C"
#elif defined(TIGERCLAW_SENTENCE_NATIVE_EXPORTS)
#define TCS_API extern "C" __declspec(dllexport)
#else
#define TCS_API extern "C" __declspec(dllimport)
#endif
#define TCS_CALL __cdecl
#else
#define TCS_API extern "C" __attribute__((visibility("default")))
#define TCS_CALL
#endif

TCS_API int TCS_CALL tcs_create_from_file(
    const char* modelPathUtf8,
    void** scorer);

TCS_API int TCS_CALL tcs_score(
    void* scorer,
    const char* const* candidatesUtf8,
    int32_t candidateCount,
    double* scores);

TCS_API void TCS_CALL tcs_destroy(void* scorer);

TCS_API const char* TCS_CALL tcs_last_error();
