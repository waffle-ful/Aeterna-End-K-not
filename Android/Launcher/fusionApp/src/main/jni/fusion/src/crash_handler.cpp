#include <crash_handler.h>
#include <blackbox.h>
#include <logger.h>
#include <stderr_pump.h>
#include <sys/syscall.h>
#include <sys/uio.h>
#include <ucontext.h>
#include <unistd.h>
#include <cerrno>
#include <csignal>
#include <ctime>

#define TAG "FusionCrashHandler"

namespace
{
    // SIGTRAP is what the breakpoint instruction raises that compilers emit for a trap
    // (unreachable code, a failed check); SIGSYS is a system call refused by the sandbox.
    constexpr int SIGNALS[] = {SIGSEGV, SIGBUS, SIGABRT, SIGILL, SIGFPE, SIGTRAP, SIGSYS};
    constexpr size_t SIGNAL_COUNT = sizeof(SIGNALS) / sizeof(SIGNALS[0]);

    struct sigaction previous[SIGNAL_COUNT];
    bool installed = false;
    constexpr int MAX_STACK_SWITCHES = 2;
    // Set once, before the handlers go in, when reading this process's memory by system call works.
    bool can_walk_frames = false;

    // Everything reachable from on_signal is limited to plain stores into the mapped header and
    // async-signal-safe calls: no allocation, no locks, no formatting, no logging.

    // Reads this process's own memory through the kernel. A bad address makes the call fail
    // instead of faulting, which matters inside the handler: the signal being handled is blocked
    // there, so a second fault would kill the process before the system crash report is written.
    bool read_own_memory(uint64_t address, void *out, size_t size)
    {
        iovec local{out, size};
        iovec remote{reinterpret_cast<void *>(address), size};
        return process_vm_readv(getpid(), &local, 1, &remote, 1, 0) == static_cast<ssize_t>(size);
    }

    void stamp(int signo, const siginfo_t *info, void *context)
    {
        BlackboxHeader *header = blackbox_header();
        if (!header)
        {
            return;
        }

        // Only the first fatal signal is recorded. A second fault while the first is being
        // handled, or another thread crashing right after, leaves the record alone.
        BlackboxCrash &crash = header->crash;
        uint32_t expected = 0;
        if (!__atomic_compare_exchange_n(&crash.stamped, &expected, 1u, false,
                                         __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE))
        {
            return;
        }

        crash.signo = static_cast<uint32_t>(signo);
        crash.tid = static_cast<uint32_t>(syscall(SYS_gettid));
        if (info)
        {
            crash.si_code = info->si_code;
            // si_addr is only meaningful for faults raised by the CPU, not for sent signals.
            if (info->si_code > 0)
            {
                crash.fault_addr = reinterpret_cast<uint64_t>(info->si_addr);
            }
        }

#if defined(__aarch64__)
        if (context)
        {
            const mcontext_t &registers = static_cast<const ucontext_t *>(context)->uc_mcontext;
            crash.pc = registers.pc;
            crash.sp = registers.sp;
            crash.lr = registers.regs[30];
            crash.fp = registers.regs[29];
            if (can_walk_frames)
            {
                crash_handler_walk_frames(crash.pc, crash.lr, crash.fp, crash.frames, BLACKBOX_MAX_FRAMES,
                                          crash.frame_count, crash.walk_stop);
            }
        }
#else
        (void) context;
#endif

        timespec now{};
        clock_gettime(CLOCK_REALTIME, &now);
        crash.realtime_ms = static_cast<uint64_t>(now.tv_sec) * 1000 + static_cast<uint64_t>(now.tv_nsec) / 1000000;
    }

    void on_signal(int signo, siginfo_t *info, void *context)
    {
        const int saved_errno = errno;
        stamp(signo, info, context);
        stderr_pump_settle();

        const struct sigaction *before = nullptr;
        for (size_t i = 0; i < SIGNAL_COUNT; i++)
        {
            if (SIGNALS[i] == signo)
            {
                before = &previous[i];
                break;
            }
        }

        errno = saved_errno;
        if (!before)
        {
            return;
        }

        if (before->sa_flags & SA_SIGINFO)
        {
            before->sa_sigaction(signo, info, context);
            return;
        }

        if (before->sa_handler == SIG_IGN)
        {
            return;
        }

        if (before->sa_handler != SIG_DFL)
        {
            before->sa_handler(signo);
            return;
        }

        // Back to the default action. A fault raised by the CPU happens again as soon as this
        // returns and then kills the process; a signal that was sent (abort, kill) would not come
        // back by itself, so it is sent again. It stays blocked until this handler returns.
        sigaction(signo, before, nullptr);
        if (!info || info->si_code <= 0)
        {
            syscall(SYS_tgkill, getpid(), syscall(SYS_gettid), signo);
        }
    }
}

