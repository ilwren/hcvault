# 可行性分析：容器内文件系统升级为 exFAT / NTFS

> **状态更新（2026-09）**：exFAT 已按本文推荐的**路线 B（vendor FatFs）实现**。
> 落点：`native/src/fatfs/`（未修改的 FatFs R0.15 + 定制 ffconf：FF_FS_EXFAT=1、
> UTF-8 API、FF_USE_LFN=2）+ `FatFsBridge.cpp`（diskio 桥接到解密数据区，盘符槽
> 注册表，异常不越过 C 边界）。创建：`VcFilesystem.ExFat`；托管侧：
> `ExFatVolume` + `VolumeFilesystem.Mount`（引导扇区自动探测 FAT/exFAT）。
> 结构验证：独立 Python 校验器按 Microsoft exFAT 规范逐项核对——引导扇区全
> 字段、FAT/簇位图、up-case 表校验和（规范 ROR32+加法算法）、三个名字哈希、
> NoFatChain 连续簇读取、300 KB 文件内容逐字节比对，全部通过。
> NTFS 结论不变：不建议实现。以下为原始分析。

## 0. 背景与硬约束

本方案的定位是**免挂载、免驱动、免管理员权限**的纯库卷操作。这决定了文件系统必须在
**用户态**、通过 `Volume.OpenStream()`（扇区流）直接读写，无法借用操作系统的格式化/文件系统服务。

上游 VeraCrypt 自己怎么做的（不可抄）：

| 平台 | 上游对 exFAT/NTFS 的做法 | 依赖 |
|---|---|---|
| Windows | 先**挂载**成盘符，再调 `format.com` / `SHFormatDrive` | 驱动 + 管理员 |
| Linux | 挂载后 shell 出去调 `mkfs.ntfs` / `mkfs.exfat`（见 `VolumeCreationOptions::GetFsFormatter()`，返回 `"mkfs.ext4"` 等） | 挂载 + 外部工具 |

也就是说：**上游没有可移植的 exFAT/NTFS 格式化代码**——FAT 是唯一一个内建格式化器（因为要支持系统加密分区加密前的格式化）。因此 exFAT/NTFS 只能自研或 vendor 第三方实现。

当前 FAT 的能力边界（实际够用的场景）：FAT32 卷上限 2 TB（512 扇区）、**单文件上限 4 GB−1**、无日志。痛点主要在"单个 >4GB 的大文件"和"超大容器"。

## 1. exFAT —— 可行性：高 ✅（推荐作为下一步）

### 1.1 有利条件

- **规格公开**：微软 2019 年在 Open Specification Promise 下公开了 exFAT 规范；Linux 内核 5.4+ 自带 GPLv2 的 exFAT 驱动可作参照（不可 vendor，可对照验证）。
- **盘上结构简单**，与 FAT 同量级、无日志、无 B 树：
  - 主引导区（9 个保留扇区 + 5 个备份扇区，带 CRC32 校验）
  - 单一 FAT（32 位簇链，语义与 FAT32 几乎相同）
  - 簇堆（cluster heap）
  - 目录项为 **32 字节三件套**：File(0x85) + Stream Extension(0xC0) + FileName(0xC1)，带条目集校验和；另有卷标(0x83)、位图(0x81)、大写表(0x82)
  - UTF-16LE 长文件名（≤255 字符），时间戳精度 10ms/2s/10ms + UTC 偏移
- **成熟可 vendor 的实现存在**：ChaN 的 **FatFs**（BSD 风格许可，允许随源码分发）带 `FF_FS_EXFAT=1` 选项。此前调研的 Android VeraCrypt 应用 CryptoContainer 正是 vendor FatFs 做 exFAT/FAT 桥接——**有直接先例**。
- 互操作性完全标准：我们创建的 exFAT 容器，用真 VeraCrypt 挂载后 Windows/macOS/Linux 直接可读；反之亦然。

### 1.2 两条实现路线

| | 路线 A：纯 C# 自研 | 路线 B：vendor FatFs（exFAT 开启） |
|---|---|---|
| 位置 | `managed/.../Fat/ExFatVolume.cs`，与现有 `FatVolume` 对称 | `native/src/fs/`，加 `vc_fs_*` C API |
| 工作量 | ~2,000–3,000 行 + 测试，**约 2–3 周** | FatFs 本身零成本；桥接 + C API + 托管包装 **约 1 周** |
| 风险 | 自研兼容性细节（时间戳/校验和/边界），需对 Windows 实测 | FatFs 千锤百炼，兼容性风险低；但 native API 面扩大（目录枚举/读写/删除要走 C 边界） |
| 许可 | 无新增 | FatFs 修改版 BSD（保留版权声明即可），与 Apache-2.0 分发兼容 |
| 依赖 | 无（纯托管） | 无新外部依赖（C 源码进包） |

