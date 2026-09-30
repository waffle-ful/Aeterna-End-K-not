#ifndef FUSIONCORE_VITALS_H
#define FUSIONCORE_VITALS_H

// Starts the "fusion-vitals" thread, which wakes once a second for the rest of the run and keeps
// the parts of the blackbox that only come from watching the process up to date:
// - every five seconds, the memory readings in the header, with a line in the ring when they
//   have moved enough to be worth one;
// - thirty seconds in, the module table once more, to pick up the libraries loaded by then;
// - once vitals_heartbeat has been called, a watch on the thread that calls it: when the calls
//   stop for 5 seconds while the app is in the foreground, that thread is interrupted by a signal
//   and its stack goes into a hang record in the header, and again at 15 seconds.
//
// The thread is attached neither to the Java VM nor to a managed runtime. Does nothing while no
// blackbox is mapped or when the thread is already running.
void vitals_start();

// Counts one beat in the header and takes the calling thread as the one to watch. Cheap enough
// to call every frame; a no-op while no blackbox is mapped.
void vitals_heartbeat();

#endif //FUSIONCORE_VITALS_H
