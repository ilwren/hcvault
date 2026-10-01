# HCVault API 文档

[English](API.md) | 简体中文

HCVault 有两种用法：.NET 项目用托管包 `HCVault.Core`，其他语言通过
`vcapi.h` 直接调用原生库 `hcvault-core`（任何有 C FFI 的语言都行）。
托管包只是 C API 上的一层薄封装，两层能力等价，按项目需要选择。
Python 用户可以直接用 [python/](../python/README.zh-CN.md) 里现成的
薄封装——命名与本文档对应，下面的参考对它同样适用。

两层都可以多线程调用，只有一条规则：同一个打开的卷（或挂载的文件
系统）同一时刻只能被一个线程使用。

---

## 1. HCVault.Core（C# / .NET 8 / .NET 10）

### 安装

```bash
dotnet add package HCVault.Core     # 托管封装（net8.0 + net10.0）
dotnet add package HCVault.Native   # 预编译原生库（runtimes/ 布局）
```

构建应用时带 `RuntimeIdentifier`（`-r linux-x64`、`-r win-x64` …），
对应的 `hcvault-core` 二进制会自动复制到输出目录；Android 上打包进
APK 的 `lib/<abi>/`。

封装层按以下顺序在运行时定位原生库：

| # | 位置 | 典型场景 |
|---|---|---|
| 1 | `$VCNATIVE_HOME`（目录或库文件完整路径） | CI、特殊目录布局 |
| 2 | 应用程序所在目录 | 手动部署 |
| 3 | `<应用>/runtimes/{rid}/native/` | NuGet `runtimes/` 约定 |
| 4 | 应用目录向上最多 6 层找 `native/runtimes/{rid}/native/` | 仓库内构建 |
| 5 | 操作系统默认搜索路径 | 其余情况 |

加载失败时抛出的 `DllNotFoundException` 会说明哪些文件被找到但被拒
绝、每个文件实际是什么架构（PE/ELF 头解析）、该怎么重建。

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
        Cipher      = VcCipher.Aes,              // 15 种级联可用
        Kdf         = VcKdf.Argon2id,            // 6 种 KDF 可用
        Filesystem  = VcFilesystem.ExFat,        // ExFat | Fat | None
        Quick       = true,                      // false = 先擦除数据区
        Progress    = p => Console.WriteLine($"{p.Fraction:P0} {p.Stage}"),
    });
}

// ---- 打开 + 卷内文件读写 --------------------------------------------------
using (var pw  = SecurePassword.FromText("correct horse battery staple"))
using (var vol = Volume.Open(new VolumeOpenOptions { Path = "demo.hc", Password = pw }))
{
    Console.WriteLine($"{vol.CipherName} / {vol.KdfName}, data {vol.DataSize} B");

    using var fs = VolumeFilesystem.Mount(vol);   // 自动识别 FAT / exFAT
    fs.CreateDirectory("\\Docs");
    fs.WriteFile("\\Docs\\notes.txt", "top secret"u8.ToArray());
    byte[] back = fs.ReadFile("\\Docs\\notes.txt");

    foreach (var e in fs.ListDirectory("\\"))
        Console.WriteLine($"{(e.IsDirectory ? "<目录>" : $"{e.SizeBytes} B"),10}  {e.Name}");

    VolumeSpace s = fs.GetSpace();
    Console.WriteLine($"{s.FreeBytes:N0} / {s.TotalBytes:N0} 可用（簇 {s.ClusterBytes} B）");

    fs.Delete("\\Docs\\notes.txt");
}

