package dev.allofus.fusioncore;

import android.os.Bundle;
import android.os.SystemClock;

import androidx.activity.ComponentActivity;
import androidx.annotation.Nullable;

import net.symbolon.ui.SymbolonEditor;

/** Hosts the Symbolon map / role editor inside the launcher. */
public class EditorActivity extends ComponentActivity {
    @Override
    protected void onCreate(@Nullable Bundle savedInstanceState) {
        long createdAt = SystemClock.elapsedRealtime();
        super.onCreate(savedInstanceState);
        SymbolonEditor.INSTANCE.attach(this, createdAt);
    }
}
