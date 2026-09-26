/*
 vcapi.cpp — C API over the VeraCrypt volume core (see vcapi.h).

 Design notes:
   * UTF-8 in, wstring inside: VeraCrypt's core works on wstring paths; a
     locale-independent UTF-8 -> UTF-16/UTF-32 decoder is implemented here
     (the stock StringConverter::ToWide is locale-dependent on POSIX).
   * C++ exceptions never cross the boundary; they are translated to
     vc_status codes with a thread-local message.
   * Progress: VolumeCreator runs on a worker thread; the create entry point
     polls its progress info and forwards it to the callback.
*/

#include "vcapi.h"

// ff.h-free bridge declarations (ff.h is confined to FatFsBridge.cpp — it
// defines isnan/isinf macros that would break <cmath> on MSVC).
#include "../fatfs/FatFsBridge.h"

#include <cstring>
#include <cstdlib>
#include <mutex>
#include <string>
#include <vector>

#include "Platform/Platform.h"
#include "Platform/SystemException.h"
#include "Platform/SystemLog.h"
#include "Volume/Volume.h"
#include "Volume/EncryptionAlgorithm.h"
#include "Volume/EncryptionTest.h"
#include "Volume/EncryptionThreadPool.h"
#include "Volume/Hash.h"
#include "Volume/Keyfile.h"
#include "Volume/Pkcs5Kdf.h"
#include "Core/Core.h"
#include "Core/CoreBase.h"
#include "Core/RandomNumberGenerator.h"
#include "Core/VolumeCreator.h"

#include "../shim/MiniCore.h"

using namespace std;
using namespace VeraCrypt;

// forward declarations (defined below — Translate formats messages with them)
static string WideToUtf8 (const wstring &in);

// ---------------------------------------------------------------------------
// error handling
// ---------------------------------------------------------------------------

static thread_local string t_lastError;
static thread_local int32_t t_lastStatus = VC_ERR_GENERIC;

static void SetLastError (const string &msg, int32_t status = VC_ERR_GENERIC)
{
	t_lastError = msg;
	t_lastStatus = status;
}

// SystemException::what() is only "function:line" (SRC_POS); the actual OS
// error lives in the exception object. Append code + system text so failures
// are self-diagnosing on user machines.
static string FormatSystemError (const SystemException &e)
{
	string msg = e.what ();
	if (e.GetErrorCode () != 0)
		msg += " (system error " + to_string ((long long) e.GetErrorCode ())
			+ ": " + WideToUtf8 (e.SystemText ()) + ")";
	return msg;
}

static int32_t Translate (const exception &e)
{
	int32_t status = VC_ERR_GENERIC;

	if (dynamic_cast<const PasswordException *> (&e))
		status = VC_ERR_WRONG_PASSWORD;
	else if (dynamic_cast<const ParameterIncorrect *> (&e))
		status = VC_ERR_ARG;
	else if (dynamic_cast<const VolumeHostInUse *> (&e))
	{
		// File::Open maps ERROR_SHARING_VIOLATION on an exclusive (ShareNone)
		// request to this — typically a password change while the volume is
		// still open somewhere.
		SetLastError (string ("volume file is locked by another open handle (sharing violation); ")
			+ "close other open volumes and retry [" + e.what () + "]", status);
		return status;
	}

	SetLastError (dynamic_cast<const SystemException *> (&e)
		? FormatSystemError (static_cast<const SystemException &> (e))
		: string (e.what ()), status);
	return status;
}

static int32_t Translate (...)
{
	SetLastError ("unknown error");
	return VC_ERR_GENERIC;
}

// NOTE: no extern "C" block here — vcapi.h declares the API with C linkage and
// these definitions inherit it. The static helpers below keep C++ linkage.

const char *vc_last_error (void) { return t_lastError.c_str(); }

vc_status vc_last_status (void) { return (vc_status) t_lastStatus; }

int32_t vc_api_version (void) { return VC_API_VERSION; }

// ---------------------------------------------------------------------------
// UTF-8 -> wstring (locale independent, surrogate pairs handled)

