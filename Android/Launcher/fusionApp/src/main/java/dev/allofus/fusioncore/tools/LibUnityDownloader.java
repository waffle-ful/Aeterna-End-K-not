package dev.allofus.fusioncore.tools;

import android.util.Log;

import java.io.BufferedInputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.security.MessageDigest;
import java.util.Locale;
import java.util.Map;
import java.util.Properties;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.FutureTask;

public final class LibUnityDownloader {
    private static final String TAG = "FusionCore";

    /**
     * Base URLs tried in order. Each must serve
     * {@code <base><unityVersion>/libunity.so.<abi>} and the matching {@code libunity.sym.so.<abi>}.
     * A mirror that has the same release assets can be appended here without other changes.
     */
    private static final String[] DOWNLOAD_BASE_URLS = {
            "https://github.com/All-Of-Us-Mods/FusionCore.UnityDependencies/releases/download/",
    };

    private static final int ATTEMPTS_PER_URL = 3;
    private static final long RETRY_BACKOFF_MS = 1000L;

    private static final String LIBUNITY_CACHE_META_FILE = "libunity.cache.properties";

    public interface DownloadProgressListener {
        void onDownloadStarted(String url, long totalBytes);
        void onDownloadProgress(long downloadedBytes, long totalBytes);
        void onDownloadFinished(boolean success, boolean usedCache);
    }

    public static boolean downloadAndCacheSafely(File outputDir,
                                                 String version,
                                                 String targetGameAbi,
                                                 DownloadProgressListener progressListener) {
        FutureTask<Boolean> task = new FutureTask<>(() -> downloadAndCache(outputDir, version, targetGameAbi, progressListener));
        Thread worker = new Thread(task, "FusionCore-LibUnityDownload");
        worker.start();

        try {
            return task.get();
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            Log.e(TAG, "Libunity download thread was interrupted", e);
            return false;
        } catch (ExecutionException e) {
            Log.e(TAG, "Libunity download failed", e.getCause() != null ? e.getCause() : e);
            return false;
        }
    }

    public static boolean downloadAndCache(File outputDir,
                                           String version,
                                           String targetGameAbi,
                                           DownloadProgressListener progressListener) {
        if (outputDir == null || version == null || version.trim().isEmpty()) {
            Log.e(TAG, "downloadAndCache called with invalid arguments");
            notifyDownloadFinished(progressListener, false, false);
            return false;
        }

        if (!outputDir.exists() && !outputDir.mkdirs()) {
            Log.e(TAG, "Failed to create output directory: " + outputDir.getAbsolutePath());
            notifyDownloadFinished(progressListener, false, false);
            return false;
        }

        String currentAbi = normalizeAbiForDownload(targetGameAbi);
        if (currentAbi == null) {
            Log.e(TAG, "Target game ABI is missing or unsupported: " + targetGameAbi);
            notifyDownloadFinished(progressListener, false, false);
            return false;
        }

        File outputLibUnity = new File(outputDir, "libunity.so");
        File outputLibUnitySym = new File(outputDir, "libunity.sym.so");
        File cacheMetaFile = new File(outputDir, LIBUNITY_CACHE_META_FILE);

        String downloadVersion = version.trim();
        String cacheKey = downloadVersion + "|" + currentAbi;

        if (isCachedLibUnityValid(outputLibUnity, outputLibUnitySym, cacheMetaFile, cacheKey)) {
            Log.i(TAG, "Using cached libunity and symbols for " + cacheKey + " at " + outputDir.getAbsolutePath());
            notifyDownloadFinished(progressListener, true, true);
            return true;
        }

        String libAsset = "libunity.so." + currentAbi;
        String symAsset = "libunity.sym.so." + currentAbi;
        Map<String, String> expected = LibUnityChecksums.resolve(downloadVersion, libAsset, symAsset);
        String libSha = expected.get(libAsset);
        String symSha = expected.get(symAsset);
        if (libSha == null || symSha == null) {
            Log.w(TAG, "No known digest for Unity " + downloadVersion + " (" + currentAbi + "); download will not be verified");
        }

        String libSeen = downloadWithRetries(downloadVersion, libAsset, libSha, outputLibUnity, progressListener);
        if (libSeen == null) {
            notifyDownloadFinished(progressListener, false, false);
            return false;
        }

        String symSeen = downloadWithRetries(downloadVersion, symAsset, symSha, outputLibUnitySym, progressListener);
        if (symSeen == null) {
            notifyDownloadFinished(progressListener, false, false);
            return false;
        }

        if (!writeLibUnityCacheMeta(cacheMetaFile, cacheKey, outputLibUnity.length(), outputLibUnitySym.length(),
                libSeen, symSeen, libSha != null && symSha != null)) {
            Log.w(TAG, "Downloaded files but failed to update cache metadata");
        }

        Log.i(TAG, "Successfully downloaded libunity and symbols to " + outputDir.getAbsolutePath());
        notifyDownloadFinished(progressListener, true, false);
        return true;
    }

