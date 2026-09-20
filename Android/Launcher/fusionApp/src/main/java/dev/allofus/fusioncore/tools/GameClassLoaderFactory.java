package dev.allofus.fusioncore.tools;

import android.content.pm.ApplicationInfo;
import android.util.Log;

import dalvik.system.DelegateLastClassLoader;

import java.io.File;
import java.util.ArrayList;
import java.util.List;

/**
 * Builds the class loader used to load the target game's code.
 * <p>
 * The loader is parented to the launcher's own class loader, so game classes can
 * see the launcher (and the framework) without any loadClass hooks, while lookups
 * still prefer the game's own dex files over the parent.
 */
public final class GameClassLoaderFactory {

    private static final String TAG = "GameClassLoaderFactory";

    private GameClassLoaderFactory() {
    }

    /**
     * Creates a class loader for the game described by {@code info}.
     * Must be called before anything mutates the shared {@link ApplicationInfo}
     * (for example the native library directory override applied at runtime).
     */
    public static ClassLoader create(ApplicationInfo info, ClassLoader parent) {
        List<String> dexPaths = new ArrayList<>();
        if (info.sourceDir != null && !info.sourceDir.isEmpty()) {
            dexPaths.add(info.sourceDir);
        }
        if (info.splitSourceDirs != null) {
            for (String split : info.splitSourceDirs) {
                if (split != null && !split.isEmpty()) {
                    dexPaths.add(split);
                }
            }
        }
        if (dexPaths.isEmpty()) {
            throw new IllegalStateException("Game package has no dex sources: " + info.packageName);
        }

        String dexPath = String.join(File.pathSeparator, dexPaths);
        String librarySearchPath = info.nativeLibraryDir;

        Log.i(TAG, "Creating game class loader: dexPath=" + dexPath
                + " librarySearchPath=" + librarySearchPath
                + " parent=" + (parent == null ? "null" : parent.getClass().getName()));

        return new DelegateLastClassLoader(dexPath, librarySearchPath, parent);
    }
}
