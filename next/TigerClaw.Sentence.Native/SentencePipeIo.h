#pragma once
#include <Windows.h>
#include <stdexcept>

namespace tigerclaw::sentence
{
    // Asynchronous handles keep cancellation probes nonblocking even when
    // llama workers query connection health concurrently. All pending I/O is
    // completed before the stack OVERLAPPED/event leaves scope.
    struct PipeOperation
    {
        OVERLAPPED State{};
        PipeOperation()
        {
            State.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!State.hEvent) throw std::runtime_error("cannot create sentence I/O event");
        }
        ~PipeOperation() { CloseHandle(State.hEvent); }
        PipeOperation(const PipeOperation&) = delete;
        PipeOperation& operator=(const PipeOperation&) = delete;
        bool Finish(HANDLE pipe, BOOL result, DWORD& transferred)
        {
            if (!result && GetLastError() != ERROR_IO_PENDING) return false;
            return GetOverlappedResult(pipe, &State, &transferred, TRUE) != FALSE;
        }
    };

    inline bool Connect(HANDLE pipe)
    {
        PipeOperation operation;
        const BOOL connected = ConnectNamedPipe(pipe, &operation.State);
        if (!connected && GetLastError() == ERROR_PIPE_CONNECTED) return true;
        DWORD transferred = 0;
        return operation.Finish(pipe, connected, transferred);
    }
    inline bool Read(HANDLE pipe, void* buffer, DWORD size, DWORD& transferred)
    {
        PipeOperation operation;
        return operation.Finish(pipe, ReadFile(pipe, buffer, size, nullptr, &operation.State), transferred);
    }
    inline bool Write(HANDLE pipe, const void* buffer, DWORD size, DWORD& transferred)
    {
        PipeOperation operation;
        return operation.Finish(pipe, WriteFile(pipe, buffer, size, nullptr, &operation.State), transferred);
    }
    inline bool __cdecl Disconnected(void* pipe)
    {
        DWORD available = 0;
        return PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr) == FALSE;
    }
}