// ---- 裸扇区访问（Filesystem.None 的卷也能用）------------------------------
using (var pw  = SecurePassword.FromText("correct horse battery staple"))
using (var vol = Volume.Open(new VolumeOpenOptions { Path = "demo.hc", Password = pw }))
using (var s   = vol.OpenStream(writable: true))  // 解密数据区上的 Stream
{
    s.Position = 0x1000;
    s.Write(new byte[] { 1, 2, 3, 4 });
}   // Dispose 时回写缓存的半写扇区
```

错误处理：所有失败都抛 `VcException` 子类（见本节末尾的表），
`Message` 带原生错误文本。密码错误抛 `VcWrongPasswordException`——
捕获后可以重试：

```csharp
try { vol = Volume.Open(new VolumeOpenOptions { Path = p, Password = pw }); }
catch (VcWrongPasswordException) { /* 重新询问 / 放弃 */ }
```

### 创建卷

`Volume.Create(VolumeCreationOptions)` 同步写卷文件，耗时数秒到数分钟
（见第 3 节*性能*）——UI 程序请放到工作线程调用。

| `VolumeCreationOptions` | 类型 | 默认 | 说明 |
|---|---|---|---|
| `Path` | `string` | 必填 | 要创建的卷文件（隐藏卷：**已存在的外层**卷） |
| `SizeBytes` | `long` | 必填 | 文件总大小（隐藏卷：隐藏区大小）；≥ 200 KiB |
| `Password` | `SecurePassword` | 必填 | 可为空（密钥文件提供熵） |
| `Pim` | `int` | 0 | 0 = 库默认；自定义 PIM 以后每次打开都要提供 |
| `KeyFiles` | `IReadOnlyList<string>` | 空 | 按列表顺序混入密码，与 VeraCrypt 程序一致 |
| `Cipher` | `VcCipher` | `Aes` | 15 种级联任选 |
| `Kdf` | `VcKdf` | `Argon2id` | 头部密钥推导 |
| `Filesystem` | `VcFilesystem` | `Fat` | `Fat`、`ExFat` 或 `None`（裸扇区） |
| `Quick` | `bool` | `true` | `false` 时格式化前先用随机数据覆写数据区 |
| `Hidden` | `bool` | `false` | 在 `Path` 的外层卷内创建隐藏卷 |
| `Progress` | `Action<CreationProgress>?` | null | 在创建线程上回调 |

进度阶段（`VcCreationStage`）：`WritingData` → `WritingBackupHeader` →
`Flushing` → `Finished`（或 `Error`）。`CreationProgress` 提供
`BytesDone`、`BytesTotal`、`Stage` 和算好的 `Fraction`。

文件系统限制：内置 FAT 格式化器大约需要 ≥ 1 MiB；exFAT 需要 ≥ 4 MiB。
更小的卷请用 `Filesystem.None` + `Volume.OpenStream`，或自行格式化
数据区。

### 打开卷

`Volume.Open(VolumeOpenOptions)` 每次尝试跑一遍头部 KDF——**密码错误
也照跑**（这正是让暴力猜测变慢的原因）。

| `VolumeOpenOptions` | 类型 | 默认 | 说明 |
|---|---|---|---|
| `Path` | `string` | 必填 | |
| `Password` | `SecurePassword` | 必填 | |
| `Pim` | `int` | 0 | 必须与创建时一致 |
| `KeyFiles` | `IReadOnlyList<string>` | 空 | 同一批文件、同一顺序 |
| `ReadOnly` | `bool` | `false` | 只读句柄（读取和 `GetSpace` 不受影响） |
| `UseBackupHeader` | `bool` | `false` | 优先尝试内嵌备份头——主头损坏时使用 |

本库创建的卷是当前 V2 布局；官方程序的 V2 和旧版 V1 卷都能打开。
Argon2id 卷在对方是 2016 年后的 VeraCrypt 才能打开。

打开后的 `Volume` 属性：

| 属性 | 含义 |
|---|---|
| `DataSize` | 数据区可用字节数（V2 卷 = 文件大小 − 262,144，见第 3 节） |
| `SectorSize` | 文件卷为 512 |
| `CipherName` / `KdfName` | 该卷实际使用的算法（如 `"AES"`、`"Argon2"`） |
| `Pim` | 本句柄打开时用的 PIM |
| `IsHidden` | 本句柄打开的是容器内的隐藏卷时为 true |
| `IsOpen` | `Dispose` 后为 false |

### 扇区级 I/O

- `ReadSectors(buffer, offset, length)` / `WriteSectors(…)`——原生裸
  访问。`offset` 相对**数据区**起点，`offset` 和 `length` 都必须是
  `SectorSize` 的整数倍。
- `OpenStream(writable)`——同一视图上的带缓冲 `Stream`，接受非对齐的
  位置和长度。它缓存一个扇区，部分写会与扇区原内容合并；
  `Dispose`/`Flush` 回写缓存扇区。`Length` 等于 `DataSize`。

`Volume` 实例不是线程安全的；每线程一个实例或自行加锁。库内部会把
加解密并行摊到多个核心。

### 更换凭据

`Volume.ChangePassword(ChangePasswordOptions)` 只重加密卷头（零点几秒
的 I/O + 两次 KDF），数据区不动。普通卷和隐藏卷都支持：用旧凭据第一
个能打开的头就是被重加密的那个。

| `ChangePasswordOptions` | 说明 |
|---|---|
| `OldPassword` / `OldPim` / `OldKeyFiles` | 现有凭据 |
| `NewPassword` / `NewPim` / `NewKeyFiles` | 新凭据（各项可单独不改） |
| `NewKdf` | `null` 保持当前 KDF，否则切换（如切到 `Argon2id`） |
| `WipeCount` | 头部擦除遍数，默认 1 |

先关闭同一文件上所有打开的 `Volume` 句柄——该操作以独占方式打开
文件。

### 隐藏卷

隐藏卷位于外层卷的空闲空间里；创建时只固定大小，位置不记录在任何
地方：

```csharp
// 外层：Filesystem.None，quick
Volume.Create(new VolumeCreationOptions { Path = "outer.hc", SizeBytes = 64 << 20,
                                          Password = outerPw, Filesystem = VcFilesystem.None, Quick = true });