static wstring Utf8ToWide (const char *s)
{
	wstring out;
	if (!s)
		return out;

	const unsigned char *p = (const unsigned char *) s;
	uint32_t cp;
	while (*p)
	{
		if (*p < 0x80) { cp = *p++; }
		else if ((*p & 0xE0) == 0xC0 && (p[1] & 0xC0) == 0x80) { cp = (*p++ & 0x1F) << 6; cp |= *p++ & 0x3F; }
		else if ((*p & 0xF0) == 0xE0 && (p[1] & 0xC0) == 0x80 && (p[2] & 0xC0) == 0x80) { cp = (*p++ & 0x0F) << 12; cp |= (*p++ & 0x3F) << 6; cp |= *p++ & 0x3F; }
		else if ((*p & 0xF8) == 0xF0 && (p[1] & 0xC0) == 0x80 && (p[2] & 0xC0) == 0x80 && (p[3] & 0xC0) == 0x80) { cp = (*p++ & 0x07) << 18; cp |= (*p++ & 0x3F) << 12; cp |= (*p++ & 0x3F) << 6; cp |= *p++ & 0x3F; }
		else { cp = 0xFFFD; ++p; } // invalid byte -> replacement char

		if (sizeof (wchar_t) == 2 && cp > 0xFFFF)
		{
			// UTF-16 surrogate pair
			cp -= 0x10000;
			out += (wchar_t) (0xD800 + (cp >> 10));
			out += (wchar_t) (0xDC00 + (cp & 0x3FF));
		}
		else
		{
			out += (wchar_t) cp;
		}
	}
	return out;
}

static string WideToUtf8 (const wstring &in)
{
	string out;
	for (size_t i = 0; i < in.size(); ++i)
	{
		uint32_t cp = (uint32_t) in[i];
		if (cp >= 0xD800 && cp <= 0xDBFF && i + 1 < in.size())
		{
			uint32_t lo = (uint32_t) in[i + 1];
			if (lo >= 0xDC00 && lo <= 0xDFFF)
			{
				cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
				++i;
			}
		}

		if (cp < 0x80) out += (char) cp;
		else if (cp < 0x800) { out += (char) (0xC0 | (cp >> 6)); out += (char) (0x80 | (cp & 0x3F)); }
		else if (cp < 0x10000) { out += (char) (0xE0 | (cp >> 12)); out += (char) (0x80 | ((cp >> 6) & 0x3F)); out += (char) (0x80 | (cp & 0x3F)); }
		else { out += (char) (0xF0 | (cp >> 18)); out += (char) (0x80 | ((cp >> 12) & 0x3F)); out += (char) (0x80 | ((cp >> 6) & 0x3F)); out += (char) (0x80 | (cp & 0x3F)); }
	}
	return out;
}

// ---------------------------------------------------------------------------
// lifecycle

static once_flag g_initOnce;
static bool g_initialized = false;

static void vc_runtime_shutdown (void)
{
	try
	{
		if (g_initialized)
		{
			RandomNumberGenerator::Stop();
			EncryptionThreadPool::Stop();
			g_initialized = false;
		}
	}
	catch (...) { }
}

int32_t vc_init (void)
{
	try
	{
		call_once (g_initOnce, [] {
			// Official cipher/hash/XTS test vectors — cheap, one-shot
			EncryptionTest::TestAll();

			// RNG pool (/dev/urandom + /dev/random on POSIX, CNG on Windows)
			RandomNumberGenerator::Start();

			// Parallelizes the multi-KDF header probing on wrong passwords.
			// (Exit-time safety: parked pool threads would block glibc's
			// pthread_cond_destroy of the static SyncEvents at process exit;
			// the atexit handler below stops them first.)
			EncryptionThreadPool::Start();

			g_initialized = true;
		});

		// Safety net: stop background threads even if the host never calls
		// vc_shutdown(). atexit handlers registered here run before the
		// static destructors of the pool's synchronization primitives.
		atexit (vc_runtime_shutdown);
		return VC_OK;
	}
	catch (const exception &e) { return Translate (e); }
	catch (...) { return Translate (); }
}

