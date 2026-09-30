#ifndef FUSIONCORE_VITALS_H
#define FUSIONCORE_VITALS_H

// Starts the "fusion-vitals" thread, which wakes once a second for the rest of the run and keeps
// the parts of the blackbox that only come from watching the process up to date:
// - every five seconds, the memory readings in the header, with a line in the ring when they
//   have moved enough to be worth one;
// - thirty seconds in, the module table once more, to pick up the libraries loaded by then;
// - once vitals_heartbeat has been called, a watch on the thread that calls it: when the calls
//   stop for 5 seconds while the app is in the foreground and has not been reported paused,
//   that thread is interrupted by a signal and its stack goes into a hang record in the header,
//   and again at 15 seconds.
//
// The thread is attached neither to the Java VM nor to a managed runtime. Does nothing while no
// blackbox is mapped or when the thread is already running.
void vitals_start();

// Counts one beat in the header and takes the calling thread as the one to watch. Cheap enough
// to call every frame; a no-op while no blackbox is mapped.
void vitals_heartbeat();

// Tells the watch that the app's frame loop has been stopped on purpose (the screen went off, a
// dialog or another screen of the app came in front) or is running again. While it is stopped
// the missing beats are not a stall. A report of "stopped" that is never taken back wears off by
// itself once beats have kept coming for a few seconds. A freeze that sets in while the app is
// reported stopped, or on the way back before it reports running, is not seen: the watch cannot
// tell it from the pause. Safe from any thread.
void vitals_set_paused(bool paused);

#endif //FUSIONCORE_VITALS_H
