package net.symbolon.ui

import android.os.SystemClock
import android.util.Log
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent

/** Entry point for Android hosts: puts the editor UI into an activity. */
object SymbolonEditor {
    private const val PERF_TAG = "SymbolonPerf"

    /**
     * Shows the editor in [activity]. [createdAtMillis] is the host's
     * `SystemClock.elapsedRealtime()` taken at the start of `onCreate`,
     * used to log the time until the first layout.
     */
    fun attach(activity: ComponentActivity, createdAtMillis: Long) {
        activity.setContent {
            EditorRoot(onFirstLayout = {
                val ms = SystemClock.elapsedRealtime() - createdAtMillis
                Log.i(PERF_TAG, "editor_ready_ms=$ms")
            })
        }
    }
}
