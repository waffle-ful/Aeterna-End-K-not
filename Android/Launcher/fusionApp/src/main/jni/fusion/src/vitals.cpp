#include <vitals.h>
#include <blackbox.h>
#include <crash_handler.h>
#include <logger.h>
#include <fcntl.h>
#include <pthread.h>
#include <sys/syscall.h>
#include <ucontext.h>
#include <unistd.h>
#include <atomic>
#include <cerrno>
#include <csignal>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>

#define TAG "FusionVitals"

namespace
{
    constexpr long TICK_NS = 1000000000L;
    constexpr uint32_t MEMORY_EVERY_TICKS = 5;
    constexpr uint32_t MODULES_AT_TICK = 30;
    // A line goes to the ring when the resident size has moved this far from the last line,
    // or this long after it.
    constexpr uint64_t NOTE_RSS_STEP_KB = 32 * 1024;
    constexpr uint64_t NOTE_INTERVAL_MS = 60000;
    // How long the watched thread has to be silent before its stack is taken, the first time
    // and the second time within one stall.
    constexpr uint64_t HANG_FIRST_MS = 5000;
    constexpr uint64_t HANG_SECOND_MS = 15000;
    constexpr uint64_t HANG_REPLY_WAIT_MS = 500;
    // A pause report is dropped after beats have arrived in this many ticks in a row. A paused
    // app may still run a frame or two, which must not count as the return.
    constexpr int RESUMED_AFTER_TICKS = 3;

    std::atomic<bool> started{false};

    // The thread that last called vitals_heartbeat; 0 before the first call.
    std::atomic<pid_t> watched_tid{0};
    // Set by the vitals thread before it sends the signal and cleared by the handler once the
    // record is complete. The handler leaves the header alone while this is not set.
    std::atomic<bool> hang_requested{false};
    std::atomic<uint64_t> hang_stalled_ms{0};
    // What the app last reported through vitals_set_paused.
    std::atomic<bool> app_paused{false};

    // The rest of the watch state belongs to the vitals thread.
    bool watching = false;
    int hang_signo = 0;
    uint64_t seen_beats = 0;
    uint64_t progress_monotonic_ms = 0;
    // Stacks taken in the current stall: 0, 1 or 2.
    int hang_stage = 0;
    // Ticks in a row that saw beats while the app was reported paused.
    int beating_ticks_while_paused = 0;

    uint64_t page_bytes = 4096;
    bool noted = false;
    uint64_t noted_rss_kb = 0;
    int32_t noted_oom_score_adj = 0;
    uint64_t noted_monotonic_ms = 0;

    uint64_t clock_ms(clockid_t clock)
    {
        timespec ts{};
        clock_gettime(clock, &ts);
        return static_cast<uint64_t>(ts.tv_sec) * 1000 + static_cast<uint64_t>(ts.tv_nsec) / 1000000;
    }

    // Reads the start of a file into `buffer` as a zero-terminated string. The files read here
    // are short, or carry what is wanted in their first lines.
    bool read_text(const char *path, char *buffer, size_t capacity)
    {
        int fd = open(path, O_RDONLY | O_CLOEXEC);
        if (fd < 0)
        {
            return false;
        }

        size_t used = 0;
        while (used < capacity - 1)
        {
            const ssize_t got = TEMP_FAILURE_RETRY(read(fd, buffer + used, capacity - 1 - used));
            if (got <= 0)
            {
                break;
            }
            used += static_cast<size_t>(got);
        }
        close(fd);

        buffer[used] = '\0';
        return used > 0;
    }

    // /proc/self/statm is "size resident shared ...", counted in pages.
    bool read_resident(uint64_t &rss_kb, uint64_t &shared_kb)
    {
        char text[128];
        if (!read_text("/proc/self/statm", text, sizeof(text)))
        {
            return false;
        }

        char *cursor = text;
        char *parsed = nullptr;
        strtoull(cursor, &parsed, 10);
        if (parsed == cursor)
        {
            return false;
        }

        cursor = parsed;
        const uint64_t resident_pages = strtoull(cursor, &parsed, 10);
        if (parsed == cursor)
        {
            return false;
        }

        cursor = parsed;
        const uint64_t shared_pages = strtoull(cursor, &parsed, 10);
        if (parsed == cursor)
        {
            return false;
        }

        rss_kb = resident_pages * page_bytes / 1024;
        shared_kb = shared_pages * page_bytes / 1024;
        return true;
    }

