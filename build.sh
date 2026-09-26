#!/usr/bin/env bash
# HCVault build script (Linux / macOS / WSL)
# -------------------------------------------------------------
# Usage:
#   ./build.sh                  # native linux-x64 + managed + tests (default)
#   ./build.sh --android        # cross-compile all 4 Android ABIs only (needs NDK)
#   ./build.sh --all            # everything this host can build (native + Android)
#   ./build.sh --pack           # additionally build both NuGet packages
#   ./build.sh --all --pack     # release: every platform + NuGet packages
#   ./build.sh --clean          # remove build outputs first
#   ./build.sh --skip-native | --skip-managed
#
# The managed tests run as the host architecture and load libhcvault-core.so
# from native/runtimes/<host rid>/native (built by this script unless
# --skip-native; with --skip-native the library must come from an earlier run).
# On .NET 8 + .NET 10 installs both target frameworks are tested, otherwise
# only the ones with a runtime present.
#
# Ninja (needed for --android): probed on PATH, ~/.local/bin and pip/pipx
# locations; if missing, the script offers to fetch the official portable
# build into .tools/ninja (VCN_NO_DOWNLOAD=1 to disable).
#
# Android NDK lookup order: $ANDROID_NDK_HOME, ~/Android/Sdk/ndk/<newest>,
# /opt/android-ndk*. Ninja is required for the Android presets.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
NATIVE="$ROOT/native"
ARCH=linux
DO_LINUX=1 DO_ANDROID=0 DO_PACK=0 DO_CLEAN=0 SKIP_NATIVE=0 SKIP_MANAGED=0

# ELF machine code this host builds/runs (used to verify the built libraries)
case "$(uname -m)" in
    aarch64|arm64) WANT_MACH=183 ;;   # EM_AARCH64
    i?86)          WANT_MACH=3   ;;   # EM_386
    armv*)         WANT_MACH=40  ;;   # EM_ARM
    *)             WANT_MACH=62  ;;   # EM_X86_64
esac

elf_machine() {   # prints the ELF e_machine of a file, or nothing
    od -An -tu2 -j18 -N2 -- "$1" 2>/dev/null | tr -d '[:space:]'
}

machine_name() {
    case "$1" in
        3) echo i386 ;; 40) echo ARM ;; 62) echo x86-64 ;; 183) echo AArch64 ;;
        *) echo "machine $1" ;;
    esac
}

for arg in "$@"; do
    case "$arg" in
        --android)       DO_ANDROID=1; DO_LINUX=0 ;;
        --all)           DO_ANDROID=1 ;;   # native + all Android ABIs + managed
        --pack)          DO_PACK=1 ;;
        --clean)         DO_CLEAN=1 ;;
        --skip-native)   SKIP_NATIVE=1 ;;
        --skip-managed)  SKIP_MANAGED=1 ;;
        *) echo "unknown option: $arg (use --android --pack --clean --skip-native --skip-managed)"; exit 2 ;;
    esac
done

# ------------------------------------------------------------------------ clean
if [ "$DO_CLEAN" = 1 ]; then
    echo '== cleaning build outputs =='
    rm -rf "$NATIVE"/build* "$NATIVE/bin" \
           "$ROOT/managed/HCVault.Core/bin" "$ROOT/managed/HCVault.Core/obj" \
           "$ROOT/app/HCVault.Explorer/bin" "$ROOT/app/HCVault.Explorer/obj" \
           "$ROOT/tests/HCVault.Core.Tests/bin" "$ROOT/tests/HCVault.Core.Tests/obj"
fi

# ----------------------------------------------------------------------- helper
find_ndk() {
    if [ -n "${ANDROID_NDK_HOME:-}" ] && [ -f "$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake" ]; then
        printf '%s\n' "$ANDROID_NDK_HOME"; return 0
    fi
    local base newest
    for base in "$HOME/Android/Sdk/ndk" "/opt"; do
        [ -d "$base" ] || continue
        newest="$(ls -d "$base"/android-ndk-* 2>/dev/null | sort -V | tail -1 || true)"
        if [ -n "$newest" ] && [ -f "$newest/build/cmake/android.toolchain.cmake" ]; then
            printf '%s\n' "$newest"; return 0
        fi
    done
    echo "Android NDK not found. Install r26/r27 (https://developer.android.com/ndk/downloads)," >&2
    echo "set ANDROID_NDK_HOME, or place it under ~/Android/Sdk/ndk/ or /opt/." >&2
    return 1
}

