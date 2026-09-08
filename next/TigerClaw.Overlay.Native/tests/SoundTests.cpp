#include "Sound.h"
#include <iostream>
using namespace tiger::overlay;
int main()
{
    HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    int result = 0;
    try
    {
        std::wstring directory(32768, L'\0');
        directory.resize(GetModuleFileNameW(nullptr, directory.data(), static_cast<DWORD>(directory.size())));
        directory.resize(directory.find_last_of(L"\\/"));
        for (int i = 0; i < 5; ++i)
        {
            Sound sound(directory);
            if (!sound.Prepare() || !sound.Prepare()) throw std::runtime_error("Audio initialization failed; check endpoint and WAV files");
            sound.Play(VK_SPACE, 0); // Silent test, never submits audible buffers.
        }
        Sound missing(directory + L"\\missing-audio-" + std::to_wstring(GetCurrentProcessId()));
        if (missing.Prepare() || missing.Prepare()) throw std::runtime_error("Missing audio was accepted");
        std::cout << "Silent audio load/release tests passed (3 samples, 6 voices, 5 cycles)\n";
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; result = 1; }
    if (SUCCEEDED(com)) CoUninitialize();
    return result;
}
