package net.symbolon.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import net.symbolon.ui.theme.Metrics
import net.symbolon.ui.theme.Tokens

/**
 * Root of the editor UI, shared by the Android and desktop shells.
 *
 * [onFirstLayout] fires once, when the root has been laid out for the first time;
 * the shells use it to record the "ready" timestamp.
 */
@Composable
fun EditorRoot(onFirstLayout: () -> Unit = {}) {
    val fired = remember { BooleanArray(1) }
    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Tokens.bg0)
            .onGloballyPositioned {
                if (!fired[0]) {
                    fired[0] = true
                    onFirstLayout()
                }
            },
        contentAlignment = Alignment.Center,
    ) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Text(
                text = "SYMBOLON",
                color = Tokens.accent,
                fontSize = 40.sp,
                letterSpacing = 12.sp,
                fontWeight = FontWeight.Light,
            )
            Text(
                text = "マップと役職のエディタ",
                color = Tokens.textMuted,
                fontSize = 16.sp,
                modifier = Modifier.padding(top = Metrics.spaceLg.dp),
            )
        }
    }
}
