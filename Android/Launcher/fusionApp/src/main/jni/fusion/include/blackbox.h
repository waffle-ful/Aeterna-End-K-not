#ifndef FUSIONCORE_BLACKBOX_H
#define FUSIONCORE_BLACKBOX_H

#include <cstddef>
#include <cstdint>
#include <string>

// A small file-backed ring of recent log lines plus a slot for the state of a fatal signal.
// The file is a shared mapping, so what was written stays on disk however the process ends,
// including deaths that leave no crash report (out-of-memory kills, force stops).
//
// Layout, little endian: a BLACKBOX_HEADER_SIZE byte header (BlackboxHeader, zero padded)
// followed by BLACKBOX_RING_SIZE bytes of ring. The ring holds text records
// "<ms since open> <tid> <text>\n"; the next byte to write is at write_pos % ring_size.
// Inside the header the sections sit at fixed offsets: the crash slot at 64, the memory
// readings at 1024, the hang records at 2048 and the module table at 4096.

constexpr uint32_t BLACKBOX_HEADER_SIZE = 65536;
constexpr uint32_t BLACKBOX_RING_SIZE = 262144;
constexpr size_t BLACKBOX_MAX_FRAMES = 64;
constexpr size_t BLACKBOX_HANG_FRAMES = 32;
constexpr size_t BLACKBOX_HANG_SLOTS = 4;
constexpr size_t BLACKBOX_MAX_MODULES = 950;
constexpr size_t BLACKBOX_MODULE_NAME_SIZE = 32;

// Why a frame pointer walk ended.
constexpr uint32_t BLACKBOX_WALK_NOT_WALKED = 0;
constexpr uint32_t BLACKBOX_WALK_UNALIGNED_FP = 1;      // the frame pointer is not a multiple of 8
constexpr uint32_t BLACKBOX_WALK_UNREADABLE = 2;        // the frame record cannot be read
constexpr uint32_t BLACKBOX_WALK_NULL_RETURN = 3;       // the return address is 0
constexpr uint32_t BLACKBOX_WALK_FP_NOT_ABOVE = 4;      // the next frame pointer is not above this one (beyond the allowed stack switches)
constexpr uint32_t BLACKBOX_WALK_FULL = 5;              // the frame array is full

struct BlackboxCrash
{
    uint32_t stamped;       // 0 until a fatal signal claims the slot, then 1
    uint32_t signo;
    int32_t si_code;
    uint32_t tid;
    uint64_t fault_addr;
    uint64_t pc;
    uint64_t sp;
    uint64_t lr;
    uint64_t fp;
    uint64_t realtime_ms;
    uint32_t frame_count;
    uint32_t walk_stop;     // BLACKBOX_WALK_*
    uint64_t frames[BLACKBOX_MAX_FRAMES];
};

// The latest reading of the process's memory use, and the highest one so far.
struct BlackboxMemory
{
    uint32_t sample_count;
    int32_t oom_score_adj;      // INT32_MIN when it cannot be read
    uint64_t last_realtime_ms;
    uint64_t rss_kb;
    uint64_t rss_shared_kb;
    uint64_t mem_available_kb;  // 0 when it cannot be read
    uint64_t peak_rss_kb;
    uint64_t peak_realtime_ms;
    uint64_t reserved;
};

struct BlackboxHangSlot
{
    uint32_t seq;           // counts from 1, 0 marks an unused slot; record `seq` goes in slot (seq - 1) % 4
    uint32_t tid;
    uint64_t realtime_ms;
    uint64_t stalled_ms;
    uint64_t pc;
    uint64_t sp;
    uint64_t lr;
    uint64_t fp;
    uint32_t frame_count;
    uint32_t walk_stop;     // BLACKBOX_WALK_*
    uint64_t frames[BLACKBOX_HANG_FRAMES];
};

struct BlackboxHangs
{
    uint32_t total_count;
    uint32_t signo;
    uint64_t beat_count;
    uint64_t last_progress_realtime_ms;
    uint32_t paused;
    uint32_t reserved;
    BlackboxHangSlot slots[BLACKBOX_HANG_SLOTS];
};

// One executable range of a mapped file: a single mapping, or several that follow one another
// without a gap and map consecutive parts of the file.
struct BlackboxModule
{
    uint64_t begin;
    uint64_t end;
    uint64_t file_offset;   // of `begin`
    uint64_t load_base;     // where offset 0 of the file is mapped, or 0 when that is not known
    char name[BLACKBOX_MODULE_NAME_SIZE];   // file name without its directory, zero padded, cut when too long
};

struct BlackboxModules
{
    uint32_t count;
    uint32_t truncated;     // 1 when there were more ranges than entries
    BlackboxModule entries[BLACKBOX_MAX_MODULES];
};