void vc_shutdown (void)
{
	vc_runtime_shutdown();
}

// ---------------------------------------------------------------------------
// algorithm enumeration

static vector<string> g_cipherNames;
static vector<string> g_kdfNames;
static once_flag g_enumOnce;

static void BuildAlgorithmLists ()
{
	foreach (shared_ptr<VeraCrypt::EncryptionAlgorithm> ea, VeraCrypt::EncryptionAlgorithm::GetAvailableAlgorithms())
		g_cipherNames.push_back (WideToUtf8 (ea->GetName()));

	foreach (shared_ptr<VeraCrypt::Pkcs5Kdf> kdf, VeraCrypt::Pkcs5Kdf::GetAvailableAlgorithms())
		g_kdfNames.push_back (WideToUtf8 (kdf->GetName()));
}

int32_t vc_get_cipher_count (void)
{
	call_once (g_enumOnce, BuildAlgorithmLists);
	return (int32_t) g_cipherNames.size();
}

const char *vc_get_cipher_name (int32_t index)
{
	call_once (g_enumOnce, BuildAlgorithmLists);
	if (index < 0 || (size_t) index >= g_cipherNames.size())
		return nullptr;
	return g_cipherNames[index].c_str();
}

int32_t vc_get_kdf_count (void)
{
	call_once (g_enumOnce, BuildAlgorithmLists);
	return (int32_t) g_kdfNames.size();
}

const char *vc_get_kdf_name (int32_t index)
{
	call_once (g_enumOnce, BuildAlgorithmLists);
	if (index < 0 || (size_t) index >= g_kdfNames.size())
		return nullptr;
	return g_kdfNames[index].c_str();
}

// ---------------------------------------------------------------------------
// helpers

static shared_ptr<VeraCrypt::EncryptionAlgorithm> FindCipher (const char *name)
{
	call_once (g_enumOnce, BuildAlgorithmLists);
	wstring wide = Utf8ToWide (name);
	foreach (shared_ptr<VeraCrypt::EncryptionAlgorithm> ea, VeraCrypt::EncryptionAlgorithm::GetAvailableAlgorithms())
		if (ea->GetName() == wide)
			return ea;
	throw ParameterIncorrect (SRC_POS);
}

static shared_ptr<VeraCrypt::Pkcs5Kdf> FindKdf (const char *name)
{
	call_once (g_enumOnce, BuildAlgorithmLists);
	shared_ptr<Pkcs5Kdf> kdf = Pkcs5Kdf::GetAlgorithm (Utf8ToWide (name));
	if (!kdf)
		throw ParameterIncorrect (SRC_POS);
	return kdf;
}

static shared_ptr<VolumePassword> MakePassword (const char *password, size_t len)
{
	return make_shared<VolumePassword> ((const uint8*) password, len);
}

static shared_ptr<KeyfileList> MakeKeyfiles (const char *const *paths, size_t count)
{
	if (!paths || count == 0)
		return shared_ptr<KeyfileList> ();

	shared_ptr<KeyfileList> list (new KeyfileList);
	for (size_t i = 0; i < count; ++i)
		list->push_back (make_shared<Keyfile> (FilesystemPath (Utf8ToWide (paths[i]))));
	return list;
}

// ---------------------------------------------------------------------------
// creation

// forward declaration (defined with the open/close section below)
static shared_ptr<Volume> OpenVolumeInternal (const char *path,
	const char *password, size_t password_len,
	const char *const *keyfile_paths, size_t keyfile_count,
	int32_t pim, bool read_only, bool use_backup_header);

