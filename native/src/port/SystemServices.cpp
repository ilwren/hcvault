/*
 port/SystemServices.cpp — SystemException / SystemInfo / SystemLog.

 SystemException mirrors Platform/Unix/SystemException.cpp; on Windows the
 error code is a GetLastError() value and SystemText() formats it with
 FormatMessage. SystemInfo is only consulted by mount code (not compiled in
 this library) but is implemented for completeness. SystemLog writes to
 OutputDebugString on Windows and syslog on POSIX.
*/

#include "Platform/PlatformBase.h"
#include "Platform/SerializerFactory.h"
#include "Platform/StringConverter.h"
#include "Platform/SystemException.h"
#include "Platform/SystemInfo.h"
#include "Platform/SystemLog.h"

#include <errno.h>

#ifdef TC_WINDOWS
#	include "Platform/Windows/System.h"
#else
#	include <sys/utsname.h>
#	include <syslog.h>
#endif

namespace VeraCrypt
{
	// ---------------------------------------------------------------------------
	// SystemException
	// ---------------------------------------------------------------------------
#ifdef TC_WINDOWS

	SystemException::SystemException ()
		: ErrorCode (GetLastError())
	{
	}

	SystemException::SystemException (const string &message)
		: Exception (message), ErrorCode (GetLastError())
	{
	}

	SystemException::SystemException (const string &message, const string &subject)
		: Exception (message, StringConverter::ToWide (subject)), ErrorCode (GetLastError())
	{
	}

	SystemException::SystemException (const string &message, const wstring &subject)
		: Exception (message, subject), ErrorCode (GetLastError())
	{
	}

	bool SystemException::IsError () const
	{
		return ErrorCode != 0;
	}

	void SystemException::Serialize (shared_ptr <Stream> stream) const
	{
		Exception::Serialize (stream);
		Serializer sr (stream);
		sr.Serialize ("ErrorCode", ErrorCode);
	}

	void SystemException::Deserialize (shared_ptr <Stream> stream)
	{
		Exception::Deserialize (stream);
		Serializer sr (stream);
		sr.Deserialize ("ErrorCode", ErrorCode);
	}

	wstring SystemException::SystemText () const
	{
		wchar_t *msg = nullptr;
		DWORD len = FormatMessageW (FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
			nullptr, (DWORD) ErrorCode, 0, (wchar_t*) &msg, 0, nullptr);

		wstring result;
		if (len && msg)
			result = wstring (msg, len);
		if (msg)
			LocalFree (msg);

		if (result.empty())
		{
			wstringstream s;
			s << L"Error 0x" << hex << (uint64) ErrorCode;
			result = s.str();
		}

		while (!result.empty() && (result[result.size() - 1] == L'\r' || result[result.size() - 1] == L'\n' || result[result.size() - 1] == L' '))
			result.erase (result.size() - 1);

		return result;
	}

#define TC_EXCEPTION(TYPE) TC_SERIALIZER_FACTORY_ADD(TYPE)
#undef TC_EXCEPTION_NODECL
#define TC_EXCEPTION_NODECL(TYPE) TC_SERIALIZER_FACTORY_ADD(TYPE)

	TC_SERIALIZER_FACTORY_ADD_EXCEPTION_SET (SystemException);

#else // POSIX — identical to Platform/Unix/SystemException.cpp

	SystemException::SystemException ()
		: ErrorCode (errno)
	{
	}

	SystemException::SystemException (const string &message)
		: Exception (message), ErrorCode (errno)
	{
	}

	SystemException::SystemException (const string &message, const string &subject)
		: Exception (message, StringConverter::ToWide (subject)), ErrorCode (errno)
	{
	}

	SystemException::SystemException (const string &message, const wstring &subject)
		: Exception (message, subject), ErrorCode (errno)
	{
	}

	bool SystemException::IsError () const
	{
		return ErrorCode != 0;
	}

	void SystemException::Serialize (shared_ptr <Stream> stream) const
	{
		Exception::Serialize (stream);
		Serializer sr (stream);
		sr.Serialize ("ErrorCode", ErrorCode);
	}

	void SystemException::Deserialize (shared_ptr <Stream> stream)
	{
		Exception::Deserialize (stream);
		Serializer sr (stream);
		sr.Deserialize ("ErrorCode", ErrorCode);
	}

	wstring SystemException::SystemText () const
	{
		return StringConverter::ToWide (strerror ((int) ErrorCode));
	}

#define TC_EXCEPTION(TYPE) TC_SERIALIZER_FACTORY_ADD(TYPE)
#undef TC_EXCEPTION_NODECL
#define TC_EXCEPTION_NODECL(TYPE) TC_SERIALIZER_FACTORY_ADD(TYPE)

	TC_SERIALIZER_FACTORY_ADD_EXCEPTION_SET (SystemException);

#endif

	// ---------------------------------------------------------------------------
	// SystemInfo
	// ---------------------------------------------------------------------------
	wstring SystemInfo::GetPlatformName ()
	{
#ifdef TC_WINDOWS
		return L"Windows";
#else
		struct utsname unameData;
		if (uname (&unameData) != 0)
			return L"Unknown";
		return StringConverter::ToWide (unameData.sysname);
#endif
	}

	vector <int> SystemInfo::GetVersion ()
	{
#ifdef TC_WINDOWS
		// Only mount code consults the OS version; this library does not mount.
		return vector <int> { 10, 0 };
#else
		vector <int> version;
		struct utsname unameData;
		if (uname (&unameData) != 0)
			return version;

		string s (unameData.release);
		size_t p = s.find ('.');
		if (p == string::npos)
			return version;

		s = s.substr (0, p);
		version.push_back (StringConverter::ToUInt32 (s));
		s = string (unameData.release).substr (p + 1);
		p = s.find ('.');
		if (p == string::npos)
			return version;

		version.push_back (StringConverter::ToUInt32 (s.substr (0, p)));
		return version;
#endif
	}

	bool SystemInfo::IsVersionAtLeast (int versionNumber1, int versionNumber2, int versionNumber3)
	{
		vector <int> osVersionNumbers = GetVersion();

		if (osVersionNumbers.size() < 2)
			throw ParameterIncorrect (SRC_POS);

		if (osVersionNumbers.size() < 3)
			osVersionNumbers.push_back (0);

		return (osVersionNumbers[0] * 10000000 + osVersionNumbers[1] * 10000 + osVersionNumbers[2]) >=
			(versionNumber1 * 10000000 + versionNumber2 * 10000 + versionNumber3);
	}

	// ---------------------------------------------------------------------------
	// SystemLog
	// ---------------------------------------------------------------------------
	void SystemLog::WriteDebug (const string &debugMessage)
	{
#ifdef TC_WINDOWS
		OutputDebugStringA ((debugMessage + "\n").c_str());
#else
		openlog ("veracrypt", LOG_PID, LOG_USER);
		syslog (LOG_DEBUG, "%s", debugMessage.c_str());
		closelog();
#endif
	}

	void SystemLog::WriteError (const string &errorMessage)
	{
#ifdef TC_WINDOWS
		OutputDebugStringA ((errorMessage + "\n").c_str());
#else
		openlog ("veracrypt", LOG_PID, LOG_USER);
		syslog (LOG_ERR, "%s", errorMessage.c_str());
		closelog();
#endif
	}
}
