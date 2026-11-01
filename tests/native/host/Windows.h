#pragma once

// Single-threaded native fixture shim; never used by the Windows add-on build.
#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstring>

#define __declspec(value)
#define WINAPI
using DWORD = uint32_t;
using LONG = int32_t;
using ULONGLONG = uint64_t;
using BOOL = int;
using HANDLE = void *;
using HMODULE = void *;
struct SRWLOCK {};
struct CONDITION_VARIABLE {};
#define SRWLOCK_INIT {}
#define CONDITION_VARIABLE_INIT {}
constexpr BOOL TRUE = 1;
constexpr DWORD ERROR_NO_DATA = 232, ERROR_PIPE_CONNECTED = 535, ERROR_PIPE_LISTENING = 536;
constexpr DWORD PIPE_ACCESS_DUPLEX = 3, PIPE_TYPE_BYTE = 0, PIPE_READMODE_BYTE = 0, PIPE_NOWAIT = 1;
constexpr DWORD WAIT_OBJECT_0 = 0, INFINITE = 0xFFFFFFFF;
inline const HANDLE INVALID_HANDLE_VALUE = reinterpret_cast<HANDLE>(intptr_t(-1));
inline void AcquireSRWLockExclusive(SRWLOCK *) {}
inline void ReleaseSRWLockExclusive(SRWLOCK *) {}
inline void WakeAllConditionVariable(CONDITION_VARIABLE *) {}
inline BOOL SleepConditionVariableSRW(CONDITION_VARIABLE *, SRWLOCK *, DWORD, DWORD) { return 0; }
inline LONG InterlockedCompareExchange(volatile LONG *value, LONG replacement, LONG expected)
{ const auto old = *value; if (old == expected) *value = replacement; return old; }
inline LONG InterlockedExchange(volatile LONG *value, LONG replacement)
{ const auto old = *value; *value = replacement; return old; }
inline LONG InterlockedIncrement(LONG *value) { return ++*value; }
inline ULONGLONG GetTickCount64()
{ return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count(); }
inline DWORD GetLastError() { return ERROR_NO_DATA; }
inline void Sleep(DWORD) {}
inline BOOL ReadFile(HANDLE, void *, DWORD, DWORD *, void *) { return 0; }
inline BOOL WriteFile(HANDLE, const void *, DWORD count, DWORD *written, void *)
{ *written = count; return 1; }
inline HANDLE CreateNamedPipeW(const wchar_t *, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, void *)
{ return INVALID_HANDLE_VALUE; }
inline BOOL ConnectNamedPipe(HANDLE, void *) { return 0; }
inline BOOL SetNamedPipeHandleState(HANDLE, DWORD *, void *, void *) { return 1; }
inline BOOL DisconnectNamedPipe(HANDLE) { return 1; }
inline BOOL CloseHandle(HANDLE) { return 1; }
inline HANDLE CreateThread(void *, size_t, DWORD (*)(void *), void *, DWORD, DWORD *) { return nullptr; }
inline DWORD WaitForSingleObject(HANDLE, DWORD) { return WAIT_OBJECT_0; }
template <size_t N> void strncpy_s(char (&dest)[N], const char *source, size_t count)
{ const auto length = std::min({count, N - 1, std::strlen(source)}); std::memcpy(dest, source, length); dest[length] = '\0'; }
template <size_t N> void strcpy_s(char (&dest)[N], const char *source)
{ strncpy_s(dest, source, N - 1); }
