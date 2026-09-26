/*
 FatFsBridge implementation.

 Include order is load-bearing (see FatFsBridge.h): every standard and
 VeraCrypt Platform header is parsed FIRST; ff.h comes LAST because it
 defines isnan/isinf macros (fatal to a later <cmath> on MSVC) and _T/_TEXT
 (collides with <tchar.h>). Nothing is included after ff.h.
*/

// this project's declarations — std headers only, no ff.h (see header comment)
#include "FatFsBridge.h"

#include <cstring>
#include <ctime>
#include <cstdlib>
#include <memory>
#include <mutex>
#include <vector>

// VeraCrypt Platform headers next (they define `shared_ptr` as a macro on GCC;
// make the unqualified name available for MSVC, which has no such macro)
using std::shared_ptr;
#include "Platform/Buffer.h"
#include "Platform/Exception.h"
#include "Volume/Volume.h"

// <tchar.h> (pulled in by Common/Tcdefs.h on Windows) defines _T/_TEXT.
// Drop them so ff.h's definitions below do not trigger C4005 redefinitions.
#undef _T
#undef _TEXT

// FatFs, LAST: ff.h defines isnan/isinf macros that would break any <cmath>
// parsed afterwards, and re-defines _T/_TEXT. Nothing may include a system
// header beyond this point. diskio.h needs ff.h's types, so it follows here.
#include "ff.h"
#include "diskio.h"

extern "C" {

	/* ---- FatFs memory hooks (required because FF_USE_LFN == 2) ---- */
	void* ff_memalloc (UINT m) { return std::malloc (m); }
	void  ff_memfree (void* m) { std::free (m); }

	/* ---- FAT timestamp: UTC, matching the managed FAT implementation ---- */
	DWORD get_fattime (void)
	{
		time_t now = time (nullptr);
		struct tm t;
#ifdef _WIN32
		gmtime_s (&t, &now);
#else
		gmtime_r (&now, &t);
#endif
		return ((DWORD) (t.tm_year - 80) << 25)
			| ((DWORD) (t.tm_mon + 1) << 21)
			| ((DWORD) t.tm_mday << 16)
			| ((DWORD) t.tm_hour << 11)
			| ((DWORD) t.tm_min << 5)
			| ((DWORD) (t.tm_sec >> 1));
	}
}

namespace VeraCryptNet
{
	using namespace VeraCrypt;

	// ---------------------------------------------------------------------------
	// drive-slot registry: pdrv -> (weak) volume
	// ---------------------------------------------------------------------------

	namespace
	{
		struct DriveSlot
		{
			std::weak_ptr<Volume> Vol;
		};

		DriveSlot g_slots[FF_VOLUMES];
		std::mutex g_slotMutex;

		shared_ptr<Volume> Lookup (BYTE pdrv)
		{
			std::lock_guard<std::mutex> lock (g_slotMutex);
			return g_slots[pdrv].Vol.lock ();
		}

		int AcquireSlot (const shared_ptr<Volume> &vol)
		{
			std::lock_guard<std::mutex> lock (g_slotMutex);
			for (int i = 0; i < FF_VOLUMES; ++i)
			{
				if (g_slots[i].Vol.expired ())
				{
					g_slots[i].Vol = vol;
					return i;
				}
			}
			throw ParameterIncorrect (SRC_POS); // all drive slots busy
		}

		void ReleaseSlot (int slot)
		{
			std::lock_guard<std::mutex> lock (g_slotMutex);
			g_slots[slot].Vol.reset ();
		}
	}

	// ---------------------------------------------------------------------------
	// diskio — FatFs calls these with C linkage; C++ exceptions must never
	// leak into ff.c (diskio.h declares them inside its own extern "C" block)
	// ---------------------------------------------------------------------------

