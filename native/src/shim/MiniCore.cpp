// MiniCore.cpp — instantiates the global Core/CoreDirect pointers used by
// VolumeCreator (its only real uses are RandomizeEncryptionAlgorithmKey and
// device-ownership helpers that MiniCore neutralizes).
#include "MiniCore.h"
#include "Core/Core.h"
#include "Volume/VolumePasswordCache.h"

namespace VeraCrypt {
    unique_ptr <CoreBase> Core (new MiniCore());
    unique_ptr <CoreBase> CoreDirect (new MiniCore());
}
