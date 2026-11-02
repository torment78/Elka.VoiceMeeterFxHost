#pragma once
#include <windows.h>
constexpr int AVRT_PRIORITY_HIGH = 1;
HANDLE AvSetMmThreadCharacteristicsW(const wchar_t*, DWORD*);
int AvSetMmThreadPriority(HANDLE, int);
int AvRevertMmThreadCharacteristics(HANDLE);
