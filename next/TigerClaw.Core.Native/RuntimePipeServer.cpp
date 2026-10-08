#include "RuntimePipeServer.h"
#include <windows.h>
#include <array>
#include <thread>
#include <list>
#include <atomic>
#include <sddl.h>
#include <vector>

namespace tiger::core
{
    namespace
    {
        struct Handle
        {
            HANDLE value = INVALID_HANDLE_VALUE;
            explicit Handle(HANDLE v) : value(v) {}
            ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
            Handle(const Handle&) = delete;
        };
        struct PipeSecurity
        {
            PSECURITY_DESCRIPTOR descriptor=nullptr;
            SECURITY_ATTRIBUTES attributes{sizeof(SECURITY_ATTRIBUTES),nullptr,FALSE};
            PipeSecurity()
            {
                HANDLE token=nullptr;
                if (!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token)) throw std::runtime_error("Cannot read pipe owner");
                Handle owner(token); DWORD length=0;
                GetTokenInformation(token,TokenUser,nullptr,0,&length);
                std::vector<unsigned char> data(length);
                if (!GetTokenInformation(token,TokenUser,data.data(),length,&length)) throw std::runtime_error("Cannot read pipe owner SID");
                LPWSTR sid=nullptr;
                if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(data.data())->User.Sid,&sid)) throw std::runtime_error("Cannot encode pipe owner SID");
                // Same trustees and ReadWrite/CreateNewInstance rights as C#
                // PipeServer.BuildPipeSecurity; remote clients stay rejected.
                std::wstring sddl=L"D:(A;;0x12019f;;;"+std::wstring(sid)+L")";
                LocalFree(sid);
                for (auto trustee : {L"SY",L"AU",L"BU",L"S-1-15-2-1",L"S-1-15-2-2"})
                    sddl+=L"(A;;0x12019f;;;"+std::wstring(trustee)+L")";
                if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(),SDDL_REVISION_1,&descriptor,nullptr))
                    throw std::runtime_error("Cannot create Core pipe ACL");
                attributes.lpSecurityDescriptor=descriptor;
            }
            ~PipeSecurity() { if(descriptor) LocalFree(descriptor); }
        };
        bool Finish(HANDLE pipe, HANDLE stop, OVERLAPPED& operation, DWORD& size, DWORD timeout)
        {
            HANDLE events[]{operation.hEvent,stop};
            if (WaitForMultipleObjects(2,events,FALSE,timeout) != WAIT_OBJECT_0)
            {
                CancelIoEx(pipe,&operation);
                GetOverlappedResult(pipe,&operation,&size,TRUE);
                return false;
            }
            return GetOverlappedResult(pipe,&operation,&size,FALSE) || GetLastError() == ERROR_MORE_DATA;
        }
        DWORD Transfer(HANDLE pipe, HANDLE stop, void* data, DWORD length, bool write)
        {
            Handle event(CreateEventW(nullptr,TRUE,FALSE,nullptr));
            if (!event.value || WaitForSingleObject(stop,0) == WAIT_OBJECT_0) return 0;
            OVERLAPPED op{}; op.hEvent = event.value; DWORD size = 0;
            BOOL ok = write ? WriteFile(pipe,data,length,&size,&op) : ReadFile(pipe,data,length,&size,&op);
            if (!ok)
            {
                auto error = GetLastError();
                if (error == ERROR_MORE_DATA) return size;
                if (error != ERROR_IO_PENDING || !Finish(pipe,stop,op,size,write ? 5000 : INFINITE)) return 0;
            }
            return size;
        }
    }
    struct RuntimePipeServer::Impl
    {
        RuntimeProtocol& protocol;
        std::function<void()> publish;
        std::wstring path;
        PipeSecurity security;
        Handle stop{CreateEventW(nullptr,TRUE,FALSE,nullptr)};
        struct Client { HANDLE pipe; std::atomic<bool> done{false}; std::thread worker; };
        std::list<std::unique_ptr<Client>> clients;
        std::thread listener;
        HANDLE Create(bool first)
        {
            return CreateNamedPipeW(path.c_str(),PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED |
                (first ? FILE_FLAG_FIRST_PIPE_INSTANCE : 0),
                PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                17,65536,65536,0,&security.attributes);
        }
        Impl(RuntimeProtocol& p, std::wstring name, std::function<void()> callback) : protocol(p), publish(std::move(callback))
        {
            if ((name!=L"BimeIPC" && !name.starts_with(L"TigerClaw.Core.Native.Test.")) || name.size() > 180 ||
                name.find_first_of(L"\\/\0",0,3) != std::wstring::npos)
                throw std::invalid_argument("Explicit TigerClaw.Core.Native.Test.* pipe required");
            if (!stop.value) throw std::runtime_error("Cannot create pipe stop event");
            path = L"\\\\.\\pipe\\" + name;
            HANDLE first = Create(true);
            if (first == INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot create isolated Core pipe");
            try { listener = std::thread([this,first] { Listen(first); }); }
            catch (...) { CloseHandle(first); throw; }
        }
        ~Impl()
        {
            SetEvent(stop.value);
            if (listener.joinable()) listener.join();
        }
        void Serve(Client& client) noexcept
        {
            Handle pipe(client.pipe);
            try
            {
                std::string pending; std::array<char,4096> buffer{};
                while (auto count = Transfer(pipe.value,stop.value,buffer.data(),static_cast<DWORD>(buffer.size()),false))
                {
                    pending.append(buffer.data(),count);
                    std::size_t begin = 0;
                    for (;;)
                    {
                        auto end = pending.find('\n',begin);
                        if (end == pending.npos) break;
                        if (end-begin > 262144) throw std::length_error("Core pipe frame too large");
                        auto line = std::string_view(pending).substr(begin,end-begin);
                        if (line.find_first_not_of(" \r\t") != line.npos)
                        {
                            auto response = protocol.Handle(line,std::chrono::system_clock::now());
                            if (response)
                            {
                                response->push_back('\n'); std::size_t offset = 0;
                                while (offset < response->size())
                                {
                                    auto written = Transfer(pipe.value,stop.value,response->data()+offset,
                                        static_cast<DWORD>(std::min<std::size_t>(65536,response->size()-offset)),true);
                                    if (!written) throw std::runtime_error("Core pipe disconnected");
                                    offset += written;
                                }
                            }
                            // Frontend response is sent before possibly slower UI work.
                            if (publish) publish();
                        }
                        begin = end+1;
                    }
                    pending.erase(0,begin);
                    if (pending.size() > 262144) break;
                }
            }
            catch (...) {} // one bad/disconnected client does not stop the listener
            DisconnectNamedPipe(pipe.value);
            client.done.store(true);
        }
        void Listen(HANDLE first) noexcept
        {
            HANDLE next = first;
            try
            {
                while (next != INVALID_HANDLE_VALUE)
                {
                    Handle pipe(next); next = INVALID_HANDLE_VALUE;
                    Handle event(CreateEventW(nullptr,TRUE,FALSE,nullptr));
                    if (!event.value || WaitForSingleObject(stop.value,0) == WAIT_OBJECT_0) break;
                    OVERLAPPED op{}; op.hEvent = event.value; DWORD size = 0;
                    BOOL connected = ConnectNamedPipe(pipe.value,&op);
                    if (!connected)
                    {
                        auto error = GetLastError();
                        connected = error == ERROR_PIPE_CONNECTED ||
                            (error == ERROR_IO_PENDING && Finish(pipe.value,stop.value,op,size,INFINITE));
                    }
                    if (!connected) break;
                    for (auto i = clients.begin(); i != clients.end();)
                    {
                        if ((*i)->done.load()) { (*i)->worker.join(); i = clients.erase(i); }
                        else ++i;
                    }
                    if (clients.size() < 16)
                    {
                        auto client = std::make_unique<Client>(); client->pipe = pipe.value;
                        clients.push_back(std::move(client)); auto& c = *clients.back();
                        c.worker = std::thread([this,&c] { Serve(c); });
                        pipe.value = INVALID_HANDLE_VALUE; // worker now owns it
                    }
                    else DisconnectNamedPipe(pipe.value);
                    next = Create(false);
                }
            }
            catch (...) { SetEvent(stop.value); }
            if (next != INVALID_HANDLE_VALUE) CloseHandle(next);
            SetEvent(stop.value);
            for (auto& client : clients) if (client->worker.joinable()) client->worker.join();
        }
    };
    RuntimePipeServer::RuntimePipeServer(RuntimeProtocol& protocol, std::wstring name, std::function<void()> publish)
        : _impl(std::make_unique<Impl>(protocol,std::move(name),std::move(publish))) {}
    RuntimePipeServer::~RuntimePipeServer() = default;
}