    // The "MemAvailable:" line of /proc/meminfo, in kB; 0 when it is not there.
    uint64_t read_available_kb()
    {
        char text[1024];
        if (!read_text("/proc/meminfo", text, sizeof(text)))
        {
            return 0;
        }

        const char *line = strstr(text, "MemAvailable:");
        return line ? strtoull(line + strlen("MemAvailable:"), nullptr, 10) : 0;
    }

    int32_t read_oom_score_adj()
    {
        char text[32];
        if (!read_text("/proc/self/oom_score_adj", text, sizeof(text)))
        {
            return INT32_MIN;
        }

        char *parsed = nullptr;
        const long value = strtol(text, &parsed, 10);
        return parsed == text ? INT32_MIN : static_cast<int32_t>(value);
    }

    void sample_memory(BlackboxHeader *header)
    {
        uint64_t rss_kb = 0;
        uint64_t shared_kb = 0;
        if (!read_resident(rss_kb, shared_kb))
        {
            return;
        }

        const uint64_t available_kb = read_available_kb();
        const int32_t oom_score_adj = read_oom_score_adj();
        const uint64_t realtime_ms = clock_ms(CLOCK_REALTIME);

        BlackboxMemory &memory = header->memory;
        memory.oom_score_adj = oom_score_adj;
        memory.last_realtime_ms = realtime_ms;
        memory.rss_kb = rss_kb;
        memory.rss_shared_kb = shared_kb;
        memory.mem_available_kb = available_kb;
        if (rss_kb > memory.peak_rss_kb)
        {
            memory.peak_rss_kb = rss_kb;
            memory.peak_realtime_ms = realtime_ms;
        }
        memory.sample_count++;

        const uint64_t monotonic_ms = clock_ms(CLOCK_MONOTONIC);
        const uint64_t moved_kb = rss_kb > noted_rss_kb ? rss_kb - noted_rss_kb : noted_rss_kb - rss_kb;
        if (noted && moved_kb < NOTE_RSS_STEP_KB && oom_score_adj == noted_oom_score_adj &&
            monotonic_ms - noted_monotonic_ms < NOTE_INTERVAL_MS)
        {
            return;
        }

        noted = true;
        noted_rss_kb = rss_kb;
        noted_oom_score_adj = oom_score_adj;
        noted_monotonic_ms = monotonic_ms;

        char adj[16] = "?";
        if (oom_score_adj != INT32_MIN)
        {
            snprintf(adj, sizeof(adj), "%d", oom_score_adj);
        }

        // Only to the ring: at this rate the lines would crowd the system log.
        char note[128];
        snprintf(note, sizeof(note), "rss=%lluMB shared=%lluMB avail=%lluMB adj=%s",
                 static_cast<unsigned long long>(rss_kb / 1024),
                 static_cast<unsigned long long>(shared_kb / 1024),
                 static_cast<unsigned long long>(available_kb / 1024),
                 adj);
        blackbox_write_tagged("vitals", note);
    }

    // Runs on the watched thread, interrupting whatever it was stuck in. Like the fatal signal
    // handler it is limited to plain stores into the mapped header and async-signal-safe calls.
    void on_hang_signal(int, siginfo_t *, void *context)
    {
        const int saved_errno = errno;
        BlackboxHeader *header = blackbox_header();
        // The signal number is shared with the rest of the process; one that was not asked for
        // by the vitals thread is ignored.
        if (header && hang_requested.load(std::memory_order_acquire))
        {
            BlackboxHangs &hangs = header->hangs;
            const uint32_t seq = hangs.total_count + 1;
            BlackboxHangSlot &slot = hangs.slots[(seq - 1) % BLACKBOX_HANG_SLOTS];

            // The slot reads as unused while the record it held is being replaced.
            __atomic_store_n(&slot.seq, 0u, __ATOMIC_RELEASE);
            slot.tid = static_cast<uint32_t>(syscall(SYS_gettid));
            timespec now{};
            clock_gettime(CLOCK_REALTIME, &now);
            slot.realtime_ms = static_cast<uint64_t>(now.tv_sec) * 1000 + static_cast<uint64_t>(now.tv_nsec) / 1000000;
            slot.stalled_ms = hang_stalled_ms.load(std::memory_order_relaxed);
            slot.pc = 0;
            slot.sp = 0;
            slot.lr = 0;
            slot.fp = 0;
            slot.frame_count = 0;
            slot.walk_stop = BLACKBOX_WALK_NOT_WALKED;

#if defined(__aarch64__)
            if (context)
            {
                const mcontext_t &registers = static_cast<const ucontext_t *>(context)->uc_mcontext;
                slot.pc = registers.pc;
                slot.sp = registers.sp;
                slot.lr = registers.regs[30];
                slot.fp = registers.regs[29];
                if (crash_handler_can_walk_frames())
                {
                    crash_handler_walk_frames(slot.pc, slot.lr, slot.fp, slot.frames, BLACKBOX_HANG_FRAMES,
                                              slot.frame_count, slot.walk_stop);
                }
                else
                {
                    slot.frames[0] = slot.pc;
                    slot.frames[1] = slot.lr;
                    slot.frame_count = 2;
                }
            }
#else
            (void) context;
#endif

            // The sequence number and the total make the record visible, in that order.
            __atomic_store_n(&slot.seq, seq, __ATOMIC_RELEASE);
            __atomic_store_n(&hangs.total_count, seq, __ATOMIC_RELEASE);
            hang_requested.store(false, std::memory_order_release);
        }
        errno = saved_errno;
    }

