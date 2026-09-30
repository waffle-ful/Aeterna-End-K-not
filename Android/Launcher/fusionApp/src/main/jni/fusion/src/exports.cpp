// Copyright (c) 2026 XtraCube
#include <exports.h>
#include <android/log.h>
#include <hooking/safehook.h>
#include <logger.h>
#include <utilities/java.h>
#include <blackbox.h>
#include <vitals.h>
#include <pthread.h>
#include <sys/mman.h>
#include <sys/syscall.h>
#include <unistd.h>
#include <csignal>
#include <cstdint>
#include <cstdlib>

namespace
{
    volatile int recursion_continues = 1;

    // Each call keeps a block of stack alive across the next one, so the recursion can neither
    // be turned into a loop nor have its frames shrunk to nothing.
    __attribute__((noinline)) int recurse_until_overflow(int depth)
    {
        volatile char block[1024];
        block[0] = static_cast<char>(depth);
        if (!recursion_continues)
        {
            return 0;
        }
        return recurse_until_overflow(depth + 1) + block[0];
    }

    void *recurse_on_thread(void *)
    {
        recurse_until_overflow(0);
        return nullptr;
    }

    void *trap_on_thread(void *)
    {
        __builtin_trap();
    }

    // Maps one page of an in-memory file, then cuts the file to nothing: the page stays mapped
    // with no file behind it, and touching it raises SIGBUS.
    void read_beyond_end_of_file()
    {
        const long page = sysconf(_SC_PAGESIZE);
        const int fd = static_cast<int>(syscall(__NR_memfd_create, "fusion-debug", 0u));
        if (fd < 0 || page <= 0 || ftruncate(fd, page) != 0)
        {
            log(LogLevel::WARN, "Fusion", "fusion_debug_crash: cannot create the file to map");
            return;
        }

        void *mapped = mmap(nullptr, static_cast<size_t>(page), PROT_READ, MAP_SHARED, fd, 0);
        if (mapped == MAP_FAILED || ftruncate(fd, 0) != 0)
        {
            log(LogLevel::WARN, "Fusion", "fusion_debug_crash: cannot map or cut the file");
            return;
        }

        volatile int value = *static_cast<volatile int *>(mapped);
        (void) value;
    }
}

void init_bridge_helper(const char *libraryPath)
{
    safehook_setup_bridge_helper(libraryPath);
}

dobby_dummy_func_t hook(void *address, dobby_dummy_func_t replace_delegate, bool specialReturnBuffer)
{
    return safehook_create_hook(address, replace_delegate, specialReturnBuffer);
}

void unhook(void *target)
{
    safehook_destroy_hook(target);
}

void create_alert(const char *title, const char *message)
{

}

void set_loader_stage(uint8_t stage)
{
    setLoadingState(stage < 2);
}

void set_loader_message(const char *text)
{
    setLoadingText(text);
}

void write_log(const char *text)
{
    log(LogLevel::INFO, "Fusion.NET", text);
}

void write_log_level(int level, const char *text)
{
    LogLevel logLevel = static_cast<LogLevel>(level);
    log(logLevel, "Fusion.NET", text);
}

int8_t get_low_memory_mode()
{
    // TODO: add configuration
    return 1;
}

void fusion_debug_crash(int kind)
{
    switch (kind)
    {
        case 1:
            abort();
        case 2:
            read_beyond_end_of_file();
            return;
        case 3:
#if defined(__aarch64__)
            __asm__ volatile(".inst 0x00000000");
#else
            raise(SIGILL);
#endif
            return;
        case 4:
            raise(SIGFPE);
            return;
        case 5:
            __builtin_trap();
        case 6:
            raise(SIGSYS);
            return;
        case 7:
            recurse_until_overflow(0);
            return;
        case 8:
        case 9:
        {
            pthread_t thread;
            if (pthread_create(&thread, nullptr, kind == 8 ? recurse_on_thread : trap_on_thread, nullptr) == 0)
            {
                pthread_join(thread, nullptr);
            }
            return;
        }
        default:
            break;
    }

    // The address is read from a volatile so the compiler cannot prove the store is invalid and
    // replace it with a trap instruction, which would raise a different signal.
    static volatile uintptr_t address = 0;
    *reinterpret_cast<volatile int *>(address) = kind;
}

void fusion_breadcrumb(const char *utf8)
{
    blackbox_write(utf8);
}

void fusion_breadcrumb_tagged(const char *tag, const char *utf8)
{
    blackbox_write_tagged(tag, utf8);
}

void fusion_heartbeat()
{
    vitals_heartbeat();
}

void fusion_set_paused(int paused)
{
    vitals_set_paused(paused != 0);
}
