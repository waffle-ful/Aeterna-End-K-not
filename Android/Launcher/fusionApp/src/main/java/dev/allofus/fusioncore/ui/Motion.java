package dev.allofus.fusioncore.ui;

import android.content.Context;
import android.provider.Settings;

/** Whether the player's system settings allow decorative animation. */
public final class Motion {
    private Motion() {
    }

    public static boolean enabled(Context context) {
        try {
            return Settings.Global.getFloat(context.getContentResolver(),
                    Settings.Global.ANIMATOR_DURATION_SCALE, 1f) > 0f;
        } catch (Exception e) {
            return true;
        }
    }
}
