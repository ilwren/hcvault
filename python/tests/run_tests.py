#!/usr/bin/env python3
"""End-to-end tests for the hcvault Python wrapper.

Run:  python tests/run_tests.py        (from the python/ directory)

The native library is found via VCNATIVE_HOME, the repository layout
(native/runtimes/<rid>/native) or the system loader - same rules as the
C# wrapper. Mirrors tests/HCVault.Core.Tests (the C# suite); the FAT tests
run through the FatFs bridge here (the C# suite uses its own managed FAT
implementation), which is extra coverage for the bridge on FAT volumes.
"""

from __future__ import annotations

import ctypes
import os
import secrets
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "src"))

import hcvault  # noqa: E402
from hcvault import (  # noqa: E402
    ExFatFileSystem,
    SecurePassword,
    VcCipher,
    VcCreationStage,
    VcFilesystem,
    VcKdf,
    Volume,
    VcError,
    VcWrongPasswordError,
)

failures: list[str] = []
section = 0


def check(what, body):
    global section
    section += 1
    try:
        body()
    except Exception as ex:  # noqa: BLE001
        failures.append(what)
        print(f"[{section:2}] FAIL {what}: {ex}")
        traceback.print_exc()
        return
    print(f"[{section:2}] OK   {what}")


def random_bytes(n: int) -> bytes:
    return secrets.token_bytes(n)


