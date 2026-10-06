# HCVault

一个创建、打开、修改 VeraCrypt 兼容加密容器文件的类库——不装驱动、不挂载、不要管理员权限。

[English](README.md) | 简体中文

如果你想在自己的应用里使用 VeraCrypt 加密卷——加密文档存储、手机上的保密
箱、备份工具——这个库直接把卷操作做掉：创建容器，用密码/密钥文件/PIM 打开，
然后按扇区或按文件读写解密后的内容。这里创建的容器可以用官方 VeraCrypt 程
序打开，官方创建的也能在这里打开。

卷核心就是 VeraCrypt 的原始源码（1.26.29），只为可移植性做了少量修补，密码
学部分原封未动。对外暴露一个很小的 C 接口（`vcapi.h`，35 个函数），上面有 .NET 8 /
.NET 10 封装和纯 ctypes 的 Python 包。

```
 你的应用（C#、C/C++，或任何有 FFI 的语言）
        │                │
        │ P/Invoke       │ C ABI (vcapi.h)
        ▼                ▼
 HCVault.Core      hcvault-core
 (.NET 封装)       (VeraCrypt 核心 + FatFs，CMake)
```

## 项目状态与免责声明

本项目是独立项目，与 IDRIX 及 VeraCrypt 官方没有任何隶属、授权或合作关系。
"VeraCrypt" 是对方的商标，且其许可证禁止衍生名称使用，所以本项目叫 HCVault
（取自容器惯用的 `.hc` 扩展名）。详见
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

本项目的代码和文档由 AI 辅助完成，**没有经过安全审计**。密码学核心继承自
VeraCrypt，但外围的集成代码（C 接口、.NET 封装、打包脚本）是新的、未经第三方
评审的。请审阅后再使用；无论如何请为重要数据保留备份——对任何加密软件都应
如此。

## 安全性

这里创建的卷和官方程序创建的一样安全吗？就容器本身而言，是的：同一种卷格
式，同一份源码——头部布局、级联加密、KDF、XTS 模式、自检和 RNG 设计都是
VeraCrypt 本来的东西，不是重新实现。

不一样的在容器周围：

- 本项目没有独立审计。VeraCrypt 被审计过；这里的集成代码没有任何人评审过。
- 快速模式（默认）不先擦除数据区——和官方的快速模式一样。需要擦除就用完整
  格式化。
- .NET 侧的 `SecurePassword` 固定内存、释放即清零，但传给 API 的普通文件缓
  冲区（`byte[]`）在托管内存里，事后不会清零。
- 不支持智能卡/安全令牌密钥文件（PKCS#11 相关代码已移除；文件密钥照常支
  持）；也不支持 TrueCrypt 模式的卷。
- 和所有软件加密一样，宿主机被攻破时一切皆休。

## 能做什么、不能做什么

能做：

- 创建普通卷和隐藏卷（文件型，最小 200 KiB），任意 cipher / KDF / 密钥文件
  / PIM 组合
- 打开当前版本和旧版（V1 布局）VeraCrypt 创建的卷；主头部损坏时可以走内嵌
  备份头部
- 按扇区读写解密数据区，或者走内置 FAT 驱动 / 内置 exFAT（FatFs R0.15）：
  列目录、读写文件、建目录、删除、查询总容量和剩余空间
- 只重加密头部即可轮换密码、KDF、密钥文件
- 在 Android 应用里运行，最低到 API 21

不能做：

- 挂载卷、分配盘符、加密系统盘/整块硬盘（需要内核驱动，有意不做）
- 替代 VeraCrypt 应用程序——它是给你做自己工具用的库

## 兼容性

| 层 | 支持情况 |
|---|---|
| 卷格式 | 创建 V2（当前版）；打开 V2 和旧版 V1；普通卷 + 隐藏卷。与官方 VeraCrypt 互通（Argon2id 卷需对方为 2016 年后的版本） |
| 原生核心 | Windows x64 / x86 / ARM64（MSVC）、Linux x64（GCC/Clang）、Android arm64-v8a / armeabi-v7a / x86 / x86_64（API 21+，NDK r26/r27，16KB 页对齐）。macOS 可编译但未测试 |
| 托管封装 | .NET 8 与 .NET 10（`net8.0;net10.0`），兼容 Native AOT |
| Python 包 | 1.4.0 — Python 3.8+（纯 ctypes，零依赖；wheel 内含原生库，CI 产出 linux-x64） |
| 演示程序 | HCVault.Explorer：Avalonia UI（Windows x64 / linux-x64 / macOS arm64），以 Native AOT 单文件发布。MAUI（Android 7.0 / API 24+）、纯 .NET Android（Android 5.0 / API 21+） |
| 基于版本 | VeraCrypt 1.26.29、FatFs R0.15 |

## 快速入门

C#：

```bash
dotnet add package HCVault.Core     # 托管封装
dotnet add package HCVault.Native   # 预编译原生库（全平台）
                                    # 应用需带 RuntimeIdentifier 构建
```