    /**
     * Tries every base URL, each up to {@link #ATTEMPTS_PER_URL} times with linear backoff.
     * Returns the sha256 of the file that ended up in place, or null when every attempt failed.
     */
    private static String downloadWithRetries(String version,
                                              String assetName,
                                              String expectedSha256,
                                              File outputFile,
                                              DownloadProgressListener progressListener) {
        for (String base : DOWNLOAD_BASE_URLS) {
            String url = base + version + "/" + assetName;
            for (int attempt = 1; attempt <= ATTEMPTS_PER_URL; attempt++) {
                Log.i(TAG, "Downloading " + assetName + " from " + base + " (attempt " + attempt + "/" + ATTEMPTS_PER_URL + ")");
                DownloadOutcome outcome = downloadUrlToFile(url, outputFile, expectedSha256, progressListener);
                if (outcome.sha256 != null) {
                    return outcome.sha256;
                }
                if (outcome.permanent) {
                    // A wrong digest or a missing asset will not change on retry; move to the next source.
                    Log.w(TAG, "Giving up on " + base + " for " + assetName + " (permanent failure)");
                    break;
                }
                if (attempt < ATTEMPTS_PER_URL) {
                    try {
                        Thread.sleep(RETRY_BACKOFF_MS * attempt);
                    } catch (InterruptedException e) {
                        Thread.currentThread().interrupt();
                        return null;
                    }
                }
            }
        }
        Log.e(TAG, "All download sources failed for " + assetName + " (" + version + ")");
        return null;
    }

    /**
     * Downloads one file to a temp path, hashing it as it streams, and moves it into place only
     * when the size matches the server's Content-Length and the digest matches the expectation
     * (when one is known).
     */
    private static final class DownloadOutcome {
        /** sha256 hex of the file now in place, or null on failure. */
        final String sha256;
        /** true when retrying the same URL cannot succeed (HTTP 4xx, digest mismatch, local I/O). */
        final boolean permanent;

        private DownloadOutcome(String sha256, boolean permanent) {
            this.sha256 = sha256;
            this.permanent = permanent;
        }

        static DownloadOutcome success(String sha256) { return new DownloadOutcome(sha256, false); }
        static DownloadOutcome transientFailure() { return new DownloadOutcome(null, false); }
        static DownloadOutcome permanentFailure() { return new DownloadOutcome(null, true); }
    }

    private static DownloadOutcome downloadUrlToFile(String urlString,
                                                     File outputFile,
                                                     String expectedSha256,
                                                     DownloadProgressListener progressListener) {
        HttpURLConnection connection = null;
        File tempFile = new File(outputFile.getParentFile(), outputFile.getName() + ".download");

        try {
            connection = (HttpURLConnection) new URL(urlString).openConnection();
            connection.setRequestMethod("GET");
            connection.setConnectTimeout(15000);
            connection.setReadTimeout(30000);
            connection.setInstanceFollowRedirects(true);

            int statusCode = connection.getResponseCode();
            if (statusCode < 200 || statusCode >= 300) {
                Log.e(TAG, "Failed to download file from " + urlString + ", HTTP " + statusCode);
                return statusCode >= 400 && statusCode < 500 && statusCode != 429
                        ? DownloadOutcome.permanentFailure()
                        : DownloadOutcome.transientFailure();
            }

            long totalBytes = connection.getContentLengthLong();
            notifyDownloadStarted(progressListener, urlString, totalBytes);

            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            byte[] buffer = new byte[65536];
            long downloadedBytes = 0L;
            long lastProgressDispatchMs = 0L;

            try (InputStream is = new BufferedInputStream(connection.getInputStream());
                 FileOutputStream fos = new FileOutputStream(tempFile, false)) {
                int count;
                while ((count = is.read(buffer)) != -1) {
                    fos.write(buffer, 0, count);
                    digest.update(buffer, 0, count);
                    downloadedBytes += count;

                    long now = System.currentTimeMillis();
                    if (now - lastProgressDispatchMs >= 120L) {
                        notifyDownloadProgress(progressListener, downloadedBytes, totalBytes);
                        lastProgressDispatchMs = now;
                    }
                }
                fos.getFD().sync();
            }

            notifyDownloadProgress(progressListener, downloadedBytes, totalBytes);

            if (totalBytes > 0 && downloadedBytes != totalBytes) {
                Log.e(TAG, "Incomplete download from " + urlString + ": " + downloadedBytes + "/" + totalBytes + " bytes");
                return DownloadOutcome.transientFailure();
            }
            if (downloadedBytes <= 0) {
                Log.e(TAG, "Empty download from " + urlString);
                return DownloadOutcome.transientFailure();
            }

            String actualSha256 = toHex(digest.digest());
            if (expectedSha256 != null && !expectedSha256.equalsIgnoreCase(actualSha256)) {
                Log.e(TAG, "Digest mismatch for " + urlString + ": expected " + expectedSha256 + ", got " + actualSha256);
                return DownloadOutcome.permanentFailure();
            }

            if (outputFile.exists() && !outputFile.delete()) {
                Log.e(TAG, "Failed to replace existing file: " + outputFile.getAbsolutePath());
                return DownloadOutcome.permanentFailure();
            }

            if (!tempFile.renameTo(outputFile)) {
                Log.e(TAG, "Failed to move downloaded file into place: " + outputFile.getAbsolutePath());
                return DownloadOutcome.permanentFailure();
            }

            return DownloadOutcome.success(actualSha256);
        } catch (Exception e) {
            Log.e(TAG, "Failed to download " + urlString, e);
            return DownloadOutcome.transientFailure();
        } finally {
            if (connection != null) {
                connection.disconnect();
            }
            if (tempFile.exists() && !tempFile.delete()) {
                Log.w(TAG, "Failed to clean temporary file: " + tempFile.getAbsolutePath());
            }
        }
    }