int32_t vc_create_volume (const char *path, uint64_t size_bytes,
                          const char *password, size_t password_len,
                          const char *const *keyfile_paths, size_t keyfile_count,
                          const char *cipher, const char *kdf, const char *filesystem,
                          int32_t pim, int32_t quick, int32_t hidden,
                          vc_progress_cb progress, void *progress_user)
{
	try
	{
		if (!path || !password || !cipher || !kdf || !filesystem)
			return VC_ERR_ARG;

		if (password_len == 0)
		{
			SetLastError ("password must not be empty");
			return VC_ERR_ARG;
		}

		if (size_bytes < 200 * 1024)
		{
			SetLastError ("volume size must be at least 200 KiB");
			return VC_ERR_ARG;
		}

		VolumeCreationOptions options;
		options.Path = VolumePath (Utf8ToWide (path));
		options.Size = size_bytes;
		options.Type = hidden ? VolumeType::Hidden : VolumeType::Normal;
		options.SectorSize = TC_SECTOR_SIZE_FILE_HOSTED_VOLUME; // 512 for file-hosted
		options.Quick = quick != 0;
		options.FilesystemClusterSize = 0;  // 0 = automatic (uninitialized = corrupted FAT!)
		options.EMVSupportEnabled = false;
		options.Pim = pim;
		options.EA = FindCipher (cipher);
		options.VolumeHeaderKdf = FindKdf (kdf);
		options.Password = MakePassword (password, password_len);
		options.Keyfiles = MakeKeyfiles (keyfile_paths, keyfile_count);

		string fs = filesystem;
		bool exfat = false;
		if (fs == "FAT" || fs == "fat")
			options.Filesystem = VolumeCreationOptions::FilesystemType::FAT;
		else if (fs == "NONE" || fs == "none")
			options.Filesystem = VolumeCreationOptions::FilesystemType::None;
		else if (fs == "EXFAT" || fs == "exfat")
		{
			// created raw, then formatted below through the FatFs bridge
			options.Filesystem = VolumeCreationOptions::FilesystemType::None;
			exfat = true;
		}
		else
		{
			SetLastError ("filesystem must be \"FAT\", \"EXFAT\" or \"NONE\"");
			return VC_ERR_ARG;
		}

		VolumeCreator creator;
		creator.CreateVolume (make_shared<VolumeCreationOptions> (options));

		// creation runs on a worker thread upstream — poll and forward progress
		VolumeCreator::ProgressInfo info = creator.GetProgressInfo();
		while (info.CreationInProgress)
		{
			if (progress)
				progress (info.SizeDone, info.TotalSize, (int) info.Stage, progress_user);

			Thread::Sleep (20);
			info = creator.GetProgressInfo();
		}

		if (progress)
			progress (info.TotalSize, info.TotalSize, (int) info.Stage, progress_user);

		creator.CheckResult(); // rethrows worker exceptions

		if (exfat)
		{
			// re-open with the fresh credentials and format the data area
			shared_ptr<Volume> vol = OpenVolumeInternal (path, password, password_len,
				keyfile_paths, keyfile_count, pim, false, false);
			finally_do_arg (shared_ptr<Volume>, vol, { try { finally_arg->Close(); } catch (...) { } });
			VeraCryptNet::ExFatFormat (vol);
		}
		return VC_OK;
	}
	catch (const exception &e) { return Translate (e); }
	catch (...) { return Translate (); }
}

// ---------------------------------------------------------------------------
// open / close

struct vc_volume
{
	shared_ptr<Volume> VolumeHandle;
	string CipherName;
	string KdfName;
	bool Hidden;
};

// Shared open path (vc_open_volume + exFAT post-create formatting).
// Throws VeraCrypt exceptions; callers translate.
static shared_ptr<Volume> OpenVolumeInternal (const char *path,
	const char *password, size_t password_len,
	const char *const *keyfile_paths, size_t keyfile_count,
	int32_t pim, bool read_only, bool use_backup_header)
{
	shared_ptr<File> file (new File);
	file->Open (VolumePath (Utf8ToWide (path)),
		read_only ? File::OpenRead : File::OpenReadWrite,
		File::ShareReadWrite);

	shared_ptr<Volume> vol (new Volume);
	vol->Open (file,
		MakePassword (password, password_len),
		pim,
		shared_ptr<Pkcs5Kdf> (),           // auto-detect KDF
		MakeKeyfiles (keyfile_paths, keyfile_count),
		false,                              // EMV support disabled in this build
		read_only ? VolumeProtection::ReadOnly : VolumeProtection::None,
		shared_ptr<VolumePassword> (), 0, shared_ptr<Pkcs5Kdf> (), shared_ptr<KeyfileList> (),
		VolumeType::Unknown,                // auto-detect normal vs hidden
		use_backup_header,
		false);
	return vol;
}

