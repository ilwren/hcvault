/*
 port/File.cpp — portable implementation of VeraCrypt::File.

 Upstream compiles this class only against POSIX (Platform/Unix/File.cpp).
 This implementation covers both halves:

   * POSIX  : open/pread/pwrite/lseek (device paths reuse upstream behavior
             for regular files; block-device ioctls are not needed for
             file-hosted volumes and throw NotImplemented)
   * Windows: Win32 CreateFileW/ReadFile/WriteFile with OVERLAPPED positional
             I/O (thread-safe per call, mirroring pread/pwrite semantics)

 Volume hosting on raw devices is intentionally out of scope for this library;
 only file-hosted volumes are supported, which is all the create/open API needs.
*/

#include "Platform/File.h"
#include "Volume/VolumeException.h"	// VolumeHostInUse
#include "Platform/Time.h"
#include "Platform/SystemException.h"
#include "Platform/TextReader.h"

#ifdef TC_WINDOWS
#	include <windows.h>
#else
#	include <errno.h>
#	include <fcntl.h>
#	include <unistd.h>
#	include <utime.h>
#	include <sys/file.h>
#	include <sys/types.h>
#	include <sys/stat.h>
#endif

namespace VeraCrypt
{
#ifdef TC_WINDOWS

	static DWORD MapCreationDisposition (File::FileOpenMode mode)
	{
		switch (mode)
		{
		case File::CreateReadWrite:
		case File::CreateWrite:
			return CREATE_ALWAYS;
		case File::OpenRead:
		case File::OpenWrite:
		case File::OpenReadWrite:
			return OPEN_EXISTING;
		default:
			throw ParameterIncorrect (SRC_POS);
		}
	}

	static DWORD MapDesiredAccess (File::FileOpenMode mode)
	{
		switch (mode)
		{
		case File::CreateWrite:
		case File::OpenWrite:
			return GENERIC_WRITE;
		case File::OpenRead:
			return GENERIC_READ;
		case File::CreateReadWrite:
		case File::OpenReadWrite:
			return GENERIC_READ | GENERIC_WRITE;
		default:
			throw ParameterIncorrect (SRC_POS);
		}
	}

	static DWORD MapShareMode (File::FileShareMode mode)
	{
		switch (mode)
		{
		case File::ShareNone:
			return 0;
		case File::ShareRead:
			return FILE_SHARE_READ;
		case File::ShareReadWrite:
		case File::ShareReadWriteIgnoreLock:
			return FILE_SHARE_READ | FILE_SHARE_WRITE;
		default:
			throw ParameterIncorrect (SRC_POS);
		}
	}

	void File::Close ()
	{
		if_debug (ValidateState());

		if (!SharedHandle)
		{
			if ((mFileOpenFlags & File::PreserveTimestamps) && Path.IsFile())
			{
				try
				{
					// unlike POSIX utime() (which works by path), SetFileTime
					// needs the handle — restore BEFORE closing it.
					throw_sys_sub_if (!SetFileTime ((HANDLE) FileHandle, nullptr, (const FILETIME*) &AccTime, (const FILETIME*) &ModTime), wstring (Path));
				}
				catch (...) // suppress errors to allow read-only media
				{
				}
			}

			::CloseHandle ((HANDLE) FileHandle);
			FileIsOpen = false;
		}
	}

	void File::Delete ()
	{
		Close();
		Path.Delete();
	}

	void File::Flush () const
	{
		if_debug (ValidateState());
		throw_sys_sub_if (!FlushFileBuffers ((HANDLE) FileHandle), wstring (Path));
	}

	uint32 File::GetDeviceSectorSize () const
	{
		// Device-hosted volumes are out of scope for this portable build.
		throw NotImplemented (SRC_POS);
	}

	uint64 File::GetPartitionDeviceStartOffset () const
	{
		throw NotImplemented (SRC_POS);
	}

	uint64 File::Length () const
	{
		if_debug (ValidateState());

		LARGE_INTEGER size;
		throw_sys_sub_if (!GetFileSizeEx ((HANDLE) FileHandle, &size), wstring (Path));
		return (uint64) size.QuadPart;
	}

