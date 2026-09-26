# HCVault API 文档

[English](API.md) | 简体中文

用 HCVault 有两种方式：.NET 项目用托管包 `HCVault.Core`，其他语言通过
`vcapi.h` 直接调用原生库 `hcvault-core`。托管包只是 C 接口外面的一层薄封装，
两层能力等价，按项目需要选一个即可。

两层都允许多线程调用，只有一条规则：同一个打开的卷（或挂载的文件系统）同一
时刻只归一个线程用。

---

## 1. HCVault.Core（C# / .NET 8 / .NET 10）

### 安装

```bash
dotnet add package HCVault.Core     # 托管封装
dotnet add package HCVault.Native   # 预编译原生库（runtimes/ 结构）
```

应用带 `RuntimeIdentifier`（`-r linux-x64`、`-r win-x64`……）构建时，
`HCVault.Native` 的预编译库会自动复制到输出目录。不带 RID 时，加载器仍会按
`$VCNATIVE_HOME` → 应用程序目录 → `runtimes/{rid}/native` → 系统默认路径的
顺序探测——自己用 CMake 编译 `native/` 也走这套规则。

### 一个完整例子

```csharp
using HCVault.Core;

// ---- 创建（卷内 exFAT，AES + Argon2id）------------------------------------
using (var pw = SecurePassword.FromText("correct horse battery staple"))
{
    Volume.Create(new VolumeCreationOptions
    {
        Path        = "demo.hc",
        SizeBytes   = 64 * 1024 * 1024,          // 最小 200 KiB
        Password    = pw,
        Cipher      = VcCipher.Aes,              // 共 15 种级联
        Kdf         = VcKdf.Argon2id,            // 共 6 种 KDF
        Filesystem  = VcFilesystem.ExFat,        // ExFat | Fat | None
        Quick       = true,                      // false = 先擦除数据区
        Progress    = p => Console.WriteLine($"{p.Fraction:P0} {p.Stage}"),
    });
}

// ---- 打开 + 卷内文件 I/O ---------------------------------------------------
using (var pw  = SecurePassword.FromText("correct horse battery staple"))
using (var vol = Volume.Open(new VolumeOpenOptions { Path = "demo.hc", Password = pw }))
{
    Console.WriteLine($"{vol.CipherName} / {vol.KdfName}, data {vol.DataSize} B");

    using var fs = VolumeFilesystem.Mount(vol);   // 自动识别 FAT / exFAT
    fs.CreateDirectory("\\Docs");
    fs.WriteFile("\\Docs\\notes.txt", "top secret"u8.ToArray());
    byte[] back = fs.ReadFile("\\Docs\\notes.txt");
    foreach (var e in fs.ListDirectory("\\"))
        Console.WriteLine($"{(e.IsDirectory ? "<dir>" : $"{e.SizeBytes} B"),10}  {e.Name}");
    fs.Delete("\\Docs\\notes.txt");
}

// ---- 原始扇区访问（Filesystem.None 的卷也能用）---------------------------
using (var pw  = SecurePassword.FromText("correct horse battery staple"))
using (var vol = Volume.Open(new VolumeOpenOptions { Path = "demo.hc", Password = pw }))
using (var s   = vol.OpenStream(writable: true))  // 解密数据区上的 Stream
{
    s.Position = 0x1000;
    s.Write(new byte[] { 1, 2, 3, 4 });
}
```

### API 一览

`HCVaultLibrary` —— 入口：

- `Initialize()` / `Shutdown()` —— 跑 VeraCrypt 的密码学自检、启停 RNG 和
  KDF 线程池。第一次卷操作会自动 `Initialize`。
- `SupportedCiphers`、`SupportedKdfs` —— 15 种级联和 6 种 KDF。
- `VerifyAlgorithmTables()` —— 托管表和原生核心交叉校验。

`Volume` —— 卷：

- `Volume.Create(VolumeCreationOptions)` —— 创建普通卷或隐藏卷，耗时几秒
  到几分钟，放到工作线程里调。
- `Volume.Open(VolumeOpenOptions)` —— 用密码/密钥文件/PIM 打开，可只读、可
  走备份头部。
- `Volume.ChangePassword(ChangePasswordOptions)` —— 重加密头部：轮换密码、
  KDF、密钥文件，不动数据区。
- `DataSize`、`SectorSize`、`CipherName`、`KdfName`、`Pim`、`IsHidden` ——
  打开后的卷属性。
