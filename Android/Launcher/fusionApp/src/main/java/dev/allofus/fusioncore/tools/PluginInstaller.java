package dev.allofus.fusioncore.tools;

import android.content.Context;
import android.content.res.AssetManager;
import android.util.Log;

import java.io.BufferedInputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileNotFoundException;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.Properties;

/**
 * Installs the plugin DLL bundled in the APK into the BepInEx plugins directory.
 *
 * The APK carries {@code assets/plugins/<name>.dll} together with
 * {@code assets/plugins/<name>.dll.properties} (version, sha256, size) written by the C# build.
 * The launcher records the identity it last installed in a marker file next to the BepInEx
 * directory and only rewrites the DLL when the bundled identity differs from that marker or
 * the DLL is missing. The DLL itself is never hashed on the device, so a build pushed by hand
 * into plugins/ stays in place until a different APK build is installed.
 */
public final class PluginInstaller {
    private static final String TAG = "PluginInstaller";
    private static final String ASSET_DIR = "plugins";
    private static final String PROPERTIES_SUFFIX = ".properties";
    private static final String MARKER_SUFFIX = ".installed.properties";
    private static final String KEY_SHA256 = "sha256";
    private static final String KEY_VERSION = "version";
    private static final String KEY_SIZE = "size";

    private PluginInstaller() {
    }

    /**
     * Installs every bundled plugin whose identity differs from the installed marker.
     *
     * @param context   any context of the launcher package
     * @param bepInExDir the BepInEx root on external storage (plugins/ is created beneath it)
     * @return true when every bundled plugin is present and matches its marker afterwards
     */
    public static boolean installBundledPlugins(Context context, File bepInExDir) {
        AssetManager assets = context.getAssets();
        String[] entries;
        try {
            entries = assets.list(ASSET_DIR);
        } catch (IOException e) {
            Log.w(TAG, "Failed to list bundled plugins", e);
            return false;
        }
        if (entries == null || entries.length == 0) {
            Log.i(TAG, "No bundled plugins in this build");
            return true;
        }

        boolean allOk = true;
        for (String entry : entries) {
            if (!entry.endsWith(".dll")) {
                continue;
            }
            allOk &= installOne(assets, bepInExDir, entry);
        }
        return allOk;
    }

    private static boolean installOne(AssetManager assets, File bepInExDir, String dllName) {
        Properties bundled = readAssetProperties(assets, ASSET_DIR + "/" + dllName + PROPERTIES_SUFFIX);
        if (bundled == null) {
            Log.w(TAG, "Bundled " + dllName + " has no identity file; skipping");
            return false;
        }
        String bundledSha = bundled.getProperty(KEY_SHA256, "").trim().toLowerCase();
        String bundledVersion = bundled.getProperty(KEY_VERSION, "").trim();
        if (bundledSha.isEmpty()) {
            Log.w(TAG, "Bundled " + dllName + " identity has no sha256; skipping");
            return false;
        }

        File pluginsDir = new File(bepInExDir, "plugins");
        File target = new File(pluginsDir, dllName);
        File marker = new File(bepInExDir, dllName + MARKER_SUFFIX);

        Properties installed = readFileProperties(marker);
        String installedSha = installed == null ? "" : installed.getProperty(KEY_SHA256, "").trim().toLowerCase();
        if (target.isFile() && bundledSha.equals(installedSha)) {
            Log.i(TAG, dllName + " " + bundledVersion + " already installed");
            return true;
        }

        if (!pluginsDir.isDirectory() && !pluginsDir.mkdirs()) {
            Log.e(TAG, "Failed to create " + pluginsDir.getAbsolutePath());
            return false;
        }

        File temp = new File(pluginsDir, dllName + ".tmp");
        long copied;
        try (InputStream in = new BufferedInputStream(assets.open(ASSET_DIR + "/" + dllName));
             FileOutputStream out = new FileOutputStream(temp)) {
            copied = copy(in, out);
            // The marker written below promises that the file on disk is complete, so the
            // bytes must reach storage before the rename makes them visible under the final name.
            out.getFD().sync();
        } catch (IOException e) {
            Log.e(TAG, "Failed to extract bundled " + dllName, e);
            deleteQuietly(temp);
            return false;
        }

        long expectedSize = parseLong(bundled.getProperty(KEY_SIZE));
        if (expectedSize > 0 && copied != expectedSize) {
            Log.e(TAG, "Bundled " + dllName + " size mismatch: expected " + expectedSize + ", got " + copied);
            deleteQuietly(temp);
            return false;
        }

        // rename(2) replaces an existing target atomically, so there is never a moment where
        // neither the old nor the new DLL exists under the final name.
        if (!temp.renameTo(target)) {
            Log.e(TAG, "Failed to move " + temp.getAbsolutePath() + " into place");
            deleteQuietly(temp);
            return false;
        }

        Properties record = new Properties();
        record.setProperty(KEY_SHA256, bundledSha);
        record.setProperty(KEY_VERSION, bundledVersion);
        record.setProperty(KEY_SIZE, Long.toString(copied));
        try (OutputStream out = new FileOutputStream(marker)) {
            record.store(out, "bundled plugin installed by the launcher");
        } catch (IOException e) {
            Log.w(TAG, "Installed " + dllName + " but failed to write " + marker.getName(), e);
        }

        Log.i(TAG, "Installed " + dllName + " " + bundledVersion + " (" + copied + " bytes, "
                + (installedSha.isEmpty() ? "first install" : "replaced " + installedSha.substring(0, Math.min(8, installedSha.length()))) + ")");
        return true;
    }

    private static Properties readAssetProperties(AssetManager assets, String path) {
        try (InputStream in = assets.open(path)) {
            return load(in);
        } catch (FileNotFoundException e) {
            return null;
        } catch (IOException e) {
            Log.w(TAG, "Failed to read " + path, e);
            return null;
        }
    }

    private static Properties readFileProperties(File file) {
        if (!file.isFile()) {
            return null;
        }
        try (InputStream in = new FileInputStream(file)) {
            return load(in);
        } catch (IOException e) {
            Log.w(TAG, "Failed to read " + file.getAbsolutePath(), e);
            return null;
        }
    }

    private static Properties load(InputStream in) throws IOException {
        Properties properties = new Properties();
        properties.load(new java.io.InputStreamReader(in, StandardCharsets.UTF_8));
        return properties;
    }

    private static long copy(InputStream in, OutputStream out) throws IOException {
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = in.read(buffer)) != -1) {
            out.write(buffer, 0, read);
            total += read;
        }
        out.flush();
        return total;
    }

    private static long parseLong(String value) {
        if (value == null) {
            return -1;
        }
        try {
            return Long.parseLong(value.trim());
        } catch (NumberFormatException e) {
            return -1;
        }
    }

    private static void deleteQuietly(File file) {
        if (file.exists() && !file.delete()) {
            Log.w(TAG, "Failed to delete " + file.getAbsolutePath());
        }
    }
}
