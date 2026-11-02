#pragma once
using HANDLE = void*;
using DWORD = unsigned long;
constexpr int THREAD_PRIORITY_HIGHEST = 2;
constexpr int THREAD_PRIORITY_TIME_CRITICAL = 15;
constexpr int THREAD_PRIORITY_ERROR_RETURN = 0x7fffffff;
DWORD GetEnvironmentVariableW(const wchar_t*, wchar_t*, DWORD);
HANDLE GetCurrentThread();
int GetThreadPriority(HANDLE);
int SetThreadPriority(HANDLE, int);
