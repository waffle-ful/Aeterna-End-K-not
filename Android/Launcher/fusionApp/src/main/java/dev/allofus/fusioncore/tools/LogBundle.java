package dev.allofus.fusioncore.tools;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import android.widget.Toast;

import androidx.core.content.FileProvider;

import java.io.BufferedInputStream;
import java.io.BufferedReader;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;

import dev.allofus.fusioncore.R;

/**
 * Packs the diagnostic files into one zip under the cache directory and hands it to the
 * system share sheet. Nothing is uploaded by the launcher itself; the player picks the
 * destination app.
 *
 * Included: the BepInEx log of the game (tail only, capped), the crash notes the launcher
 * wrote next to it, and this process's own logcat. The itch.io token file, shared
 * preferences and the BepInEx config directory are never touched.
 */
public final class LogBundle {
    private static final String TAG = "FusionCore";
    private static final String SHARE_DIR = "share";
    private static final String AUTHORITY_SUFFIX = ".fileprovider";
    private static final long LOG_TAIL_BYTES = 4L * 1024 * 1024;
    private static final int MAX_CRASH_NOTES = 20;
    private static final long OLD_BUNDLE_AGE_MS = 30L * 60 * 1000;
    private static final AtomicBoolean IN_FLIGHT = new AtomicBoolean(false);

    private LogBundle() {
    }

    /** Builds the zip off the main thread, then opens the share sheet; errors surface as a toast. */
    public static void shareAsync(Activity activity, String targetPackage) {
        if (!IN_FLIGHT.compareAndSet(false, true)) {
            return;
        }
        Context app = activity.getApplicationContext();
        Toast.makeText(app, R.string.logs_preparing, Toast.LENGTH_SHORT).show();
        Handler main = new Handler(Looper.getMainLooper());
        Thread worker = new Thread(() -> {
            File zip;
            try {
                zip = build(app, targetPackage);
            } catch (Exception e) {
                Log.w(TAG, "Log bundle failed", e);
                String reason = e.getMessage() != null ? e.getMessage() : e.getClass().getSimpleName();
                main.post(() -> {
                    IN_FLIGHT.set(false);
                    Toast.makeText(app, app.getString(R.string.logs_share_failed, reason), Toast.LENGTH_LONG).show();
                });
                return;
            }
            main.post(() -> {
                IN_FLIGHT.set(false);
                if (zip == null) {
                    Toast.makeText(app, R.string.logs_share_empty, Toast.LENGTH_LONG).show();
                    return;
                }
                if (activity.isFinishing() || activity.isDestroyed()) {
                    return;
                }
                openShareSheet(activity, zip);
            });
        }, "LogBundle");
        worker.setDaemon(true);
        worker.start();
    }

    /** Returns the zip, or null when there was nothing to pack. */
    static File build(Context context, String targetPackage) throws IOException {
        File shareDir = new File(context.getCacheDir(), SHARE_DIR);
        if (!shareDir.isDirectory() && !shareDir.mkdirs()) {
            throw new IOException("cannot create " + shareDir);
        }
        // Bundles handed to the share sheet stay readable for a while; only stale ones go.
        File[] old = shareDir.listFiles();
        if (old != null) {
            long cutoff = System.currentTimeMillis() - OLD_BUNDLE_AGE_MS;
            for (File f : old) {
                if (f.lastModified() < cutoff) {
                    //noinspection ResultOfMethodCallIgnored
                    f.delete();
                }
            }
        }
        String stamp = new SimpleDateFormat("yyyyMMdd-HHmmss", Locale.US).format(new Date());
        File zip = new File(shareDir, "endknot-logs-" + stamp + "-" + Long.toHexString(System.nanoTime()) + ".zip");

        File filesRoot = context.getFilesDir();
        File gameRoot = new File(filesRoot, targetPackage);
        File bepInExLog = new File(new File(gameRoot, "BepInEx"), "LogOutput.log");

        int entries = 0;
        try (ZipOutputStream out = new ZipOutputStream(new FileOutputStream(zip))) {
            if (bepInExLog.isFile()) {
                addTail(out, "BepInEx/LogOutput.log", bepInExLog, LOG_TAIL_BYTES);
                entries++;
            }
            for (File note : crashNotes(filesRoot)) {
                addWhole(out, "crash/" + note.getName(), note);
                entries++;
            }
            byte[] logcat = readLogcat();
            if (logcat.length > 0) {
                addBytes(out, "logcat.txt", logcat);
                entries++;
            }
            addBytes(out, "bundle-info.txt", describe(context, targetPackage).getBytes(StandardCharsets.UTF_8));
        }
        if (entries == 0) {
            //noinspection ResultOfMethodCallIgnored
            zip.delete();
            return null;
        }
        return zip;
    }

    private static void openShareSheet(Activity activity, File zip) {
        Uri uri = FileProvider.getUriForFile(activity, activity.getPackageName() + AUTHORITY_SUFFIX, zip);
        Intent send = new Intent(Intent.ACTION_SEND);
        send.setType("application/zip");
        send.putExtra(Intent.EXTRA_STREAM, uri);
        send.putExtra(Intent.EXTRA_SUBJECT, zip.getName());
        // ClipData carries the grant through the chooser, so its preview can read the file too.
        send.setClipData(android.content.ClipData.newUri(activity.getContentResolver(), zip.getName(), uri));
        send.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
        Intent chooser = Intent.createChooser(send, activity.getString(R.string.logs_share_title));
        chooser.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
        activity.startActivity(chooser);
    }

