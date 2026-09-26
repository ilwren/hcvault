// MiniCore.h — minimal CoreBase implementation for "create/access without mounting".
// All mount/filesystem/device operations throw NotImplemented; everything
// volume-format-related (create, open, change password, keyfiles) is inherited
// from the real CoreBase implementation.
#ifndef VC_MINI_CORE_H
#define VC_MINI_CORE_H

#include "Core/CoreBase.h"
#include "Volume/VolumePasswordCache.h"

namespace VeraCrypt {

class MiniCore : public CoreBase
{
public:
    MiniCore() {}
    virtual ~MiniCore() {}

    // --- unused platform surface (everything the Android/C# library doesn't need) ---
    void CheckFilesystem (shared_ptr <VolumeInfo>, bool) const override { throw NotImplemented (SRC_POS); }
    void DismountFilesystem (const DirectoryPath &, bool) const override { throw NotImplemented (SRC_POS); }
    shared_ptr <VolumeInfo> DismountVolume (shared_ptr <VolumeInfo>, bool, bool) override { throw NotImplemented (SRC_POS); }
    bool FilesystemSupportsLargeFiles (const FilePath &) const override { throw NotImplemented (SRC_POS); }
    DirectoryPath GetDeviceMountPoint (const DevicePath &) const override { throw NotImplemented (SRC_POS); }
    uint32 GetDeviceSectorSize (const DevicePath &) const override { throw NotImplemented (SRC_POS); }
    uint64 GetDeviceSize (const DevicePath &) const override { throw NotImplemented (SRC_POS); }
    HostDeviceList GetHostDevices (bool) const override { throw NotImplemented (SRC_POS); }
    int GetOSMajorVersion () const override { return 0; }
    int GetOSMinorVersion () const override { return 0; }
    VolumeInfoList GetMountedVolumes (const VolumePath &) const override { return VolumeInfoList(); }
    bool HasAdminPrivileges () const override { return false; }  // no admin needed: we never mount
    bool IsDevicePresent (const DevicePath &) const override { return false; }
    bool IsInPortableMode () const override { return true; }
    bool IsMountPointAvailable (const DirectoryPath &) const override { return false; }
    bool IsOSVersion (int, int) const override { return false; }
    bool IsOSVersionLower (int, int) const override { return false; }
    bool IsPasswordCacheEmpty () const override { return true; }
    VolumeSlotNumber MountPointToSlotNumber (const DirectoryPath &) const override { throw NotImplemented (SRC_POS); }
    shared_ptr <VolumeInfo> MountVolume (MountOptions &) override { throw NotImplemented (SRC_POS); }
    void SetFileOwner (const FilesystemPath &, const UserId &) const override { throw NotImplemented (SRC_POS); }
    DirectoryPath SlotNumberToMountPoint (VolumeSlotNumber) const override { throw NotImplemented (SRC_POS); }
    void WipePasswordCache () const override { VolumePasswordCache::Clear(); }

#if defined(TC_UNIX)
    // pure-virtual only on Unix CoreBase (guarded there by #if defined(TC_UNIX))
    bool IsProtectedSystemDirectory (const DirectoryPath &) const override { return false; }
    bool IsDirectoryOnUserPath (const DirectoryPath &) const override { return false; }
#endif
};

} // namespace VeraCrypt

#endif
