/*
 FatFsBridge — glues ChaN's FatFs (exFAT enabled) to VeraCrypt volumes.

 FatFs sees each mounted volume as a "physical drive" (pdrv 0..FF_VOLUMES-1).
 The diskio callbacks implemented in FatFsBridge.cpp translate its sector
 requests into Volume::ReadSectors/WriteSectors calls on the decrypted data
 area, so the exFAT structures live INSIDE the encrypted container and never
 touch the host filesystem.

 IMPORTANT — why this header does NOT include ff.h:
   ff.h (upstream, unmodified) defines these macros on Windows:
     #define isnan(v)  _isnan(v)
     #define isinf(v)  (!_finite(v))
   Any <cmath> parsed afterwards explodes (MSVC C2588 "illegal global
   finalizer" on its isinf/isnan declarations), and its _T/_TEXT definitions
   collide with <tchar.h>. ff.h is therefore confined to FatFsBridge.cpp,
   included AFTER all standard/platform headers, and every type it declares
   is opaque here. Do not include ff.h anywhere else.

 FatFs is (C) ChaN, redistributed under its BSD-style license
 (LICENSE-FatFs.txt). ff.c/ff.h/ffunicode.c/diskio.h are unmodified; all
 integration lives in FatFsBridge.cpp.
*/

#pragma once

#include <memory>
#include <string>

namespace VeraCrypt { class Volume; }

namespace VeraCryptNet
{
	// Opaque states (defined in FatFsBridge.cpp; own FatFs objects and the
	// drive slot). All are deleted by the corresponding bridge function.
	struct ExFatMountState;
	struct ExFatDirHandle;
	struct ExFatFileHandle;

	// All entry points throw VeraCrypt exceptions on failure; vcapi translates.
	void ExFatFormat (const std::shared_ptr<VeraCrypt::Volume> &volume);
	ExFatMountState* ExFatMount (std::shared_ptr<VeraCrypt::Volume> volume);
	void ExFatUnmount (ExFatMountState *mount);

	ExFatDirHandle* ExFatOpenDir (ExFatMountState *mount, const std::string &utf8Path);
	// returns false at end of directory; fills the out-parameters for one entry
	bool ExFatReadDir (ExFatDirHandle *dir, std::string &outName, bool &outIsDir,
		unsigned long long &outSize, unsigned short &outDate, unsigned short &outTime);
	void ExFatCloseDir (ExFatDirHandle *dir);

	// mode: 0 = read existing, 1 = create/truncate write, 2 = open/append write
	ExFatFileHandle* ExFatOpenFile (ExFatMountState *mount, const std::string &utf8Path, int mode);
	unsigned long long ExFatFileSize (ExFatFileHandle *file);
	size_t ExFatReadFile (ExFatFileHandle *file, unsigned char *buffer, size_t length);
	size_t ExFatWriteFile (ExFatFileHandle *file, const unsigned char *buffer, size_t length);
	void ExFatSeekFile (ExFatFileHandle *file, unsigned long long position);
	void ExFatCloseFile (ExFatFileHandle *file);   // flushes + destroys

	// Total capacity (all clusters), free space and cluster size, in bytes.
	void ExFatGetSpace (ExFatMountState *mount,
	                    unsigned long long &outTotalBytes,
	                    unsigned long long &outFreeBytes,
	                    unsigned long long &outClusterBytes);

	void ExFatMkdir (ExFatMountState *mount, const std::string &utf8Path);
	void ExFatDelete (ExFatMountState *mount, const std::string &utf8Path);
}