- `ReadSectors` / `WriteSectors` —— 扇区对齐的加密 I/O；
  `OpenStream(writable)` 把它包成 `Stream`，处理非对齐访问。

选项记录：

| 记录 | 成员 |
|---|---|
| `VolumeCreationOptions` | `Path`、`SizeBytes`（≥ 200 KiB）、`Password`、`Pim`、`KeyFiles`、`Cipher`、`Kdf`、`Filesystem`（`ExFat`/`Fat`/`None`）、`Quick`、`Hidden`、`Progress` |
| `VolumeOpenOptions` | `Path`、`Password`、`Pim`、`KeyFiles`、`ReadOnly`、`UseBackupHeader` |
| `ChangePasswordOptions` | `Path`、`OldPassword`/`OldPim`/`OldKeyFiles`、`NewPassword`/`NewPim`/`NewKeyFiles`、`NewKdf`、`WipeCount` |
| `CreationProgress` | `BytesDone`、`BytesTotal`、`Stage`、`Fraction` |

文件系统访问：`VolumeFilesystem.Mount(volume)` 读引导扇区判断类型，返回
`IVolumeFileSystem` —— `ExFatVolume`（原生 FatFs）或 `FatVolume`（托管实
现）。两者都有：

- `ListDirectory(path)` → `IReadOnlyList<FatEntry>`（`Name`、`IsDirectory`、
  `SizeBytes`、`ModifiedUtc`）
- `ReadFile(path)`、`WriteFile(path, data)`（创建或覆盖）
- `CreateDirectory(path)`（父目录必须存在）、`Delete(path)`（文件或空目录）
- `GetSpace()` → `VolumeSpace`（`TotalBytes`、`FreeBytes`、`UsedBytes`、
  `ClusterBytes`）——实时值。`TotalBytes` 只统计文件可用的簇，因此小于
  `Volume.DataSize`；exFAT 走 FatFs（`f_getfree`），FAT 由内存中的 FAT 现算。

路径是根路径，`"\\file.txt"` 或 `"/file.txt"`，两种分隔符都行。

出错时抛 `VcException` 的子类，与原生状态码一一对应：
`VcWrongPasswordException`、`VcVolumeNotFoundException`、
`VcArgumentException`、`VcUnsupportedException` 等。

`SecurePassword` 把密码放在固定内存里，`Dispose` 时清零。用 `FromText` 或
`FromBytes` 构造；不要把真实密码放进 `string`。

### 几个容易踩的点

- 小于 1 MiB 的卷放不下 FAT 文件系统，只能 `Filesystem.None`（卷最小
  200 KiB）。
- 对同一个文件调 `ChangePassword` 前先把卷 Dispose 掉，它是独占打开的。
- `Hidden = true` 是在已存在的外层卷里建隐藏卷：`Path` 指向外层卷，
  `SizeBytes` 是隐藏卷的大小。
- Android 上把 `libhcvault-core.so` 作为 `AndroidNativeLibrary` 打进 APK
  （落在 `lib/<abi>/`），默认探测就能加载到。

---

## 2. hcvault-core（C ABI）

### 构建与链接

```bash
# 独立编译
cmake -S native -B build -DCMAKE_BUILD_TYPE=Release && cmake --build build
#    -> native/runtimes/<rid>/native/hcvault-core.{dll,so}

# 或挂进你自己的 CMake 工程
add_subdirectory(native)
target_link_libraries(myapp PRIVATE hcvault-core)
```

也可以直接用 `HCVault.Native` NuGet 包，编译发生在你的构建里。Android 用
CMake 预设（`android-{arm64,x64,arm,x86}-release`，API 21，NDK r26/r27）。

### 一个完整例子（C）

```c
#include <stdio.h>
#include "vcapi.h"

int main(void)
{
    vc_init();                       /* 密码学自检 + RNG 池 */

    /* 创建 64 MiB 卷：AES + Argon2id + 内置 FAT，快速格式化 */
    int rc = vc_create_volume("demo.hc", 64ULL << 20,
                              "correct horse battery staple", 27,
                              NULL, 0,                 /* 无密钥文件 */
                              "AES", "Argon2", "FAT",
                              0,                       /* PIM 0 = 默认 */
                              1, 0,                    /* 快速，非隐藏 */
                              NULL, NULL);             /* 无进度回调 */
    if (rc != VC_OK) { fprintf(stderr, "create: %s\n", vc_last_error()); return 1; }

    vc_volume *v = vc_open_volume("demo.hc", "correct horse battery staple", 27,
                                  NULL, 0, 0, 0, 0);
    if (!v) { fprintf(stderr, "open: %s\n", vc_last_error()); return 1; }
    printf("数据区: %llu 字节, 扇区 %u, %s/%s\n",
           (unsigned long long)vc_get_data_size(v),
           vc_get_sector_size(v), vc_get_cipher_used(v), vc_get_kdf_used(v));

    unsigned char buf[512];
    if (vc_read_sectors(v, buf, 0, sizeof buf) < 0)
        fprintf(stderr, "read: %s\n", vc_last_error());

    vc_close_volume(v);
    return 0;
}
```