// 内层：在 outer.hc 内，独立密码和文件系统
Volume.Create(new VolumeCreationOptions { Path = "outer.hc", SizeBytes = 16 << 20,
                                          Password = innerPw, Filesystem = VcFilesystem.Fat,
                                          Quick = true, Hidden = true });
```

打开哪个卷完全取决于提供哪个密码。VeraCrypt 威胁模型的两条规则在这
里同样适用：外层卷里放一些像样的文件；**往外层卷写数据可能毁掉隐藏
卷**——外层文件系统不知道隐藏区已被占用。

### 算法

加密算法（`VcCipher`，全部 XTS 模式，每级联成员 256 位密钥）：

| 枚举 | 原生名 | 枚举 | 原生名 |
|---|---|---|---|
| `Aes` | AES | `CamelliaKuznyechik` | Camellia-Kuznyechik |
| `Serpent` | Serpent | `CamelliaSerpent` | Camellia-Serpent |
| `Twofish` | Twofish | `KuznyechikAes` | Kuznyechik-AES |
| `Camellia` | Camellia | `KuznyechikSerpentCamellia` | Kuznyechik-Serpent-Camellia |
| `Kuznyechik` | Kuznyechik | `KuznyechikTwofish` | Kuznyechik-Twofish |
| `AesTwofish` | AES-Twofish | `SerpentAes` | Serpent-AES |
| `AesTwofishSerpent` | AES-Twofish-Serpent | `SerpentTwofishAes` | Serpent-Twofish-AES |
| `TwofishSerpent` | Twofish-Serpent | | |

头部 KDF（`VcKdf`）：`HmacSha512`（经典默认）、`HmacSha256`、
`HmacBlake2s256`、`HmacWhirlpool`、`HmacStreebog`、`Argon2id`（原生名
`"Argon2"`）。运行时用 `HCVaultLibrary.SupportedCiphers` /
`SupportedKdfs` 枚举，不要把名字映射写死。

### 文件系统层

`VolumeFilesystem.Mount(volume)` 读引导扇区，返回 `ExFatVolume`（原生
FatFs）或 `FatVolume`（托管实现）；数据区没有可识别文件系统时抛
`VcException`（如 `Filesystem.None` 的卷）。

路径是根路径，`"\\file.txt"` 或 `"/file.txt"`，两种分隔符都行，支持
Unicode 文件名（FAT 走 LFN，exFAT 是 UTF-8）。

| 成员 | 行为 |
|---|---|
| `ListDirectory(path)` | 每项一个 `FatEntry`：`Name`、`IsDirectory`、`SizeBytes`、`ModifiedUtc`（UTC）、`FirstCluster`（仅 FAT；exFAT 恒为 0） |
| `ReadFile(path)` | 整个文件读进新 `byte[]` |
| `WriteFile(path, data)` | 创建或**覆盖**（截断或增长） |
| `CreateDirectory(path)` | 父目录必须存在 |
| `Delete(path)` | 文件，或**空**目录 |
| `GetSpace()` | 实时 `VolumeSpace`（见下） |

`VolumeSpace`（单位字节，实时值）：

| 字段 | 含义 |
|---|---|
| `TotalBytes` | 文件可用容量 = 全部簇。小于 `Volume.DataSize`（见第 3 节） |
| `FreeBytes` | 未分配空间 |
| `UsedBytes` | `TotalBytes − FreeBytes`（文件、目录、文件系统簿记） |
| `ClusterBytes` | 分配粒度——每个文件占用它的整数倍 |

写入 1 MiB 文件，`FreeBytes` 恰好减少 1 MiB（按整簇取整），删除后恢
复原值。exFAT 的数字来自 FatFs（`f_getfree`，分配位图扫描）；FAT 由
内存中的 FAT 表现算。

### 错误

| 异常 | 原生状态码 | 典型原因 |
|---|---|---|
| `VcWrongPasswordException` | `VC_ERR_WRONG_PASSWORD` (2) | 密码 / 密钥文件 / PIM 不对 |
| `VcArgumentException` | `VC_ERR_ARG` (3) | 路径非法、大小低于下限…… |
| `VcUnsupportedException` | `VC_ERR_UNSUPPORTED` (5) | API 版本不匹配、未知算法 |
| `VcException` | `VC_ERR_VOLUME_NOT_FOUND` (4) | 文件不存在或无有效卷头 |
| `VcException` | `VC_ERR_GENERIC` (1) | 其余一切；`Message` 带原生错误文本 |

第一次卷操作抛 `DllNotFoundException` 说明原生库缺失或无法加载——
消息里写明了找过哪里。

### `SecurePassword`

密码以钉住（pinned）、Dispose 时清零的 UTF-8 字节保存
（`FromText` / `FromBytes`）；不用 CLR 的 `string`，因为字符串内容无法
确定性擦除。API 调用用完就 Dispose，真实密码永远别存进 string。

### `HCVaultLibrary`

`Initialize()`（幂等、线程安全；跑 VeraCrypt 密码学自检、启动 RNG 和
KDF 线程池——第一次卷操作会自动调用）和 `Shutdown()`。
`VerifyAlgorithmTables()` 把托管枚举表与原生核心交叉校验——防封装层
与核心版本漂移的廉价手段，适合放进测试。

---

## 2. hcvault-core（C ABI）

### 构建与链接

```bash
# 独立构建（输出：native/runtimes/<rid>/native/hcvault-core.{dll,so}）
cmake -S native -B build -DCMAKE_BUILD_TYPE=Release && cmake --build build