	extern "C" {

	DSTATUS disk_initialize (BYTE)
	{
		return 0;
	}

	DSTATUS disk_status (BYTE pdrv)
	{
		return Lookup (pdrv) ? 0 : (DSTATUS) (STA_NODISK | STA_NOINIT);
	}

	DRESULT disk_read (BYTE pdrv, BYTE* buff, LBA_t sector, UINT count)
	{
		shared_ptr<Volume> vol = Lookup (pdrv);
		if (!vol)
			return RES_NOTRDY;
		try
		{
			if (vol->GetSectorSize () != 512)
				return RES_PARERR;
			vol->ReadSectors (BufferPtr (buff, (size_t) count * 512), (uint64) sector * 512);
			return RES_OK;
		}
		catch (...) { return RES_ERROR; }
	}

	DRESULT disk_write (BYTE pdrv, const BYTE* buff, LBA_t sector, UINT count)
	{
		shared_ptr<Volume> vol = Lookup (pdrv);
		if (!vol)
			return RES_NOTRDY;
		try
		{
			if (vol->GetSectorSize () != 512)
				return RES_PARERR;
			vol->WriteSectors (ConstBufferPtr (buff, (size_t) count * 512), (uint64) sector * 512);
			return RES_OK;
		}
		catch (...) { return RES_ERROR; }
	}

	DRESULT disk_ioctl (BYTE pdrv, BYTE cmd, void* buff)
	{
		shared_ptr<Volume> vol = Lookup (pdrv);
		if (!vol)
			return RES_NOTRDY;

		switch (cmd)
		{
		case CTRL_SYNC:
			return RES_OK;    // writes are immediate (no host cache involved)
		case GET_SECTOR_SIZE:
			*(WORD*) buff = (WORD) vol->GetSectorSize ();
			return RES_OK;
		case GET_SECTOR_COUNT:
			*(LBA_t*) buff = (LBA_t) (vol->GetSize () / vol->GetSectorSize ());
			return RES_OK;
		case GET_BLOCK_SIZE:
			*(DWORD*) buff = 1; // erase-block hint: irrelevant for file-hosted volumes
			return RES_OK;
		default:
			return RES_PARERR;
		}
	}

	} // extern "C"

	// ---------------------------------------------------------------------------
	// helpers
	// ---------------------------------------------------------------------------

	namespace
	{
		const char* FrName (FRESULT fr)
		{
			switch (fr)
			{
			case FR_OK: return "succeeded";
			case FR_DISK_ERR: return "disk I/O error";
			case FR_INT_ERR: return "internal error (assertion)";
			case FR_NOT_READY: return "drive not ready";
			case FR_NO_FILE: return "file not found";
			case FR_NO_PATH: return "path not found";
			case FR_INVALID_NAME: return "invalid path name";
			case FR_DENIED: return "access denied (directory not empty or busy?)";
			case FR_EXIST: return "already exists";
			case FR_INVALID_OBJECT: return "invalid object";
			case FR_WRITE_PROTECTED: return "write protected";
			case FR_INVALID_DRIVE: return "invalid drive";
			case FR_NOT_ENABLED: return "not enabled";
			case FR_NO_FILESYSTEM: return "no valid exFAT/FAT filesystem";
			case FR_MKFS_ABORTED: return "format aborted (volume too small?)";
			case FR_TIMEOUT: return "timeout";
			case FR_LOCKED: return "locked";
			case FR_NOT_ENOUGH_CORE: return "out of memory";
			case FR_TOO_MANY_OPEN_FILES: return "too many open files";
			case FR_INVALID_PARAMETER: return "invalid parameter";
			default: return "unknown error";
			}
		}

		[[noreturn]] void ThrowFr (const char *op, FRESULT fr)
		{
			throw Exception (string ("exFAT: ") + op + " failed: " + FrName (fr)
				+ " (fr=" + to_string ((int) fr) + ")");
		}

		void CheckFr (const char *op, FRESULT fr)
		{
			if (fr != FR_OK)
				ThrowFr (op, fr);
		}

		// FatFs accepts '/' separators; normalize Windows-style paths.
		std::string NormalizePath (const std::string &utf8Path)
		{
			std::string p = utf8Path;
			for (char &c : p)
			{
				if (c == '\\')
					c = '/';
			}
			while (p.size () >= 2 && p[0] == '/' && p[1] == '/')
				p.erase (0, 1);
			return p;
		}
	}