find_ninja() {
    # prints the directory containing ninja, nothing if on PATH, fails with guidance
    command -v ninja >/dev/null && return 0
    local d
    for d in "$ROOT/.tools/ninja" "$HOME/.local/bin"; do
        [ -x "$d/ninja" ] && { printf '%s\n' "$d"; return 0; }
    done
    local hit
    hit="$(ls "$HOME"/.pyenv/versions/*/bin/ninja "$HOME"/Library/Python/*/bin/ninja 2>/dev/null | head -1 || true)"
    if [ -n "$hit" ]; then printf '%s\n' "$(dirname "$hit")"; return 0; fi

    if [ "${VCN_NO_DOWNLOAD:-0}" != "1" ]; then
        printf 'ninja not found - download the official portable build into .tools/ninja? [Y/n] '
        local ans
        read -r ans || ans=y
        if [ -z "$ans" ] || [ "$ans" = y ] || [ "$ans" = Y ]; then
            d="$ROOT/.tools/ninja"; mkdir -p "$d"
            local url='https://github.com/ninja-build/ninja/releases/latest/download/ninja-linux.zip'
            echo "  downloading $url ..."
            if curl -fsSL "$url" -o "$d/ninja.zip" && unzip -o -q "$d/ninja.zip" -d "$d" && chmod +x "$d/ninja"; then
                rm -f "$d/ninja.zip"
                echo "  ninja ready: $d (delete the folder to undo)"
                printf '%s\n' "$d"; return 0
            fi
            rm -rf "$d"
            echo "  download failed" >&2
        fi
    fi
    echo "ninja not found - install it via: apt install ninja-build / brew install ninja / pip install ninja" >&2
    return 1
}

# ---------------------------------------------------------------- native build
if [ "$SKIP_NATIVE" = 0 ] && [ "$DO_LINUX" = 1 ]; then
    echo '== native: linux-x64 =='
    cmake -S "$NATIVE" -B "$NATIVE/build-local" -DCMAKE_BUILD_TYPE=Release
    cmake --build "$NATIVE/build-local" --parallel
    echo "  -> native/runtimes/linux-x64/native/libhcvault-core.so"

    # verify the built library really is this host's architecture
    SO="$(find "$NATIVE/runtimes" -path '*/linux-*/native/libhcvault-core.so' 2>/dev/null | head -n1)"
    GOT="$(elf_machine "$SO")"
    if [ -n "$GOT" ] && [ "$GOT" != "$WANT_MACH" ]; then
        echo "$SO is a $(machine_name "$GOT") binary, but this machine builds $(machine_name "$WANT_MACH") - output layout corrupted." >&2
        echo "Delete the native/runtimes folder and rebuild." >&2
        exit 1
    fi
fi

if [ "$SKIP_NATIVE" = 0 ] && [ "$DO_ANDROID" = 1 ]; then
    NDK="$(find_ndk)" || exit 1
    export ANDROID_NDK_HOME="$NDK"
    echo "== Android NDK: $NDK =="
    case "$NDK" in
        *\ * ) echo '  note: NDK path contains spaces - handled by the explicit C++ runtime link in CMakeLists.txt.' ;;
    esac
    NINJA_DIR="$(find_ninja)" || exit 1
    [ -n "$NINJA_DIR" ] && export PATH="$NINJA_DIR:$PATH"
    cd "$NATIVE"   # cmake --preset reads CMakePresets.json from the cwd
    for abi in arm64 x64 arm x86; do
        echo "== native: android-$abi-release =="
        cmake --preset "android-$abi-release"
        cmake --build --preset "android-$abi-release" --parallel
        rm -rf "$NATIVE/build-android-$abi"
    done
    echo '  -> native/runtimes/android-{arm64,x64,arm,x86}/native/libhcvault-core.so'