    bool is_own_handler(const struct sigaction &action)
    {
        return (action.sa_flags & SA_SIGINFO) && action.sa_sigaction == on_hang_signal;
    }

    // Claims a real-time signal nothing else in the process handles, looking from the top of
    // the range, where the ones the runtimes use are least likely to be.
    void install_hang_signal(BlackboxHeader *header)
    {
        for (int signo = SIGRTMAX - 1; signo >= SIGRTMIN; signo--)
        {
            struct sigaction current{};
            if (sigaction(signo, nullptr, &current) != 0 || current.sa_handler != SIG_DFL)
            {
                continue;
            }

            struct sigaction action{};
            action.sa_sigaction = on_hang_signal;
            action.sa_flags = SA_SIGINFO | SA_RESTART | SA_ONSTACK;
            sigemptyset(&action.sa_mask);
            if (sigaction(signo, &action, nullptr) != 0)
            {
                continue;
            }

            hang_signo = signo;
            header->hangs.signo = static_cast<uint32_t>(signo);
            log_format(LogLevel::INFO, TAG, "Watching thread {} for stalls with signal {}",
                       watched_tid.load(std::memory_order_relaxed), signo);
            return;
        }

        log(LogLevel::WARN, TAG, "Stall watching is off: no real-time signal is free");
    }

    void sleep_ms(long ms)
    {
        timespec duration{ms / 1000, (ms % 1000) * 1000000L};
        while (nanosleep(&duration, &duration) != 0 && errno == EINTR)
        {
        }
    }

    // Interrupts the watched thread so that it records its own stack, and waits for the record.
    void take_hang_stack(BlackboxHeader *header, uint64_t stalled_ms)
    {
        // The default action of a real-time signal ends the process, so it is only sent while
        // the handler is still the one installed above.
        struct sigaction current{};
        if (sigaction(hang_signo, nullptr, &current) != 0 || !is_own_handler(current))
        {
            log_format(LogLevel::WARN, TAG, "Stall watching is off: the handler of signal {} was replaced", hang_signo);
            hang_signo = 0;
            return;
        }

        const pid_t tid = watched_tid.load(std::memory_order_relaxed);
        hang_stalled_ms.store(stalled_ms, std::memory_order_relaxed);
        hang_requested.store(true, std::memory_order_release);
        if (syscall(SYS_tgkill, getpid(), tid, hang_signo) != 0)
        {
            hang_requested.store(false, std::memory_order_release);
            log_format(LogLevel::WARN, TAG, "Thread {} stalled for {} ms and cannot be signalled: {}",
                       tid, stalled_ms, strerror(errno));
            return;
        }

        const uint64_t sent_ms = clock_ms(CLOCK_MONOTONIC);
        while (hang_requested.load(std::memory_order_acquire) &&
               clock_ms(CLOCK_MONOTONIC) - sent_ms < HANG_REPLY_WAIT_MS)
        {
            sleep_ms(10);
        }

        // A signal taken later than this finds the request withdrawn and records nothing.
        if (hang_requested.exchange(false, std::memory_order_acq_rel))
        {
            log_format(LogLevel::WARN, TAG, "Thread {} stalled for {} ms and did not take the signal", tid, stalled_ms);
            return;
        }

        log_format(LogLevel::WARN, TAG, "Thread {} stalled for {} ms; stack recorded as hang {}",
                   tid, stalled_ms, __atomic_load_n(&header->hangs.total_count, __ATOMIC_ACQUIRE));
    }

