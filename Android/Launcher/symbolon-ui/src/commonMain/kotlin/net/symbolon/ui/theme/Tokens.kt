package net.symbolon.ui.theme

import androidx.compose.ui.graphics.Color

/**
 * Colour tokens of the Symbolon look ("a hot spring at night").
 * Every colour drawn by the editor comes from here; screens never use ad-hoc colours.
 */
object Tokens {
    // Background layers — dark with a hint of blue, never pure black.
    val bg0 = Color(0xFF0F1417)       // deepest background
    val bg1 = Color(0xFF151C21)       // header / footer bars
    val bg2 = Color(0xFF1E272C)       // buttons, inputs
    val bg3 = Color(0xFF0B1014)       // pickers, tile grid
    val bgDialog = Color(0xFF1A242A)
    val bgToast = Color(0xFF22303A)
    val bgHover = Color(0xFF2A353B)

    // Borders
    val border = Color(0xFF2F3C43)
    val borderBar = Color(0xFF1E2A31)

    // Text
    val textPrimary = Color(0xFFE8EFF0)
    val textMuted = Color(0xFF8FA3A8)

    // Accents — lights and nature found around a bath house
    val accent = Color(0xFFE9A648)    // lantern
    val ok = Color(0xFF86C287)        // moss
    val info = Color(0xFF8FC2D2)      // water
    val danger = Color(0xFFE08578)    // autumn leaves
    val warn = Color(0xFFE4BC80)

    // Active backgrounds (fixed colours, not translucent overlays)
    val bgActiveAccent = Color(0xFF372B1B)
    val bgActiveOk = Color(0xFF1F3325)
    val bgActiveInfo = Color(0xFF1E3038)

    // Decorative
    val hinoki = Color(0xFFC9A87C)    // cypress wood rules
    val yu = Color(0xFFBFD9D4)        // steam
}

/** Spacing and shape scale, in dp. */
object Metrics {
    const val spaceXs = 6
    const val spaceSm = 8
    const val spaceMd = 10
    const val spaceLg = 16
    const val radiusSm = 6
    const val radiusMd = 8
    const val radiusLg = 10
    const val radiusXl = 12
    const val touchTarget = 44
}