fi

# -------------------------------------------------------- managed + tests
if [ "$SKIP_MANAGED" = 0 ]; then
    echo '== managed: build + tests =='

    # The managed tests load hcvault-core for the architecture this process
    # runs as. Look for that library first; if it is missing, fall back to
    # whatever was built. With no library at all the tests are skipped with a
    # note - only the tests need it, the managed build does not.
    case "$(uname -m)" in
        aarch64|arm64) RID=linux-arm64 ;;
        i?86)          RID=linux-x86 ;;
        armv*)         RID=linux-arm ;;
        *)             RID=linux-x64 ;;
    esac
    LIB="$NATIVE/runtimes/$RID/native/libhcvault-core.so"
    RUN_TESTS=1
    if [ ! -f "$LIB" ]; then
        # fall back to another linux build (never an Android .so - bionic
        # libraries cannot load into a glibc process anyway)
        BUILT="$(find "$NATIVE/runtimes" -path '*/linux-*/native/libhcvault-core.so' 2>/dev/null | head -n1)"
        if [ -n "$BUILT" ]; then
            BUILT_RID="$(basename "$(dirname "$(dirname "$BUILT")")")"
            echo "  note: libhcvault-core.so was only built for $BUILT_RID, but this machine runs $RID - trying it anyway."
            LIB="$BUILT"
        elif [ "$SKIP_NATIVE" = 1 ] || [ "$DO_LINUX" = 0 ]; then
            echo "  no libhcvault-core.so for $RID - the managed tests need it, tests skipped."
            echo "  Run ./build.sh (without --skip-native) to build it; the managed build does not need it."
            RUN_TESTS=0
        else
            echo "native build finished but $LIB was not found - output-layout bug, please report." >&2
            exit 1
        fi
    fi
    GOT="$(elf_machine "$LIB")"
    if [ -n "$GOT" ] && [ "$GOT" != "$WANT_MACH" ]; then
        echo "  note: $LIB is a $(machine_name "$GOT") binary, but this machine runs $(machine_name "$WANT_MACH") - tests skipped."
        RUN_TESTS=0
    fi
    if [ "$RUN_TESTS" = 1 ]; then
        export VCNATIVE_HOME="$LIB"
        echo "  tests use: $LIB"

        # one test pass per target framework; legs without an installed runtime are skipped
        RUNTIMES="$(dotnet --list-runtimes)"
        for tfm in net8.0 net10.0; do
            major="${tfm#net}"
            if ! printf '%s\n' "$RUNTIMES" | grep -q "Microsoft.NETCore.App ${major}"; then
                echo "  skipping $tfm tests (that .NET runtime is not installed)"
                continue
            fi
            if ! dotnet run --project "$ROOT/tests/HCVault.Core.Tests" -c Release --framework "$tfm"; then
                echo "MANAGED TESTS FAILED ($tfm)"
                exit 1
            fi
        done
    fi
    if [ "$RUN_TESTS" = 1 ]; then
        echo '  -> managed library built, managed tests passed'
    else
        echo '  -> managed library built, managed tests skipped (no native library)'
    fi
fi

# ------------------------------------------------------------------------ pack
if [ "$DO_PACK" = 1 ]; then
    echo '== NuGet packages =='
    # HCVault.Native ships prebuilt libraries - require at least the host set
    miss=0
    [ -f "$NATIVE/runtimes/linux-x64/native/libhcvault-core.so" ] || { echo "linux-x64 library missing (this run builds it if --skip-native is off)"; miss=1; }
    [ -f "$NATIVE/runtimes/android-arm64/native/libhcvault-core.so" ] || { echo "android libraries missing - run ./build.sh --android first"; miss=1; }
    [ "$miss" = 0 ] || exit 1
    dotnet pack "$NATIVE/HCVault.Native.pack.csproj" -c Release
    dotnet pack "$ROOT/managed/HCVault.Core" -c Release
    echo '  -> native/bin/Release/*.nupkg + managed/HCVault.Core/bin/Release/*.nupkg'
fi

echo 'BUILD OK'
