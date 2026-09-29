package dev.allofus.fusioncore.tools;

import android.content.Context;
import android.os.Build;
import android.util.Log;
import android.view.View;
import android.view.WindowInsets;

import androidx.annotation.Nullable;
import androidx.core.content.pm.PackageInfoCompat;

import java.io.BufferedInputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.Properties;
import java.util.zip.ZipEntry;
import java.util.zip.ZipInputStream;

public class Utilities {
    private static final String TAG = "FusionCore";
    private static final String EXTRACTED_MARKER_NAME = ".extracted.properties";

    /**
     * Root of the per-game data (BepInEx tree, Unity data copy, crash notes). It lives in the
     * launcher's private files directory, so no storage permission is needed and nothing is
     * readable by other apps; the game's own data directory is pointed here as well.
     */
    private static volatile File sStorageRoot;

    /** Remembers the private files directory; idempotent, safe from any component. */
    public static void initStorage(Context context) {
        if (sStorageRoot == null) {
            sStorageRoot = context.getApplicationContext().getFilesDir();
        }
    }

    public static File getExternalFusionCoreDirectory(@Nullable String targetPackage) {
        File root = sStorageRoot;
        if (root == null) {
            throw new IllegalStateException("Utilities.initStorage was not called");
        }
        File fusionStorage = targetPackage != null ? new File(root, targetPackage) : root;
        if (!fusionStorage.exists()) {
            fusionStorage.mkdirs();
        }
        return fusionStorage;
    }

    public static void applyWindowInsets(View root, int basePadding) {
        root.setOnApplyWindowInsetsListener((v, insets) -> {
            int insetTop;
            int insetBottom;
            int insetLeft;
            int insetRight;

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars());
                insetTop = bars.top;
                insetBottom = bars.bottom;
                insetLeft = bars.left;
                insetRight = bars.right;
            } else {
                insetTop = insets.getSystemWindowInsetTop();
                insetBottom = insets.getSystemWindowInsetBottom();
                insetLeft = insets.getSystemWindowInsetLeft();
                insetRight = insets.getSystemWindowInsetRight();
            }

            v.setPadding(
                    basePadding + insetLeft,
                    basePadding + insetTop,
                    basePadding + insetRight,
                    basePadding + insetBottom
            );
            return insets;
        });
        root.requestApplyInsets();
    }

    public static String formatVersionText(String versionName, long versionCode) {
        if (versionCode > 0L) {
            return "v" + versionName + " (" + versionCode + ")";
        }
        return "v" + versionName;
    }

    /**
     * Extracts a bundled zip once per launcher build. The launcher's own install time and
     * version code identify the bundle (assets only change with the APK), and {@code sentinel}
     * is a file the extraction must have produced; when either the marker or the sentinel is
     * missing the zip is extracted again on top of whatever is there.
     */
    public static boolean extractZipFromAssetsOnce(Context context, String assetName, File outputFolder, String sentinel) {
        Properties expected = new Properties();
        expected.setProperty("asset", assetName);
        try {
            android.content.pm.PackageInfo self = context.getPackageManager().getPackageInfo(context.getPackageName(), 0);
            expected.setProperty("launcherLastUpdateTime", Long.toString(self.lastUpdateTime));
            expected.setProperty("launcherVersionCode", Long.toString(PackageInfoCompat.getLongVersionCode(self)));
        } catch (android.content.pm.PackageManager.NameNotFoundException e) {
            Log.w(TAG, "Could not read the launcher's own package info; extracting " + assetName + " unconditionally");
            expected = null;
        }

        File marker = new File(outputFolder, EXTRACTED_MARKER_NAME);
        if (expected != null && new File(outputFolder, sentinel).isFile()) {
            Properties recorded = readProperties(marker);
            boolean same = recorded != null;
            if (same) {
                for (String key : expected.stringPropertyNames()) {
                    if (!expected.getProperty(key).equals(recorded.getProperty(key))) {
                        same = false;
                        break;
                    }
                }
            }
            if (same) {
                Log.i(TAG, assetName + " already extracted to " + outputFolder.getAbsolutePath());
                return true;
            }
        }

        // The marker promises a complete extraction, so it never survives into a rewrite.
        if (marker.isFile() && !marker.delete()) {
            Log.w(TAG, "Could not remove " + marker.getAbsolutePath());
        }
        if (!extractZipFromAssets(context, assetName, outputFolder)) {
            return false;
        }
        if (!new File(outputFolder, sentinel).isFile()) {
            Log.e(TAG, assetName + " extracted but " + sentinel + " is missing");
            return false;
        }
        if (expected != null) {
            try (OutputStream out = new FileOutputStream(marker)) {
                expected.store(out, "bundle extracted by the launcher");
            } catch (IOException e) {
                Log.w(TAG, "Extracted " + assetName + " but failed to write " + marker.getName(), e);
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

    public static boolean extractZipFromAssets(Context context, String assetName, File outputFolder) {
        try {
            if (!outputFolder.exists() && !outputFolder.mkdirs()) {
                throw new IOException("Failed to create output directory: " + outputFolder.getAbsolutePath());
            }

            String outputRoot = outputFolder.getCanonicalPath() + File.separator;
            byte[] buffer = new byte[8192];

            try (InputStream is = context.getAssets().open(assetName);
                 ZipInputStream zis = new ZipInputStream(new BufferedInputStream(is))) {
                ZipEntry ze;
                while ((ze = zis.getNextEntry()) != null) {
                    String entryName = ze.getName();
                    if (entryName == null || entryName.isEmpty()) {
                        zis.closeEntry();
                        continue;
                    }

                    File target = new File(outputFolder, entryName);
                    String targetPath = target.getCanonicalPath();

                    if (!targetPath.startsWith(outputRoot)) {
                        throw new IOException("Blocked zip entry outside output folder: " + entryName);
                    }

                    if (ze.isDirectory()) {
                        if (!target.exists() && !target.mkdirs()) {
                            throw new IOException("Failed to create directory: " + targetPath);
                        }
                    } else {
                        File parent = target.getParentFile();
                        if (parent != null && !parent.exists() && !parent.mkdirs()) {
                            throw new IOException("Failed to create parent directory: " + parent.getAbsolutePath());
                        }

                        try (FileOutputStream fos = new FileOutputStream(target)) {
                            int count;
                            while ((count = zis.read(buffer)) != -1) {
                                fos.write(buffer, 0, count);
                            }
                        }
                    }

                    zis.closeEntry();
                }
            }
        } catch (IOException e) {
            Log.e(TAG, "Failed to extract " + assetName + " from assets!", e);
            return false;
        }
        return true;
    }

    public static boolean deleteRecursive(File file) {
        if (file == null || !file.exists()) {
            return true;
        }

        if (file.isDirectory()) {
            File[] files = file.listFiles();
            if (files != null) {
                for (File f : files) {
                    if (!deleteRecursive(f)) {
                        return false;
                    }
                }
            }
        }

        return file.delete();
    }
}
