// Copyright (c) 2026 XtraCube
#include <exports.h>
#include <android/log.h>
#include <hooking/safehook.h>
#include <logger.h>
#include <utilities/java.h>
#include <blackbox.h>
#include <cstdint>
#include <cstdlib>

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
    if (kind == 1)
    {
        abort();
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