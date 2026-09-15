#include "SentenceQwenNative.h"
#include <atomic>
#include <array>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <memory>
#include <stdexcept>

namespace
{
    struct AbortState { std::atomic<int> Calls{0}; int Limit = 1; };
    bool TCS_CALL Abort(void* parameter)
    {
        auto& state = *static_cast<AbortState*>(parameter);
        return ++state.Calls >= state.Limit;
    }
    void Require(bool condition, const char* text)
    {
        if (!condition) throw std::runtime_error(text);
    }
    struct Destroy { void operator()(void* scorer) const { tcs_destroy(scorer); } };
}

int main(int argc, char** argv)
{
    try
    {
        if (argc != 2) throw std::runtime_error("Usage: CancelProbe MODEL.gguf");
        void* raw = nullptr;
        const int load = tcs_create_from_file(argv[1], &raw);
        if (load != 0) throw std::runtime_error(tcs_last_error());
        std::unique_ptr<void, Destroy> scorer(raw);
        const char* texts[] = { u8"今天我们一起讨论输入法的性能优化方案。",
            u8"今天我们一起讨论输入法的性能优化方法。", u8"今天我们一起讨论输入法的性能改进方案。",
            u8"今天我们一起讨论输入法的内存优化方案。", u8"今天我们一起讨论输入法的性能优化计划。" };
        std::array<double, 5> baseline{}, output{};
        Require(tcs_score(raw, texts, 5, baseline.data()) == 0, tcs_last_error());
        for (int limit : { 1, 16, 64 })
        {
            AbortState state; state.Limit = limit;
            output.fill(123456.0);
            const int status = tcs_score_cancellable(raw, texts, 5, output.data(), Abort, &state);
            Require(status != 0 && state.Calls >= limit, "abort callback did not stop scoring");
            for (double score : output) Require(score == 123456.0, "partial scores escaped cancellation");
            Require(tcs_score(raw, texts, 5, output.data()) == 0, tcs_last_error());
            Require(std::memcmp(baseline.data(), output.data(), sizeof(baseline)) == 0,
                "scoring after cancellation changed exact scores");
            std::cout << "{\"test\":\"native_qwen_abort\",\"limit\":" << limit
                << ",\"callback_calls\":" << state.Calls << ",\"status\":\"passed\"}" << std::endl;
        }
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << std::endl; return 1; }
}
