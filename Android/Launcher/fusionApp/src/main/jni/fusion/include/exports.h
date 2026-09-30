// Copyright (c) 2026 XtraCube
#ifndef FUSIONCORE_EXPORTS_H
#define FUSIONCORE_EXPORTS_H
#include <external/dobby.h>

#ifdef __cplusplus
extern "C"
{
#endif

void init_bridge_helper(const char *libraryPath);

dobby_dummy_func_t hook(void *address, dobby_dummy_func_t replace_delegate, bool specialReturnBuffer);

void unhook(void *target);

void create_alert(const char *title, const char *message);

void set_loader_stage(uint8_t stage);

void set_loader_message(const char *text);

void write_log(const char *text);

void write_log_level(int level, const char *text);

int8_t get_low_memory_mode();

// Crashes the process on purpose, to exercise crash reporting. kind 0 = invalid write (SIGSEGV),
// 1 = abort().
void fusion_debug_crash(int kind);

// Appends one UTF-8 line to the blackbox ring that is kept on disk for post-mortem reading.
void fusion_breadcrumb(const char *utf8);

// As fusion_breadcrumb, with the line stored as "<tag>: <utf8>".
void fusion_breadcrumb_tagged(const char *tag, const char *utf8);

#ifdef __cplusplus
}
#endif

#endif //FUSIONCORE_EXPORTS_H
