# HCVault build script (Windows PowerShell)
# -------------------------------------------------------------
# Usage:
#   .\build.ps1                     # native (host arch) + managed + tests (default)
#   .\build.ps1 -Arch x86           # native for another arch (also: x64, ARM64)
#   .\build.ps1 -Arch All           # native x64 + x86 + ARM64 (all Windows archs)
#   .\build.ps1 -Android            # cross-compile all 4 Android ABIs only (needs NDK)
#   .\build.ps1 -Arch All -Android -Pack   # release: Windows + Android + NuGet
#                                    # (an explicit -Arch adds the Windows builds
#                                    #  to an -Android run; plain -Android skips them)
#   .\build.ps1 -Pack               # additionally build both NuGet packages
#   .\build.ps1 -Clean              # remove build outputs first
#   .\build.ps1 -SkipNative / -SkipManaged
#
# The managed tests run as the host architecture and load hcvault-core.dll from
# native\runtimes\<host rid>\native (built by this script unless -SkipNative;
# with -SkipNative the library must come from an earlier run).
# On .NET 8 + .NET 10 installs both target frameworks are tested, otherwise
# only the ones with a runtime present.
#
# Android NDK lookup order: $env:ANDROID_NDK_HOME, Android Studio default
# (%LOCALAPPDATA%\Android\Sdk\ndk\<ver>), Visual Studio install
# (C:\Program Files\Android\Android SDK\ndk\<ver>). Requires Ninja (installed
# with the "C++ CMake tools" VS component, an Android SDK cmake package, a
# package manager shim (winget/choco/scoop/pip), or PATH). If none is found the
# script offers to download the official portable build into .tools\ninja
# (no admin rights, no system changes; suppress with -NoDownload).
param(
    # empty = build for the architecture this machine runs as
    [ValidateSet('x64', 'x86', 'ARM64', 'All', '')]
    [string]$Arch = '',
    [switch]$Android,
    [switch]$Pack,
    [switch]$Clean,
    [switch]$SkipNative,
    [switch]$SkipManaged,
    [switch]$NoDownload        # never fetch a portable ninja automatically
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$native = Join-Path $root 'native'

# Host architecture the managed tests will run as (also the default -Arch).
$procArch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
$hostArch = if ($procArch -eq 'Arm64') { 'ARM64' } elseif ($procArch -eq 'X86') { 'x86' } else { 'x64' }
$hostRid  = if ($procArch -eq 'Arm64') { 'win-arm64' } elseif ($procArch -eq 'X86') { 'win-x86' } else { 'win-x64' }
$archExplicit = -not [string]::IsNullOrEmpty($Arch)
if (-not $archExplicit) { $Arch = $hostArch }

# -Android alone builds only the Android ABIs; an explicit -Arch adds the
# Windows libraries ('-Arch All -Android' = every platform, used for packaging)
$buildWindows = (-not $Android) -or $archExplicit

# --------------------------------------------------------------------- clean
if ($Clean) {
    Write-Host '== cleaning build outputs ==' -ForegroundColor Cyan
    # every CMake preset has its own build dir (build-local, build-android-*, build-win-*)
    Get-ChildItem -Directory (Join-Path $native 'build*') -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force
    foreach ($d in @(
        (Join-Path $native 'bin'),
        (Join-Path $root 'managed\HCVault.Core\bin'),
        (Join-Path $root 'managed\HCVault.Core\obj'),
        (Join-Path $root 'app\HCVault.Explorer\bin'),
        (Join-Path $root 'app\HCVault.Explorer\obj'),
        (Join-Path $root 'tests\HCVault.Core.Tests\bin'),
        (Join-Path $root 'tests\HCVault.Core.Tests\obj')
    )) {
        if (Test-Path $d) { Remove-Item -Recurse -Force $d }
    }
}

# ------------------------------------------------------------------- helpers
function Find-Cmake {
    $c = Get-Command cmake -ErrorAction SilentlyContinue
    if ($c) { return 'cmake' }
    # Visual Studio bundles CMake with the "C++ CMake tools" component
    $vsCmake = Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio' `
        | Get-ChildItem -Directory -ErrorAction SilentlyContinue `
        | ForEach-Object { Join-Path $_.FullName 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe' } `
        | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($vsCmake) { return $vsCmake }
    throw 'cmake not found. Install the "Desktop development with C++" workload (C++ CMake tools) or add cmake to PATH.'
}

function Find-VsGenerator {
    # CMake generator name of the newest Visual Studio that has C++ tools.
    # The CMakePresets.json Windows presets hardcode the VS 2026 generator;
    # detecting it here keeps build.ps1 working on machines (and CI runners)
    # that only have an older Visual Studio installed.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        throw 'Visual Studio not found. Install VS 2022 or 2026 with the "Desktop development with C++" workload.'
    }
    $ver = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationVersion
    if (-not $ver) {
        throw 'No Visual Studio with C++ tools found. Install the "Desktop development with C++" workload.'
    }
    $major = ($ver -split '\.')[0]
    $years = @{ '17' = '2022'; '18' = '2026' }
    if (-not $years.ContainsKey($major)) {
        throw "Visual Studio $ver is not supported by this script yet - add its generator to build.ps1 and CMakePresets.json."
    }
    return "Visual Studio $major $($years[$major])"
}