	void File::Open (const FilePath &path, FileOpenMode mode, FileShareMode shareMode, FileOpenFlags flags)
	{
		DWORD sysFlags = (flags & File::DisableWriteCaching) ? FILE_FLAG_WRITE_THROUGH : 0;

		HANDLE h = CreateFileW (wstring (path).c_str(), MapDesiredAccess (mode), MapShareMode (shareMode),
			nullptr, MapCreationDisposition (mode), sysFlags, nullptr);

		if (h == INVALID_HANDLE_VALUE)
		{
			DWORD err = GetLastError();
			if (err == ERROR_SHARING_VIOLATION && shareMode == File::ShareNone)
				throw VolumeHostInUse (SRC_POS);
			throw SystemException (SRC_POS, (int64) err);
		}

		if ((flags & File::PreserveTimestamps) && path.IsFile())
		{
			FILETIME acc, mod;
			if (GetFileTime (h, nullptr, &acc, &mod))
			{
				AccTime = *(uint64*) &acc;
				ModTime = *(uint64*) &mod;
			}
		}

		FileHandle = (SystemFileHandleType) h;
		Path = path;
		mFileOpenFlags = flags;
		FileIsOpen = true;
	}

	static uint64 FileIo (HANDLE h, bool write, const BufferPtr &buffer, uint64 position)
	{
		OVERLAPPED ov;
		ZeroMemory (&ov, sizeof (ov));
		ov.Offset = (DWORD) (position & 0xFFFFFFFFUL);
		ov.OffsetHigh = (DWORD) (position >> 32);

		DWORD done = 0;
		BOOL ok = write
			? WriteFile (h, buffer.Get(), (DWORD) buffer.Size(), &done, &ov)
			: ReadFile (h, buffer.Get(), (DWORD) buffer.Size(), &done, &ov);

		if (!ok)
		{
			DWORD err = GetLastError();

			// pread()/pwrite() semantics for callers: a read that BEGINS at or
			// beyond EOF yields "0 bytes", not an error. Win32 ReadFile fails
			// such reads with ERROR_HANDLE_EOF whenever lpOverlapped is used
			// (even on synchronous handles), so translate it back — upstream
			// loops like Keyfile.cpp's `while (file.Read (buf) > 0)` rely on it.
			// (Reads that merely STRADDLE EOF succeed with a partial count.)
			if (!write && err == ERROR_HANDLE_EOF)
				return 0;

			throw SystemException (SRC_POS, (int64) err);
		}

		return done;
	}

	uint64 File::Read (const BufferPtr &buffer) const
	{
		if_debug (ValidateState());
		uint64 done = FileIo ((HANDLE) FileHandle, false, buffer, SeekPosition);
		SeekPosition += done;
		return done;
	}

	uint64 File::ReadAt (const BufferPtr &buffer, uint64 position) const
	{
		if_debug (ValidateState());
		return FileIo ((HANDLE) FileHandle, false, buffer, position);
	}

	void File::SeekAt (uint64 position) const
	{
		if_debug (ValidateState());
		SeekPosition = position;
	}

	void File::SeekEnd (int offset) const
	{
		if_debug (ValidateState());
		SeekPosition = Length() + offset;
	}

	void File::SetLength (uint64 length) const
	{
		if_debug (ValidateState());

		LARGE_INTEGER pos;
		pos.QuadPart = (LONGLONG) length;
		throw_sys_sub_if (!SetFilePointerEx ((HANDLE) FileHandle, pos, nullptr, FILE_BEGIN), wstring (Path));
		throw_sys_sub_if (!SetEndOfFile ((HANDLE) FileHandle), wstring (Path));
	}

	void File::Write (const ConstBufferPtr &buffer) const
	{
		if_debug (ValidateState());

		OVERLAPPED ov;
		ZeroMemory (&ov, sizeof (ov));
		ov.Offset = (DWORD) (SeekPosition & 0xFFFFFFFFUL);
		ov.OffsetHigh = (DWORD) (SeekPosition >> 32);

		DWORD done = 0;
		throw_sys_sub_if (!WriteFile ((HANDLE) FileHandle, buffer.Get(), (DWORD) buffer.Size(), &done, &ov) || done != buffer.Size(), wstring (Path));
		SeekPosition += buffer.Size();
	}

	void File::WriteAt (const ConstBufferPtr &buffer, uint64 position) const
	{
		if_debug (ValidateState());

		OVERLAPPED ov;
		ZeroMemory (&ov, sizeof (ov));
		ov.Offset = (DWORD) (position & 0xFFFFFFFFUL);
		ov.OffsetHigh = (DWORD) (position >> 32);

		DWORD done = 0;
		throw_sys_sub_if (!WriteFile ((HANDLE) FileHandle, buffer.Get(), (DWORD) buffer.Size(), &done, &ov) || done != buffer.Size(), wstring (Path));
	}

#else // POSIX

