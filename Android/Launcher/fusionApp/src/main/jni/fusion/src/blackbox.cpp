#include <blackbox.h>
#include <android/log.h>
#include <fcntl.h>
#include <sys/mman.h>
#include <unistd.h>
#include <algorithm>
#include <atomic>
#include <cerrno>
#include <charconv>
#include <cstdio>
#include <cstring>
#include <ctime>
#include <filesystem>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
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
    std::mutex modules_mutex;

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

    // Reads the whole of /proc/self/maps into `text`. The file reports no size, so it is read
    // until it ends.
    bool read_maps(std::vector<char> &text)
    {
        int fd = open("/proc/self/maps", O_RDONLY | O_CLOEXEC);
        if (fd < 0)
        {
            return false;
        }

        text.resize(1 << 18);
        size_t used = 0;
        while (true)
        {
            if (used == text.size())
            {
                text.resize(text.size() * 2);
            }

            const ssize_t got = TEMP_FAILURE_RETRY(read(fd, text.data() + used, text.size() - used));
            if (got <= 0)
            {
                close(fd);
                text.resize(used);
                return got == 0;
            }
            used += static_cast<size_t>(got);
        }
    }

    struct Mapping
    {
        uint64_t begin;
        uint64_t end;
        uint64_t file_offset;
        bool executable;
        std::string_view path;
    };

    bool parse_hex(const char *&cursor, const char *end, uint64_t &out)
    {
        const auto parsed = std::from_chars(cursor, end, out, 16);
        cursor = parsed.ptr;
        return parsed.ec == std::errc();
    }

    // One line of /proc/self/maps: "begin-end perms offset dev inode   path". The path is absent
    // for anonymous memory and may hold spaces, so it is everything after the inode.
    bool parse_mapping(std::string_view line, Mapping &out)
    {
        const char *cursor = line.data();
        const char *end = cursor + line.size();
        if (!parse_hex(cursor, end, out.begin) || cursor == end || *cursor++ != '-' ||
            !parse_hex(cursor, end, out.end) || end - cursor < 6 || cursor[0] != ' ' || cursor[5] != ' ')
        {
            return false;
        }

        out.executable = cursor[3] == 'x';
        cursor += 6;
        if (!parse_hex(cursor, end, out.file_offset))
        {
            return false;
        }

        // The device and the inode.
        for (int field = 0; field < 2; field++)
        {
            while (cursor < end && *cursor == ' ')
            {
                cursor++;
            }
            while (cursor < end && *cursor != ' ')
            {
                cursor++;
            }
        }
        while (cursor < end && *cursor == ' ')
        {
            cursor++;
        }

        out.path = std::string_view(cursor, static_cast<size_t>(end - cursor));
        return true;
    }

    // The name of the mapped file without its directory. The kernel marks a file that has been
    // unlinked by a suffix, which is not part of the name.
    std::string_view file_name(std::string_view path)
    {
        constexpr std::string_view deleted = " (deleted)";
        if (path.ends_with(deleted))
        {
            path.remove_suffix(deleted.size());
        }
        return path.substr(path.rfind('/') + 1);
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
    memcpy(mapped->magic, "EKBB0002", sizeof(mapped->magic));
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

void blackbox_record_modules()
{
    BlackboxHeader *mapped = header.load(std::memory_order_acquire);
    if (!mapped)
    {
        return;
    }

    std::lock_guard<std::mutex> lock(modules_mutex);

    // The paths below are views into this text.
    std::vector<char> text;
    if (!read_maps(text))
    {
        LOGW("Cannot read /proc/self/maps: %s", strerror(errno));
        return;
    }

    struct Found
    {
        Mapping mapping;
        uint64_t load_base;
    };
    std::vector<Found> found;
    found.reserve(1024);
    // Where offset 0 of each file was last seen mapped. The lines come in address order, so
    // at any line this is the closest such mapping below it.
    std::unordered_map<std::string_view, uint64_t> bases;

    const std::string_view all(text.data(), text.size());
    for (size_t line_start = 0; line_start < all.size();)
    {
        size_t line_end = all.find('\n', line_start);
        if (line_end == std::string_view::npos)
        {
            line_end = all.size();
        }
        const std::string_view line = all.substr(line_start, line_end - line_start);
        line_start = line_end + 1;

        Mapping mapping{};
        if (!parse_mapping(line, mapping) || !mapping.path.starts_with('/'))
        {
            continue;
        }

        if (mapping.file_offset == 0)
        {
            bases[mapping.path] = mapping.begin;
        }
        if (!mapping.executable)
        {
            continue;
        }

        // Changing the protection of single pages splits one mapping into many. Pieces that
        // touch and map consecutive parts of the same file are put back together.
        if (!found.empty())
        {
            Mapping &previous = found.back().mapping;
            if (previous.end == mapping.begin && previous.path == mapping.path &&
                previous.begin - previous.file_offset == mapping.begin - mapping.file_offset)
            {
                previous.end = mapping.end;
                continue;
            }
        }

        const auto base = bases.find(mapping.path);
        found.push_back({mapping, base != bases.end() ? base->second : 0});
    }

    // The files that came with the app are the ones a report is most often about, so they
    // are placed first and stay in the table when it overflows.
    std::vector<BlackboxModule> entries(BLACKBOX_MAX_MODULES);
    uint32_t count = 0;
    for (const bool app_owned : {true, false})
    {
        for (const Found &item : found)
        {
            if (item.mapping.path.starts_with("/data/") != app_owned || count == BLACKBOX_MAX_MODULES)
            {
                continue;
            }

            BlackboxModule &entry = entries[count++];
            entry.begin = item.mapping.begin;
            entry.end = item.mapping.end;
            entry.file_offset = item.mapping.file_offset;
            entry.load_base = item.load_base;
            // The name always ends with a zero byte.
            const std::string_view name = file_name(item.mapping.path);
            memcpy(entry.name, name.data(), std::min(name.size(), BLACKBOX_MODULE_NAME_SIZE - 1));
        }
    }

    // The count goes in after the entries it covers, and the entries it no longer covers are
    // cleared after it, so a reader of the file never counts an entry that is not there.
    BlackboxModules &modules = mapped->modules;
    memcpy(modules.entries, entries.data(), count * sizeof(BlackboxModule));
    __atomic_store_n(&modules.truncated, found.size() > BLACKBOX_MAX_MODULES ? 1u : 0u, __ATOMIC_RELEASE);
    __atomic_store_n(&modules.count, count, __ATOMIC_RELEASE);
    memset(modules.entries + count, 0, (BLACKBOX_MAX_MODULES - count) * sizeof(BlackboxModule));

    LOGI("Recorded %u of %zu executable file ranges", count, found.size());
}