def main() -> int:
    # ------------------------------------------------ fail fast on a missing
    # native library - every other test would cascade-fail otherwise
    try:
        rid, candidates = hcvault.candidate_paths()
        print("work rid:", rid)
        for c in candidates:
            print("  candidate:", c, "(found)" if c.is_file() else "")
        hcvault.initialize()
    except Exception as ex:  # noqa: BLE001
        print("FATAL:", ex)
        print("\nNo tests were run.")
        return 2

    with tempfile.TemporaryDirectory(prefix="hcvault-py-") as tmp:
        work = Path(tmp)
        vol = work / "test-volume.hc"
        keyfile = work / "test.key"
        keyfile.write_bytes(random_bytes(64_000))
        print("work dir:", work)

        # ------------------------------------------------------ enumerate
        def algorithm_tables():
            hcvault.verify_algorithm_tables()

        check("algorithm tables match native core", algorithm_tables)

        def struct_layout():
            # cross-verified against vcapi.h with a C probe program:
            # sizeof(vc_exfat_entry)=280, offsetof(size)=264 ...
            assert ctypes.sizeof(hcvault._native.VcExfatEntry) == 280
            assert hcvault._native.VcExfatEntry.size.offset == 264
            assert hcvault._native.VcExfatEntry.modified_time.offset == 274
            assert ctypes.sizeof(hcvault._native.VcExfatSpace) == 24

        check("ctypes struct layout matches vcapi.h", struct_layout)

        # -------------------------------------------------------- create
        progress_stages: list[int] = []

        def on_progress(done, total, stage):
            progress_stages.append(int(stage))
            if stage == VcCreationStage.FINISHED:
                print(f"       create progress: {done / total:.0%}")

        def create_volume():
            with SecurePassword.from_str("managed-test-passw0rd!") as pw:
                Volume.create(
                    vol, 5 * 1024 * 1024, pw,
                    cipher=VcCipher.AES, kdf=VcKdf.ARGON2ID,
                    filesystem=VcFilesystem.FAT, pim=10,
                    keyfiles=[keyfile], quick=True,
                    progress=on_progress,
                )
            assert VcCreationStage.FINISHED in progress_stages, progress_stages
            print(f"       file size: {vol.stat().st_size} bytes")

        check("create volume (AES + Argon2id + keyfile + PIM 10 + FAT, quick)", create_volume)

        # ---------------------------------------------------------- open
        def open_without_keyfile_fails():
            with SecurePassword.from_str("managed-test-passw0rd!") as pw:
                try:
                    Volume.open(vol, pw, pim=10)
                except VcWrongPasswordError:
                    return
                raise AssertionError("opened without keyfile?!")

        check("open without keyfile fails", open_without_keyfile_fails)

        volume = None

        def open_with_keyfile():
            nonlocal volume
            with SecurePassword.from_str("managed-test-passw0rd!") as pw:
                volume = Volume.open(vol, pw, pim=10, keyfiles=[keyfile])
            print(f"       cipher={volume.cipher_name} kdf={volume.kdf_name} "
                  f"sector={volume.sector_size} data={volume.data_size} "
                  f"hidden={volume.is_hidden}")

        check("open with keyfile + PIM", open_with_keyfile)

        def sector_roundtrip():
            probe = volume.read_data(0, 16)
            print("       first bytes:", probe.hex())
            volume.write_data(0x1000, bytes(range(1, 9)))
            back = volume.read_data(0x1000, 8)
            assert back == bytes(range(1, 9)), "read-after-write mismatch"

        check("sector roundtrip (read_data/write_data)", sector_roundtrip)

        # ----------------------------------------------------------- FAT
        fat = None

        def mount_fat():
            nonlocal fat
            fat = ExFatFileSystem.mount(volume)

        check("mount FAT filesystem (via the FatFs bridge)", mount_fat)

        def write_long_file():
            data = random_bytes(100 * 1024)
            fat.write_file("/hello-world-with-a-long-name.txt", data)
            write_long_file.data = data
            entries = fat.listdir("/")
            for e in entries:
                print(f"       {e.name}  dir={e.is_directory}  {e.size} B  {e.modified}")
            assert any(e.name == "hello-world-with-a-long-name.txt" for e in entries), \
                "file not listed after write"

        check("write file (long name, 100 KiB)", write_long_file)

        def read_back():
            data = fat.read_file("/hello-world-with-a-long-name.txt")
            assert data == write_long_file.data, "content mismatch"
            assert len(data) == 100 * 1024

        check("read file back and compare", read_back)

        def nested():
            fat.mkdir("/Documents")
            fat.write_file("/Documents/notes.md", b"encrypted inside a container")
            sub = fat.listdir("/Documents")
            assert len(sub) == 1 and sub[0].name == "notes.md", "nested file not found"
            st = fat.stat("/Documents/notes.md")
            assert st.size == len(b"encrypted inside a container")

        check("create directory + nested file (+ stat)", nested)

        def overwrite():
            fat.write_file("/Documents/notes.md", b"tiny")
            assert fat.read_file("/Documents/notes.md") == b"tiny", "did not shrink"
            big = random_bytes(300 * 1024)  # larger than before -> new chain
            fat.write_file("/Documents/notes.md", big)
            assert fat.read_file("/Documents/notes.md") == big, "regrow content mismatch"

        check("overwrite file (shrink + regrow)", overwrite)

        def delete_file():
            fat.remove("/Documents/notes.md")
            try:
                fat.read_file("/Documents/notes.md")
            except VcError:
                return
            raise AssertionError("file still readable after delete")

        check("delete file", delete_file)

        def fat_space():
            total0, free0, cluster = fat.get_space()
            print(f"       space: total={total0} free={free0} cluster={cluster}")
            assert 0 < free0 < total0, (total0, free0)
            assert cluster >= 512 and total0 % cluster == 0, cluster
            assert total0 <= volume.data_size, (total0, volume.data_size)

            fat.write_file("/space-probe.bin", bytes(1024 * 1024))
            total1, free1, _ = fat.get_space()
            assert free0 - free1 == 1024 * 1024, f"1 MiB file consumed {free0 - free1}"
            assert (total1 - free1) - (total0 - free0) == 1024 * 1024, \
                "used bytes did not grow by the file size"

            fat.remove("/space-probe.bin")
            _, free2, _ = fat.get_space()
            assert free2 == free0, f"free space not restored: {free2} != {free0}"

        check("FAT space: total/free/used track allocation exactly", fat_space)

        # the volume handle must be released before the password change below -
        # change-password opens the file exclusively
        fat.unmount()
        volume.close()

        # ------------------------------------------------ change password
        def change_password():
            with SecurePassword.from_str("managed-test-passw0rd!") as old, \
                    SecurePassword.from_str("rotated!") as new:
                Volume.change_password(
                    vol, old, new,
                    old_pim=10, old_keyfiles=[keyfile],
                    new_kdf=VcKdf.HMAC_SHA512,
                )

        check("change password + KDF, drop keyfile", change_password)

        def rotation_effective():
            with SecurePassword.from_str("managed-test-passw0rd!") as old:
                try:
                    Volume.open(vol, old, pim=10, keyfiles=[keyfile])
                except VcWrongPasswordError:
                    pass
                else:
                    raise AssertionError("old credentials still work?!")
            with SecurePassword.from_str("rotated!") as new:
                with Volume.open(vol, new) as v2:
                    print(f"       kdf after rotation: {v2.kdf_name}")

        check("old credentials rejected, new accepted", rotation_effective)

        # --------------------------------------------------------- hidden
        def hidden_volume():
            outer = work / "hidden.hc"
            with SecurePassword.from_str("outer-pw") as opw:
                Volume.create(outer, 10 * 1024 * 1024, opw,
                              filesystem=VcFilesystem.NONE, quick=True)
            with SecurePassword.from_str("inner-pw") as ipw:
                Volume.create(outer, 3 * 1024 * 1024, ipw,
                              filesystem=VcFilesystem.FAT, quick=True, hidden=True)

            with SecurePassword.from_str("outer-pw") as opw:
                with Volume.open(outer, opw) as o, \
                        SecurePassword.from_str("inner-pw") as ipw, \
                        Volume.open(outer, ipw) as i, \
                        ExFatFileSystem.mount(i) as fi:
                    fi.write_file("/secret.txt", b"hidden data")
                    secret = fi.read_file("/secret.txt")
                    assert not o.is_hidden and i.is_hidden
                    print(f"       outer hidden={o.is_hidden}, inner hidden={i.is_hidden}, "
                          f"secret in inner: {secret.decode()}")

        check("hidden volume: create outer (NONE) + inner (FAT)", hidden_volume)

        # --------------------------------------------------------- exFAT
        xvol = None
        xfs = None
        exfat_path = work / "exfat.hc"

        def create_exfat():
            with SecurePassword.from_str("exfat-passw0rd!") as pw:
                Volume.create(exfat_path, 8 * 1024 * 1024, pw,
                              filesystem=VcFilesystem.EXFAT, quick=True)

        check("create exFAT volume (AES + Argon2id)", create_exfat)

        def mount_exfat():
            nonlocal xvol, xfs
            with SecurePassword.from_str("exfat-passw0rd!") as pw:
                xvol = Volume.open(exfat_path, pw)
            xfs = ExFatFileSystem.mount(xvol)  # sniffs the boot sector via FatFs

        check("mount exFAT", mount_exfat)

        def exfat_write_list():
            data = random_bytes(300 * 1024)
            xfs.write_file("/hello-exfat-with-a-long-name.txt", data)
            xfs.mkdir("/Documents")
            xfs.write_file("/Documents/résumé-exfat.md", b"exfat nested")
            for e in xfs.listdir("/"):
                print(f"       {e.name}  dir={e.is_directory}  {e.size} B")
            assert xfs.read_file("/hello-exfat-with-a-long-name.txt") == data, \
                "exFAT content mismatch"
            assert any(e.name == "Documents" and e.is_directory
                       for e in xfs.listdir("/")), "exFAT directory not listed"
            st = xfs.stat("/Documents/résumé-exfat.md")  # unicode path lookup
            assert st.size == len(b"exfat nested")

        check("exFAT write + list + read (long + unicode names)", exfat_write_list)

        def exfat_overwrite_delete():
            xfs.write_file("/hello-exfat-with-a-long-name.txt", b"tiny")
            assert len(xfs.read_file("/hello-exfat-with-a-long-name.txt")) == 4, \
                "exFAT overwrite did not shrink"
            xfs.remove("/Documents/résumé-exfat.md")
            xfs.remove("/Documents")

        check("exFAT overwrite + delete nested", exfat_overwrite_delete)

        def exfat_space():
            total0, free0, cluster = xfs.get_space()
            print(f"       space: total={total0} free={free0} cluster={cluster}")
            assert 0 < free0 < total0, (total0, free0)
            assert cluster >= 512 and total0 % cluster == 0, cluster
            assert total0 <= xvol.data_size, (total0, xvol.data_size)

            xfs.write_file("/space-probe.bin", bytes(1024 * 1024))
            _, free1, _ = xfs.get_space()
            assert free0 - free1 == 1024 * 1024, f"1 MiB consumed {free0 - free1}"

            xfs.remove("/space-probe.bin")
            _, free2, _ = xfs.get_space()
            assert free2 == free0, f"free space not restored: {free2} != {free0}"

        check("exFAT space: total/free track allocation exactly", exfat_space)

        if xfs is not None:
            xfs.unmount()
        if xvol is not None:
            xvol.close()

    # ---------------------------------------------------------- summary
    print()
    if not failures:
        print("ALL PYTHON TESTS PASSED")
        hcvault.shutdown()
        return 0
    print(f"{len(failures)} FAILURE(S):")
    for f in failures:
        print(f"  - {f}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