vc_volume *vc_open_volume (const char *path,
                           const char *password, size_t password_len,
                           const char *const *keyfile_paths, size_t keyfile_count,
                           int32_t pim, int32_t read_only, int32_t use_backup_header)
{
	try
	{
		if (!path || !password)
		{
			SetLastError ("invalid argument");
			return nullptr;
		}

		shared_ptr<Volume> vol;
		try
		{
			vol = OpenVolumeInternal (path, password, password_len,
				keyfile_paths, keyfile_count, pim, read_only, use_backup_header != 0);
		}
		catch (SystemException &e)
		{
			SetLastError (string ("cannot open volume file: ") + FormatSystemError (e), VC_ERR_VOLUME_NOT_FOUND);
			return nullptr;
		}

		vc_volume *h = new vc_volume;
		h->VolumeHandle = vol;
		h->CipherName = WideToUtf8 (vol->GetEncryptionAlgorithm()->GetName());
		h->KdfName = WideToUtf8 (vol->GetPkcs5Kdf()->GetName());
		h->Hidden = (vol->GetType() == VolumeType::Hidden);
		return h;
	}
	catch (const exception &e) { Translate (e); return nullptr; }
	catch (...) { Translate (); return nullptr; }
}

int64_t vc_read_sectors (vc_volume *volume, uint8_t *buffer, uint64_t offset, size_t len)
{
	if (!volume || !buffer)
	{
		SetLastError ("invalid argument");
		return VC_ERR_ARG;
	}

	try
	{
		size_t sectorSize = volume->VolumeHandle->GetSectorSize();
		if (offset % sectorSize || len % sectorSize)
		{
			SetLastError ("offset and length must be sector-aligned");
			return VC_ERR_ARG;
		}

		volume->VolumeHandle->ReadSectors (BufferPtr (buffer, len), offset);
		return (int64_t) len;
	}
	catch (const exception &e) { return Translate (e); }
	catch (...) { return Translate (); }
}

int64_t vc_write_sectors (vc_volume *volume, const uint8_t *buffer, uint64_t offset, size_t len)
{
	if (!volume || !buffer)
	{
		SetLastError ("invalid argument");
		return VC_ERR_ARG;
	}

	try
	{
		size_t sectorSize = volume->VolumeHandle->GetSectorSize();
		if (offset % sectorSize || len % sectorSize)
		{
			SetLastError ("offset and length must be sector-aligned");
			return VC_ERR_ARG;
		}

		volume->VolumeHandle->WriteSectors (ConstBufferPtr (buffer, len), offset);
		return (int64_t) len;
	}
	catch (const exception &e) { return Translate (e); }
	catch (...) { return Translate (); }
}

uint64_t vc_get_data_size (const vc_volume *volume)
{
	return volume ? volume->VolumeHandle->GetSize() : 0;
}

uint32_t vc_get_sector_size (const vc_volume *volume)
{
	return volume ? (uint32_t) volume->VolumeHandle->GetSectorSize() : 0;
}

int32_t vc_get_pim (const vc_volume *volume)
{
	return volume ? volume->VolumeHandle->GetPim() : 0;
}

const char *vc_get_cipher_used (const vc_volume *volume) { return volume ? volume->CipherName.c_str() : nullptr; }
const char *vc_get_kdf_used (const vc_volume *volume)    { return volume ? volume->KdfName.c_str() : nullptr; }
int32_t vc_is_hidden (const vc_volume *volume)           { return volume && volume->Hidden ? 1 : 0; }

void vc_close_volume (vc_volume *volume)
{
	if (!volume)
		return;

	try { volume->VolumeHandle->Close(); } catch (...) { }
	delete volume;
}

