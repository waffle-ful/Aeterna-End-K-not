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
import java.util.zip.ZipEntry;
import java.util.zip.ZipInputStream;

/**
 * Installs the pre-generated Il2CppInterop assemblies bundled in the APK into BepInEx/interop.
 *
 * BepInEx generates these on the device the first time it runs against a game build, which
 * takes minutes and more memory than a phone has to spare once Unity is up. The APK instead
 * carries {@code assets/interop-arm64.zip} (interop/*.dll plus the assembly-hash.txt BepInEx
 * checks before generating) together with {@code assets/interop-arm64.properties} written by
 * tools/android-interop-pack.ps1. The launcher installs the set once per bundled identity,
 * recorded in a marker next to the BepInEx directory, and leaves it alone afterwards.
 *
 * The bundle is made for one game build (its versionCode is in the properties). A different
 * installed game would fail BepInEx's hash check and fall back to generating on the device.
 */
public final class InteropInstaller {
    private static final String TAG = "InteropInstaller";
    private static final String ASSET_ZIP = "interop-arm64.zip";
    private static final String ASSET_PROPERTIES = "interop-arm64.properties";
    private static final String MARKER_NAME = "interop.installed.properties";
    private static final String KEY_SHA256 = "sha256";
    private static final String KEY_GAME_VERSION_CODE = "gameVersionCode";
    private static final String KEY_GAME_VERSION_NAME = "gameVersionName";
    private static final String KEY_INTEROP_HASH = "interopHash";

    /** Directories the generator leaves behind; they must not outlive the bundled set. */
    private static final String[] STALE_DIRS = {"interop", "unity-libs", "dummy"};

    private InteropInstaller() {
    }

    /** Result of {@link #installBundledInterop}. */
    public enum Result {
        /** The APK carries no interop bundle; BepInEx generates on the device as before. */
        NOT_BUNDLED,
        /** The bundled set is in place (already installed or installed now). */
        INSTALLED,
        /** The bundle was made for another game build; see {@link #bundledGameVersionCode}. */
        GAME_MISMATCH,
        /** Extraction failed; whatever was under BepInEx/interop is gone and BepInEx regenerates. */
        FAILED
    }

    /** versionCode of the game the bundled set was generated for, or -1 when there is no bundle. */
    public static long bundledGameVersionCode(Context context) {
        Properties bundled = readAssetProperties(context.getAssets(), ASSET_PROPERTIES);
        return bundled == null ? -1 : parseLong(bundled.getProperty(KEY_GAME_VERSION_CODE));
    }

    /** versionName the bundle was generated against, for the update prompt. */
    public static String bundledGameVersionName(Context context) {
        Properties bundled = readAssetProperties(context.getAssets(), ASSET_PROPERTIES);
        return bundled == null ? "?" : bundled.getProperty(KEY_GAME_VERSION_NAME, "?");
    }

