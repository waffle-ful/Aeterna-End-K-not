#ifndef FUSIONCORE_CRASH_HANDLER_H
#define FUSIONCORE_CRASH_HANDLER_H

#include <cstdint>

// Installs handlers for the fatal signals (SIGSEGV, SIGBUS, SIGABRT, SIGILL, SIGFPE) that record
// the faulting state in the blackbox header and then pass the signal on to the handler that was
// installed before, so the system crash report is produced as usual.
//
// Must run before the managed runtimes start. A runtime that installs its handler afterwards is
// asked first and turns the faults it owns (such as a null dereference in managed code) into
// exceptions; only the faults it declines reach this handler.
void crash_handler_install();

// True when crash_handler_install found that this process's memory can be read by system call,
// which crash_handler_walk_frames depends on. Async-signal-safe.
bool crash_handler_can_walk_frames();

// Follows the frame pointer chain that starts at `fp` and stores the return addresses in
// `frames`, innermost first, after `pc` and `lr`. `frame_count` receives the number of entries
// stored and `walk_stop` the BLACKBOX_WALK_* reason the walk ended. The chain may belong to any
// thread of this process. Async-signal-safe; to be called only while
// crash_handler_can_walk_frames() is true. Stores nothing on architectures other than arm64.
void crash_handler_walk_frames(uint64_t pc, uint64_t lr, uint64_t fp, uint64_t *frames, uint32_t capacity,
                               uint32_t &frame_count, uint32_t &walk_stop);

#endif //FUSIONCORE_CRASH_HANDLER_H