// ---------------------------------------------------------------------------
// password change

int32_t vc_change_password (const char *path,
                            const char *old_password, size_t old_password_len,
                            const char *const *old_keyfile_paths, size_t old_keyfile_count,
                            int32_t old_pim,
                            const char *new_password, size_t new_password_len,
                            const char *const *new_keyfile_paths, size_t new_keyfile_count,
                            int32_t new_pim,
                            const char *new_kdf,
                            int32_t wipe_count)
{
	try
	{
		if (!path || !old_password || !new_password)
			return VC_ERR_ARG;

		shared_ptr<Pkcs5Kdf> newKdf;
		if (new_kdf && strcmp (new_kdf, "") != 0)
			newKdf = FindKdf (new_kdf);

		// upstream re-encrypts inside `for (i = 1; i <= wipeCount)`; 0 would be a no-op
		if (wipe_count < 1)
			wipe_count = 1;

		Core->ChangePassword (
			make_shared<VolumePath> (Utf8ToWide (path)),
			true,                                            // preserve timestamps
			MakePassword (old_password, old_password_len),
			old_pim,
			shared_ptr<Pkcs5Kdf> (),                         // auto-detect current KDF
			MakeKeyfiles (old_keyfile_paths, old_keyfile_count),
			MakePassword (new_password, new_password_len),
			new_pim,
			MakeKeyfiles (new_keyfile_paths, new_keyfile_count),
			false,                                           // EMV disabled
			newKdf,
			wipe_count);

		return VC_OK;
	}
	catch (const exception &e) { return Translate (e); }
	catch (...) { return Translate (); }
}

// ---------------------------------------------------------------------------
// exFAT (FatFs bridge)
// ---------------------------------------------------------------------------

// raw pointers: the bridge states are opaque here (defined only in
// FatFsBridge.cpp) and deleted through the corresponding bridge functions
struct vc_exfat      { VeraCryptNet::ExFatMountState* M; };
struct vc_exfat_dir  { VeraCryptNet::ExFatDirHandle* D; };
struct vc_exfat_file { VeraCryptNet::ExFatFileHandle* F; };

int32_t vc_exfat_format (vc_volume *volume)
{
	try
	{
		if (!volume) { SetLastError ("invalid argument"); return VC_ERR_ARG; }
		VeraCryptNet::ExFatFormat (volume->VolumeHandle);
		return VC_OK;
	}
	catch (const exception &e) { return Translate (e); }
	catch (...) { return Translate (); }
}

vc_exfat *vc_exfat_mount (vc_volume *volume)
{
	try
	{
		if (!volume) { SetLastError ("invalid argument"); return nullptr; }
		vc_exfat *h = new vc_exfat;
		h->M = VeraCryptNet::ExFatMount (volume->VolumeHandle);
		return h;
	}
	catch (const exception &e) { Translate (e); return nullptr; }
	catch (...) { Translate (); return nullptr; }
}

void vc_exfat_unmount (vc_exfat *fs)
{
	if (!fs)
		return;
	VeraCryptNet::ExFatUnmount (fs->M);
	delete fs;
}

int32_t vc_exfat_mkdir (vc_exfat *fs, const char *path)
{
	try
	{
		if (!fs || !path) { SetLastError ("invalid argument"); return VC_ERR_ARG; }
		VeraCryptNet::ExFatMkdir (fs->M, path);
		return VC_OK;
	}
	catch (const exception &e) { return Translate (e); }
	catch (...) { return Translate (); }
}

int32_t vc_exfat_delete (vc_exfat *fs, const char *path)
{
	try
	{
		if (!fs || !path) { SetLastError ("invalid argument"); return VC_ERR_ARG; }
		VeraCryptNet::ExFatDelete (fs->M, path);
		return VC_OK;
	}
	catch (const exception &e) { return Translate (e); }
	catch (...) { return Translate (); }
}

