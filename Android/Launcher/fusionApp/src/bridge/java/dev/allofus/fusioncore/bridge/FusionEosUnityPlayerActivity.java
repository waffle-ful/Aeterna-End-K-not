package dev.allofus.fusioncore.bridge;

import android.os.Bundle;

/**
 * Stand-in for the game's main activity. It is compiled against stubs, dexed on its own
 * (fusion-bridge.dex) and defined by a loader whose parent is the game class loader, so
 * its superclass chain is the game's real EosUnityPlayerActivity.
 *
 * <p>{@code onCreate} deliberately does not call {@code super.onCreate}: the two inherited
 * implementations are replayed by {@link UnityActivityHost#onCreate} with the one
 * difference that matters, the Context handed to the UnityPlayer constructor.
 */
public class FusionEosUnityPlayerActivity extends com.innersloth.spacemafia.EosUnityPlayerActivity {
    @Override
    protected void onCreate(Bundle icicle) {
        UnityActivityHost.onCreate(this, icicle);
    }
}
