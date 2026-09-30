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

constexpr uint32_t BLACKBOX_HEADER_SIZE = 4096;
constexpr uint32_t BLACKBOX_RING_SIZE = 262144;
constexpr size_t BLACKBOX_MAX_FRAMES = 64;

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
    uint32_t reserved;
    uint64_t frames[BLACKBOX_MAX_FRAMES];
};

struct BlackboxHeader
{
    char magic[8];              // "EKBB0001"
    uint32_t header_size;
    uint32_t ring_size;
    uint32_t pid;
    uint32_t reserved;
    uint64_t start_realtime_ms;
    uint64_t write_pos;         // total bytes ever written to the ring
    uint8_t padding[24];
    BlackboxCrash crash;
};

static_assert(offsetof(BlackboxHeader, start_realtime_ms) == 24);
static_assert(offsetof(BlackboxHeader, write_pos) == 32);
static_assert(offsetof(BlackboxHeader, crash) == 64);
static_assert(offsetof(BlackboxCrash, fault_addr) == 16);
static_assert(offsetof(BlackboxCrash, frame_count) == 64);
static_assert(offsetof(BlackboxCrash, frames) == 72);
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

#endif //FUSIONCORE_BLACKBOX_H
