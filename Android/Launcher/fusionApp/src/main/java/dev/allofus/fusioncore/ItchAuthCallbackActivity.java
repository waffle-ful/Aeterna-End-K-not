package dev.allofus.fusioncore;

import android.content.Intent;
import android.os.Bundle;
import android.widget.Toast;

import androidx.appcompat.app.AppCompatActivity;

import dev.allofus.fusioncore.tools.ItchAuth;

/** Receives the {@code endknot://itch-auth} redirect from the itch.io sign-in page. */
public class ItchAuthCallbackActivity extends AppCompatActivity {
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        boolean ok = ItchAuth.handleRedirect(this, SelectorActivity.TARGET_PACKAGE, getIntent().getData());
        Toast.makeText(this, ok ? R.string.itch_sign_in_done : R.string.itch_sign_in_failed, Toast.LENGTH_LONG).show();
        if (BootstrapActivity.isGameLoaded()) {
            // The game already runs in this process; the stored token takes effect on the next
            // launch, and re-entering the selector into a live game is not safe.
            finish();
            return;
        }
        Intent back = new Intent(this, SelectorActivity.class);
        back.putExtra(SelectorActivity.EXTRA_AFTER_SIGN_IN, ok);
        back.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_SINGLE_TOP);
        startActivity(back);
        finish();
    }
}