### 1.3 格式化器（创建时）

exFAT 格式化本身很轻：引导扇区 + 校验和、空 FAT、位图、**128 KB 大写表**、空根目录，约 300–600 行。路线 A/B 均可承载（A 在托管层直接经扇区流写；B 由 FatFs `f_mkfs` 完成）。

### 1.4 结论

**exFAT 完全可行**，且收益明确：突破 4 GB 单文件限制、支持超大容器（exFAT 上限 128 PB）、无日志带来的损坏风险与 FAT 相当（容器场景可接受）。建议作为下一个里程碑，优先路线 B（快、稳），时间紧可退到路线 A。

## 2. NTFS —— 可行性：中低 ⚠️（不建议近期做）

分三个能力层次评估：

### 2.1 格式化（创建 NTFS 卷）：难

最小合法 NTFS 需要写出一整套系统元文件（$Boot、$MFT、$MFTMirr、$LogFile、$Volume、
$AttrDef、$Root、$Bitmap、$Boot、$BadClus、$Secure、$UpCase、$Extend/$ObjId/$Quota/$Reparse
约 20+ 个），每个都带 $STANDARD_INFORMATION/$FILE_NAME/$DATA 属性、MFT 记录的 USA
fixup、目录还要 $INDEX_ROOT/$INDEX_ALLOCATION，以及一份让 Windows 接受的空 $LogFile。
参考实现 mkntfs（ntfs-3g 项目）约 6,000 行、打磨多年。自研约 3–6k 行，
**最麻烦的不是"写出来"而是"让 chkdsk 不吭声"**——兼容性细节非常多。

### 2.2 读取（浏览/导出）：中等

MFT 扫描、目录 B 树索引、resident/non-resident 属性、runlist 解析——只读浏览约
2–4k 行 C#，有大量开源参照（Linux ntfs3、dissect.ntfs 等），**可做**。
（压缩/稀疏属性可先不支持。）

### 2.3 写入/删除：难，且有数据安全风险

安全写需要处理 $LogFile 日志一致性（或至少保证 Windows 挂载时能干净恢复）、
MFT 记录分配、属性增长时的 resident→non-resident 迁移、runlist/B$Bitmap 维护等。
实现不完整时轻则 chkdsk 报错，重则数据损坏。**没有许可兼容的成熟库可 vendor**：
ntfs-3g 是 GPLv2（无法并入 Apache-2.0 分发），Windows 内核实现闭源。

### 2.4 结论

容器内文件存储并不需要 NTFS 的特有能力（ACL/压缩/日志）。除非有"必须与
NTFS 生态互通"的硬需求，建议：**整体观望**；如确需，降级为"NTFS **只读**浏览 +
导出"（约 2–4k 行，风险可控）。

## 3. 汇总

| | FAT（现状） | exFAT | NTFS |
|---|---|---|---|
| 已有内建支持 | ✅ 上游格式化器 + 自研驱动 | ❌ | ❌ |
| 规格 | 公开 | 公开（OSP） | 部分公开/逆向文档 |
| 格式化器自研成本 | 已有 | 低（0.3–0.6k 行） | 高（3–6k 行，chkdsk 兼容风险） |
| 读驱动成本 | 已有（~1.5k 行） | 中（与 FAT 同级） | 中高（B 树/runlist） |
| 写驱动成本 | 已有 | 中（无日志，语义同 FAT） | 高（$LogFile/MFT 分配，数据风险） |
| 可 vendor 的许可兼容实现 | — | **FatFs（BSD）** | 无（ntfs-3g 为 GPL） |
| 单文件 >4GB | ❌ | ✅ | ✅ |
| 无日志损坏风险 | 与 exFAT 相当 | 同 FAT | 有日志（若完整实现） |
| **建议** | 保持 | **✅ 下一步做（路线 B 优先）** | ⚠️ 观望或只读 |

## 4. 落地时对现有架构的影响（供排期参考）

- 卷创建：`vc_create_volume` 的 `filesystem` 参数增加 `"EXFAT"`；路线 B 在 native 层
  由 FatFs `f_mkfs` 完成，路线 A 在托管层经 `OpenStream` 写（native 不动）。
- 托管层：新增 `ExFatVolume`（或 `FatFsVolume` 包装），与 `FatVolume` 同一抽象
  （都吃 `Volume` 的扇区流）；WPF Explorer 无需改动（只依赖 ListDirectory/Read/Write/Delete）。
- 测试：exFAT 读写往返 + 用真实 mkfs.exfat 生成的镜像做交叉验证。
