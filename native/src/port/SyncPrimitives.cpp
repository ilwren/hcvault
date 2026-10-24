/*
 port/SyncPrimitives.cpp — Mutex, SyncEvent, Thread, Time for Windows and POSIX.

 The class declarations in Platform/Mutex.h, SyncEvent.h, Thread.h already carry
 complete TC_WINDOWS branches (CRITICAL_SECTION, HANDLE events, Win32 threads)
 inherited from TrueCrypt's cross-platform design; this file implements both
 halves side by side. Semantics mirror Platform/Unix/*.cpp exactly.
*/

#include "Platform/PlatformBase.h"
#include "Platform/Exception.h"
#include "Platform/Mutex.h"
#include "Platform/SyncEvent.h"
#include "Platform/SystemException.h"
#include "Platform/SystemLog.h"
#include "Platform/Thread.h"
#include "Platform/Time.h"

#include <assert.h>

#ifdef TC_WINDOWS
#	include "Platform/Windows/System.h"
#else
#	include <pthread.h>
#	include <unistd.h>
#	include <sys/time.h>
#	include <time.h>
#endif

namespace VeraCrypt
{
	// ---------------------------------------------------------------------------
	// Mutex
	// ---------------------------------------------------------------------------
#ifdef TC_WINDOWS

	Mutex::Mutex ()
	{
		InitializeCriticalSection (&SystemMutex);
		Initialized = true;
	}

	Mutex::~Mutex ()
	{
		Initialized = false;
		DeleteCriticalSection (&SystemMutex);
	}

	void Mutex::Lock ()
	{
		assert (Initialized);
		EnterCriticalSection (&SystemMutex);
	}

	void Mutex::Unlock ()
	{
		LeaveCriticalSection (&SystemMutex);
	}

#else // POSIX

	Mutex::Mutex ()
	{
		pthread_mutexattr_t attributes;

		int status = pthread_mutexattr_init (&attributes);
		if (status != 0)
			throw SystemException (SRC_POS, status);

		status = pthread_mutexattr_settype (&attributes, PTHREAD_MUTEX_RECURSIVE);
		if (status != 0)
			throw SystemException (SRC_POS, status);

		status = pthread_mutex_init (&SystemMutex, &attributes);
		if (status != 0)
			throw SystemException (SRC_POS, status);

		Initialized = true;
	}

	Mutex::~Mutex ()
	{
		Initialized = false;
		pthread_mutex_destroy (&SystemMutex);
	}

	void Mutex::Lock ()
	{
		assert (Initialized);
		int status = pthread_mutex_lock (&SystemMutex);
		if (status != 0)
			throw SystemException (SRC_POS, status);
	}

	void Mutex::Unlock ()
	{
		int status = pthread_mutex_unlock (&SystemMutex);
		if (status != 0)
			throw SystemException (SRC_POS, status);
	}

#endif

	// ---------------------------------------------------------------------------
	// SyncEvent — auto-reset event (signal wakes exactly one waiter, flag clears)
	// ---------------------------------------------------------------------------
#ifdef TC_WINDOWS

	SyncEvent::SyncEvent ()
	{
		SystemSyncEvent = CreateEventW (nullptr, FALSE /* auto-reset */, FALSE, nullptr);
		if (!SystemSyncEvent)
			throw SystemException (SRC_POS, (int64) GetLastError());
		Initialized = true;
	}

	SyncEvent::~SyncEvent ()
	{
		CloseHandle (SystemSyncEvent);
		Initialized = false;
	}

	void SyncEvent::Reset ()
	{
		assert (Initialized);
		ResetEvent (SystemSyncEvent);
	}

	void SyncEvent::Signal ()
	{
		assert (Initialized);
		if (!SetEvent (SystemSyncEvent))
			throw SystemException (SRC_POS, (int64) GetLastError());
	}

	void SyncEvent::Wait ()
	{
		assert (Initialized);
		if (WaitForSingleObject (SystemSyncEvent, INFINITE) != WAIT_OBJECT_0)
			throw SystemException (SRC_POS, (int64) GetLastError());
	}

#else // POSIX

	SyncEvent::SyncEvent ()
	{
		int status = pthread_cond_init (&SystemSyncEvent, nullptr);
		if (status != 0)
			throw SystemException (SRC_POS, status);

		Signaled = false;
		Initialized = true;
	}

	SyncEvent::~SyncEvent ()
	{
		pthread_cond_destroy (&SystemSyncEvent);
		Initialized = false;
	}

