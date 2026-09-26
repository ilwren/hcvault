/*
 vcapi.h — VeraCrypt volume core exposed as a plain C API (version 4).

 This is the boundary consumed by the C# wrapper (P/Invoke) and any other
 .NET/JNI/native consumer. All functions are thread-safe at the library level
 unless noted; a single vc_volume handle must not be used from two threads
 simultaneously.

 Capabilities:
   * create volumes (normal or hidden, FAT or raw, all VeraCrypt cipher/KDF
     combinations, keyfiles, PIM, quick mode, progress callback)
   * open volumes (password / keyfiles / PIM / read-only / backup header)
   * sector-level encrypted read/write (no mounting, no admin rights)
   * change password / KDF / keyfiles of an existing volume

 Platform support: Windows (x86, x64, ARM64) and Linux (x86, x64, armhf, arm64),
 file-hosted volumes only. All strings are UTF-8.
*/

#ifndef VC_API_H
#define VC_API_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) && defined(VC_CORE_EXPORTS)
#	define VC_API __declspec(dllexport)
#elif defined(__GNUC__) || defined(__clang__)
#	define VC_API __attribute__((visibility("default")))
#else
#	define VC_API
#endif

#define VC_API_VERSION 4

/* opaque volume handle */
typedef struct vc_volume vc_volume;

typedef enum vc_status {
	VC_OK = 0,
	VC_ERR_GENERIC = 1,        /* see vc_last_error() for details */
	VC_ERR_WRONG_PASSWORD = 2, /* password/keyfiles/PIM combination rejected */
	VC_ERR_ARG = 3,            /* invalid argument */
	VC_ERR_VOLUME_NOT_FOUND = 4, /* file missing / no valid volume header */
	VC_ERR_UNSUPPORTED = 5     /* feature not available in this build */
} vc_status;

/* creation progress stages (passed to vc_progress_cb) */
enum {
	VC_STAGE_NONE = 0,
	VC_STAGE_WRITING_DATA = 1,
	VC_STAGE_WRITING_BACKUP_HEADER = 2,
	VC_STAGE_FLUSHING = 3,
	VC_STAGE_FINISHED = 4,
	VC_STAGE_ERROR = 5
};

/* typedef void (*vc_progress_cb)(uint64_t done, uint64_t total, int stage, void *user); */
typedef void (*vc_progress_cb)(uint64_t done, uint64_t total, int stage, void *user);

/* ---------------------------------------------------------------- lifecycle */

/* One-time initialization: crypto self-tests + RNG pool. Safe to call more
   than once; returns VC_OK or a negative vc_status on failure. */
VC_API int32_t  vc_init (void);
VC_API void     vc_shutdown (void);
VC_API int32_t  vc_api_version (void);

/* Human-readable description of the last error on the calling thread. */
VC_API const char *vc_last_error (void);

/* vc_status of the last failed call on this thread (VC_ERR_GENERIC if unknown). */
VC_API vc_status  vc_last_status (void);

/* --------------------------------------------------------------- algorithms */

/* Enumerate supported encryption algorithms / header KDFs by index.
   Names are the canonical VeraCrypt names, e.g. "AES", "Serpent-Twofish-AES",
   "HMAC-SHA-512", "Argon2" (Argon2id). */
VC_API int32_t     vc_get_cipher_count (void);
VC_API const char *vc_get_cipher_name (int32_t index);
VC_API int32_t     vc_get_kdf_count (void);
VC_API const char *vc_get_kdf_name (int32_t index);

/* ----------------------------------------------------------------- creation */

/* Create a volume at `path`.
     size_bytes   : size of the volume file (file-hosted normal volume)
     filesystem   : "FAT" (built-in formatter) or "NONE" (raw encrypted area)
     cipher/kdf   : names as reported by the enumeration functions above
     pim          : 0 = default iteration count, otherwise the VeraCrypt PIM
     quick        : skip wiping the data area (nonzero = quick)
     hidden       : create a hidden volume inside the existing outer volume at
                    `path` (path must be an existing normal volume; size_bytes
                    is the hidden volume size)
     progress     : optional callback (may be NULL); called from the creating
                    thread while the background worker writes the volume
   Returns VC_OK or a vc_status error code. */
VC_API int32_t vc_create_volume (const char *path,
                                 uint64_t    size_bytes,
                                 const char *password,       size_t password_len,
                                 const char *const *keyfile_paths, size_t keyfile_count,
                                 const char *cipher,
                                 const char *kdf,
                                 const char *filesystem,
                                 int32_t     pim,
                                 int32_t     quick,
                                 int32_t     hidden,
                                 vc_progress_cb progress,
                                 void       *progress_user);

/* ------------------------------------------------------------------- access */

/* Open a volume. Returns NULL on failure (call vc_last_error).
     read_only        : nonzero opens with write protection
     use_backup_header: try the embedded backup header first */
VC_API vc_volume *vc_open_volume (const char *path,
                                  const char *password, size_t password_len,
                                  const char *const *keyfile_paths, size_t keyfile_count,
                                  int32_t pim,
                                  int32_t read_only,
                                  int32_t use_backup_header);

