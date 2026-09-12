#include "Sound.h"
#include <algorithm>
#include <cstring>
#include <filesystem>
#include <fstream>

namespace tiger::overlay
{
    Sound::~Sound()
    {
        Release();
    }
    void Sound::Release()
    {
        for (auto& voice : voices_)
        {
            if (voice) voice->DestroyVoice();
            voice = nullptr;
        }
        if (master_) master_->DestroyVoice();
        master_ = nullptr;
        engine_.Reset();
        for (auto& sample : samples_)
        {
            sample.format = {};
            std::vector<BYTE>().swap(sample.bytes);
        }
    }
    bool Sound::Prepare()
    {
        if (engine_) return true;
        if (GetTickCount64() < retryAfter_) return false;
        try { Initialize(); return true; }
        catch (...)
        {
            Release();
            retryAfter_ = GetTickCount64() + 2000;
            return false;
        }
    }
    void Sound::Initialize()
    {
        const wchar_t* names[] = {L"KeyNormal.wav", L"KeySpace.wav", L"KeyFunc.wav"};
        for (int i = 0; i < 3; ++i)
        {
            std::ifstream file(std::filesystem::path(directory_) / L"sounds" / names[i], std::ios::binary | std::ios::ate);
            if (!file) throw std::runtime_error("Sound file missing");
            auto size = file.tellg();
            if (size < 12 || size > 8 * 1024 * 1024) throw std::runtime_error("Invalid WAV size");
            std::vector<char> bytes(static_cast<std::size_t>(size));
            file.seekg(0); file.read(bytes.data(), size);
            if (!file || std::memcmp(bytes.data(), "RIFF", 4) || std::memcmp(bytes.data() + 8, "WAVE", 4))
                throw std::runtime_error("Invalid WAV header");
            auto& sample = samples_[i];
            for (std::size_t pos = 12; pos + 8 <= bytes.size();)
            {
                std::uint32_t length = 0;
                std::memcpy(&length, bytes.data() + pos + 4, 4);
                if (length > bytes.size() - pos - 8) throw std::runtime_error("Invalid WAV chunk");
                if (!std::memcmp(bytes.data() + pos, "fmt ", 4) && length >= 16)
                    std::memcpy(&sample.format, bytes.data() + pos + 8, 16);
                if (!std::memcmp(bytes.data() + pos, "data", 4))
                    sample.bytes.assign(bytes.begin() + pos + 8, bytes.begin() + pos + 8 + length);
                pos += 8 + static_cast<std::size_t>(length) + (length & 1);
            }
            if (sample.format.wFormatTag != WAVE_FORMAT_PCM || sample.bytes.empty() ||
                !sample.format.nChannels || !sample.format.nSamplesPerSec || !sample.format.nBlockAlign)
                throw std::runtime_error("Unsupported WAV format");
        }
        CheckHr(XAudio2Create(engine_.GetAddressOf(), 0, XAUDIO2_DEFAULT_PROCESSOR));
        CheckHr(engine_->CreateMasteringVoice(&master_));
        for (unsigned i = 0; i < voices_.size(); ++i)
            CheckHr(engine_->CreateSourceVoice(&voices_[i], &samples_[i < 4 ? 0 : i - 3].format));
    }
    void Sound::Play(int vk, int volume)
    {
        if (volume <= 0 || !Prepare()) return;
        try
        {
            unsigned slot;
            if (vk == VK_SPACE) slot = 4;
            else if (vk == VK_BACK || vk == VK_RETURN || vk == VK_ESCAPE || vk == VK_SHIFT || vk == VK_LSHIFT || vk == VK_RSHIFT) slot = 5;
            else { slot = next_; next_ = (next_ + 1) % 4; }
            auto voice = voices_[slot];
            const auto& sample = samples_[slot < 4 ? 0 : slot - 3];
            CheckHr(voice->Stop()); CheckHr(voice->FlushSourceBuffers());
            CheckHr(voice->SetVolume(std::clamp(volume, 0, 100) / 100.0f));
            XAUDIO2_BUFFER buffer{};
            buffer.AudioBytes = static_cast<UINT32>(sample.bytes.size());
            buffer.pAudioData = sample.bytes.data(); buffer.Flags = XAUDIO2_END_OF_STREAM;
            CheckHr(voice->SubmitSourceBuffer(&buffer));
            CheckHr(voice->Start());
        }
        catch (...)
        {
            Release();
            retryAfter_ = GetTickCount64() + 2000;
        }
    }
}
