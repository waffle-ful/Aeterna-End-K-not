package dev.allofus.fusioncore;

import android.content.Context;
import android.content.SharedPreferences;

public final class FusionSettings {
    private static final String PREFS_NAME = "fusion_settings";
    private static final String KEY_ACTIVITY_OVERRIDE = "activity_override";

    private FusionSettings() {
    }

    private static SharedPreferences prefs(Context context) {
        return context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
    }

    // The launch activity is always resolved automatically (the bridge only serves the game's
    // main activity). Older builds stored an override, sometimes as the localized "automatic"
    // label, so any stored value is legacy and is dropped rather than compared to a label.
    public static boolean dropActivityOverrideForGame(Context context, String targetPackage) {
        String key = targetPackage + ":" + KEY_ACTIVITY_OVERRIDE;
        SharedPreferences prefs = prefs(context);
        if (!prefs.contains(key)) {
            return false;
        }
        prefs.edit().remove(key).apply();
        return true;
    }
}