	void File::Close ()
	{
		if_debug (ValidateState());

		if (!SharedHandle)
		{
			close (FileHandle);
			FileIsOpen = false;

			if ((mFileOpenFlags & File::PreserveTimestamps) && Path.IsFile())
			{
				struct utimbuf u;
				u.actime = AccTime;
				u.modtime = ModTime;

				try
				{
					throw_sys_sub_if (utime (string (Path).c_str(), &u) == -1, wstring (Path));
				}
				catch (...) // suppress errors to allow read-only media
				{
				}
			}
		}
	}

	void File::Delete ()
	{
		Close();
		Path.Delete();
	}

	void File::Flush () const
	{
		if_debug (ValidateState());
		throw_sys_sub_if (fsync (FileHandle) != 0, wstring (Path));
	}

	uint32 File::GetDeviceSectorSize () const
	{
		// Device-hosted volumes are out of scope for this portable build.
		throw NotImplemented (SRC_POS);
	}

	uint64 File::GetPartitionDeviceStartOffset () const
	{
		throw NotImplemented (SRC_POS);
	}

	uint64 File::Length () const
	{
		if_debug (ValidateState());

		off_t current = lseek (FileHandle, 0, SEEK_CUR);
		throw_sys_sub_if (current == -1, wstring (Path));
		SeekEnd (0);
		uint64 length = lseek (FileHandle, 0, SEEK_CUR);
		SeekAt (current);
		return length;
	}

	void File::Open (const FilePath &path, FileOpenMode mode, FileShareMode shareMode, FileOpenFlags flags)
	{
		int sysFlags = 0;

		switch (mode)
		{
		case CreateReadWrite:
			sysFlags |= O_CREAT | O_TRUNC | O_RDWR;
			break;
		case CreateWrite:
			sysFlags |= O_CREAT | O_TRUNC | O_WRONLY;
			break;
		case OpenRead:
			sysFlags |= O_RDONLY;
			break;
		case OpenWrite:
			sysFlags |= O_WRONLY;
			break;
		case OpenReadWrite:
			sysFlags |= O_RDWR;
			break;
		default:
			throw ParameterIncorrect (SRC_POS);
		}

		if ((flags & File::PreserveTimestamps) && path.IsFile())
		{
			struct stat statData;
			throw_sys_sub_if (stat (string (path).c_str(), &statData) == -1, wstring (path));
			AccTime = statData.st_atime;
			ModTime = statData.st_mtime;
		}

		FileHandle = open (string (path).c_str(), sysFlags, S_IRUSR | S_IWUSR);
		throw_sys_sub_if (FileHandle == -1, wstring (path));

		Path = path;
		mFileOpenFlags = flags;
		FileIsOpen = true;
	}

	uint64 File::Read (const BufferPtr &buffer) const
	{
		if_debug (ValidateState());
		ssize_t bytesRead = read (FileHandle, buffer, buffer.Size());
		throw_sys_sub_if (bytesRead == -1, wstring (Path));
		return bytesRead;
	}

	uint64 File::ReadAt (const BufferPtr &buffer, uint64 position) const
	{
		if_debug (ValidateState());
		ssize_t bytesRead = pread (FileHandle, buffer, buffer.Size(), position);
		throw_sys_sub_if (bytesRead == -1, wstring (Path));
		return bytesRead;
	}

	void File::SeekAt (uint64 position) const
	{
		if_debug (ValidateState());
		throw_sys_sub_if (lseek (FileHandle, position, SEEK_SET) == -1, wstring (Path));
	}

	void File::SeekEnd (int offset) const
	{
		if_debug (ValidateState());
		throw_sys_sub_if (lseek (FileHandle, offset, SEEK_END) == -1, wstring (Path));
	}

	void File::SetLength (uint64 length) const
	{
		if_debug (ValidateState());
		throw_sys_sub_if (ftruncate (FileHandle, length) != 0, wstring (Path));
	}

	void File::Write (const ConstBufferPtr &buffer) const
	{
		if_debug (ValidateState());
		throw_sys_sub_if (write (FileHandle, buffer, buffer.Size()) != (ssize_t) buffer.Size(), wstring (Path));
	}

	void File::WriteAt (const ConstBufferPtr &buffer, uint64 position) const
	{
		if_debug (ValidateState());
		throw_sys_sub_if (pwrite (FileHandle, buffer, buffer.Size(), position) != (ssize_t) buffer.Size(), wstring (Path));
	}

#endif // TC_WINDOWS
}