	// ---------------------------------------------------------------------------
	// mount state (structs declared opaque in FatFsBridge.h)
	// ---------------------------------------------------------------------------

	struct ExFatMountState
	{
		shared_ptr<Volume> Vol;
		int Slot = -1;
		FATFS Fs {};
		std::mutex Mutex;

		~ExFatMountState ();
		const char* Path () const;
	};

	struct ExFatDirHandle
	{
		ExFatMountState* M;
		DIR D {};
	};

	struct ExFatFileHandle
	{
		ExFatMountState* M;
		FIL F {};
	};

	ExFatMountState::~ExFatMountState ()
	{
		if (Slot >= 0)
		{
			f_mount (nullptr, Path (), 0);
			ReleaseSlot (Slot);
		}
	}

	const char* ExFatMountState::Path () const
	{
		static char paths[FF_VOLUMES][3];
		char* p = paths[Slot];
		p[0] = (char) ('0' + Slot);
		p[1] = ':';
		p[2] = 0;
		return p;
	}

	static void RequireSectorSize (const shared_ptr<Volume> &vol)
	{
		if (vol->GetSectorSize () != 512)
			throw ParameterIncorrect (SRC_POS); // FatFs configured for 512-byte sectors
	}

	// ---------------------------------------------------------------------------
	// public bridge API
	// ---------------------------------------------------------------------------

	void ExFatFormat (const shared_ptr<Volume> &volume)
	{
		RequireSectorSize (volume);
		int slot = AcquireSlot (volume);
		try
		{
			char path[3] = { (char) ('0' + slot), ':', 0 };

			FATFS fs;
			CheckFr ("mount (format)", f_mount (&fs, path, 0)); // register work area

			MKFS_PARM parm;
			std::memset (&parm, 0, sizeof (parm));
			parm.fmt = FM_EXFAT | FM_SFD;  // super-floppy: filesystem at sector 0,
			                               // matching how VeraCrypt formats containers
			parm.n_fat = 1;

			std::vector<unsigned char> work (128 * 1024);
			CheckFr ("format exFAT", f_mkfs (path, &parm, work.data (), (UINT) work.size ()));

			f_mount (nullptr, path, 0);
			ReleaseSlot (slot);
		}
		catch (...)
		{
			ReleaseSlot (slot);
			throw;
		}
	}

	ExFatMountState* ExFatMount (shared_ptr<Volume> volume)
	{
		RequireSectorSize (volume);

		int slot = AcquireSlot (volume);
		auto *m = new ExFatMountState;
		m->Vol = std::move (volume);

		char path[3] = { (char) ('0' + slot), ':', 0 };
		FRESULT fr = f_mount (&m->Fs, path, 1); // 1 = mount now (validates the FS)
		if (fr != FR_OK)
		{
			ReleaseSlot (slot);
			delete m;
			ThrowFr ("mount exFAT volume (not an exFAT filesystem?)", fr);
		}
		m->Slot = slot;
		return m;
	}

	void ExFatUnmount (ExFatMountState *mount)
	{
		delete mount;
	}

	ExFatDirHandle* ExFatOpenDir (ExFatMountState *mount, const std::string &utf8Path)
	{
		auto *d = new ExFatDirHandle;
		d->M = mount;
		std::lock_guard<std::mutex> lock (mount->Mutex);
		FRESULT fr = f_opendir (&d->D, NormalizePath (utf8Path).c_str ());
		if (fr != FR_OK)
		{
			delete d;
			ThrowFr ("open directory", fr);
		}
		return d;
	}

	bool ExFatReadDir (ExFatDirHandle *dir, std::string &outName, bool &outIsDir,
		unsigned long long &outSize, unsigned short &outDate, unsigned short &outTime)
	{
		std::lock_guard<std::mutex> lock (dir->M->Mutex);
		FILINFO fno;
		if (f_readdir (&dir->D, &fno) != FR_OK || fno.fname[0] == 0)
			return false;
		outName = fno.fname;
		outIsDir = (fno.fattrib & AM_DIR) != 0;
		outSize = fno.fsize;
		outDate = fno.fdate;
		outTime = fno.ftime;
		return true;
	}

