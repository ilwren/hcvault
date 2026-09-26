#
# HCVault.Native — hcvault-core shared library
#
# Builds the volume core (create/open/read/write encrypted volumes without
# mounting) as `hcvault-core.dll` / `libhcvault-core.so`.
#
# The portable configuration compiles the pure-C implementations of all
# ciphers/hashes on every platform (identical to upstream's NOSSE2 build),
# so no assembler toolchain is required. See PORTING-NOTES.md for how to
# re-enable SIMD units per platform if you need the extra throughput.
#
# Usage:
#   cmake --preset linux-release
#   cmake --build --preset linux-release
#
# Cross builds (e.g. arm64 on an x64 host) use the standard CMake toolchain
# mechanism; see CMakePresets.json and build-linux.sh.
#
cmake_minimum_required(VERSION 3.16)
project(hcvault-core LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 14)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT VC_RID)
  # infer a .NET-style RID from the current toolchain when none is given
  if(CMAKE_SYSTEM_NAME STREQUAL "Windows")
    # Prefer CMAKE_GENERATOR_PLATFORM (always set by the Visual Studio generator
    # and available before the compiler check) over CMAKE_SIZEOF_VOID_P, which
    # is only reliable once the compiler has been probed. A wrong inference here
    # would silently write a library into another architecture's runtimes dir.
    if(CMAKE_GENERATOR_PLATFORM MATCHES "^[Aa][Rr][Mm]64$" OR CMAKE_SYSTEM_PROCESSOR MATCHES "ARM64|aarch64")
      set(VC_RID "win-arm64")
    elseif(CMAKE_GENERATOR_PLATFORM MATCHES "^[Ww]in32$" OR CMAKE_SIZEOF_VOID_P EQUAL 4)
      set(VC_RID "win-x86")
    else()
      set(VC_RID "win-x64")
    endif()
  elseif(CMAKE_SYSTEM_NAME STREQUAL "Android")
    # ANDROID_ABI: arm64-v8a / armeabi-v7a / x86_64 / x86
    if(ANDROID_ABI STREQUAL "arm64-v8a")
      set(VC_RID "android-arm64")
    elseif(ANDROID_ABI STREQUAL "armeabi-v7a")
      set(VC_RID "android-arm")
    elseif(ANDROID_ABI STREQUAL "x86")
      set(VC_RID "android-x86")
    else()
      set(VC_RID "android-x64")
    endif()
  else()
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "aarch64|arm64")
      set(VC_RID "linux-arm64")
    elseif(CMAKE_SYSTEM_PROCESSOR MATCHES "^arm")
      set(VC_RID "linux-arm")
    elseif(CMAKE_SIZEOF_VOID_P EQUAL 4)
      set(VC_RID "linux-x86")
    else()
      set(VC_RID "linux-x64")
    endif()
  endif()
endif()

message(STATUS "hcvault-core: target RID = ${VC_RID}")

# ---------------------------------------------------------------------------
# sources (single list for every platform — that is the point of the port)
# ---------------------------------------------------------------------------
set(VC_ROOT ${CMAKE_CURRENT_SOURCE_DIR}/src)

set(PLATFORM_SRC
  ${VC_ROOT}/veracrypt/Platform/Buffer.cpp
  ${VC_ROOT}/veracrypt/Platform/Exception.cpp
  ${VC_ROOT}/veracrypt/Platform/Event.cpp
  ${VC_ROOT}/veracrypt/Platform/FileCommon.cpp
  ${VC_ROOT}/veracrypt/Platform/MemoryStream.cpp
  ${VC_ROOT}/veracrypt/Platform/Memory.cpp
  ${VC_ROOT}/veracrypt/Platform/PlatformTest.cpp
  ${VC_ROOT}/veracrypt/Platform/Serializable.cpp
  ${VC_ROOT}/veracrypt/Platform/Serializer.cpp
  ${VC_ROOT}/veracrypt/Platform/SerializerFactory.cpp
  ${VC_ROOT}/veracrypt/Platform/StringConverter.cpp
  ${VC_ROOT}/veracrypt/Platform/TextReader.cpp
)

set(PORT_SRC
  ${VC_ROOT}/port/File.cpp
  ${VC_ROOT}/port/SyncPrimitives.cpp
  ${VC_ROOT}/port/SystemServices.cpp
  ${VC_ROOT}/port/FilesystemPath.cpp
  ${VC_ROOT}/port/HwRandom.c
)

