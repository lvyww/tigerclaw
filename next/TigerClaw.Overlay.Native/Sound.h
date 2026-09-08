#pragma once
#include "WinResources.h"
#include <xaudio2.h>
#include <array>
#include <vector>

namespace tiger::overlay
{
    class Sound
    {
        struct Sample { WAVEFORMATEX format{}; std::vector<BYTE> bytes; };
        std::wstring directory_;
        ComPtr<IXAudio2> engine_;
        IXAudio2MasteringVoice* master_ = nullptr;
        std::array<IXAudio2SourceVoice*, 6> voices_{};
        std::array<Sample, 3> samples_;
        unsigned next_ = 0;
        ULONGLONG retryAfter_ = 0;
        void Initialize();
        void Release();
    public:
        explicit Sound(std::wstring directory) : directory_(std::move(directory)) {}
        ~Sound();
        Sound(const Sound&) = delete;
        Sound& operator=(const Sound&) = delete;
        bool Prepare();
        void Play(int vk, int volume);
    };
}
