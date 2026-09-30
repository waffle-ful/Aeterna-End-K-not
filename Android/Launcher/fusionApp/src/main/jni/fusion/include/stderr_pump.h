#ifndef FUSIONCORE_STDERR_PUMP_H
#define FUSIONCORE_STDERR_PUMP_H

// Points the process's standard error at a pipe and starts the "fusion-stderr" thread, which
// copies every line written there to the blackbox ring and to the system log. An app's standard
// error normally goes nowhere, yet it is where the managed runtime prints the reason right
// before it aborts: a stack overflow's repeated frames, an unhandled exception's trace.
//
// Writers never block: when the pipe is full, what does not fit is dropped. Does nothing when
// the pump is already running.
void stderr_pump_start();

// Waits, for a short bounded time, until what has been written to standard error so far has
// reached the ring. For the fatal signal handler: the text explaining an abort is usually
// written just before it. Async-signal-safe; returns at once on the pump's own thread and while
// the pump is not running.
void stderr_pump_settle();

#endif //FUSIONCORE_STDERR_PUMP_H