	void SyncEvent::Reset ()
	{
		assert (Initialized);
		ScopeLock lock (EventMutex);
		Signaled = false;
	}

	void SyncEvent::Signal ()
	{
		assert (Initialized);
		ScopeLock lock (EventMutex);
		Signaled = true;
		int status = pthread_cond_signal (&SystemSyncEvent);
		if (status != 0)
			throw SystemException (SRC_POS, status);
	}

	void SyncEvent::Wait ()
	{
		assert (Initialized);
		ScopeLock lock (EventMutex);
		while (!Signaled)
		{
			int status = pthread_cond_wait (&SystemSyncEvent, EventMutex.GetSystemHandle());
			if (status != 0)
				throw SystemException (SRC_POS, status);
		}
		Signaled = false;
	}

#endif

	// ---------------------------------------------------------------------------
	// Thread — 512 KiB worker stacks (see Thread.h: pool workers per CPU add up)
	// ---------------------------------------------------------------------------
#ifdef TC_WINDOWS

	void Thread::Join () const
	{
		if (Reaped)
			return;

		if (WaitForSingleObject (SystemHandle, INFINITE) != WAIT_OBJECT_0)
			throw SystemException (SRC_POS, (int64) GetLastError());

		// Release the handle now that the thread is gone; upstream left it
		// open forever (one leaked kernel handle per worker).
		CloseHandle (SystemHandle);
		Reaped = true;
	}

	void Thread::Detach () const
	{
		if (Reaped)
			return;

		if (!CloseHandle (SystemHandle))
			throw SystemException (SRC_POS, (int64) GetLastError());
		Reaped = true;
	}

	void Thread::Start (ThreadProcPtr threadProc, void *parameter)
	{
		SystemHandle = CreateThread (nullptr, MinThreadStackSize, threadProc, parameter, 0, nullptr);
		if (!SystemHandle)
			throw SystemException (SRC_POS, (int64) GetLastError());
		Started = true;
		Reaped = false;
	}

	void Thread::Sleep (uint32 milliSeconds)
	{
		::Sleep (milliSeconds);
	}

#else // POSIX

	namespace
	{
		struct PthreadAttr
		{
			PthreadAttr ()
			{
				int status = pthread_attr_init (&Attr);
				if (status != 0)
					throw SystemException (SRC_POS, status);
			}

			~PthreadAttr ()
			{
				pthread_attr_destroy (&Attr);
			}

			pthread_attr_t Attr;
		};
	}

	void Thread::Join () const
	{
		if (Reaped)
			return;

		int status = pthread_join (SystemHandle, nullptr);
		if (status != 0)
			throw SystemException (SRC_POS, status);
		Reaped = true;
	}

	void Thread::Detach () const
	{
		if (Reaped)
			return;

		int status = pthread_detach (SystemHandle);
		if (status != 0)
			throw SystemException (SRC_POS, status);
		Reaped = true;
	}

	void Thread::Start (ThreadProcPtr threadProc, void *parameter)
	{
		PthreadAttr attr;
		int status;

		// Always set the worker stack size explicitly. The old code only
		// raised stacks smaller than MinThreadStackSize, which never fired
		// on glibc (default stack is 8 MiB) - so "8 MiB minimum" silently
		// meant "8 MiB stacks", and every worker reserved 8 MiB of VmData
		// that .NET reports as Process.PrivateMemorySize64. Worker threads
		// here only run iterative crypto/I/O, so MinThreadStackSize is the
		// size we want in both directions (see Thread.h).
		status = pthread_attr_setstacksize (&attr.Attr, MinThreadStackSize);
		if (status != 0)
			throw SystemException (SRC_POS, status);

		status = pthread_create (&SystemHandle, &attr.Attr, threadProc, parameter);
		if (status != 0)
			throw SystemException (SRC_POS, status);
		Started = true;
		Reaped = false;
	}

	void Thread::Sleep (uint32 milliSeconds)
	{
		::usleep (milliSeconds * 1000);
	}

#endif

	// ---------------------------------------------------------------------------
	// Time — hundreds of nanoseconds since 1601-01-01 (Windows file time)
	// ---------------------------------------------------------------------------
	uint64 Time::GetCurrent ()
	{
#ifdef TC_WINDOWS
		FILETIME ft;
		GetSystemTimeAsFileTime (&ft);
		return *(uint64*) &ft;
#else
		struct timeval tv;
		gettimeofday (&tv, NULL);
		return ((uint64) tv.tv_sec + 134774LL * 24 * 3600) * 1000LL * 1000 * 10;
#endif
	}
}
