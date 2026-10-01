# hcvault（Python）

[HCVault](../README.zh-CN.md) 的 Python 绑定 —— 用 Python 创建、打开和编辑
VeraCrypt 兼容的加密容器。

纯 ctypes 实现：无需编译器、无扩展模块、无运行时依赖（Python 3.8+）。
绑定走的是与 .NET 包装相同的 `vcapi.h` C API，这里创建的卷可以在官方
VeraCrypt 程序中打开，反之亦然。

[English](README.md) | 简体中文

项目免责声明同样适用：本项目独立、非官方、由 AI 辅助编写、**未经安全审计**。
见[根 README](../README.zh-CN.md)与
[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。

## 安装

从 GitHub Releases 下载 wheel 安装（内含原生库；CI 构建的为 linux-x64，
glibc 2.34+）：

```bash
pip install hcvault-1.4.0-py3-none-manylinux_2_34_x86_64.whl
```

或在仓库检出中自行构建（`native/runtimes/` 下有哪些平台的库就打进去哪些；
一个都没有则产出纯 wheel，运行时经 `VCNATIVE_HOME` 或系统加载器找库）：

```bash
pip wheel python/
```

本包运行于 Python 3.8+（`py3` wheel，无版本上限——套件在 3.8、3.13、
3.14 上全部通过；CI 测 3.8 与 runner 当前 Python）。旧系统注意：pip
需 ≥ 20.3 才能识别 `manylinux_2_XX` 文件名（Ubuntu 20.04 自带的
pip 20.0 需先 `pip install --upgrade pip`）。

导入时按以下顺序定位原生库，先命中先用：

1. `$VCNATIVE_HOME` —— 目录或库文件的完整路径
2. `hcvault/_binaries/<rid>/` —— wheel 内捆绑的副本
3. `native/runtimes/<rid>/native/` —— 仓库检出（向上最多 6 级）
4. 系统加载器（`ctypes.CDLL("hcvault-core")`）

## 快速上手

```python
from hcvault import SecurePassword, Volume, VcFilesystem, ExFatFileSystem

# 创建一个内含 exFAT 文件系统的容器
with SecurePassword.from_str("correct horse battery staple") as pw:
    Volume.create("demo.hc", 64 * 1024 * 1024, pw,
                  filesystem=VcFilesystem.EXFAT)

# 打开它，读写里面的文件
with SecurePassword.from_str("correct horse battery staple") as pw, \
        Volume.open("demo.hc", pw) as vol, \
        ExFatFileSystem.mount(vol) as fs:
    fs.mkdir("/Documents")
    fs.write_file("/Documents/notes.txt", b"hello, encryption")
    print(fs.read_file("/Documents/notes.txt"))
    for e in fs.listdir("/"):
        print(e.name, e.size, "字节", "(目录)" if e.is_directory else "")

    total, free, cluster = fs.get_space()
```

FAT 卷同样走 `ExFatFileSystem`（FatFs 两种都支持）。此外还有：扇区级读写
（`Volume.read_data` / `write_data`）、隐藏卷（`hidden=True`）、密钥文件与
PIM、密码/KDF 轮换（`Volume.change_password`）、建卷进度回调。C 层的完整
API 见 [docs/API.zh-CN.md](../docs/API.zh-CN.md)——Python 命名与之对应。

## 需要知道的几件事

- **一个句柄一个线程。** 卷、挂载及其文件同一时刻只归一个线程使用——与
  C / .NET 层规则相同。
- **密码只做尽力清零。** `SecurePassword` 用可变缓冲区，在 `close()` /
  `__exit__` 时擦除。Python 无法保证这一点（GC、副本），所以真实秘密不要
 放进 `str`，并交给上下文管理器负责擦除。
- **错误自带原生消息。** 调用失败后包装层会立刻读取线程局部错误文本（在其
  他调用使其失效之前）——像平常一样捕获 `VcError` 子类
  （`VcWrongPasswordError`、`VcVolumeNotFoundError`……）即可。
- **进度回调里的异常只打印不抛出。** 回调运行在 C 栈帧上，异常无处可去。

## 测试

```bash
python tests/run_tests.py     # 在 python/ 目录下运行
```

21 项检查，与 C# 测试套件同构（创建/打开/密钥文件/PIM、经桥的 FAT 与 exFAT
文件读写、空间统计、隐藏卷、密码轮换），另含 C# 套件没有的「FAT 走桥」覆盖。

## 构建

使用本包无需构建（从源码构建 wheel 需 Python 3.9+ / setuptools 77；
安装构建好的 wheel 无此要求）。要产出捆绑原生库的 wheel，先构建原生库（例如
`cmake -S native -B build && cmake --build build` 会放到
`native/runtimes/<rid>/native/`），再 `pip wheel python/`。Linux 上 wheel
标签（`manylinux_2_XX_x86_64`）由 `.so` 实际依赖的 glibc 符号版本推得。

尚未上 PyPI；wheel 从 GitHub Releases 获取。