function Find-Ndk {
    if ($env:ANDROID_NDK_HOME -and (Test-Path (Join-Path $env:ANDROID_NDK_HOME 'build\cmake\android.toolchain.cmake'))) {
        return $env:ANDROID_NDK_HOME
    }
    foreach ($base in @(
        (Join-Path $env:LOCALAPPDATA 'Android\Sdk\ndk'),                    # Android Studio
        (Join-Path ${env:ProgramFiles(x86)} 'Android\AndroidNDK'),           # VS standalone NDK (r27 default)
        (Join-Path ${env:ProgramFiles(x86)} 'Android\Android SDK\ndk'),     # Visual Studio
        (Join-Path ${env:ProgramFiles} 'Android\Android SDK\ndk'),          # Visual Studio (older layout)
        'C:\Android\android-sdk\ndk'
    )) {
        if (Test-Path $base) {
            $ndk = Get-ChildItem -Directory $base | Sort-Object Name -Descending | Select-Object -First 1
            if ($ndk -and (Test-Path (Join-Path $ndk.FullName 'build\cmake\android.toolchain.cmake'))) {
                return $ndk.FullName
            }
        }
    }
    throw @'
Android NDK not found. Install it via either:
  - Android Studio: Settings > Languages & Frameworks > Android SDK > SDK Tools > NDK (side by side)
    (default location: %LOCALAPPDATA%\Android\Sdk\ndk\<version>)
  - Visual Studio: "Mobile development with .NET" workload (NDK is under
    C:\Program Files\Android\Android SDK\ndk\<version>)
  - then set $env:ANDROID_NDK_HOME = "<that folder>" and re-run.
'@
}

