package dev.allofus.fusioncore.tools;

import android.os.Process;
import android.os.SystemClock;
import android.system.Os;
import android.util.Log;

import java.util.ArrayList;
import java.util.List;

/**
 * Records when each launcher boot stage finished, measured from process start on the
 * monotonic clock, and publishes a one-line summary to logcat right before the game
 * activity is started. The summary is exported to the environment so the game side, which
 * runs in this same process, can put it next to its own marks.
 */
public final class BootTimeline {
    private static final String TAG = "BootTimeline";

    /** Launcher stage summary, e.g. {@code selector=210ms bootstrap=1520ms ... launch=9800ms}. */
    public static final String ENV_LAUNCHER = "ENDKNOT_BOOT_LAUNCHER";

    private static final List<String> marks = new ArrayList<>();

    private BootTimeline() {
    }

    private static long processStartMs() {
        return Process.getStartUptimeMillis();
    }

    /** Starts a new timeline. Called when the selector hands off to a bootstrap, so a retry
     *  in the same process does not append to the previous attempt. */
    public static synchronized void reset() {
        marks.clear();
    }

    /** Records that {@code stage} finished now. Safe from any thread. */
    public static synchronized void mark(String stage) {
        long since = SystemClock.uptimeMillis() - processStartMs();
        marks.add(stage + "=" + since + "ms");
    }

    public static synchronized String summary() {
        return String.join(" ", marks);
    }

    /** Logs the summary and hands it to the game process through the environment. */
    public static synchronized void publish() {
        String summary = summary();
        Log.i(TAG, "launcher: " + summary);
        try {
            Os.setenv(ENV_LAUNCHER, summary, true);
        } catch (Exception e) {
            Log.w(TAG, "Could not export the boot timeline to the environment", e);
        }
    }
}