set(VOLUME_SRC
  ${VC_ROOT}/veracrypt/Volume/Cipher.cpp
  ${VC_ROOT}/veracrypt/Volume/EncryptionAlgorithm.cpp
  ${VC_ROOT}/veracrypt/Volume/EncryptionMode.cpp
  ${VC_ROOT}/veracrypt/Volume/EncryptionTest.cpp
  ${VC_ROOT}/veracrypt/Volume/EncryptionThreadPool.cpp
  ${VC_ROOT}/veracrypt/Volume/Hash.cpp
  ${VC_ROOT}/veracrypt/Volume/Keyfile.cpp
  ${VC_ROOT}/veracrypt/Volume/Pkcs5Kdf.cpp
  ${VC_ROOT}/veracrypt/Volume/Volume.cpp
  ${VC_ROOT}/veracrypt/Volume/VolumeException.cpp
  ${VC_ROOT}/veracrypt/Volume/VolumeHeader.cpp
  ${VC_ROOT}/veracrypt/Volume/VolumeInfo.cpp
  ${VC_ROOT}/veracrypt/Volume/VolumeLayout.cpp
  ${VC_ROOT}/veracrypt/Volume/VolumePassword.cpp
  ${VC_ROOT}/veracrypt/Volume/VolumePasswordCache.cpp
  ${VC_ROOT}/veracrypt/Volume/EncryptionModeXTS.cpp
)

set(CORE_SRC
  ${VC_ROOT}/veracrypt/Core/CoreBase.cpp
  ${VC_ROOT}/veracrypt/Core/FatFormatter.cpp
  ${VC_ROOT}/veracrypt/Core/HostDevice.cpp
  ${VC_ROOT}/veracrypt/Core/RandomNumberGenerator.cpp
  ${VC_ROOT}/veracrypt/Core/VolumeCreator.cpp
)

set(COMMON_SRC
  ${VC_ROOT}/veracrypt/Common/Crc.c
  ${VC_ROOT}/veracrypt/Common/Endian.c
  ${VC_ROOT}/veracrypt/Common/GfMul.c
  ${VC_ROOT}/veracrypt/Common/Pkcs5.c
)

set(CRYPTO_SRC
  ${VC_ROOT}/veracrypt/Crypto/Aeskey.c
  ${VC_ROOT}/veracrypt/Crypto/Aestab.c
  ${VC_ROOT}/veracrypt/Crypto/Aescrypt.c
  ${VC_ROOT}/veracrypt/Crypto/SerpentFast.c
  ${VC_ROOT}/veracrypt/Crypto/Twofish.c
  ${VC_ROOT}/veracrypt/Crypto/Camellia.c
  ${VC_ROOT}/veracrypt/Crypto/kuznyechik.c
  ${VC_ROOT}/veracrypt/Crypto/Streebog.c
  ${VC_ROOT}/veracrypt/Crypto/Sha2.c
  ${VC_ROOT}/veracrypt/Crypto/Whirlpool.c
  ${VC_ROOT}/veracrypt/Crypto/blake2s.c
  ${VC_ROOT}/veracrypt/Crypto/cpu.c
  ${VC_ROOT}/veracrypt/Crypto/jitterentropy-base.c
  ${VC_ROOT}/veracrypt/Crypto/Argon2/src/argon2.c
  ${VC_ROOT}/veracrypt/Crypto/Argon2/src/core.c
  ${VC_ROOT}/veracrypt/Crypto/Argon2/src/blake2/blake2b.c
  ${VC_ROOT}/veracrypt/Crypto/Argon2/src/ref.c
  ${VC_ROOT}/veracrypt/Crypto/Argon2/src/opt_sse2.c
  ${VC_ROOT}/veracrypt/Crypto/Argon2/src/opt_avx2.c
)

set(SHIM_SRC
  ${VC_ROOT}/shim/MiniCore.cpp
)

# ChaN's FatFs (unmodified, exFAT enabled via our ffconf.h) + the diskio
# bridge that routes its sector I/O through the decrypted volume data area.
set(FATFS_SRC
  ${VC_ROOT}/fatfs/ff.c
  ${VC_ROOT}/fatfs/ffunicode.c
  ${VC_ROOT}/fatfs/FatFsBridge.cpp
)

