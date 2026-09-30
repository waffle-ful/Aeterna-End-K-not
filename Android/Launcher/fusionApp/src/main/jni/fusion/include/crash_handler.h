#ifndef FUSIONCORE_CRASH_HANDLER_H
#define FUSIONCORE_CRASH_HANDLER_H

// Installs handlers for the fatal signals (SIGSEGV, SIGBUS, SIGABRT, SIGILL, SIGFPE) that record
// the faulting state in the blackbox header and then pass the signal on to the handler that was
// installed before, so the system crash report is produced as usual.
//
// Must run before the managed runtimes start. A runtime that installs its handler afterwards is
// asked first and turns the faults it owns (such as a null dereference in managed code) into
// exceptions; only the faults it declines reach this handler.
void crash_handler_install();

#endif //FUSIONCORE_CRASH_HANDLER_H
