// Copyright (c) XtraCube 2026

#include <logger.h>
#include <android/log.h>
#include <blackbox.h>

void log(LogLevel level, const char *tag, const char *message)
{
    __android_log_write(static_cast<int>(level), tag, message);
    // Kept in the blackbox as well, so the last lines before a death survive on disk.
    blackbox_write_tagged(tag, message);
}