struct BlackboxHeader
{
    char magic[8];              // "EKBB0002"
    uint32_t header_size;
    uint32_t ring_size;
    uint32_t pid;
    uint32_t reserved;
    uint64_t start_realtime_ms;
    uint64_t write_pos;         // total bytes ever written to the ring
    uint8_t padding[24];
    BlackboxCrash crash;
    uint8_t crash_padding[376];
    BlackboxMemory memory;
    uint8_t memory_padding[960];
    BlackboxHangs hangs;
    uint8_t hangs_padding[736];
    BlackboxModules modules;
};

static_assert(offsetof(BlackboxHeader, start_realtime_ms) == 24);
static_assert(offsetof(BlackboxHeader, write_pos) == 32);
static_assert(offsetof(BlackboxHeader, crash) == 64);
static_assert(offsetof(BlackboxCrash, fault_addr) == 16);
static_assert(offsetof(BlackboxCrash, frame_count) == 64);
static_assert(offsetof(BlackboxCrash, walk_stop) == 68);
static_assert(offsetof(BlackboxCrash, frames) == 72);
static_assert(offsetof(BlackboxHeader, memory) == 1024);
static_assert(offsetof(BlackboxMemory, oom_score_adj) == 4);
static_assert(offsetof(BlackboxMemory, last_realtime_ms) == 8);
static_assert(offsetof(BlackboxMemory, rss_kb) == 16);
static_assert(offsetof(BlackboxMemory, rss_shared_kb) == 24);
static_assert(offsetof(BlackboxMemory, mem_available_kb) == 32);
static_assert(offsetof(BlackboxMemory, peak_rss_kb) == 40);
static_assert(offsetof(BlackboxMemory, peak_realtime_ms) == 48);
static_assert(sizeof(BlackboxMemory) == 64);
static_assert(offsetof(BlackboxHeader, hangs) == 2048);
static_assert(offsetof(BlackboxHangs, signo) == 4);
static_assert(offsetof(BlackboxHangs, beat_count) == 8);
static_assert(offsetof(BlackboxHangs, last_progress_realtime_ms) == 16);
static_assert(offsetof(BlackboxHangs, paused) == 24);
static_assert(offsetof(BlackboxHangs, slots) == 32);
static_assert(offsetof(BlackboxHangSlot, tid) == 4);
static_assert(offsetof(BlackboxHangSlot, realtime_ms) == 8);
static_assert(offsetof(BlackboxHangSlot, stalled_ms) == 16);
static_assert(offsetof(BlackboxHangSlot, pc) == 24);
static_assert(offsetof(BlackboxHangSlot, sp) == 32);
static_assert(offsetof(BlackboxHangSlot, lr) == 40);
static_assert(offsetof(BlackboxHangSlot, fp) == 48);
static_assert(offsetof(BlackboxHangSlot, frame_count) == 56);
static_assert(offsetof(BlackboxHangSlot, walk_stop) == 60);
static_assert(offsetof(BlackboxHangSlot, frames) == 64);
static_assert(sizeof(BlackboxHangSlot) == 320);
static_assert(sizeof(BlackboxHangs) == 1312);
static_assert(offsetof(BlackboxHeader, modules) == 4096);
static_assert(offsetof(BlackboxModules, truncated) == 4);
static_assert(offsetof(BlackboxModules, entries) == 8);
static_assert(offsetof(BlackboxModule, end) == 8);
static_assert(offsetof(BlackboxModule, file_offset) == 16);
static_assert(offsetof(BlackboxModule, load_base) == 24);
static_assert(offsetof(BlackboxModule, name) == 32);
static_assert(sizeof(BlackboxModule) == 64);
static_assert(offsetof(BlackboxHeader, modules) + sizeof(BlackboxModules) <= BLACKBOX_HEADER_SIZE);
static_assert(sizeof(BlackboxHeader) <= BLACKBOX_HEADER_SIZE);

// Creates and maps blackbox-<pid>.bin in `directory`, after deleting all but the `keep` most
// recent files of earlier runs. Does nothing when a ring is already mapped.
void blackbox_open(const std::string &directory, size_t keep);

// Appends one record. Line breaks in `text` become spaces and the text is cut at 512 bytes.
// Safe from any thread; a no-op until the ring is mapped.
void blackbox_write(const char *text);

// As blackbox_write, with the text "<tag>: <message>".
void blackbox_write_tagged(const char *tag, const char *message);

// The mapped header, or nullptr while no ring is mapped. Reading this is async-signal-safe.
BlackboxHeader *blackbox_header();

// Rewrites the module table in the header from /proc/self/maps: the executable mappings of
// files, those of files under /data/ first. Code is loaded throughout the run, so this is called
// again at later points. Safe from any thread but not from a signal handler; a no-op until the
// ring is mapped.
void blackbox_record_modules();

#endif //FUSIONCORE_BLACKBOX_H
