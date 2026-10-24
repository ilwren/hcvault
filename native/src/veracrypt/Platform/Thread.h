/*
 Derived from source code of TrueCrypt 7.1a, which is
 Copyright (c) 2008-2012 TrueCrypt Developers Association and which is governed
 by the TrueCrypt License 3.0.

 Modifications and additions to the original source code (contained in this file)
 and all other portions of this file are Copyright (c) 2013-2026 AM Crypto
 and are governed by the Apache License 2.0 the full text of which is
 contained in the file License.txt included in VeraCrypt binary and source
 code distribution packages.
*/

#ifndef TC_HEADER_Platform_Thread
#define TC_HEADER_Platform_Thread

#ifdef TC_WINDOWS
#	include "System.h"
#	define TC_THREAD_PROC DWORD WINAPI
#else
#	include <pthread.h>
#	define TC_THREAD_PROC void*
#endif
#include "PlatformBase.h"
#include "Functor.h"
#include "SharedPtr.h"
#include "SyncEvent.h"

namespace VeraCrypt
{
	class Thread
	{
	public:
#ifdef TC_WINDOWS
		typedef HANDLE ThreadSystemHandle;
		typedef LPTHREAD_START_ROUTINE ThreadProcPtr;
#else
		typedef pthread_t ThreadSystemHandle;
		typedef void* (*ThreadProcPtr) (void *);
#endif
		Thread () : SystemHandle (), Started (false), Reaped (false) { };
		virtual ~Thread ()
		{
			// Safety net: an abandoned joinable thread keeps its stack
			// reserved until it is joined or detached (POSIX), which is an
			// 512 KiB VmData leak per thread. Detach releases it.
			try
			{
				if (Started && !Reaped)
					Detach();
			}
			catch (...) { }
		};

		void Join () const;
		void Detach () const;
		void Start (ThreadProcPtr threadProc, void *parameter = nullptr);

		void Start (Functor *functor)
		{
			Start (Thread::FunctorEntry, (void *)functor);
		}

		static void Sleep (uint32 milliSeconds);

	protected:
		static TC_THREAD_PROC FunctorEntry (void *functorArg)
		{
			Functor *functor = (Functor *) functorArg;
			try
			{
				(*functor) ();
			}
			catch (...) { }

			delete functor;
			return 0;
		}

		// Worker threads run iterative crypto and I/O only - no deep call
		// chains - so a small stack is enough. This MUST stay small: the
		// encryption pool starts one worker per logical CPU (up to
		// MaxThreadCount), and on Linux/Android every thread stack counts
		// against VmData - which is exactly what .NET reports as
		// Process.PrivateMemorySize64. Upstream's 8 MiB (sized for the GUI
		// process) made the first pool start look like a ~256 MiB memory
		// spike on a 32-CPU machine. 512 KiB keeps a full pool at 4 MiB.
		static const size_t MinThreadStackSize = 512 * 1024;

		ThreadSystemHandle SystemHandle;
		bool Started;
		mutable bool Reaped;

	private:
		Thread (const Thread &);
		Thread &operator= (const Thread &);
	};

}

#endif // TC_HEADER_Platform_Thread