```csharp
using HCVault.Core;

// 创建一个卷内是 exFAT 文件系统的容器
using (var pw = SecurePassword.FromText("correct horse battery staple"))
{
    Volume.Create(new VolumeCreationOptions
    {
        Path = "demo.hc", SizeBytes = 64 * 1024 * 1024,
        Password = pw, Cipher = VcCipher.Aes, Kdf = VcKdf.Argon2id,
        Filesystem = VcFilesystem.ExFat,
    });
}

// 打开，操作卷里的文件
using (var pw = SecurePassword.FromText("correct horse battery staple"))
using (var vol = Volume.Open(new VolumeOpenOptions { Path = "demo.hc", Password = pw }))
using (var fs = VolumeFilesystem.Mount(vol))      // FAT / exFAT 自动识别
{
    fs.WriteFile("\\notes.txt", "hello, encryption"u8.ToArray());
    foreach (var e in fs.ListDirectory("\\"))
        Console.WriteLine($"{e.Name}  {e.SizeBytes} B");
}
```

Python：

```python
# 从 GitHub Releases 安装 hcvault wheel
from hcvault import SecurePassword, Volume, VcFilesystem, ExFatFileSystem

with SecurePassword.from_str("correct horse battery staple") as pw:
    Volume.create("demo.hc", 64 * 1024 * 1024, pw,
                  filesystem=VcFilesystem.EXFAT)

with SecurePassword.from_str("correct horse battery staple") as pw, \
        Volume.open("demo.hc", pw) as vol, ExFatFileSystem.mount(vol) as fs:
    fs.write_file("/notes.txt", b"hello, encryption")
    for e in fs.listdir("/"):
        print(e.name, e.size, "bytes")
```

C：

```c
#include "vcapi.h"

vc_init();
vc_create_volume("demo.hc", 64ULL << 20, "pw", 2, NULL, 0,
                 "AES", "Argon2", "FAT", 0, 1, 0, NULL, NULL);
vc_volume *v = vc_open_volume("demo.hc", "pw", 2, NULL, 0, 0, 0, 0);
uint8_t sector[512];
vc_read_sectors(v, sector, 0, sizeof sector);   /* 解密后的第一个扇区 */
vc_close_volume(v);
```

C 与 .NET 两层的完整 API 文档：[docs/API.zh-CN.md](docs/API.zh-CN.md)
（[English](docs/API.md)）。Python 用法见
[python/README.zh-CN.md](python/README.zh-CN.md)。

## 构建

前置条件：.NET 10 SDK（类库本身同时面向 .NET 8——测试套件对每个已安装的
运行时各跑一遍）、CMake ≥ 3.15、C++ 工具链。Android 另需 NDK r26/r27；
MAUI 演示另需 `maui-android` 工作负载。

```powershell
.\build.ps1                              # 原生（本机架构）+ 托管 + 测试 + Avalonia 演示
.\build.ps1 -Arch All                   # 原生 x64 + x86 + ARM64
.\build.ps1 -Android                    # 全部 4 个 Android ABI
.\build.ps1 -Arch All -Android -Pack    # 全平台 + 两个 NuGet 包
```

```bash
./build.sh                               # 原生 linux-x64 + 托管 + 测试
./build.sh --android                     # 全部 4 个 Android ABI
./build.sh --all --pack                  # 全平台 + 两个 NuGet 包
```

脚本会自行探测 CMake、NDK 和 Ninja（必要时可下载便携版 Ninja，`-NoDownload` /
`VCN_NO_DOWNLOAD=1` 禁用）。

每次 push 会在 CI 上跑自动测试（原生 + 托管，两个目标框架各一遍、Python
封装一遍，另有 Native AOT 冒烟一遍）：
[.github/workflows/build-demos.yml](.github/workflows/build-demos.yml)。
版本发布手动触发：Actions → build-demos → Run workflow → 填入版本标签
（如 `v1.4.0`，需与项目内的版本号一致）。该次运行会构建全部平台，并把
编译产物附到 GitHub Release：Avalonia 演示的 Native AOT 单文件可执行
（win-x64 zip、linux-x64 / macos-arm64 tar.gz——无需 .NET 运行时，macOS
尽力构建）、MAUI 演示的逐 ABI APK、Android 5.0 通用演示 APK、两个
NuGet 包，以及 Python wheel（linux-x64、Windows x64/x86/ARM64、macOS
arm64 尽力构建；内含原生库）。

Android 演示 APK：直接构建得到默认包（arm64-v8a + x86_64）；
`dotnet publish -c Release -r android-arm64`（还有 `-arm`/`-x64`/`-x86`）
按单一 ABI 出包，下载体积更小。

## 目录结构

| 路径 | 内容 |
|---|---|
| `native/` | CMake 工程：引入的 VeraCrypt 核心 + FatFs + `vcapi.h` + C 测试 |
| `managed/HCVault.Core/` | .NET 8/10 封装（NuGet `HCVault.Core`） |
| `python/` | Python 绑定（纯 ctypes）+ 测试 + wheel 打包 |
| `app/HCVault.Explorer/` | Avalonia 演示（跨平台、Native AOT） |
| `app/HCVault.MauiDemo/` | MAUI 演示，Android 7.0+ |
| `app/HCVault.AndroidDemo/` | 纯 .NET Android 演示，Android 5.0+ |
| `tests/` | 托管端到端测试 |
| `docs/` | API 文档（中英） |

## 许可证

Apache-2.0，见 [LICENSE](LICENSE)。引入的第三方组件保留各自许可（VeraCrypt：
Apache-2.0 / TrueCrypt-3.0 双许可；FatFs：BSD 类；Argon2：CC0），清单见
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。版本历史：
[CHANGELOG.md](CHANGELOG.md)。