# The CPU Jitter RNG must be compiled without optimizations (timing-jitter
# correctness). MSVC/GCC handle this via in-file pragmas, but clang's
# `#pragma clang optimize off` does not clear __OPTIMIZE__ (the file then
# #errors out under -O2/-O3), so force -O0 for this one TU everywhere.
set_source_files_properties(${VC_ROOT}/veracrypt/Crypto/jitterentropy-base.c
  PROPERTIES COMPILE_OPTIONS "$<$<NOT:$<C_COMPILER_ID:MSVC>>:-O0>")

add_library(hcvault-core SHARED
  ${PLATFORM_SRC}
  ${PORT_SRC}
  ${VOLUME_SRC}
  ${CORE_SRC}
  ${COMMON_SRC}
  ${CRYPTO_SRC}
  ${SHIM_SRC}
  ${FATFS_SRC}
  ${VC_ROOT}/vcapi/vcapi.cpp
)

set_target_properties(hcvault-core PROPERTIES OUTPUT_NAME hcvault-core)

target_include_directories(hcvault-core PRIVATE
  ${VC_ROOT}
  ${VC_ROOT}/veracrypt
  ${VC_ROOT}/veracrypt/Crypto
  ${VC_ROOT}/veracrypt/Crypto/Argon2/include
  ${VC_ROOT}/veracrypt/Common
  ${VC_ROOT}/fatfs
)

# NOTE: do NOT add ${VC_ROOT}/veracrypt/Platform to the include path — on the
# case-insensitive Windows filesystems, MSVC's <ctime> (#include <time.h>)
# would resolve to VeraCrypt's Platform/Time.h and break every TU.
# port/*.cpp therefore include the Windows glue via "Platform/Windows/System.h"
# (resolved against ${VC_ROOT}/veracrypt), and upstream Platform/System.h
# resolves its own "Windows/System.h" relative to its directory.


# ---------------------------------------------------------------------------
# platform configuration
# ---------------------------------------------------------------------------
if(WIN32)
  target_compile_definitions(hcvault-core PRIVATE
    TC_WINDOWS
    VC_CORE_EXPORTS
    _CRT_SECURE_NO_WARNINGS
    ARGON2_NO_THREADS
    CRYPTOPP_DISABLE_ASM
    CRYPTOPP_DISABLE_SSE2
    CRYPTOPP_DISABLE_SSSE3
    CRYPTOPP_DISABLE_SHANI
    CRYPTOPP_DISABLE_AESNI
  )
  target_link_libraries(hcvault-core PRIVATE bcrypt)
  if(MSVC)
    target_compile_options(hcvault-core PRIVATE /utf-8 /EHsc /W3)
  endif()
