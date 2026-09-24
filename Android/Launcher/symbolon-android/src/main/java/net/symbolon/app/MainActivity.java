package net.symbolon.app;

import android.os.Bundle;
import android.os.SystemClock;

import androidx.activity.ComponentActivity;
import androidx.annotation.Nullable;

import net.symbolon.ui.SymbolonEditor;

/** Standalone host of the editor, for measurements on devices without the launcher. */
public class MainActivity extends ComponentActivity {
    @Override
    protected void onCreate(@Nullable Bundle savedInstanceState) {
        long createdAt = SystemClock.elapsedRealtime();
        super.onCreate(savedInstanceState);
        SymbolonEditor.INSTANCE.attach(this, createdAt);
    }
}
