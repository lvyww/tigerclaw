#include "RuntimeProtocol.h"
#include "OutputServices.h"
#include <fstream>
#include <iostream>
#include <nlohmann/json.hpp>
#ifdef _WIN32
#include "RuntimePipeServer.h"
#include "RuntimeUiPublisher.h"
#include "RuntimeFrontendHost.h"
#include "RuntimeStartup.h"
#include "RuntimeEndpoints.h"
#include "OwnedSentenceService.h"
#include "ManualTimer.h"
#include <windows.h>
#include <shellapi.h>
#include <condition_variable>
#include <thread>
#endif
int RunRuntimeProtocolProbe(const std::filesystem::path& root, const std::filesystem::path& source, const std::filesystem::path& target)
{
    using namespace tiger::core;
    std::ifstream reader(source, std::ios::binary);
    std::ofstream writer(target, std::ios::binary);
    auto uiPath=target; uiPath += ".ui.jsonl";
    std::ofstream uiWriter(uiPath,std::ios::binary);
    auto commandsPath=target; commandsPath += ".commands.jsonl";
    std::ofstream commandWriter(commandsPath,std::ios::binary);
    if (!reader || !writer) throw std::runtime_error("Cannot open runtime protocol probe files");
    RuntimeLexicons runtime(root); runtime.Reload(root / "config.txt", {}, true);
    RuntimeInput input(runtime, MakeUpperCaseServices([](auto) {}), root/"Models/sentence-fivegram-mobile.bin", nullptr, true);
    std::vector<int> commands;
    RuntimeProtocol protocol(runtime, input, root / "config.txt",[&](RuntimeUiCommand command) { commands.push_back(static_cast<int>(command)); });
    for (std::string line; std::getline(reader, line);)
    {
        auto response = protocol.Handle(line, std::chrono::system_clock::now());
        writer << (response ? *response : "null") << '\n';
        uiWriter << protocol.CaptureUiState() << '\n';
        commandWriter << nlohmann::json(commands).dump() << '\n'; commands.clear();
    }
    if (!writer || !uiWriter || !commandWriter) throw std::runtime_error("Cannot write runtime protocol probe output");
    return 0;
}
#ifdef _WIN32
static int RunRuntimeHost(const std::filesystem::path& root, const tiger::core::RuntimeEndpoints& endpoints, bool withUi, bool autoOverlay, bool launchHook, bool waitStdin)
{
    using namespace tiger::core;
    // Production owns the singleton before calling this function. Test roots
    // must never synchronize registry or touch production endpoint names.
    if(!endpoints.isolated)
    {
        EnsureConfigFile(root/"config.txt");
        EnsureSelectionBindingsFile(root/u"自定义选重键.txt");
    }
    RuntimeLexicons runtime(root); runtime.Reload(root / "config.txt",{},true);
    ManualTimer timer;
    std::unique_ptr<OwnedSentenceService> neural;
    auto sidecar=root/"sentence/TigerClaw.Sentence.exe";
    auto neuralModel=root/"sentence/Models/sentence-qwen-q8.gguf";
    if (!std::filesystem::is_regular_file(sidecar)) sidecar=root/"TigerClaw.Sentence.exe";
    if (!std::filesystem::is_regular_file(neuralModel)) neuralModel=root/"Models/sentence-qwen-q8.gguf";
    if (std::filesystem::is_regular_file(sidecar) && std::filesystem::is_regular_file(neuralModel))
        neural=std::make_unique<OwnedSentenceService>(std::filesystem::absolute(sidecar),std::filesystem::absolute(neuralModel),endpoints.sentence);
    RuntimeInput input(runtime,MakeUpperCaseServices([&](auto code) { timer.ScheduleCommand(code); }),root/"Models/sentence-fivegram-mobile.bin",neural ? &neural->Lifecycle() : nullptr,true);
    struct StopEvent { HANDLE value=CreateEventW(nullptr,TRUE,FALSE,nullptr); ~StopEvent() { if(value) CloseHandle(value); } } stopped;
    if(!stopped.value) throw std::runtime_error("Cannot create host shutdown event");
    std::atomic<bool> exitRequested{false};
    std::unique_ptr<RuntimeFrontendHost> frontends;
    if (withUi) frontends=std::make_unique<RuntimeFrontendHost>(root,endpoints.pipe);
    RuntimeProtocol protocol(runtime,input,root / "config.txt",[&](RuntimeUiCommand command)
    {
        if(command==RuntimeUiCommand::ExitCore) exitRequested.store(true);
        else if (frontends)
        {
            bool ok=command==RuntimeUiCommand::ShowMenu ? frontends->Overlay(true) :
                frontends->Dialog(command==RuntimeUiCommand::ShowAddCi);
            if (!ok) throw std::runtime_error("Cannot launch frontend from isolated root");
        }
        else throw std::runtime_error("UI requires --serve-isolated-ui and frontend files in the explicit root");
    },[&](std::u16string_view target,bool select)
    {
        if (!withUi) return false;
        std::wstring path(target.begin(),target.end());
        if (select)
        {
            auto arguments=L"/select,\""+path+L"\"";
            return reinterpret_cast<INT_PTR>(ShellExecuteW(nullptr,L"open",L"explorer.exe",arguments.c_str(),nullptr,SW_SHOWNORMAL))>32;
        }
        return reinterpret_cast<INT_PTR>(ShellExecuteW(nullptr,L"open",path.c_str(),nullptr,nullptr,SW_SHOWNORMAL))>32;
    });
    std::unique_ptr<RuntimeUiPublisher> publisher;
    std::atomic<RuntimeUiPublisher*> livePublisher{nullptr};
    std::mutex configSync; std::uint64_t savedVersion=0;
    auto publish=[&]
    {
        if(auto active=livePublisher.load()) active->Publish(protocol.CaptureUiState());
        if(!endpoints.isolated)
        {
            std::lock_guard lock(configSync); auto snapshot=runtime.Read();
            if(savedVersion!=snapshot->configVersion)
            {
                bool enabled=false;
                for(const auto& [key,value]:snapshot->config)
                    if(key==u"开机自动启动") enabled=ParseConfigBool(value,false);
                SyncCoreAutoStart(enabled);
                try { runtime.SaveConfig(root/"config.txt"); } catch(...) {}
                savedVersion=snapshot->configVersion;
            }
        }
    };
    // Claim the pipe before opening any production UI maps. An occupied pipe
    // must never let a second Core overwrite the running Core's UI/heartbeat.
    RuntimePipeServer server(protocol,endpoints.pipe,[&]
    {
        publish();
        if(exitRequested.exchange(false)) SetEvent(stopped.value);
    });
    publisher=std::make_unique<RuntimeUiPublisher>(endpoints.ui);
    livePublisher.store(publisher.get());
    RuntimeHeartbeat heartbeat(endpoints.heartbeat);
    publish();
    if (frontends && autoOverlay && !frontends->Overlay() && endpoints.isolated)
        throw std::runtime_error("Cannot launch isolated Overlay");
    if(launchHook && (!frontends || !frontends->Hook())) throw std::runtime_error("Cannot launch TigerClaw.exe native Hook");
    // Pump asynchronous Beam results and expire fresh-caret gates even when no
    // further key arrives. Stop-aware wait avoids delaying shutdown.
    std::jthread ui([&](std::stop_token stop)
    {
        std::mutex mutex; std::unique_lock lock(mutex); std::condition_variable_any wake;
        while (!stop.stop_requested())
        {
            wake.wait_for(lock,stop,std::chrono::milliseconds(20),[] { return false; });
            if (!stop.stop_requested())
            {
                try { publish(); } catch (...) {}
                if (frontends && autoOverlay) { try { frontends->Tick(); } catch (...) {} }
            }
        }
    });
    if(!waitStdin)
    {
        // Normal TSF/autorun launch has no stdin. EOF must never stop Core.
        if(endpoints.isolated) std::cout << "Isolated detached Core pipe ready.\n" << std::flush;
        WaitForSingleObject(stopped.value,INFINITE);
        return 0;
    }
    std::cout << "Isolated Core pipe ready; send quit on stdin to stop.\n" << std::flush;
    std::thread commands([&]
    {
        for (std::string line; std::getline(std::cin,line);) if (line=="quit") break;
        SetEvent(stopped.value);
    });
    WaitForSingleObject(stopped.value,INFINITE);
    CancelSynchronousIo(commands.native_handle()); commands.join();
    return 0;
}
int RunIsolatedRuntimeHost(const std::filesystem::path& root, const std::wstring& pipeName, bool withUi, bool waitStdin)
{
    return RunRuntimeHost(root,tiger::core::RuntimeEndpoints::Isolated(pipeName),withUi,withUi,false,waitStdin);
}
int RunProductionRuntimeHost(bool withOverlay)
{
    using namespace tiger::core;
    if(!CanStartCore()) return 0;
    auto executable=CoreExecutablePath(); auto root=executable.parent_path();
    bool launchHook=!IsTsfRegistered();
    if(launchHook && !std::filesystem::is_regular_file(root/L"TigerClaw.exe"))
    {
        MessageBoxW(nullptr,L"未检测到 TigerClaw 输入法注册。请先运行安装.bat 完成注册后再启动。",L"TigerClaw",MB_OK|MB_ICONWARNING);
        return 0;
    }
    CoreInstance instance(executable); if(!instance.Acquired()) return 0;
    return RunRuntimeHost(root,RuntimeEndpoints{},true,withOverlay,launchHook,false);
}
#endif
