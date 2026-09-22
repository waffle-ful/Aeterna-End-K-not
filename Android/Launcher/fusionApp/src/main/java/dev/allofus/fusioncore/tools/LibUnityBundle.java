package dev.allofus.fusioncore.tools;

import android.content.Context;
import android.content.res.AssetManager;
import android.util.Log;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.security.MessageDigest;
import java.util.Locale;

import dev.allofus.fusioncore.BuildConfig;

/**
 * Places the Unity runtime that ships inside the APK into the code cache, where the native side
 * expects {@code libunity.so} and {@code libunity.sym.so}. The build embeds the files for exactly
 * one Unity version and ABI (see {@link BuildConfig#BUNDLED_UNITY_VERSION}); the digest of each
 * file is checked while copying, and the result is recorded with the same cache metadata the
 * downloader writes, so later launches take the cached path without touching the assets again.
 */
public final class LibUnityBundle {
    private static final String TAG = "FusionCore";
    private static final String ASSET_ROOT = "libunity";

    public enum Result {
        /** Files were copied from the APK and verified. */
        INSTALLED,
        /** A verified copy for this version and ABI was already in place. */
        CACHED,
        /** The APK does not carry this Unity version or ABI. */
        NOT_BUNDLED,
        /** Copying or verification failed; the cache directory holds no usable copy. */
        FAILED
    }

    private LibUnityBundle() {
    }

    public static boolean isBundled(String unityVersion, String targetGameAbi) {
        String abi = LibUnityDownloader.normalizeAbiForDownload(targetGameAbi);
        return unityVersion != null
                && unityVersion.trim().equals(BuildConfig.BUNDLED_UNITY_VERSION)
                && BuildConfig.BUNDLED_LIBUNITY_ABI.equals(abi);
    }

    public static Result install(Context context, File outputDir, String unityVersion, String targetGameAbi) {
        if (!isBundled(unityVersion, targetGameAbi)) {
            return Result.NOT_BUNDLED;
        }
        String abi = BuildConfig.BUNDLED_LIBUNITY_ABI;
        String version = BuildConfig.BUNDLED_UNITY_VERSION;
        if (!outputDir.exists() && !outputDir.mkdirs()) {
            Log.e(TAG, "Failed to create output directory: " + outputDir.getAbsolutePath());
            return Result.FAILED;
        }

        File lib = new File(outputDir, "libunity.so");
        File sym = new File(outputDir, "libunity.sym.so");
        File meta = new File(outputDir, "libunity.cache.properties");
        String cacheKey = version + "|" + abi;
        if (LibUnityDownloader.isCachedLibUnityValid(lib, sym, meta, cacheKey)) {
            return Result.CACHED;
        }

        String assetDir = ASSET_ROOT + "/" + version + "/" + abi + "/";
        AssetManager assets = context.getAssets();
        try {
            String libSeen = copyVerified(assets, assetDir + "libunity.so", lib, BuildConfig.BUNDLED_LIBUNITY_SHA256);
            String symSeen = copyVerified(assets, assetDir + "libunity.sym.so", sym, BuildConfig.BUNDLED_LIBUNITY_SYM_SHA256);
            if (!LibUnityDownloader.writeLibUnityCacheMeta(meta, cacheKey, lib.length(), sym.length(), libSeen, symSeen, true)) {
                Log.w(TAG, "Copied bundled libunity but failed to write cache metadata");
            }
            Log.i(TAG, "Installed bundled libunity " + cacheKey + " into " + outputDir.getAbsolutePath());
            return Result.INSTALLED;
        } catch (Exception e) {
            Log.e(TAG, "Bundled libunity install failed", e);
            // Leave nothing half-written behind: the native side only checks for existence.
            //noinspection ResultOfMethodCallIgnored
            lib.delete();
            //noinspection ResultOfMethodCallIgnored
            sym.delete();
            //noinspection ResultOfMethodCallIgnored
            meta.delete();
            //noinspection ResultOfMethodCallIgnored
            new File(lib.getPath() + ".tmp").delete();
            //noinspection ResultOfMethodCallIgnored
            new File(sym.getPath() + ".tmp").delete();
            return Result.FAILED;
        }
    }

    /** Streams one asset to {@code target} via a temp file, returning its SHA-256 once it matches. */
    private static String copyVerified(AssetManager assets, String assetPath, File target, String expectedSha256) throws Exception {
        File temp = new File(target.getPath() + ".tmp");
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        try (InputStream in = assets.open(assetPath, AssetManager.ACCESS_STREAMING);
             FileOutputStream out = new FileOutputStream(temp, false)) {
            byte[] buffer = new byte[256 * 1024];
            int read;
            while ((read = in.read(buffer)) > 0) {
                digest.update(buffer, 0, read);
                out.write(buffer, 0, read);
            }
            out.getFD().sync();
        }
        String seen = toHex(digest.digest());
        if (!seen.equalsIgnoreCase(expectedSha256)) {
            //noinspection ResultOfMethodCallIgnored
            temp.delete();
            throw new IOException("Digest mismatch for " + assetPath + ": expected " + expectedSha256 + ", got " + seen);
        }
        if (!temp.renameTo(target)) {
            //noinspection ResultOfMethodCallIgnored
            temp.delete();
            throw new IOException("Failed to move " + temp.getName() + " into place");
        }
        return seen;
    }

    private static String toHex(byte[] bytes) {
        StringBuilder sb = new StringBuilder(bytes.length * 2);
        for (byte b : bytes) {
            sb.append(String.format(Locale.US, "%02x", b));
        }
        return sb.toString();
    }
}
