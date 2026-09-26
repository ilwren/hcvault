/*
 Windows/System.h — platform glue for building the VeraCrypt C++ core on Windows.

 The upstream VeraCrypt sources never compiled the C++ Platform layer on Windows
 (the official Windows GUI uses the older C codebase), but the headers carry
 complete TC_WINDOWS branches (CRITICAL_SECTION / HANDLE / LPTHREAD_START_ROUTINE)
 inherited from TrueCrypt's original cross-platform design.

 This file supplies the single missing piece those branches expect:
 Platform/System.h includes "Windows/System.h" when TC_WINDOWS is defined.
*/

#ifndef TC_HEADER_Platform_Windows_System
#define TC_HEADER_Platform_Windows_System

#ifndef WIN32_LEAN_AND_MEAN
#	define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#	define NOMINMAX
#endif
#include <windows.h>

#endif // TC_HEADER_Platform_Windows_System
