package net.symbolon.desktop

import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Window
import androidx.compose.ui.window.application
import androidx.compose.ui.window.rememberWindowState
import net.symbolon.ui.EditorRoot

private val startedAtNanos: Long = System.nanoTime()

fun main() = application {
    Window(
        onCloseRequest = ::exitApplication,
        title = "Symbolon",
        state = rememberWindowState(width = 1280.dp, height = 800.dp),
    ) {
        EditorRoot(onFirstLayout = {
            val ms = (System.nanoTime() - startedAtNanos) / 1_000_000
            reportPerf("editor_ready_ms=$ms")
        })
    }
}

/**
 * Writes a measurement line to stdout and, when the SYMBOLON_PERF_LOG environment
 * variable names a file, appends it there too (the packaged launcher has no console).
 */
private fun reportPerf(line: String) {
    val text = "SymbolonPerf $line"
    println(text)
    val path = System.getenv("SYMBOLON_PERF_LOG") ?: return
    runCatching { java.io.File(path).appendText(text + System.lineSeparator()) }
}