	void ExFatCloseDir (ExFatDirHandle *dir)
	{
		delete dir;
	}

	ExFatFileHandle* ExFatOpenFile (ExFatMountState *mount, const std::string &utf8Path, int mode)
	{
		BYTE flags = mode == 0
			? (BYTE) (FA_READ | FA_OPEN_EXISTING)
			: (BYTE) (FA_READ | FA_WRITE | (mode == 2 ? (BYTE) FA_OPEN_APPEND : (BYTE) FA_CREATE_ALWAYS));

		auto *f = new ExFatFileHandle;
		f->M = mount;
		std::lock_guard<std::mutex> lock (mount->Mutex);
		FRESULT fr = f_open (&f->F, NormalizePath (utf8Path).c_str (), flags);
		if (fr != FR_OK)
		{
			delete f;
			ThrowFr ("open file", fr);
		}
		return f;
	}

	unsigned long long ExFatFileSize (ExFatFileHandle *file)
	{
		return f_size (&file->F);
	}

	size_t ExFatReadFile (ExFatFileHandle *file, unsigned char *buffer, size_t length)
	{
		std::lock_guard<std::mutex> lock (file->M->Mutex);
		UINT done = 0;
		CheckFr ("read file", f_read (&file->F, buffer, (UINT) length, &done));
		return done;
	}

	size_t ExFatWriteFile (ExFatFileHandle *file, const unsigned char *buffer, size_t length)
	{
		std::lock_guard<std::mutex> lock (file->M->Mutex);
		UINT done = 0;
		CheckFr ("write file", f_write (&file->F, buffer, (UINT) length, &done));
		if (done != length)
			ThrowFr ("write file (short write — volume full?)", FR_DISK_ERR);
		return done;
	}

	void ExFatSeekFile (ExFatFileHandle *file, unsigned long long position)
	{
		std::lock_guard<std::mutex> lock (file->M->Mutex);
		CheckFr ("seek file", f_lseek (&file->F, (FSIZE_t) position));
	}

	void ExFatCloseFile (ExFatFileHandle *file)
	{
		std::lock_guard<std::mutex> lock (file->M->Mutex);
		FRESULT fr = f_close (&file->F);
		delete file;
		CheckFr ("close file", fr);
	}

	void ExFatMkdir (ExFatMountState *mount, const std::string &utf8Path)
	{
		std::lock_guard<std::mutex> lock (mount->Mutex);
		CheckFr ("create directory", f_mkdir (NormalizePath (utf8Path).c_str ()));
	}

	void ExFatDelete (ExFatMountState *mount, const std::string &utf8Path)
	{
		std::lock_guard<std::mutex> lock (mount->Mutex);
		CheckFr ("delete", f_unlink (NormalizePath (utf8Path).c_str ()));
	}

	void ExFatGetSpace (ExFatMountState *mount,
	                    unsigned long long &outTotalBytes,
	                    unsigned long long &outFreeBytes,
	                    unsigned long long &outClusterBytes)
	{
		std::lock_guard<std::mutex> lock (mount->Mutex);

		DWORD freeClusters = 0;
		FATFS *fs = nullptr;
		CheckFr ("get free space", f_getfree (mount->Path (), &freeClusters, &fs));
		if (fs != &mount->Fs)
			throw ParameterIncorrect (SRC_POS);  // wrong work area (cannot happen)

		unsigned long long clusterBytes =
			(unsigned long long) fs->csize * 512;            /* sectors/cluster * bytes/sector (FF_MAX_SS = FF_MIN_SS = 512) */
		unsigned long long totalClusters =
			(unsigned long long) fs->n_fatent - 2;             /* clusters 2..n_fatent-1 */

		outTotalBytes = totalClusters * clusterBytes;
		outFreeBytes = (unsigned long long) freeClusters * clusterBytes;
		outClusterBytes = clusterBytes;
	}
}