bool crash_handler_can_walk_frames()
{
    return can_walk_frames;
}

// Each frame record is {caller's frame pointer, return address}. JIT-compiled managed code
// keeps these records but has no unwind tables, so the system unwinder stops at it while
// this walk carries on through it.
void crash_handler_walk_frames(uint64_t pc, uint64_t lr, uint64_t fp, uint64_t *frames, uint32_t capacity,
                               uint32_t &frame_count, uint32_t &walk_stop)
{
#if defined(__aarch64__)
    uint32_t count = 0;
    if (count < capacity)
    {
        frames[count++] = pc;
    }
    if (count < capacity)
    {
        frames[count++] = lr;
    }

    uint32_t stop = BLACKBOX_WALK_FULL;
    bool first = true;
    int stack_switches = 0;
    while (count < capacity)
    {
        if ((fp & 7) != 0)
        {
            stop = BLACKBOX_WALK_UNALIGNED_FP;
            break;
        }

        uint64_t record[2];
        if (!read_own_memory(fp, record, sizeof(record)))
        {
            stop = BLACKBOX_WALK_UNREADABLE;
            break;
        }

        const uint64_t next_fp = record[0];
        const uint64_t return_address = record[1];
        if (return_address == 0)
        {
            stop = BLACKBOX_WALK_NULL_RETURN;
            break;
        }

        // The innermost record usually holds the link register's value again.
        if (!first || return_address != lr)
        {
            frames[count++] = return_address;
        }
        first = false;

        // The stack grows down, so a caller's record sits at a higher address, except where
        // the chain leaves the alternate signal stack for the stack that was interrupted, which
        // may lie anywhere. A few such steps are followed; the frame array bounds the walk in
        // any case.
        if (next_fp == fp || (next_fp < fp && ++stack_switches > MAX_STACK_SWITCHES))
        {
            stop = BLACKBOX_WALK_FP_NOT_ABOVE;
            break;
        }
        fp = next_fp;
    }

    frame_count = count;
    walk_stop = stop;
#else
    (void) pc;
    (void) lr;
    (void) fp;
    (void) frames;
    (void) capacity;
    frame_count = 0;
    walk_stop = BLACKBOX_WALK_NOT_WALKED;
#endif
}

void crash_handler_install()
{
    if (installed)
    {
        return;
    }
    installed = true;

    // Tried here, in normal context, rather than found out inside the handler: a readable address
    // must read back, and an unmapped one must fail without a fault.
    uint64_t probe_value = 0x0123456789abcdefULL;
    uint64_t probe_read = 0;
    const bool reads_valid = read_own_memory(reinterpret_cast<uint64_t>(&probe_value), &probe_read, sizeof(probe_read))
                             && probe_read == probe_value;
    errno = 0;
    const bool rejects_invalid = !read_own_memory(0x10, &probe_read, sizeof(probe_read)) && errno == EFAULT;
    can_walk_frames = reads_valid && rejects_invalid;
    if (!can_walk_frames)
    {
        log_format(LogLevel::WARN, TAG, "Frame walking is off: reading own memory failed the probe (valid={}, invalid={})",
                   reads_valid, rejects_invalid);
    }

    struct sigaction action{};
    action.sa_sigaction = on_signal;
    action.sa_flags = SA_SIGINFO | SA_ONSTACK;
    sigemptyset(&action.sa_mask);

    for (size_t i = 0; i < SIGNAL_COUNT; i++)
    {
        if (sigaction(SIGNALS[i], &action, &previous[i]) != 0)
        {
            log_format(LogLevel::WARN, TAG, "Cannot install the handler for signal {}", SIGNALS[i]);
        }
    }

    // The handler relies on the alternate signal stack the C library gives every thread.
    stack_t stack{};
    const bool has_stack = sigaltstack(nullptr, &stack) == 0 && !(stack.ss_flags & SS_DISABLE);
    log_format(LogLevel::INFO, TAG, "Fatal signal handlers installed (alternate stack: {} bytes, frame walking: {})",
               has_stack ? stack.ss_size : 0, can_walk_frames ? "on" : "off");
}