在打开的卷上做 exFAT 文件操作（FatFs 支撑）：

```c
vc_exfat      *fs = vc_exfat_mount(v);        /* 或 vc_exfat_format(v) */
vc_exfat_mkdir(fs, "/Docs");

vc_exfat_file *f = vc_exfat_open(fs, "/Docs/notes.txt", 1 /*创建/截断*/);
vc_exfat_write(f, (const uint8_t *)"hello", 5);
vc_exfat_close(f);

vc_exfat_dir  *d = vc_exfat_opendir(fs, "/Docs");
vc_exfat_entry e;
while (vc_exfat_readdir(d, &e) == 1)
    printf("%s %llu\n", e.name, (unsigned long long)e.size);
vc_exfat_closedir(d);

vc_exfat_space sp;                          /* 总容量 / 剩余 / 簇大小（字节） */
if (vc_exfat_get_space(fs, &sp) == VC_OK)
    printf("free %llu of %llu bytes\n",
           (unsigned long long)sp.free_bytes,
           (unsigned long long)sp.total_bytes);

vc_exfat_unmount(fs);
```

### 函数一览

共 35 个函数，`VC_API_VERSION 4`。头文件
（[native/src/vcapi/vcapi.h](../native/src/vcapi/vcapi.h)）自包含、注释齐
全，这里只列概要。

| 分组 | 函数 |
|---|---|
| 生命周期 | `vc_init`、`vc_shutdown`、`vc_api_version`、`vc_last_error`、`vc_last_status` |
| 算法枚举 | `vc_get_cipher_count`、`vc_get_cipher_name`、`vc_get_kdf_count`、`vc_get_kdf_name` |
| 创建 | `vc_create_volume` |
| 打开 / I/O | `vc_open_volume`、`vc_close_volume`、`vc_read_sectors`、`vc_write_sectors` |
| 信息 | `vc_get_data_size`、`vc_get_sector_size`、`vc_get_pim`、`vc_get_cipher_used`、`vc_get_kdf_used`、`vc_is_hidden` |
| 头部 | `vc_change_password` |
| exFAT | `vc_exfat_format`、`vc_exfat_mount`、`vc_exfat_unmount`、`vc_exfat_mkdir`、`vc_exfat_delete`、`vc_exfat_opendir`、`vc_exfat_readdir`、`vc_exfat_closedir`、`vc_exfat_open`、`vc_exfat_read`、`vc_exfat_write`、`vc_exfat_seek`、`vc_exfat_close`、`vc_exfat_get_space` |

约定：

- 函数返回 `VC_OK`（0）或错误码；`vc_open_volume` 这类返回句柄的函数失败时
  返回 `NULL`。细节用 `vc_last_error()` / `vc_last_status()` 查——按线程隔
  离，到该线程下一次调用为止。
- 字符串一律 UTF-8。
- `vc_read_sectors` / `vc_write_sectors` 里 `offset` 和 `len` 必须是扇区大小
  的整数倍，`offset` 从数据区开头算起（解密视图，不含头部）。
- 一个 `vc_volume` / `vc_exfat` 句柄同一时刻只给一个线程用。
- 加载库后检查 `vc_api_version() == VC_API_VERSION`。`vc_*` ABI 只增不改：
  函数不会被删掉或改签名。

---

## 3. 互操作与安全说明

卷在各个方向都可以互换：这里创建的官方能打开，官方创建的这里也能打开。安
全性方面哪些继承自 VeraCrypt、哪些不同、有什么注意事项，见
[README](../README.zh-CN.md) 的"安全性"一节。

两个偶尔会坑人的点：KDF 名称是 VeraCrypt 的规范字符串（`"Argon2"` 就是
Argon2id），建议运行时枚举而不是硬编码；快速模式跳过格式化前的擦除，如果磁
盘上原有内容需要抹掉，用完整格式化。