# 或并入你自己的 CMake 工程
add_subdirectory(native)
target_link_libraries(myapp PRIVATE hcvault-core)

# Android（NDK r26/r27、API 21、16KB 页对齐）：
#   预设 android-{arm64,x64,arm,x86}-release
# Windows（MSVC、静态 CRT——无需 Visual C++ 运行库）：
#   预设 windows-{x64,x86,arm64}-release
```

Linux 用 `-lhcvault-core` 链接，Windows 用 `hcvault-core.lib`；加载后
调一次 `vc_api_version()` 与 `VC_API_VERSION` 比对。Windows 库以
`/MT` 构建；Linux/Android 库除 libc/libstdc++ 外无运行时依赖
（Android 上静态链接）。

### 一个完整例子（C）

```c
#include <stdio.h>
#include "vcapi.h"

int main(void)
{
    vc_init();                       /* 密码学自检 + RNG 池 */

    /* 创建 64 MiB 卷：AES + Argon2id + 内置 FAT，quick 格式化 */
    int rc = vc_create_volume("demo.hc", 64ULL << 20,
                              "correct horse battery staple", 27,
                              NULL, 0,                 /* 无密钥文件 */
                              "AES", "Argon2", "FAT",
                              0,                       /* PIM 0 = 默认 */
                              1, 0,                    /* quick，非隐藏 */
                              NULL, NULL);             /* 无进度回调 */
    if (rc != VC_OK) { fprintf(stderr, "create: %s\n", vc_last_error()); return 1; }

    vc_volume *v = vc_open_volume("demo.hc", "correct horse battery staple", 27,
                                  NULL, 0, 0, 0, 0);
    if (!v) { fprintf(stderr, "open: %s\n", vc_last_error()); return 1; }
    printf("data area: %llu bytes, sector %u, %s/%s\n",
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

### 约定

- 函数返回 `VC_OK` (0) 或 `vc_status`；返回句柄的函数
  （`vc_open_volume`、`vc_exfat_mount` …）失败返回 `NULL`。
- `vc_last_error()` / `vc_last_status()` 描述**当前线程**最后一次失
  败；字符串在该线程下一次调用前有效。
- 所有字符串 UTF-8。exFAT 层的路径以数据区为根，`/` 或 `\` 都行。
- `vc_read_sectors` / `vc_write_sectors`：`offset` 和 `len` 必须是扇区
  大小整数倍；`offset` 从数据区起点算（解密视图，无卷头）。
- 一个 `vc_volume` / `vc_exfat` 句柄同一时刻只归一个线程；库本身线程
  安全、惰性初始化。
- ABI 只增不改：函数永不删除、永不改签名，新库总能配合旧调用方。
  加载后检查 `vc_api_version() == VC_API_VERSION`。

状态码（`vc_status`）：

| 值 | 名称 | 含义 |
|---|---|---|
| 0 | `VC_OK` | 成功 |
| 1 | `VC_ERR_GENERIC` | 见 `vc_last_error()` |
| 2 | `VC_ERR_WRONG_PASSWORD` | 密码/密钥文件/PIM 被拒 |
| 3 | `VC_ERR_ARG` | 参数非法 |
| 4 | `VC_ERR_VOLUME_NOT_FOUND` | 文件不存在 / 无有效卷头 |
| 5 | `VC_ERR_UNSUPPORTED` | 本构建不支持该特性 |

### 函数一览

共 35 个函数，`VC_API_VERSION 4`。头文件
（[native/src/vcapi/vcapi.h](../native/src/vcapi/vcapi.h)）自包含、注释
齐全，这里只列概要。

**生命周期**

| 函数 | 说明 |
|---|---|
| `vc_init()` | 一次性初始化：自检、RNG 池；幂等、线程安全 |
| `vc_shutdown()` | 停后台线程（可选；退出时有兜底） |
| `vc_api_version()` | 库的 ABI 版本（`VC_API_VERSION`） |
| `vc_last_error()` | 本线程最后一次错误的 UTF-8 文本 |
| `vc_last_status()` | 本线程最后一次失败调用的 `vc_status` |

**算法**——名字是 VeraCrypt 规范名；先取数量再按 `0..count-1` 取名
（越界返回 NULL，不算错误）：

| 函数 | 说明 |
|---|---|
| `vc_get_cipher_count()` / `vc_get_cipher_name(i)` | 15 种算法/级联 |
| `vc_get_kdf_count()` / `vc_get_kdf_name(i)` | 6 种头部 KDF |

**创建**

`vc_create_volume(path, size_bytes, password, password_len,
keyfile_paths, keyfile_count, cipher, kdf, filesystem, pim, quick,
hidden, progress, progress_user)`——创建普通卷或隐藏卷。
`filesystem` 取 `"FAT"`、`"EXFAT"` 或 `"NONE"`；`pim` 0 = 默认；
`quick` 非零跳过数据区擦除；`hidden` 非零在 `path` 的现有外层卷内创
建（`size_bytes` = 隐藏区大小）。`progress` 在调用线程上回调，阶段常
量：`VC_STAGE_WRITING_DATA` (1)、`VC_STAGE_WRITING_BACKUP_HEADER` (2)、
`VC_STAGE_FLUSHING` (3)、`VC_STAGE_FINISHED` (4)、`VC_STAGE_ERROR` (5)。

**打开 / 扇区 I/O / 信息**

| 函数 | 说明 |
|---|---|
| `vc_open_volume(path, password, password_len, keyfile_paths, keyfile_count, pim, read_only, use_backup_header)` | 失败返回 `NULL` |
| `vc_close_volume(v)` | 销毁句柄 |
| `vc_read_sectors(v, buf, offset, len)` / `vc_write_sectors(v, buf, offset, len)` | 扇区对齐 I/O；返回字节数或负状态码 |
| `vc_get_data_size(v)` | 数据区可用字节数 |
| `vc_get_sector_size(v)` | 文件卷为 512 |
| `vc_get_pim(v)` / `vc_get_cipher_used(v)` / `vc_get_kdf_used(v)` / `vc_is_hidden(v)` | 打开卷的属性 |

**头部重加密**

`vc_change_password(path, old_password, …, new_password, …, new_kdf,
wipe_count)`——与托管 `ChangePassword`（第 1 节）语义相同；
`new_kdf` 为 NULL 保持当前 KDF。

**exFAT（FatFs 桥）**

| 函数 | 说明 |
|---|---|
| `vc_exfat_format(v)` | 把**打开的**卷的数据区格式化为 exFAT（毁掉原内容） |
| `vc_exfat_mount(v)` / `vc_exfat_unmount(fs)` | 挂载（校验文件系统）/ 释放 |
| `vc_exfat_mkdir(fs, path)` / `vc_exfat_delete(fs, path)` | delete 接受文件或**空**目录 |
| `vc_exfat_opendir(fs, path)` / `vc_exfat_readdir(dir, out)` / `vc_exfat_closedir(dir)` | `readdir` 返回 1 = 有一条目，0 = 结束，< 0 = 出错 |
| `vc_exfat_open(fs, path, mode)` | mode 0 = 读现有，1 = 创建/截断，2 = 打开/追加 |
| `vc_exfat_read(f, buf, len)` / `vc_exfat_write(f, buf, len)` | 返回字节数或负状态码；短写说明卷满 |
| `vc_exfat_seek(f, position)` / `vc_exfat_close(f)` | close 会刷新 |

`vc_exfat_entry` 字段：`name[256]`（UTF-8，NUL 结尾）、`is_directory`、
`size`、`modified_date` / `modified_time`（FAT 日期/时间编码，UTC）、
`reserved0` / `reserved1`（对齐用，须忽略）。

`vc_exfat_get_space(fs, out)` 填充 `vc_exfat_space`
{ `total_bytes`、`free_bytes`、`cluster_bytes` }，实时值——与托管
`GetSpace()` 同源同数。

---

## 3. 互操作、容量与性能

### 一个卷的字节都去哪了

V2 卷文件 = 64 KiB 头部区 + 64 KiB 预留隐藏卷槽 + 数据区 + 128 KiB
备份头组。以 8 MiB 容器为例：

| 层 | 字节数 |
|---|---|
| 卷文件 | 8,388,608 |
| `DataSize`（解密数据区） | 8,126,464（= 文件 − 262,144） |
| exFAT `TotalBytes`（全部簇，每簇 4 KiB） | 8,101,888 |
| 刚格式化后的 exFAT `FreeBytes` | 8,085,504 |

`DataSize` 与 `TotalBytes` 的差是文件系统自身的簿记（引导扇区、
FAT/位图、根目录）；写入文件后 `UsedBytes` 按整簇增长。

### 兼容性

与官方程序双向互通：这里创建的卷官方能开，官方创建的这里能开
（Argon2id 需对方为 2016 年后的 VeraCrypt）。密钥文件的施加方式与
VeraCrypt 程序完全一致——按列出顺序构建密钥文件池，再与密码字节混
合。旧版 V1 卷可读可写。

### 性能

- 创建卷要跑 **2 次**头部 KDF（主头 + 备份头），每次打开跑 **1 次——
  尝试失败也算**。默认 PIM 下 Argon2id 用 416 MiB 内存、6 遍迭代
  （VeraCrypt 默认值）；移动端若在意启动时间，且威胁模型允许，可选用
  `VcKdf.HmacSha512`。
- `Quick = true` 跳过格式化前对数据区的覆写——数据区仍然全量加密，
  但磁盘上原有明文的模式会残留在密文统计特征里。在意磁盘原内容时用
  完整格式化。
- `Volume.OpenStream` 只缓存一个扇区；顺序流式读写接近裸扇区吞吐。
  随机非对齐写每个扇区多付一次读-改-写。
- exFAT（FatFs）与托管 FAT 驱动都在内存里积极缓存；`Delete`/覆盖
  会刷新所改的部分。

### 安全说明

安全性的完整讨论（哪些继承自 VeraCrypt、哪些不同、注意事项——包括
本项目独立开发、AI 辅助、未经安全审计）见
[README](../README.md) 的"安全性"一节。密码保存在钉住、Dispose 时清
零的缓冲区（`SecurePassword`）里；原生库绝不会把明文写到卷自身解密
视图之外；全程用户态文件 I/O，不需要管理员权限或驱动。