/* Sector-aligned encrypted I/O. Offsets are relative to the start of the
   volume's data area (decrypted view). `len` and `offset` must be multiples
   of the volume's sector size (query with vc_get_sector_size).
   Return: number of bytes transferred, or a negative vc_status on error. */
VC_API int64_t vc_read_sectors  (vc_volume *volume, uint8_t *buffer, uint64_t offset, size_t len);
VC_API int64_t vc_write_sectors (vc_volume *volume, const uint8_t *buffer, uint64_t offset, size_t len);

/* Volume properties. */
VC_API uint64_t    vc_get_data_size   (const vc_volume *volume);  /* usable data area size */
VC_API uint32_t    vc_get_sector_size (const vc_volume *volume);
VC_API int32_t     vc_get_pim         (const vc_volume *volume);
VC_API const char *vc_get_cipher_used (const vc_volume *volume);  /* e.g. "AES" */
VC_API const char *vc_get_kdf_used    (const vc_volume *volume);  /* e.g. "Argon2" */
VC_API int32_t     vc_is_hidden       (const vc_volume *volume);  /* 1 if hidden volume */

VC_API void vc_close_volume (vc_volume *volume);

/* ---------------------------------------------------------- password change */

/* Change the header protection of the volume at `path` (works on normal and
   hidden volumes alike — the first header that opens with the given old
   credentials is re-encrypted). Re-encrypts the header with a fresh salt.
     new_kdf    : NULL keeps the current KDF, otherwise a KDF name
     wipe_count : header wipe passes (0 = default of 1) */
VC_API int32_t vc_change_password (const char *path,
                                   const char *old_password, size_t old_password_len,
                                   const char *const *old_keyfile_paths, size_t old_keyfile_count,
                                   int32_t old_pim,
                                   const char *new_password, size_t new_password_len,
                                   const char *const *new_keyfile_paths, size_t new_keyfile_count,
                                   int32_t new_pim,
                                   const char *new_kdf,
                                   int32_t wipe_count);

/* ------------------------------------------------------------- exFAT (FatFs)

   exFAT support is provided by ChaN's FatFs (R0.15, FF_FS_EXFAT=1) bridged
   onto the decrypted data area. Paths are UTF-8 with '/' or '\' separators,
   rooted at the volume's data area. The filesystem is validated by VeraCrypt
   itself and Windows (VeraCrypt formats containers as "super-floppy"). */

typedef struct vc_exfat vc_exfat;          /* opaque mount */
typedef struct vc_exfat_dir vc_exfat_dir;  /* opaque directory iterator */
typedef struct vc_exfat_file vc_exfat_file;/* opaque open file */

typedef struct vc_exfat_entry
{
    char     name[256];      /* UTF-8, NUL-terminated */
    uint8_t  is_directory;
    uint8_t  reserved0[7];
    uint64_t size;           /* bytes */
    uint16_t modified_date;  /* FAT date (see FAT spec) */
    uint16_t modified_time;  /* FAT time */
    uint32_t reserved1;
} vc_exfat_entry;

/* Format the data area of an OPEN volume as exFAT (destroyes its contents). */
VC_API int32_t vc_exfat_format (vc_volume *volume);

/* Mount the exFAT filesystem inside an open volume. NULL + vc_last_error(). */
VC_API vc_exfat *vc_exfat_mount (vc_volume *volume);
VC_API void      vc_exfat_unmount (vc_exfat *fs);

VC_API int32_t vc_exfat_mkdir  (vc_exfat *fs, const char *path);
VC_API int32_t vc_exfat_delete (vc_exfat *fs, const char *path);  /* file or empty dir */

/* Directory iteration: readdir returns 1 for an entry, 0 at end, < 0 on error. */
VC_API vc_exfat_dir *vc_exfat_opendir  (vc_exfat *fs, const char *path);
VC_API int32_t       vc_exfat_readdir  (vc_exfat_dir *dir, vc_exfat_entry *out);
VC_API void          vc_exfat_closedir (vc_exfat_dir *dir);

/* mode: 0 = read existing, 1 = create/truncate write, 2 = open/append write. */
VC_API vc_exfat_file *vc_exfat_open (vc_exfat *fs, const char *path, int32_t mode);
VC_API int64_t vc_exfat_read  (vc_exfat_file *file, uint8_t *buffer, size_t len);
VC_API int64_t vc_exfat_write (vc_exfat_file *file, const uint8_t *buffer, size_t len);
VC_API int32_t vc_exfat_seek  (vc_exfat_file *file, uint64_t position);
VC_API int32_t vc_exfat_close (vc_exfat_file *file);  /* flush + destroy handle */

/* Space usage of a mounted exFAT filesystem. `total_bytes` is the capacity
   available to files (all clusters), `free_bytes` the unallocated part and
   `cluster_bytes` the allocation granularity. Returns VC_OK or VC_ERR_ARG. */
typedef struct vc_exfat_space
{
	uint64_t total_bytes;    /* capacity available to files      */
	uint64_t free_bytes;     /* unallocated space                */
	uint64_t cluster_bytes;  /* allocation granularity (cluster) */
} vc_exfat_space;

VC_API int32_t vc_exfat_get_space (vc_exfat *fs, vc_exfat_space *out);

#ifdef __cplusplus
}
#endif

#endif /* VC_API_H */
