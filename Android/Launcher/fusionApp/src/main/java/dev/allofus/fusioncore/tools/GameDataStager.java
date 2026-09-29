package dev.allofus.fusioncore.tools;

import android.content.pm.PackageInfo;
import android.content.res.AssetManager;
import android.util.Log;

import androidx.core.content.pm.PackageInfoCompat;

import java.io.BufferedInputStream;
import java.io.File;
import java.io.FileNotFoundException;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.Properties;

/**
 * Stages the few files BepInEx reads from the game's Unity data directory. The full
 * {@code bin/Data} tree is hundreds of megabytes, but the runtime only ever opens the IL2CPP
 * metadata (when it has to regenerate the interop assemblies) and one of the player files that
 * carry the Unity version. Everything else is read by Unity straight from the APK.
 * <p>
 * The copy is keyed on the installed game build, so it is redone once per game update and
 * skipped on every other launch. Dropping in or removing a {@code global-metadata.dat} override
 * next to the BepInEx tree also triggers a fresh copy, so the original metadata comes back when
 * the override is removed.
 */
public final class GameDataStager {
    private static final String TAG = "GameDataStager";
    private static final String ASSET_ROOT = "bin/Data";
    private static final String STAMP_NAME = ".staged.properties";
    private static final String KEY_SOURCE = "gameSourceDir";
    private static final String KEY_UPDATED = "gameLastUpdateTime";
    private static final String KEY_VERSION_CODE = "gameVersionCode";
    private static final String KEY_OVERRIDE = "metadataOverride";
    private static final String KEY_LAYOUT = "layout";
    /** Bump when the set of staged files changes, so an older stage is redone. */
    private static final String LAYOUT = "1";

    public static final String METADATA_RELATIVE_PATH = "Managed/Metadata/global-metadata.dat";
    /** Files the Unity version can be read from; a build ships one of them. */
    private static final String[] VERSION_FILES = {"globalgamemanagers", "data.unity3d", "mainData"};

    private GameDataStager() {
    }

    /**
     * Makes sure {@code target} holds the staged files for the installed game build.
     *
     * @param gameAssets       the game's asset manager
     * @param gameInfo         the installed game package, or null when it could not be read
     * @param target           the directory handed to BepInEx as the game data directory
     * @param metadataOverride the optional override metadata file; used only for its identity
     * @return true when the staged files are in place afterwards
     */
    public static boolean stage(AssetManager gameAssets, PackageInfo gameInfo, File target, File metadataOverride) {
        Properties expected = describe(gameInfo, metadataOverride);
        File stamp = new File(target, STAMP_NAME);
        if (expected != null && matches(readProperties(stamp), expected) && new File(target, METADATA_RELATIVE_PATH).isFile()) {
            Log.i(TAG, "Unity data already staged in " + target.getAbsolutePath());
            return true;
        }

        // The stamp vouches for a complete stage, so it goes first: whatever happens below, an
        // old stamp must not describe a half-written tree.
        if (stamp.isFile() && !stamp.delete()) {
            Log.e(TAG, "Could not remove " + stamp.getAbsolutePath());
            return false;
        }
        // A stale or unknown stage is replaced whole; this also clears any full copy an earlier
        // launcher left behind.
        if (!Utilities.deleteRecursive(target)) {
            Log.w(TAG, "Could not clear " + target.getAbsolutePath() + "; staging on top of it");
        }
        if (!target.isDirectory() && !target.mkdirs()) {
            Log.e(TAG, "Failed to create " + target.getAbsolutePath());
            return false;
        }

        try {
            if (!copyAsset(gameAssets, METADATA_RELATIVE_PATH, new File(target, METADATA_RELATIVE_PATH))) {
                Log.e(TAG, "The game has no " + METADATA_RELATIVE_PATH + " asset");
                return false;
            }
            int versionFiles = 0;
            for (String name : VERSION_FILES) {
                if (copyAsset(gameAssets, name, new File(target, name))) {
                    versionFiles++;
                }
            }
            if (versionFiles == 0) {
                // The version guard falls back to a placeholder without one of these; the
                // stage is retried every launch rather than recorded as good.
                Log.w(TAG, "No Unity version file found among the game assets; staged without a stamp");
                return true;
            }
        } catch (IOException e) {
            Log.e(TAG, "Failed to stage Unity data", e);
            return false;
        }

        if (expected == null) {
            // Without a game identity the stage cannot be trusted next time, so no stamp is
            // written and the copy is redone on the next launch.
            Log.w(TAG, "Game package identity unavailable; staged without a stamp");
            return true;
        }
        try (OutputStream out = new FileOutputStream(stamp)) {
            expected.store(out, "Unity data staged by the launcher");
        } catch (IOException e) {
            Log.w(TAG, "Staged Unity data but failed to write " + stamp.getName(), e);
        }
        Log.i(TAG, "Staged Unity data into " + target.getAbsolutePath());
        return true;
    }

    private static Properties describe(PackageInfo gameInfo, File metadataOverride) {
        if (gameInfo == null || gameInfo.applicationInfo == null) {
            return null;
        }
        Properties p = new Properties();
        p.setProperty(KEY_LAYOUT, LAYOUT);
        p.setProperty(KEY_SOURCE, String.valueOf(gameInfo.applicationInfo.sourceDir));
        p.setProperty(KEY_UPDATED, Long.toString(gameInfo.lastUpdateTime));
        p.setProperty(KEY_VERSION_CODE, Long.toString(PackageInfoCompat.getLongVersionCode(gameInfo)));
        p.setProperty(KEY_OVERRIDE, metadataOverride != null && metadataOverride.isFile()
                ? metadataOverride.length() + ":" + metadataOverride.lastModified()
                : "none");
        return p;
    }

    private static boolean matches(Properties actual, Properties expected) {
        if (actual == null) {
            return false;
        }
        for (String key : expected.stringPropertyNames()) {
            if (!expected.getProperty(key).equals(actual.getProperty(key))) {
                return false;
            }
        }
        return true;
    }

    /** Copies one asset; returns false when the game does not ship it. */
    private static boolean copyAsset(AssetManager assets, String relativePath, File target) throws IOException {
        File parent = target.getParentFile();
        if (parent != null && !parent.isDirectory() && !parent.mkdirs()) {
            throw new IOException("Failed to create " + parent.getAbsolutePath());
        }
        InputStream in;
        try {
            in = assets.open(ASSET_ROOT + "/" + relativePath);
        } catch (FileNotFoundException e) {
            return false;
        }
        byte[] buffer = new byte[64 * 1024];
        try (InputStream is = new BufferedInputStream(in);
             OutputStream os = new FileOutputStream(target)) {
            int length;
            while ((length = is.read(buffer)) > 0) {
                os.write(buffer, 0, length);
            }
        }
        return true;
    }

    private static Properties readProperties(File file) {
        if (!file.isFile()) {
            return null;
        }
        try (InputStream in = new java.io.FileInputStream(file)) {
            Properties properties = new Properties();
            properties.load(new java.io.InputStreamReader(in, StandardCharsets.UTF_8));
            return properties;
        } catch (IOException e) {
            return null;
        }
    }
}