int32_t vc_exfat_get_space (vc_exfat *fs, vc_exfat_space *out)
{
	try
	{
		if (!fs || !out) { SetLastError ("invalid argument"); return VC_ERR_ARG; }
		unsigned long long total = 0, freec = 0, cluster = 0;
		VeraCryptNet::ExFatGetSpace (fs->M, total, freec, cluster);
		out->total_bytes = total;
		out->free_bytes = freec;
		out->cluster_bytes = cluster;
		return VC_OK;
	}
	catch (const exception &e) { return Translate (e); }
	catch (...) { return Translate (); }
}

vc_exfat_dir *vc_exfat_opendir (vc_exfat *fs, const char *path)
{
	try
	{
		if (!fs || !path) { SetLastError ("invalid argument"); return nullptr; }
		vc_exfat_dir *h = new vc_exfat_dir;
		h->D = VeraCryptNet::ExFatOpenDir (fs->M, path);
		return h;
	}
	catch (const exception &e) { Translate (e); return nullptr; }
	catch (...) { Translate (); return nullptr; }
}

int32_t vc_exfat_readdir (vc_exfat_dir *dir, vc_exfat_entry *out)
{
	try
	{
		if (!dir || !out) { SetLastError ("invalid argument"); return VC_ERR_ARG; }

		std::string name;
		bool isDir;
		unsigned long long size;
		unsigned short date, time;
		if (!VeraCryptNet::ExFatReadDir (dir->D, name, isDir, size, date, time))
			return 0; // end of directory

		std::memset (out, 0, sizeof (*out));
		std::strncpy (out->name, name.c_str (), sizeof (out->name) - 1);
		out->is_directory = isDir ? 1 : 0;
		out->size = (uint64_t) size;
		out->modified_date = date;
		out->modified_time = time;
		return 1;
	}
	catch (const exception &e) { return Translate (e); }
	catch (...) { return Translate (); }
}

void vc_exfat_closedir (vc_exfat_dir *dir)
{
	if (!dir)
		return;
	VeraCryptNet::ExFatCloseDir (dir->D);
	delete dir;
}

vc_exfat_file *vc_exfat_open (vc_exfat *fs, const char *path, int32_t mode)
{
	try
	{
		if (!fs || !path || mode < 0 || mode > 2) { SetLastError ("invalid argument"); return nullptr; }
		vc_exfat_file *h = new vc_exfat_file;
		h->F = VeraCryptNet::ExFatOpenFile (fs->M, path, mode);
		return h;
	}
	catch (const exception &e) { Translate (e); return nullptr; }
	catch (...) { Translate (); return nullptr; }
}

int64_t vc_exfat_read (vc_exfat_file *file, uint8_t *buffer, size_t len)
{
	try
	{
		if (!file || (!buffer && len)) { SetLastError ("invalid argument"); return VC_ERR_ARG; }
		return (int64_t) VeraCryptNet::ExFatReadFile (file->F, buffer, len);
	}
	catch (const exception &e) { return Translate (e); }
	catch (...) { return Translate (); }
}

int64_t vc_exfat_write (vc_exfat_file *file, const uint8_t *buffer, size_t len)
{
	try
	{
		if (!file || (!buffer && len)) { SetLastError ("invalid argument"); return VC_ERR_ARG; }
		return (int64_t) VeraCryptNet::ExFatWriteFile (file->F, buffer, len);
	}
	catch (const exception &e) { return Translate (e); }
	catch (...) { return Translate (); }
}

int32_t vc_exfat_seek (vc_exfat_file *file, uint64_t position)
{
	try
	{
		if (!file) { SetLastError ("invalid argument"); return VC_ERR_ARG; }
		VeraCryptNet::ExFatSeekFile (file->F, position);
		return VC_OK;
	}
	catch (const exception &e) { return Translate (e); }
	catch (...) { return Translate (); }
}

int32_t vc_exfat_close (vc_exfat_file *file)
{
	try
	{
		if (!file) { SetLastError ("invalid argument"); return VC_ERR_ARG; }
		VeraCryptNet::ExFatCloseFile (file->F);
		delete file;
		return VC_OK;
	}
	catch (const exception &e) { delete file; return Translate (e); }
	catch (...) { delete file; return Translate (); }
}
