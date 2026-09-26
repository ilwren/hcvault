/*
 port/FilesystemPath.cpp — FilesystemPath + Directory for Windows and POSIX.

 Only the operations used by the volume core are implemented: type queries,
 deletion, base-name, owner (POSIX; used by a device-only code path kept for
 parity) and directory enumeration (used by Keyfile directory expansion).
 Host-drive-of-partition is a device-only concept and throws NotImplemented.
*/

#include "Platform/FilesystemPath.h"
#include "Platform/Directory.h"
#include "Platform/SystemException.h"
#include "Platform/Finally.h"
#include "Platform/ForEach.h"
#include "Platform/User.h"

#include <sys/stat.h>
#include <sys/types.h>

#ifdef TC_WINDOWS
#	include "Platform/Windows/System.h"
#else
#	include <dirent.h>
#	include <unistd.h>
#endif

namespace VeraCrypt
{
	// ---------------------------------------------------------------------------
	// FilesystemPath
	// ---------------------------------------------------------------------------
	void FilesystemPath::Delete () const
	{
#ifdef TC_WINDOWS
		if (!DeleteFileW (Path.c_str()))
			throw SystemException (SRC_POS, (int64) GetLastError());
#else
		throw_sys_sub_if (remove (StringConverter::ToSingle (Path).c_str()) == -1, Path);
#endif
	}

	UserId FilesystemPath::GetOwner () const
	{
#ifdef TC_WINDOWS
		throw NotImplemented (SRC_POS);
#else
		struct stat statData;
		throw_sys_sub_if (stat (StringConverter::ToSingle (Path).c_str(), &statData) == -1, Path);
		return UserId (statData.st_uid);
#endif
	}

	FilesystemPathType::Enum FilesystemPath::GetType () const
	{
		if (Path.empty())
			return FilesystemPathType::Unknown;

#ifdef TC_WINDOWS
		DWORD attr = GetFileAttributesW (Path.c_str());
		if (attr == INVALID_FILE_ATTRIBUTES)
			return FilesystemPathType::Unknown;

		if (attr & FILE_ATTRIBUTE_DIRECTORY)
			return FilesystemPathType::Directory;

		return FilesystemPathType::File;
#else
		struct stat statData;
		throw_sys_sub_if (stat (StringConverter::ToSingle (Path).c_str(), &statData) == -1, Path);

		if (S_ISREG (statData.st_mode))
			return FilesystemPathType::File;
		if (S_ISDIR (statData.st_mode))
			return FilesystemPathType::Directory;
		if (S_ISLNK (statData.st_mode))
			return FilesystemPathType::SymbolickLink;
		if (S_ISBLK (statData.st_mode))
			return FilesystemPathType::BlockDevice;
		if (S_ISCHR (statData.st_mode))
			return FilesystemPathType::CharacterDevice;

		return FilesystemPathType::Unknown;
#endif
	}

	FilesystemPath FilesystemPath::ToBaseName () const
	{
		wstring path = Path;
		size_t pos = path.find_last_of (L"/\\");

		if (pos == wstring::npos)
			return Path;

		return Path.substr (pos + 1);
	}

	FilesystemPath FilesystemPath::ToHostDriveOfPartition () const
	{
		// Device-hosted volumes are out of scope for this portable build.
		throw NotImplemented (SRC_POS);
	}

	// ---------------------------------------------------------------------------
	// Directory
	// ---------------------------------------------------------------------------
	void Directory::Create (const DirectoryPath &path)
	{
#ifdef TC_WINDOWS
		if (!CreateDirectoryW (wstring (path).c_str(), nullptr))
		{
			DWORD err = GetLastError();
			if (err != ERROR_ALREADY_EXISTS)
				throw SystemException (SRC_POS, (int64) err);
		}
#else
		string p = StringConverter::ToSingle (path);
		throw_sys_sub_if (mkdir (p.c_str(), S_IRUSR | S_IWUSR | S_IXUSR) == -1 && errno != EEXIST, wstring (path));
#endif
	}

	DirectoryPath Directory::AppendSeparator (const DirectoryPath &path)
	{
		wstring p = path;
		if (p.empty() || (p[p.size() - 1] != L'/' && p[p.size() - 1] != L'\\'))
			p += L"/";
		return p;
	}

	FilePathList Directory::GetFilePaths (const DirectoryPath &path, bool regularFilesOnly)
	{
		FilePathList files;

#ifdef TC_WINDOWS

		WIN32_FIND_DATAW findData;
		HANDLE finder = FindFirstFileW (((wstring) AppendSeparator (path) + L"*").c_str(), &findData);
		if (finder == INVALID_HANDLE_VALUE)
			throw SystemException (SRC_POS, (int64) GetLastError());

		finally_do_arg (HANDLE, finder, { FindClose (finally_arg); });

		while (true)
		{
			wstring name (findData.cFileName);
			if (name != L"." && name != L"..")
			{
				if (!(regularFilesOnly && (findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)))
					files.push_back (make_shared <FilePath> ((wstring) AppendSeparator (path) + name));
			}

			if (!FindNextFileW (finder, &findData))
				break;
		}

#else

		DIR *dir = opendir (StringConverter::ToSingle (path).c_str());
		throw_sys_sub_if (!dir, wstring (path));
		finally_do_arg (DIR*, dir, { closedir (finally_arg); });

		struct dirent *entry;
		while ((entry = readdir (dir)) != nullptr)
		{
			wstring name = StringConverter::ToWide (string (entry->d_name));
			if (name == L"." || name == L"..")
				continue;

			FilePath filePath ((wstring) AppendSeparator (path) + name);
			if (!regularFilesOnly || filePath.IsFile())
				files.push_back (make_shared <FilePath> (filePath));
		}

#endif

		return files;
	}
}