function Find-Ninja {
    # returns the DIRECTORY containing ninja.exe, or $null when already on PATH

    # 0. portable copy fetched by an earlier run of this script
    $local = Join-Path $root '.tools\ninja\ninja.exe'
    if (Test-Path $local) { return (Split-Path $local) }

    # 1. already on PATH
    if (Get-Command ninja -ErrorAction SilentlyContinue) { return $null }

    # 2. well-known install locations (wildcards allowed; first hit wins)
    $candidates = @()
    if (${env:ProgramFiles}) {
        # Visual Studio "C++ CMake tools for Windows"
        $candidates += Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio\*\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'
    }
    if ($env:LOCALAPPDATA) {
        $candidates += Join-Path $env:LOCALAPPDATA 'Android\Sdk\cmake\*\bin\ninja.exe'      # Android Studio SDK (cmake package)
        $candidates += Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\ninja.exe'        # winget
    }
    if (${env:ProgramFiles(x86)}) {
        $candidates += Join-Path ${env:ProgramFiles(x86)} 'Android\Android SDK\cmake\*\bin\ninja.exe'
    }
    foreach ($sdkVar in 'ANDROID_SDK_ROOT', 'ANDROID_HOME') {
        if ([Environment]::GetEnvironmentVariable($sdkVar)) {
            $candidates += Join-Path ([Environment]::GetEnvironmentVariable($sdkVar)) 'cmake\*\bin\ninja.exe'
        }
    }
    if ($env:APPDATA)  { $candidates += Join-Path $env:APPDATA 'Python\Python*\Scripts\ninja.exe' }   # pip --user
    if ($env:USERPROFILE) { $candidates += Join-Path $env:USERPROFILE 'scoop\shims\ninja.exe' }        # scoop
    $candidates += 'C:\ProgramData\chocolatey\bin\ninja.exe'                                          # choco

    foreach ($pattern in $candidates) {
        $hit = Resolve-Path -Path $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) {
            Write-Host "  ninja found: $($hit.Path)" -ForegroundColor DarkGray
            return (Split-Path $hit.Path)
        }
    }

    # 3. offer the official portable build -> <repo>\.tools\ninja (no admin, no PATH changes)
    if (-not $NoDownload) {
        $answer = Read-Host 'ninja not found - download the official portable build into .tools\ninja? (Y/n)'
        if ($answer -eq '' -or $answer -match '^[Yy]') {
            $dir = Join-Path $root '.tools\ninja'
            $zip = Join-Path ([System.IO.Path]::GetTempPath()) 'ninja-win.zip'
            Write-Host '  downloading https://github.com/ninja-build/ninja/releases (ninja-win.zip) ...'
            try {
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                Invoke-WebRequest -Uri 'https://github.com/ninja-build/ninja/releases/latest/download/ninja-win.zip' `
                    -OutFile $zip -UseBasicParsing
                Expand-Archive -Path $zip -DestinationPath $dir -Force
            }
            finally { Remove-Item $zip -ErrorAction SilentlyContinue }
            if (Test-Path (Join-Path $dir 'ninja.exe')) {
                Write-Host "  ninja ready: $dir (delete the folder to undo)" -ForegroundColor Green
                return $dir
            }
        }
    }

    throw @'
ninja not found - it is required by the Android CMake presets. Install via any of:
  winget install Ninja-build.Ninja        (then reopen the terminal)
  choco install ninja  /  scoop install ninja
  pip install ninja
  Visual Studio Installer: "Desktop development with C++" -> "C++ CMake tools for Windows"
  Android Studio SDK Manager: SDK Tools tab -> "NDK (side by side)" + "CMake" (bundles ninja)
'@
}

# ------------------------------------------------- PE architecture helpers
# IMAGE_FILE_MACHINE_* values, keyed by the lowercase RID arch (win-x64 -> x64)
$script:PeMachineOf = @{ 'x64' = 0x8664; 'x86' = 0x014C; 'arm64' = 0xAA64 }
$script:PeMachineNames = @{ 0x8664 = 'x64'; 0x014C = 'x86'; 0xAA64 = 'ARM64'; 0x01C0 = 'ARM'; 0x0200 = 'Itanium' }

function Get-PeMachine {
    # IMAGE_FILE_MACHINE value of a PE file, or $null when unreadable / not PE
    param([string]$Path)
    try {
        $fs = [IO.File]::OpenRead($Path)
        try {
            $buf = New-Object byte[] 64
            if ($fs.Read($buf, 0, 64) -lt 64) { return $null }
            if ($buf[0] -ne 0x4D -or $buf[1] -ne 0x5A) { return $null }        # 'MZ'
            $peOff = [BitConverter]::ToInt32($buf, 0x3C)
            if ($peOff -lt 0 -or ($peOff + 6) -gt $fs.Length) { return $null }
            $fs.Position = $peOff
            $hdr = New-Object byte[] 6
            if ($fs.Read($hdr, 0, 6) -lt 6) { return $null }
            if ($hdr[0] -ne 0x50 -or $hdr[1] -ne 0x45) { return $null }        # 'PE'
            return [int][BitConverter]::ToUInt16($hdr, 4)
        } finally { $fs.Dispose() }
    } catch { return $null }
}

function Get-PeMachineName([int]$Machine) {
    if ($script:PeMachineNames.ContainsKey($Machine)) { return $script:PeMachineNames[$Machine] }
    return ('unknown machine 0x{0:X4}' -f $Machine)
}

function Test-RuntimesLayout {
    # verifies every built hcvault-core.dll matches its runtimes directory
    # (win-x64 must hold an x64 PE, ...). Returns an array of problem strings.
    $problems = @()
    # forward slashes work on Windows PowerShell too, and keep the check testable anywhere
    Get-ChildItem -Path (Join-Path $native 'runtimes/win-*/native/hcvault-core.dll') -ErrorAction SilentlyContinue |
        ForEach-Object {
            $ridArch = $_.Directory.Parent.Name.Substring(4).ToLowerInvariant()   # win-x64 -> x64
            $expected = $script:PeMachineOf[$ridArch]
            $machine = Get-PeMachine $_.FullName
            if ($null -eq $machine) {
                $problems += "$($_.FullName) is not a readable PE file"
            }
            elseif ($machine -ne $expected) {
                $problems += "$($_.FullName) is a $(Get-PeMachineName $machine) binary but sits in a win-$ridArch folder (stale or corrupted build - delete native\runtimes and rebuild)"
            }
        }
    return ,$problems
}

# ------------------------------------------------------------- native builds
if (-not $SkipNative) {
    $cmake = Find-Cmake

    # cmake --preset reads CMakePresets.json from the CURRENT directory
    Push-Location $native
    try {
        if ($buildWindows) {
            # 'All' = every architecture this Windows host can build
            $archs = if ($Arch -eq 'All') { @('x64', 'x86', 'ARM64') } else { @($Arch) }
            $generator = Find-VsGenerator
            $platOf = @{ 'x64' = 'x64'; 'x86' = 'Win32'; 'ARM64' = 'ARM64' }   # VS generator platform names
            Write-Host "  generator: $generator" -ForegroundColor DarkGray
            foreach ($a in $archs) {
                $rid = $a.ToLowerInvariant()      # runtimes directory names are lowercase
                $dir = "build-win-$rid"
                Write-Host "== native: windows-$rid-release ==" -ForegroundColor Cyan
                # -G/-A passed explicitly instead of the Windows presets: the
                # presets hardcode the VS 2026 generator, this works with any
                # installed Visual Studio (the other settings are identical)
                & $cmake -S . -B $dir -G $generator -A $platOf[$a] -D CMAKE_CONFIGURATION_TYPES=Release
                if ($LASTEXITCODE) { exit 1 }
                & $cmake --build $dir --config Release --parallel
                if ($LASTEXITCODE) { exit 1 }

                # verify the DLL really is the requested architecture - a wrong-arch
                # file here would later surface as a cryptic 0x8007000B load error
                $dll = Join-Path $native "runtimes/win-$rid/native/hcvault-core.dll"
                $machine = Get-PeMachine $dll
                if ($null -eq $machine) {
                    throw "$dll was built but is not a readable PE file."
                }
                if ($machine -ne $script:PeMachineOf[$rid]) {
                    throw "$dll is a $(Get-PeMachineName $machine) binary, but $a was requested - the output layout is corrupted. Delete the native\runtimes folder and run .\build.ps1 again."
                }
                Write-Host "  -> native\runtimes\win-$rid\native\hcvault-core.dll ($(Get-PeMachineName $machine))" -ForegroundColor Green
                # each preset configures into build-win-<arch>; drop it to keep the tree clean
                Remove-Item -Recurse -Force (Join-Path $native "build-win-$rid") -ErrorAction SilentlyContinue
            }
        }

        if ($Android) {
            $ndk = Find-Ndk
            $env:ANDROID_NDK_HOME = $ndk
            Write-Host "== Android NDK: $ndk ==" -ForegroundColor Cyan
            if ($ndk -match ' ') {
                # The CMakeLists links the static C++ runtime archives by absolute
                # path, so a spaced NDK path works. (Without that workaround the
                # NDK clang driver silently drops libc++ from the link and every
                # std::__ndk1/__cxa symbol comes out undefined.)
                Write-Host '  note: NDK path contains spaces - handled by the explicit C++ runtime link in CMakeLists.txt.' -ForegroundColor DarkYellow
            }
            $ninjaDir = Find-Ninja          # may throw with install guidance
            if ($ninjaDir) { $env:PATH = "$ninjaDir;$env:PATH" }

            foreach ($abi in 'arm64', 'x64', 'arm', 'x86') {
                $preset = "android-$abi-release"
                Write-Host "== native: $preset ==" -ForegroundColor Cyan
                & $cmake --preset $preset
                if ($LASTEXITCODE) { exit 1 }
                & $cmake --build --preset $preset --parallel
                if ($LASTEXITCODE) { exit 1 }
                Remove-Item -Recurse -Force (Join-Path $native "build-android-$abi") -ErrorAction SilentlyContinue
            }
            Write-Host "  -> native\runtimes\android-*\native\libhcvault-core.so (all 4 ABIs)" -ForegroundColor Green
        }
    }
    finally {
        Pop-Location
    }
}

# ----------------------------------------------------------- managed + tests
if (-not $SkipManaged) {
    Write-Host "== managed: build + tests ==" -ForegroundColor Cyan

    # The managed tests load hcvault-core for the architecture the dotnet test
    # process runs as. Look for that library first (PowerShell's own
    # architecture is a good guess for it); if it is missing, fall back to
    # whatever was built and let the test loader print a precise error on an
    # architecture mismatch. With no library at all the tests are skipped with
    # a note - only the tests need it, the managed and WPF builds do not.
    $testLib = Join-Path $native "runtimes\$hostRid\native\hcvault-core.dll"
    $runTests = $true
    if (-not (Test-Path $testLib)) {
        $built = @(Get-ChildItem -Path (Join-Path $native 'runtimes\win-*\native\hcvault-core.dll') -ErrorAction SilentlyContinue)
        if ($built.Count -gt 0) {
            $testLib = $built[0].FullName
            $builtRid = $built[0].Directory.Parent.Name
            Write-Host "  note: hcvault-core.dll was only built for $builtRid, but this machine appears to run $hostRid." -ForegroundColor Yellow
            Write-Host "        Trying it anyway; on an architecture mismatch rebuild with:  .\build.ps1 -Arch $hostArch" -ForegroundColor Yellow
        }
        elseif ($SkipNative -or -not $buildWindows) {
            Write-Host '  no hcvault-core.dll under native\runtimes - the managed tests need it, tests skipped.' -ForegroundColor Yellow
            Write-Host '  Run .\build.ps1 (without -SkipNative) to build it; the managed and WPF builds below do not need it.' -ForegroundColor Yellow
            $runTests = $false
        }
        else {
            throw 'The native build appeared to succeed, but no hcvault-core.dll was found under native\runtimes. This is an output-layout bug - please report it.'
        }
    }

    # a DLL whose architecture does not match its runtimes folder means a stale
    # or corrupted build - report it instead of letting the tests fail obscurely
    $layoutProblems = Test-RuntimesLayout
    if ($layoutProblems.Count -gt 0) {
        Write-Host '  corrupted native\runtimes layout detected:' -ForegroundColor Yellow
        $layoutProblems | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
        Write-Host '  The managed tests are skipped. Delete the native\runtimes folder and rebuild with .uild.ps1.' -ForegroundColor Yellow
        $runTests = $false
    }

    dotnet build (Join-Path $root 'HCVault.slnx') -c Release
    if ($LASTEXITCODE) { exit 1 }

    if ($runTests) {
        $env:VCNATIVE_HOME = $testLib
        Write-Host "  tests use: $testLib" -ForegroundColor DarkGray

        # one test pass per target framework; legs without an installed runtime are skipped
        $runtimes = (dotnet --list-runtimes) -join "`n"
        foreach ($tfm in @('net8.0', 'net10.0')) {
            $major = $tfm.Substring(3)
            if ($runtimes -notmatch "Microsoft\.NETCore\.App $major\.") {
                Write-Host "  skipping $tfm tests (that .NET runtime is not installed)" -ForegroundColor Yellow
                continue
            }
            dotnet run --project (Join-Path $root 'tests\HCVault.Core.Tests') -c Release --framework $tfm
            if ($LASTEXITCODE) { Write-Host "MANAGED TESTS FAILED ($tfm)" -ForegroundColor Red; exit 1 }
        }
    }

    dotnet build (Join-Path $root 'app\HCVault.Explorer') -c Release
    if ($LASTEXITCODE) { exit 1 }
    if ($runTests) {
        Write-Host '  -> managed + WPF app built, managed tests passed' -ForegroundColor Green
    } else {
        Write-Host '  -> managed + WPF app built (managed tests skipped: no native library)' -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------------- pack
if ($Pack) {
    Write-Host "== NuGet packages ==" -ForegroundColor Cyan

    # every DLL must match its runtimes folder - refuse to ship a mismatched set
    $layoutProblems = Test-RuntimesLayout
    if ($layoutProblems.Count -gt 0) {
        throw "Corrupted native\runtimes layout (delete the folder and rebuild):`n$($layoutProblems -join "`n")"
    }

    # HCVault.Native ships prebuilt libraries - refuse to pack a partial set
    $missing = @()
    foreach ($rid in 'win-x64', 'win-x86', 'win-arm64') {
        if (-not (Test-Path (Join-Path $native "runtimes\$rid\native\hcvault-core.dll"))) { $missing += $rid }
    }
    foreach ($abi in 'arm64', 'arm', 'x64', 'x86') {
        if (-not (Test-Path (Join-Path $native "runtimes\android-$abi\native\libhcvault-core.so"))) { $missing += "android-$abi" }
    }
    if ($missing.Count -gt 0) {
        throw "No prebuilt native libraries for: $($missing -join ', '). The package ships binaries, so build them first: .\build.ps1 -Arch All -Android (and see docs for linux-x64)."
    }

    dotnet pack (Join-Path $native 'HCVault.Native.pack.csproj') -c Release
    if ($LASTEXITCODE) { exit 1 }
    dotnet pack (Join-Path $root 'managed\HCVault.Core') -c Release
    if ($LASTEXITCODE) { exit 1 }
    Write-Host '  -> native\bin\Release\*.nupkg + managed\...\bin\Release\*.nupkg' -ForegroundColor Green
}

Write-Host 'BUILD OK' -ForegroundColor Green
