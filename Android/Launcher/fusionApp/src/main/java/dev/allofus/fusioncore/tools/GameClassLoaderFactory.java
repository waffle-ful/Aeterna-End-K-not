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
     * <p>
     * {@code preferredLibDirs} are searched for native libraries before the game's own
     * directory, in the given order. {@link dalvik.system.BaseDexClassLoader#findLibrary}
     * returns the first directory that holds the file at lookup time, so a launcher-provided
     * {@code libmain.so} or a patched {@code libil2cpp.so} wins over the game's copy, and a
     * missing file simply falls through to the game's original.
     */
    public static ClassLoader create(ApplicationInfo info, ClassLoader parent, File... preferredLibDirs) {
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
        List<String> libDirs = new ArrayList<>();
        if (preferredLibDirs != null) {
            for (File dir : preferredLibDirs) {
                if (dir == null) {
                    continue;
                }
                // DexPathList keeps only the entries that are directories when the loader is
                // built and silently drops the rest, so a directory that is filled later (the
                // code cache right after an install) must already exist here.
                if (!dir.isDirectory() && !dir.mkdirs()) {
                    Log.w(TAG, "Native library directory unavailable, skipping: " + dir);
                    continue;
                }
                libDirs.add(dir.getAbsolutePath());
            }
        }
        if (info.nativeLibraryDir != null && !info.nativeLibraryDir.isEmpty()) {
            libDirs.add(info.nativeLibraryDir);
        }
        String librarySearchPath = String.join(File.pathSeparator, libDirs);

        Log.i(TAG, "Creating game class loader: dexPath=" + dexPath
                + " librarySearchPath=" + librarySearchPath
                + " parent=" + (parent == null ? "null" : parent.getClass().getName()));

        return new DelegateLastClassLoader(dexPath, librarySearchPath, parent);
    }
}