    private static String toHex(byte[] bytes) {
        StringBuilder sb = new StringBuilder(bytes.length * 2);
        for (byte b : bytes) {
            sb.append(String.format(Locale.ROOT, "%02x", b));
        }
        return sb.toString();
    }

    private static void notifyDownloadStarted(DownloadProgressListener listener, String url, long totalBytes) {
        if (listener != null) {
            listener.onDownloadStarted(url, totalBytes);
        }
    }

    private static void notifyDownloadProgress(DownloadProgressListener listener, long downloadedBytes, long totalBytes) {
        if (listener != null) {
            listener.onDownloadProgress(downloadedBytes, totalBytes);
        }
    }

    private static void notifyDownloadFinished(DownloadProgressListener listener, boolean success, boolean usedCache) {
        if (listener != null) {
            listener.onDownloadFinished(success, usedCache);
        }
    }

    private static boolean isCachedLibUnityValid(File outputLibUnity, File outputLibUnitySym, File cacheMetaFile, String expectedCacheKey) {
        if (!outputLibUnity.exists() || !outputLibUnity.isFile() || outputLibUnity.length() <= 0) {
            return false;
        }
        if (!outputLibUnitySym.exists() || !outputLibUnitySym.isFile() || outputLibUnitySym.length() <= 0) {
            return false;
        }
        if (!cacheMetaFile.exists() || !cacheMetaFile.isFile()) {
            return false;
        }

        Properties meta = new Properties();
        try (FileInputStream fis = new FileInputStream(cacheMetaFile)) {
            meta.load(fis);
        } catch (IOException e) {
            Log.w(TAG, "Failed reading libunity cache metadata", e);
            return false;
        }

        String actualKey = meta.getProperty("cacheKey", "");
        if (!expectedCacheKey.equals(actualKey)) {
            return false;
        }

        try {
            long expectedSize = Long.parseLong(meta.getProperty("libunitySize", "0"));
            long expectedSymSize = Long.parseLong(meta.getProperty("libunitySymSize", "0"));
            return expectedSize > 0 && expectedSize == outputLibUnity.length() &&
                   expectedSymSize > 0 && expectedSymSize == outputLibUnitySym.length();
        } catch (NumberFormatException e) {
            Log.w(TAG, "Invalid libunity cache metadata size", e);
            return false;
        }
    }

    private static boolean writeLibUnityCacheMeta(File cacheMetaFile,
                                                  String cacheKey,
                                                  long libunitySize,
                                                  long libunitySymSize,
                                                  String libunitySha256,
                                                  String libunitySymSha256,
                                                  boolean verified) {
        Properties meta = new Properties();
        meta.setProperty("cacheKey", cacheKey);
        meta.setProperty("libunitySize", Long.toString(libunitySize));
        meta.setProperty("libunitySymSize", Long.toString(libunitySymSize));
        meta.setProperty("libunitySha256", libunitySha256);
        meta.setProperty("libunitySymSha256", libunitySymSha256);
        meta.setProperty("verified", Boolean.toString(verified));

        try (FileOutputStream fos = new FileOutputStream(cacheMetaFile, false)) {
            meta.store(fos, "libunity cache metadata");
            return true;
        } catch (IOException e) {
            Log.w(TAG, "Failed writing libunity cache metadata", e);
            return false;
        }
    }

    private static String normalizeAbiForDownload(String abiValue) {
        if (abiValue == null) {
            return null;
        }

        String normalized = abiValue.trim().toLowerCase(Locale.ROOT);
        if (normalized.isEmpty()) {
            return null;
        }

        int slash = normalized.lastIndexOf('/');
        if (slash >= 0 && slash < normalized.length() - 1) {
            normalized = normalized.substring(slash + 1);
        }

        int backslash = normalized.lastIndexOf('\\');
        if (backslash >= 0 && backslash < normalized.length() - 1) {
            normalized = normalized.substring(backslash + 1);
        }

        return switch (normalized) {
            case "arm64", "aarch64", "arm64-v8a" -> "arm64-v8a";
            case "armeabi-v7a", "armeabi", "armv7" -> "armeabi-v7a";
            case "x86" -> "x86";
            case "x86_64", "x64" -> "x86_64";
            default -> null;
        };
    }
}