    void watch_heartbeat(BlackboxHeader *header)
    {
        BlackboxHangs &hangs = header->hangs;
        const uint64_t beats = __atomic_load_n(&hangs.beat_count, __ATOMIC_RELAXED);
        if (beats == 0)
        {
            return;
        }

        const uint64_t monotonic_ms = clock_ms(CLOCK_MONOTONIC);
        if (!watching)
        {
            watching = true;
            install_hang_signal(header);
            // The first beat comes from managed code, so the runtime and its libraries are in.
            blackbox_record_modules();
        }
        else if (beats == seen_beats)
        {
            beating_ticks_while_paused = 0;

            // Frames stop by design while the app is not in the foreground, and while it is in
            // the foreground but paused, which the system's view of the process does not show.
            // The time spent there does not count: the stall is measured again from the return.
            const int32_t oom_score_adj = read_oom_score_adj();
            if (app_paused.load(std::memory_order_relaxed) || (oom_score_adj != 0 && oom_score_adj != INT32_MIN))
            {
                hangs.paused = 1;
                progress_monotonic_ms = monotonic_ms;
                hang_stage = 0;
                return;
            }
            hangs.paused = 0;

            const uint64_t stalled_ms = monotonic_ms - progress_monotonic_ms;
            if (hang_signo != 0 && ((hang_stage == 0 && stalled_ms >= HANG_FIRST_MS) ||
                                    (hang_stage == 1 && stalled_ms >= HANG_SECOND_MS)))
            {
                hang_stage++;
                take_hang_stack(header, stalled_ms);
            }
            return;
        }

        if (!app_paused.load(std::memory_order_relaxed))
        {
            beating_ticks_while_paused = 0;
        }
        else if (++beating_ticks_while_paused >= RESUMED_AFTER_TICKS)
        {
            beating_ticks_while_paused = 0;
            app_paused.store(false, std::memory_order_relaxed);
            blackbox_write_tagged("vitals", "frames are running again; the pause report is dropped");
        }

        seen_beats = beats;
        progress_monotonic_ms = monotonic_ms;
        hangs.last_progress_realtime_ms = clock_ms(CLOCK_REALTIME);
        hangs.paused = 0;
        hang_stage = 0;
    }

    void *run(void *)
    {
        pthread_setname_np(pthread_self(), "fusion-vitals");

        const long page_size = sysconf(_SC_PAGESIZE);
        if (page_size > 0)
        {
            page_bytes = static_cast<uint64_t>(page_size);
        }

        timespec wake{};
        clock_gettime(CLOCK_MONOTONIC, &wake);

        for (uint32_t tick = 0;; tick++)
        {
            BlackboxHeader *header = blackbox_header();
            if (header && tick % MEMORY_EVERY_TICKS == 0)
            {
                sample_memory(header);
            }
            if (tick == MODULES_AT_TICK)
            {
                blackbox_record_modules();
            }
            if (header)
            {
                watch_heartbeat(header);
            }

            // Ticks are timed against the clock rather than against the end of the work above,
            // so they do not drift. After a long stall the schedule starts over from now instead
            // of running the missed ticks in a burst.
            wake.tv_nsec += TICK_NS;
            if (wake.tv_nsec >= 1000000000L)
            {
                wake.tv_nsec -= 1000000000L;
                wake.tv_sec++;
            }

            timespec now{};
            clock_gettime(CLOCK_MONOTONIC, &now);
            if (now.tv_sec > wake.tv_sec || (now.tv_sec == wake.tv_sec && now.tv_nsec >= wake.tv_nsec))
            {
                wake = now;
                continue;
            }

            while (clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME, &wake, nullptr) == EINTR)
            {
            }
        }
    }
}

void vitals_start()
{
    if (!blackbox_header() || started.exchange(true))
    {
        return;
    }

    pthread_t thread;
    const int error = pthread_create(&thread, nullptr, run, nullptr);
    if (error != 0)
    {
        started.store(false);
        log_format(LogLevel::WARN, TAG, "Cannot start the vitals thread: {}", strerror(error));
        return;
    }
    pthread_detach(thread);
}

void vitals_heartbeat()
{
    BlackboxHeader *header = blackbox_header();
    if (!header)
    {
        return;
    }

    const pid_t tid = gettid();
    if (watched_tid.load(std::memory_order_relaxed) != tid)
    {
        watched_tid.store(tid, std::memory_order_relaxed);
    }
    __atomic_fetch_add(&header->hangs.beat_count, 1, __ATOMIC_RELAXED);
}

void vitals_set_paused(bool paused)
{
    if (!blackbox_header() || app_paused.exchange(paused, std::memory_order_relaxed) == paused)
    {
        return;
    }

    blackbox_write_tagged("vitals", paused ? "app paused" : "app resumed");
}
