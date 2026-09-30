#ifndef FUSIONCORE_RUNTIME_LOG_TAP_H
#define FUSIONCORE_RUNTIME_LOG_TAP_H

// Makes every line the managed runtime (libcoreclr) sends to the system log go to the blackbox
// ring as well. On Android the runtime reports its fatal conditions there rather than on
// standard error: the repeated frames of a stack overflow, the trace of an unhandled exception,
// the message of a fail-fast. The system log is not kept with the crash report, so without this
// those lines are gone by the time the report is read.
//
// Works by pointing the runtime library's import of __android_log_write at a function in this
// library that writes the ring and then calls the real one. Only that library's import is
// changed; nothing else in the process is affected. Must run after libcoreclr is loaded and is
// best run before the runtime starts. Does nothing, with a warning in the log, when the import
// cannot be found. Only implemented for arm64.
void runtime_log_tap_install();

#endif //FUSIONCORE_RUNTIME_LOG_TAP_H
