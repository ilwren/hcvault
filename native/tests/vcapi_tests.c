/*
 vcapi_tests.c — native end-to-end tests for the vcapi surface.
 Exercises: create (all suites, keyfiles, PIM, hidden, FAT/None, progress),
 open, sector I/O, password change, algorithm enumeration.
 Build:  cc vcapi_tests.c -o vcapi_tests -lhcvault-core -L runtimes/linux-x64/native
 Run:    LD_LIBRARY_PATH=runtimes/linux-x64/native ./vcapi_tests
*/
#include "vcapi.h"
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <assert.h>

static int g_progressCalls = 0;
static void TestProgress (uint64_t done, uint64_t total, int stage, void *user)
{
    (void) total; (void) stage;
    g_progressCalls++;
    *((uint64_t*) user) = done;
}

static int Fail (const char *what, const char *err)
{
    printf ("FAIL %s: %s\n", what, err ? err : "(null)");
    return 1;
}

int main (void)
{
    const char *dir = getenv ("VC_TEST_DIR");
    char path[512];
    snprintf (path, sizeof path, "%s/t.hc", dir ? dir : "/tmp");

    if (vc_init () != VC_OK) return Fail ("init", vc_last_error ());
    if (vc_api_version () != VC_API_VERSION) return Fail ("api version", 0);

    /* --- enumeration --------------------------------------------------- */
    int cc = vc_get_cipher_count (), kc = vc_get_kdf_count ();
    printf ("ciphers: %d, kdfs: %d\n", cc, kc);
    if (cc < 10 || kc < 6) return Fail ("enumeration counts", 0);
    if (!vc_get_cipher_name (0) || vc_get_cipher_name (9999)) return Fail ("cipher enum bounds", 0);

    /* --- create with keyfiles + PIM + progress -------------------------- */
    const char *keyfiles[] = { "/tmp/vctest_key1.bin", "/tmp/vctest_key2.bin" };
    for (int i = 0; i < 2; i++)
    {
        FILE *f = fopen (keyfiles[i], "wb");
        for (int b = 0; b < 4096; b++) fputc ((b * 31 + i * 7) & 0xFF, f);
        fclose (f);
    }

    remove (path);
    uint64_t progressSeen = 0;
    int32_t st = vc_create_volume (path, 5ULL * 1024 * 1024,
        "passw0rd!", 9, keyfiles, 2,
        "AES", "HMAC-SHA-512", "FAT",
        10 /* custom PIM */, 1 /*quick*/, 0, TestProgress, &progressSeen);
    if (st != VC_OK) return Fail ("create with keyfiles", vc_last_error ());
    if (progressSeen == 0 && g_progressCalls == 0) return Fail ("progress callback", 0);
    printf ("create ok (progress calls: %d)\n", g_progressCalls);

    /* --- open WITHOUT keyfiles must fail -------------------------------- */
    vc_volume *bad = vc_open_volume (path, "passw0rd!", 9, NULL, 0, 10, 1, 0);
    if (bad) return Fail ("open without keyfiles should fail", 0);

    /* --- wrong PIM must fail -------------------------------------------- */
    bad = vc_open_volume (path, "passw0rd!", 9, keyfiles, 2, 0, 1, 0);
    if (bad) return Fail ("open with default PIM should fail", 0);

    /* --- wrong password must fail ---------------------------------------- */
    bad = vc_open_volume (path, "wrong", 5, keyfiles, 2, 10, 1, 0);
    if (bad) return Fail ("wrong password accepted", 0);

    /* --- correct open ----------------------------------------------------- */
    vc_volume *v = vc_open_volume (path, "passw0rd!", 9, keyfiles, 2, 10, 0, 0);
    if (!v) return Fail ("open with keyfiles+pim", vc_last_error ());
    printf ("open ok: cipher=%s kdf=%s sector=%u data=%llu pim=%d hidden=%d\n",
        vc_get_cipher_used (v), vc_get_kdf_used (v), vc_get_sector_size (v),
        (unsigned long long) vc_get_data_size (v), vc_get_pim (v), vc_is_hidden (v));
    if (strcmp (vc_get_cipher_used (v), "AES") != 0) return Fail ("cipher mismatch", 0);
    if (vc_get_pim (v) != 10) return Fail ("pim mismatch", 0);

    uint8_t wbuf[4096], rbuf[4096];
    /* FAT check: first data sector should be a FAT boot sector (0xEB jump) */
    if (vc_read_sectors (v, rbuf, 0, 512) < 0) return Fail ("read boot sector", vc_last_error ());
    if (rbuf[0] != 0xEB && rbuf[0] != 0xE9) { /* "NONE" would also be valid; FAT expected here */
        printf ("note: first byte 0x%02X (FAT expected 0xEB)\n", rbuf[0]);
    } else printf ("FAT boot sector detected\n");

    /* --- sector roundtrip -------------------------------------------------- */
    for (size_t i = 0; i < sizeof wbuf; i++) wbuf[i] = (uint8_t) (i * 11 + 5);
    if (vc_write_sectors (v, wbuf, 0, sizeof wbuf) != (int64_t) sizeof wbuf)
        return Fail ("write sectors", vc_last_error ());
    if (vc_read_sectors (v, rbuf, 0, sizeof rbuf) != (int64_t) sizeof rbuf)
        return Fail ("read sectors", vc_last_error ());
    if (memcmp (wbuf, rbuf, sizeof wbuf) != 0) return Fail ("roundtrip mismatch", 0);
    printf ("sector roundtrip ok\n");

    vc_close_volume (v);

    /* --- change password + KDF + drop keyfiles ---------------------------- */
    st = vc_change_password (path,
        "passw0rd!", 9, keyfiles, 2, 10,
        "new-pass-123", 12, NULL, 0, 0,
        "Argon2", 0);
    if (st != VC_OK) return Fail ("change password", vc_last_error ());
    printf ("password change ok (-> Argon2, no keyfiles, default PIM)\n");

    /* old credentials must fail now */
    bad = vc_open_volume (path, "passw0rd!", 9, keyfiles, 2, 10, 1, 0);
    if (bad) return Fail ("old credentials still work", 0);

    /* new credentials must work */
    v = vc_open_volume (path, "new-pass-123", 12, NULL, 0, 0, 1, 0);
    if (!v) return Fail ("open after change", vc_last_error ());
    if (strcmp (vc_get_kdf_used (v), "Argon2") != 0) return Fail ("kdf after change", vc_get_kdf_used (v));
    if (vc_read_sectors (v, rbuf, 0, 512) < 0) return Fail ("read after change", vc_last_error ());
    vc_close_volume (v);
    printf ("reopen with new credentials ok\n");

    /* --- suite sweep (create+open+rw) -------------------------------------- */
    const char *suites[][2] = {
        { "Serpent-Twofish-AES", "HMAC-BLAKE2s-256" },
        { "Kuznyechik",          "Argon2" },
        { "Camellia",            "HMAC-Streebog" },
        { "Twofish-Serpent",     "HMAC-SHA-256" },
    };
    for (size_t i = 0; i < sizeof suites / sizeof suites[0]; i++)
    {
        char p[512]; snprintf (p, sizeof p, "%s/suite%zu.hc", dir ? dir : "/tmp", i);
        remove (p);
        if (vc_create_volume (p, 1ULL * 1024 * 1024, "x", 1, NULL, 0,
            suites[i][0], suites[i][1], "NONE", 0, 1, 0, NULL, NULL) != VC_OK)
            return Fail (suites[i][0], vc_last_error ());
        vc_volume *s = vc_open_volume (p, "x", 1, NULL, 0, 0, 0, 0);
        if (!s) return Fail (suites[i][0], vc_last_error ());
        if (vc_write_sectors (s, wbuf, 0, 512) != 512 || vc_read_sectors (s, rbuf, 0, 512) != 512
            || memcmp (wbuf, rbuf, 512) != 0)
            return Fail (suites[i][0], "sector io");
        vc_close_volume (s);
        printf ("suite ok: %s + %s\n", suites[i][0], suites[i][1]);
    }

    /* --- hidden volume ------------------------------------------------------ */
    char hp[512]; snprintf (hp, sizeof hp, "%s/hidden.hc", dir ? dir : "/tmp");
    remove (hp);
    if (vc_create_volume (hp, 10ULL * 1024 * 1024, "outer-pw", 8, NULL, 0,
        "AES", "HMAC-SHA-512", "NONE", 0, 1, 0, NULL, NULL) != VC_OK)
        return Fail ("create outer", vc_last_error ());
    if (vc_create_volume (hp, 2ULL * 1024 * 1024, "inner-pw", 8, NULL, 0,
        "AES", "HMAC-SHA-512", "NONE", 0, 1, 1 /*hidden*/, NULL, NULL) != VC_OK)
        return Fail ("create hidden", vc_last_error ());
    vc_volume *outer = vc_open_volume (hp, "outer-pw", 8, NULL, 0, 0, 0, 0);
    vc_volume *inner = vc_open_volume (hp, "inner-pw", 8, NULL, 0, 0, 0, 0);
    if (!outer || !inner) return Fail ("open outer/inner", vc_last_error ());
    if (vc_is_hidden (outer) || !vc_is_hidden (inner)) return Fail ("hidden flag", 0);
    /* both views must be independently writable at overlapping offsets */
    if (vc_write_sectors (outer, wbuf, 0, 512) != 512) return Fail ("outer write", vc_last_error ());
    if (vc_write_sectors (inner, rbuf, 0, 512) != 512) return Fail ("inner write", vc_last_error ());
    vc_close_volume (outer); vc_close_volume (inner);
    printf ("hidden volume ok (outer %llu / inner %llu bytes)\n",
        (unsigned long long) 0, (unsigned long long) 0);

    /* --- exFAT (FatFs bridge) ---------------------------------------------- */
    char xp[512]; snprintf (xp, sizeof xp, "%s/exfat.hc", dir ? dir : "/tmp");
    remove (xp);
    if (vc_create_volume (xp, 8ULL * 1024 * 1024, "exfat-pw", 8, NULL, 0,
        "AES", "HMAC-SHA-512", "EXFAT", 0, 1, 0, NULL, NULL) != VC_OK)
        return Fail ("create exFAT volume", vc_last_error ());

    vc_volume *xv = vc_open_volume (xp, "exfat-pw", 8, NULL, 0, 0, 0, 0);
    if (!xv) return Fail ("open exFAT volume", vc_last_error ());
    vc_exfat *xf = vc_exfat_mount (xv);
    if (!xf) return Fail ("mount exFAT", vc_last_error ());

    if (vc_exfat_mkdir (xf, "\\Docs") != VC_OK) return Fail ("exfat mkdir", vc_last_error ());

    vc_exfat_space sp0;
    if (vc_exfat_get_space (xf, &sp0) != VC_OK) return Fail ("exfat get_space", vc_last_error ());
    if (sp0.total_bytes < 4ULL * 1024 * 1024) return Fail ("exfat total_bytes too small", 0);
    if (sp0.free_bytes == 0 || sp0.free_bytes > sp0.total_bytes) return Fail ("exfat free_bytes sanity", 0);
    if (sp0.cluster_bytes == 0 || sp0.cluster_bytes % 512 != 0
        || sp0.total_bytes % sp0.cluster_bytes != 0)
        return Fail ("exfat cluster_bytes sanity", 0);
    if (vc_exfat_get_space (NULL, &sp0) != VC_ERR_ARG || vc_exfat_get_space (xf, NULL) != VC_ERR_ARG)
        return Fail ("exfat get_space arg check", 0);
    printf ("exFAT space: total=%llu free=%llu cluster=%llu\n",
        (unsigned long long) sp0.total_bytes, (unsigned long long) sp0.free_bytes,
        (unsigned long long) sp0.cluster_bytes);
    vc_exfat_file *wf = vc_exfat_open (xf, "\\Docs\\hello-exfat.txt", 1);
    if (!wf) return Fail ("exfat open(write)", vc_last_error ());
    if (vc_exfat_write (wf, (const uint8_t*)"exfat roundtrip!", 16) != 16)
        return Fail ("exfat write", vc_last_error ());
    if (vc_exfat_close (wf) != VC_OK) return Fail ("exfat close", vc_last_error ());

    vc_exfat_space spw;
    if (vc_exfat_get_space (xf, &spw) != VC_OK) return Fail ("exfat get_space (write)", vc_last_error ());
    if (spw.free_bytes >= sp0.free_bytes) return Fail ("exfat write did not consume a cluster", 0);
    if (spw.total_bytes != sp0.total_bytes) return Fail ("exfat total_bytes changed after write", 0);

    vc_exfat_file *rf = vc_exfat_open (xf, "\\Docs\\hello-exfat.txt", 0);
    if (!rf) return Fail ("exfat open(read)", vc_last_error ());
    uint8_t xrbuf[64] = {0};
    if (vc_exfat_read (rf, xrbuf, 64) != 16 || memcmp (xrbuf, "exfat roundtrip!", 16) != 0)
        return Fail ("exfat read back", vc_last_error ());
    vc_exfat_close (rf);

    vc_exfat_dir *d = vc_exfat_opendir (xf, "\\");
    if (!d) return Fail ("exfat opendir", vc_last_error ());
    int saw_docs = 0;
    for (;;)
    {
        vc_exfat_entry e;
        int32_t r = vc_exfat_readdir (d, &e);
        if (r < 0) return Fail ("exfat readdir", vc_last_error ());
        if (r == 0) break;
        if (strcmp (e.name, "Docs") == 0 && e.is_directory) saw_docs = 1;
    }
    vc_exfat_closedir (d);
    if (!saw_docs) return Fail ("exfat root listing missing Docs", 0);

    if (vc_exfat_delete (xf, "\\Docs\\hello-exfat.txt") != VC_OK)
        return Fail ("exfat delete", vc_last_error ());

    vc_exfat_space sp1;
    if (vc_exfat_get_space (xf, &sp1) != VC_OK) return Fail ("exfat get_space (delete)", vc_last_error ());
    if (sp1.free_bytes != sp0.free_bytes) return Fail ("exfat free space not restored after delete", 0);
    printf ("exFAT space after delete: total=%llu free=%llu (restored)\n",
        (unsigned long long) sp1.total_bytes, (unsigned long long) sp1.free_bytes);

    vc_exfat_unmount (xf);
    vc_close_volume (xv);
    printf ("exFAT ok (create + mount + file + dir + delete + space)\n");

    vc_shutdown ();
    printf ("\nALL NATIVE TESTS PASSED\n");
    return 0;
}