else()
  target_compile_definitions(hcvault-core PRIVATE
    TC_UNIX
    TC_LINUX
    VC_CORE_EXPORTS
    ARGON2_NO_THREADS
    CRYPTOPP_DISABLE_ASM
    CRYPTOPP_DISABLE_SSE2
    CRYPTOPP_DISABLE_SSSE3
    CRYPTOPP_DISABLE_SHANI
    CRYPTOPP_DISABLE_AESNI
  )
  find_package(Threads REQUIRED)
  target_link_libraries(hcvault-core PRIVATE Threads::Threads ${CMAKE_DL_LIBS})
  # hide everything except the C API
  target_compile_options(hcvault-core PRIVATE -fvisibility=hidden -w)

  if(ANDROID)
    # Android 15+ supports 16 KB memory page sizes; shared libraries must be
    # aligned accordingly to load on such devices.
    target_link_options(hcvault-core PRIVATE "-Wl,-z,max-page-size=16384")
    # the NDK toolchain compiles with -g by default; strip the debug info
    # (exported vc_* symbols live in the dynamic table and survive stripping)
    target_link_options(hcvault-core PRIVATE "-Wl,--strip-all")

    # --- link the static C++ runtime explicitly ------------------------------
    # The NDK clang driver normally injects libc++ through driver-internal
    # linker arguments (-Bstatic -lc++ -Bdynamic, -l:libunwind.a and the
    # compiler-rt builtins archive), resolved via its own -L search paths.
    # On Windows hosts that injection silently disappears when the NDK is
    # installed in a directory whose path contains spaces (e.g.
    # "C:\Program Files (x86)\Android\AndroidNDK"): every libc++/libc++abi
    # symbol then comes out undefined at link time.
    # Passing the archives as absolute-path inputs routes them through the
    # same argument channel as --sysroot (which keeps working in that setup),
    # so the link no longer depends on the driver-internal -L/-l resolution.
    # On other hosts the driver still adds its own copies on top; duplicate
    # archives are harmless (members are only pulled for symbols that are
    # still undefined).
    set(_vcn_libarch "")
    set(_vcn_clangarch "")
    if(ANDROID_ABI STREQUAL "arm64-v8a")
      set(_vcn_libarch aarch64-linux-android)
      set(_vcn_clangarch aarch64)
    elseif(ANDROID_ABI STREQUAL "armeabi-v7a")
      set(_vcn_libarch arm-linux-androideabi)
      set(_vcn_clangarch arm)
    elseif(ANDROID_ABI STREQUAL "x86_64")
      set(_vcn_libarch x86_64-linux-android)
      set(_vcn_clangarch x86_64)
    else()
      set(_vcn_libarch i686-linux-android)
      set(_vcn_clangarch i686)
    endif()
    if(NOT ANDROID_TOOLCHAIN_ROOT)
      get_filename_component(_vcn_tc_root "${CMAKE_CXX_COMPILER}" DIRECTORY) # .../bin
      get_filename_component(_vcn_tc_root "${_vcn_tc_root}" DIRECTORY)       # toolchain root
      set(ANDROID_TOOLCHAIN_ROOT "${_vcn_tc_root}")
    endif()
    set(_vcn_stl_libs
      "${CMAKE_SYSROOT}/usr/lib/${_vcn_libarch}/libc++_static.a"
      "${CMAKE_SYSROOT}/usr/lib/${_vcn_libarch}/libc++abi.a")
    file(GLOB _vcn_resdir "${ANDROID_TOOLCHAIN_ROOT}/lib/clang/*/lib/linux")
    if(_vcn_resdir)
      list(GET _vcn_resdir 0 _vcn_resdir)
      if(EXISTS "${_vcn_resdir}/${_vcn_clangarch}/libunwind.a")
        list(APPEND _vcn_stl_libs "${_vcn_resdir}/${_vcn_clangarch}/libunwind.a")
      endif()
      if(EXISTS "${_vcn_resdir}/libclang_rt.builtins-${_vcn_clangarch}-android.a")
        list(APPEND _vcn_stl_libs
          "${_vcn_resdir}/libclang_rt.builtins-${_vcn_clangarch}-android.a")
      endif()
    endif()
    foreach(_vcn_rtlib ${_vcn_stl_libs})
      if(NOT EXISTS "${_vcn_rtlib}")
        message(FATAL_ERROR "Android C++ runtime archive not found: ${_vcn_rtlib}")
      endif()
    endforeach()
    target_link_libraries(hcvault-core PRIVATE ${_vcn_stl_libs})
    message(STATUS "hcvault-core: explicit Android C++ runtime archives "
                   "(${_vcn_libarch})")
  endif()
endif()

# ---------------------------------------------------------------------------
# output layout: runtimes/{rid}/native/hcvault-core.{dll,so}
# (this is the layout the HCVault.Native NuGet package expects)
# ---------------------------------------------------------------------------
set_target_properties(hcvault-core PROPERTIES
  LIBRARY_OUTPUT_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/runtimes/${VC_RID}/native
  RUNTIME_OUTPUT_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/runtimes/${VC_RID}/native
)

# MSVC: static CRT (/MT) so the DLL has no Visual C++ Redistributable dependency
if(MSVC)
  set_target_properties(hcvault-core PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>"
  )
endif()

# Multi-config generators (Visual Studio) append a per-config subdirectory to
# the output directory unless the per-config properties are set — flatten them
# so the DLL always lands in runtimes/{rid}/native/ itself.
foreach(_cfg IN LISTS CMAKE_CONFIGURATION_TYPES)
  string(TOUPPER "${_cfg}" _cfg_upper)
  set_target_properties(hcvault-core PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY_${_cfg_upper} ${CMAKE_CURRENT_SOURCE_DIR}/runtimes/${VC_RID}/native
    RUNTIME_OUTPUT_DIRECTORY_${_cfg_upper} ${CMAKE_CURRENT_SOURCE_DIR}/runtimes/${VC_RID}/native
  )
endforeach()

# convenience alias for a plain local build (no runtimes/ layout)
add_custom_target(native-lib-only
  COMMAND ${CMAKE_COMMAND} -E copy
    $<TARGET_FILE:hcvault-core>
    ${CMAKE_CURRENT_BINARY_DIR}/$<TARGET_FILE_NAME:hcvault-core>
)
