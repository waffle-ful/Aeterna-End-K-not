#include <blackbox.h>
#include <android/log.h>
#include <fcntl.h>
#include <sys/mman.h>
#include <unistd.h>
#include <algorithm>
#include <atomic>
#include <cerrno>
#include <cstdio>
#include <cstring>
#include <ctime>
#include <filesystem>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

// Diagnostics here go straight to logcat: the shared log() feeds this ring.
#define TAG "FusionBlackbox"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, TAG, __VA_ARGS__)

namespace fs = std::filesystem;

namespace
{
    constexpr size_t MAX_TEXT = 512;
    constexpr std::string_view FILE_PREFIX = "blackbox-";
    constexpr std::string_view FILE_SUFFIX = ".bin";

    std::atomic<BlackboxHeader *> header{nullptr};
    char *ring = nullptr;
    uint64_t start_monotonic_ms = 0;

    uint64_t clock_ms(clockid_t clock)
    {
        timespec ts{};
        clock_gettime(clock, &ts);
        return static_cast<uint64_t>(ts.tv_sec) * 1000 + static_cast<uint64_t>(ts.tv_nsec) / 1000000;
    }

    void prune(const fs::path &directory, size_t keep)
    {
        std::error_code ec;
        std::vector<std::pair<fs::file_time_type, fs::path>> files;
        for (const auto &entry : fs::directory_iterator(directory, ec))
        {
            const std::string name = entry.path().filename().string();
            if (!name.starts_with(FILE_PREFIX) || !name.ends_with(FILE_SUFFIX))
            {
                continue;
            }

            std::error_code time_ec;
            auto written = entry.last_write_time(time_ec);
            if (!time_ec)
            {
                files.emplace_back(written, entry.path());
            }
        }

        if (files.size() <= keep)
        {
            return;
        }

        std::sort(files.begin(), files.end(), [](const auto &a, const auto &b) { return a.first > b.first; });
        for (size_t i = keep; i < files.size(); i++)
        {
            std::error_code remove_ec;
            fs::remove(files[i].second, remove_ec);
        }
    }

    // Copies into the ring at `position`, continuing from the start when the end is reached.
    void ring_copy(uint64_t position, const char *data, size_t length)
    {
        const size_t offset = position % BLACKBOX_RING_SIZE;
        const size_t first = std::min<size_t>(length, BLACKBOX_RING_SIZE - offset);
        memcpy(ring + offset, data, first);
        if (first < length)
        {
            memcpy(ring, data + first, length - first);
        }
    }

    void write_record(const char *tag, const char *text)
    {
        BlackboxHeader *mapped = header.load(std::memory_order_acquire);
        if (!mapped || !text)
        {
            return;
        }

        char record[MAX_TEXT + 64];
        const uint64_t elapsed = clock_ms(CLOCK_MONOTONIC) - start_monotonic_ms;
        int length = snprintf(record, 48, "%llu %d ", static_cast<unsigned long long>(elapsed), gettid());
        if (length < 0)
        {
            return;
        }

        const size_t text_start = static_cast<size_t>(length);
        size_t used = 0;
        auto append = [&](const char *part)
        {
            for (; *part && used < MAX_TEXT; part++, used++)
            {
                const char c = *part;
                record[text_start + used] = (c == '\n' || c == '\r') ? ' ' : c;
            }
            return *part == '\0';
        };

        bool complete = true;
        if (tag)
        {
            complete = append(tag) && append(": ");
        }
        complete = complete && append(text);

        if (!complete)
        {
            // The cut must not leave the first bytes of a multi-byte UTF-8 character behind.
            size_t cut = used;
            while (cut > 0 && (static_cast<unsigned char>(record[text_start + cut - 1]) & 0xC0) == 0x80)
            {
                cut--;
            }
            if (cut > 0 && static_cast<unsigned char>(record[text_start + cut - 1]) >= 0xC0)
            {
                used = cut - 1;
            }
        }

        size_t total = text_start + used;
        record[total++] = '\n';

        // The space is claimed first, so concurrent writers never share a range.
        const uint64_t position = __atomic_fetch_add(&mapped->write_pos, total, __ATOMIC_RELAXED);
        ring_copy(position, record, total);
    }
}

void blackbox_open(const std::string &directory, size_t keep)
{
    if (header.load(std::memory_order_acquire))
    {
        return;
    }

    prune(directory, keep);

    const pid_t pid = getpid();
    const std::string path = directory + "/" + std::string(FILE_PREFIX) + std::to_string(pid) + std::string(FILE_SUFFIX);
    const size_t size = BLACKBOX_HEADER_SIZE + BLACKBOX_RING_SIZE;

    // A process id can come around again; an earlier file of the same name is started over.
    int fd = open(path.c_str(), O_RDWR | O_CREAT | O_TRUNC | O_CLOEXEC, 0600);
    if (fd < 0)
    {
        LOGW("Cannot create %s: %s", path.c_str(), strerror(errno));
        return;
    }

    if (ftruncate(fd, static_cast<off_t>(size)) != 0)
    {
        LOGW("Cannot size %s: %s", path.c_str(), strerror(errno));
        close(fd);
        unlink(path.c_str());
        return;
    }

    void *mapping = mmap(nullptr, size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    close(fd);
    if (mapping == MAP_FAILED)
    {
        LOGW("Cannot map %s: %s", path.c_str(), strerror(errno));
        unlink(path.c_str());
        return;
    }

    // The new file reads as zeros: no crash recorded, nothing written.
    auto *mapped = static_cast<BlackboxHeader *>(mapping);
    memcpy(mapped->magic, "EKBB0001", sizeof(mapped->magic));
    mapped->header_size = BLACKBOX_HEADER_SIZE;
    mapped->ring_size = BLACKBOX_RING_SIZE;
    mapped->pid = static_cast<uint32_t>(pid);
    mapped->start_realtime_ms = clock_ms(CLOCK_REALTIME);

    ring = static_cast<char *>(mapping) + BLACKBOX_HEADER_SIZE;
    start_monotonic_ms = clock_ms(CLOCK_MONOTONIC);
    header.store(mapped, std::memory_order_release);

    LOGI("Recording to %s", path.c_str());
}

void blackbox_write(const char *text)
{
    write_record(nullptr, text);
}

void blackbox_write_tagged(const char *tag, const char *message)
{
    write_record(tag, message);
}

BlackboxHeader *blackbox_header()
{
    return header.load(std::memory_order_acquire);
}