    /**
     * @param context           any context of the launcher package
     * @param bepInExDir        the BepInEx root the zip was extracted into
     * @param gameVersionCode   versionCode of the installed game
     */
    public static Result installBundledInterop(Context context, File bepInExDir, long gameVersionCode) {
        AssetManager assets = context.getAssets();
        Properties bundled = readAssetProperties(assets, ASSET_PROPERTIES);
        if (bundled == null) {
            Log.i(TAG, "No bundled interop in this build");
            return Result.NOT_BUNDLED;
        }
        String bundledSha = bundled.getProperty(KEY_SHA256, "").trim().toLowerCase();
        if (bundledSha.isEmpty()) {
            Log.w(TAG, "Bundled interop identity has no sha256; skipping");
            return Result.NOT_BUNDLED;
        }
        long bundledFor = parseLong(bundled.getProperty(KEY_GAME_VERSION_CODE));
        if (bundledFor > 0 && bundledFor != gameVersionCode) {
            Log.w(TAG, "Bundled interop is for game versionCode " + bundledFor + " but " + gameVersionCode + " is installed");
            return Result.GAME_MISMATCH;
        }

        File marker = new File(bepInExDir, MARKER_NAME);
        File interopDir = new File(bepInExDir, "interop");
        File hashFile = new File(interopDir, "assembly-hash.txt");
        Properties installed = readFileProperties(marker);
        String installedSha = installed == null ? "" : installed.getProperty(KEY_SHA256, "").trim().toLowerCase();
        if (hashFile.isFile() && bundledSha.equals(installedSha)) {
            Log.i(TAG, "Bundled interop already installed (" + bundledSha.substring(0, 8) + ")");
            return Result.INSTALLED;
        }

        // A generated set (or a previous bundle) is replaced wholesale: BepInEx hashes the
        // unity-libs directory into its cache key, so leftovers would invalidate the bundled hash.
        deleteQuietly(marker);
        for (String name : STALE_DIRS) {
            File dir = new File(bepInExDir, name);
            if (dir.exists() && !Utilities.deleteRecursive(dir)) {
                // Leftovers would feed BepInEx's hash and make it regenerate behind a valid marker.
                Log.e(TAG, "Could not remove " + dir.getAbsolutePath() + "; not installing the bundled interop");
                return Result.FAILED;
            }
        }

        long files;
        try {
            files = extract(assets, bepInExDir);
        } catch (IOException | RuntimeException e) {
            Log.e(TAG, "Failed to extract bundled interop", e);
            Utilities.deleteRecursive(interopDir);
            return Result.FAILED;
        }
        if (!hashFile.isFile()) {
            Log.e(TAG, "Bundled interop has no assembly-hash.txt; BepInEx would regenerate anyway");
            Utilities.deleteRecursive(interopDir);
            return Result.FAILED;
        }

        Properties record = new Properties();
        record.setProperty(KEY_SHA256, bundledSha);
        record.setProperty(KEY_GAME_VERSION_CODE, Long.toString(bundledFor));
        record.setProperty(KEY_INTEROP_HASH, bundled.getProperty(KEY_INTEROP_HASH, ""));
        try (OutputStream out = new FileOutputStream(marker)) {
            record.store(out, "bundled interop installed by the launcher");
        } catch (IOException e) {
            Log.w(TAG, "Installed interop but failed to write " + marker.getName(), e);
        }
        Log.i(TAG, "Installed bundled interop: " + files + " files (" + bundledSha.substring(0, 8)
                + (installedSha.isEmpty() ? ", first install)" : ", replaced " + installedSha.substring(0, Math.min(8, installedSha.length())) + ")"));
        return Result.INSTALLED;
    }

    /**
     * Extracts interop/ from the asset. The hash file is what makes BepInEx trust the directory,
     * so it is written last, after every DLL is on disk: an interrupted extract then leaves a
     * directory BepInEx regenerates and the launcher re-extracts, never one it accepts as complete.
     */
    private static long extract(AssetManager assets, File bepInExDir) throws IOException {
        String outputRoot = new File(bepInExDir, "interop").getCanonicalPath() + File.separator;
        byte[] buffer = new byte[64 * 1024];
        long files = 0;
        byte[] hashBytes = null;
        try (InputStream is = assets.open(ASSET_ZIP);
             ZipInputStream zis = new ZipInputStream(new BufferedInputStream(is))) {
            ZipEntry ze;
            while ((ze = zis.getNextEntry()) != null) {
                String entryName = ze.getName();
                if (entryName == null || entryName.isEmpty() || !entryName.startsWith("interop/")) {
                    zis.closeEntry();
                    continue;
                }
                File target = new File(bepInExDir, entryName);
                if (!target.getCanonicalPath().startsWith(outputRoot)) {
                    throw new IOException("Blocked zip entry outside interop/: " + entryName);
                }
                if (entryName.equals("interop/assembly-hash.txt")) {
                    java.io.ByteArrayOutputStream hash = new java.io.ByteArrayOutputStream();
                    int count;
                    while ((count = zis.read(buffer)) != -1) {
                        hash.write(buffer, 0, count);
                    }
                    hashBytes = hash.toByteArray();
                    zis.closeEntry();
                    continue;
                }
                if (ze.isDirectory()) {
                    if (!target.isDirectory() && !target.mkdirs()) {
                        throw new IOException("Failed to create " + target.getAbsolutePath());
                    }
                } else {
                    File parent = target.getParentFile();
                    if (parent != null && !parent.isDirectory() && !parent.mkdirs()) {
                        throw new IOException("Failed to create " + parent.getAbsolutePath());
                    }
                    try (FileOutputStream out = new FileOutputStream(target)) {
                        int count;
                        while ((count = zis.read(buffer)) != -1) {
                            out.write(buffer, 0, count);
                        }
                    }
                    files++;
                }
                zis.closeEntry();
            }
        }
        if (hashBytes == null) {
            return files;
        }
        File hashFile = new File(bepInExDir, "interop/assembly-hash.txt");
        File hashTemp = new File(bepInExDir, "interop/assembly-hash.txt.tmp");
        try (FileOutputStream out = new FileOutputStream(hashTemp)) {
            out.write(hashBytes);
            out.getFD().sync();
        }
        if (!hashTemp.renameTo(hashFile)) {
            throw new IOException("Failed to move " + hashTemp.getAbsolutePath() + " into place");
        }
        return files + 1;
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