    /** Newest crash notes first, capped so an old device with many records stays small. */
    private static List<File> crashNotes(File filesRoot) {
        List<File> notes = new ArrayList<>();
        File[] all = filesRoot.listFiles((dir, name) ->
                (name.startsWith("exit_info_") || name.startsWith("java_crash_")) && name.endsWith(".txt"));
        if (all == null) {
            return notes;
        }
        java.util.Arrays.sort(all, (a, b) -> Long.compare(b.lastModified(), a.lastModified()));
        for (File f : all) {
            if (notes.size() >= MAX_CRASH_NOTES) {
                break;
            }
            if (f.isFile()) {
                notes.add(f);
            }
        }
        return notes;
    }

    private static byte[] readLogcat() {
        int pid = android.os.Process.myPid();
        List<String> args = new ArrayList<>();
        args.add("logcat");
        args.add("-d");
        args.add("-v");
        args.add("threadtime");
        args.add("--pid=" + pid);
        try {
            Process p = new ProcessBuilder(args).redirectErrorStream(true).start();
            try (InputStream in = new BufferedInputStream(p.getInputStream());
                 java.io.ByteArrayOutputStream buf = new java.io.ByteArrayOutputStream()) {
                copy(in, buf);
                byte[] all = buf.toByteArray();
                if (all.length <= LOG_TAIL_BYTES) {
                    return all;
                }
                // Keep the newest part, starting at the first full line after the cut.
                int from = (int) (all.length - LOG_TAIL_BYTES);
                while (from < all.length && all[from - 1] != '\n') {
                    from++;
                }
                byte[] head = ("[truncated: first " + from + " bytes omitted]\n").getBytes(StandardCharsets.UTF_8);
                byte[] tail = new byte[head.length + all.length - from];
                System.arraycopy(head, 0, tail, 0, head.length);
                System.arraycopy(all, from, tail, head.length, all.length - from);
                return tail;
            } finally {
                p.destroy();
            }
        } catch (IOException e) {
            Log.w(TAG, "logcat unavailable for the bundle: " + e.getMessage());
            return new byte[0];
        }
    }

    private static String describe(Context context, String targetPackage) {
        StringBuilder sb = new StringBuilder();
        sb.append("launcher=").append(context.getPackageName()).append('\n');
        try {
            var info = context.getPackageManager().getPackageInfo(context.getPackageName(), 0);
            sb.append("launcherVersion=").append(info.versionName).append('\n');
        } catch (Exception ignored) {
        }
        try {
            var info = context.getPackageManager().getPackageInfo(targetPackage, 0);
            sb.append("game=").append(targetPackage).append(' ').append(info.versionName).append('\n');
        } catch (Exception e) {
            sb.append("game=").append(targetPackage).append(" (not installed)\n");
        }
        String mod = PluginInstaller.bundledVersion(context);
        sb.append("bundledMod=").append(mod != null ? mod : "none").append('\n');
        sb.append("device=").append(android.os.Build.MANUFACTURER).append(' ').append(android.os.Build.MODEL)
                .append(" android ").append(android.os.Build.VERSION.RELEASE).append('\n');
        sb.append("time=").append(new SimpleDateFormat("yyyy-MM-dd HH:mm:ss Z", Locale.US).format(new Date())).append('\n');
        return sb.toString();
    }

    private static void addWhole(ZipOutputStream out, String name, File file) throws IOException {
        out.putNextEntry(new ZipEntry(name));
        try (InputStream in = new BufferedInputStream(new FileInputStream(file))) {
            copy(in, out);
        }
        out.closeEntry();
    }

    /** Writes the last {@code maxBytes} of the file, starting at the first full line. */
    private static void addTail(ZipOutputStream out, String name, File file, long maxBytes) throws IOException {
        out.putNextEntry(new ZipEntry(name));
        long length = file.length();
        long skip = Math.max(0L, length - maxBytes);
        try (FileInputStream raw = new FileInputStream(file)) {
            if (skip > 0) {
                //noinspection ResultOfMethodCallIgnored
                raw.skip(skip);
                BufferedReader reader = new BufferedReader(new InputStreamReader(raw, StandardCharsets.UTF_8));
                reader.readLine();
                out.write(("[truncated: first " + skip + " bytes omitted]\n").getBytes(StandardCharsets.UTF_8));
                String line;
                while ((line = reader.readLine()) != null) {
                    out.write(line.getBytes(StandardCharsets.UTF_8));
                    out.write('\n');
                }
            } else {
                copy(new BufferedInputStream(raw), out);
            }
        }
        out.closeEntry();
    }

    private static void addBytes(ZipOutputStream out, String name, byte[] data) throws IOException {
        out.putNextEntry(new ZipEntry(name));
        out.write(data);
        out.closeEntry();
    }

    private static void copy(InputStream in, OutputStream out) throws IOException {
        byte[] buf = new byte[16 * 1024];
        int n;
        while ((n = in.read(buf)) > 0) {
            out.write(buf, 0, n);
        }
    }
}
