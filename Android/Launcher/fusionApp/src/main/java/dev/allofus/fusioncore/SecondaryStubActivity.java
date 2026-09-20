package dev.allofus.fusioncore;

import android.content.Intent;
import android.os.Bundle;

/**
 * Standard-launch-mode stub for the game's secondary activities (ad controllers,
 * web sign-in and similar). The single-task {@link StubActivity} hosts the main
 * game activity, and a second explicit start routed there would only be delivered
 * to the running instance instead of creating the requested activity.
 */
public class SecondaryStubActivity extends android.app.Activity {

    @Override
    protected void onCreate(Bundle bundle) {
        super.onCreate(null);
        Intent intent = new Intent(this, SelectorActivity.class);
        startActivity(intent);
        finish();
    }
}
