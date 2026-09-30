#ifndef FUSIONCORE_CRASH_MAPS_H
#define FUSIONCORE_CRASH_MAPS_H

#include <cstddef>
#include <cstdint>
#include <string>

// Symbol tables kept next to the saved crash reports. A tombstone only carries raw addresses
// for JIT-compiled managed code and for libil2cpp; these files turn them back into names.

// Deletes the oldest CoreCLR perf maps in `directory`, keeping the `keep` most recent of each kind.
void crash_maps_prune_perf_maps(const std::string &directory, size_t keep);

// Writes `il2cpp-methods.map` into `directory`: every managed method compiled into libil2cpp,
// as its offset from the library base. The file is tied to one libil2cpp build through the size
// of `original_library_path` and is left alone while that size still matches.
// Must be called on a thread attached to the il2cpp domain, after il2cpp_init has returned.
void crash_maps_write_il2cpp_methods(const std::string &directory,
                                     const std::string &original_library_path,
                                     uintptr_t library_base,
                                     size_t library_size);

#endif //FUSIONCORE_CRASH_MAPS_H
