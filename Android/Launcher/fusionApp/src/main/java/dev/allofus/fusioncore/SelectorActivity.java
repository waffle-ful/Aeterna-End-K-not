package dev.allofus.fusioncore;

import android.app.ActivityManager;
import android.content.Intent;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.graphics.drawable.Drawable;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.util.Log;
import android.view.View;
import android.widget.Button;
import android.widget.ImageView;
import android.widget.TextView;

import androidx.activity.result.ActivityResultLauncher;
import androidx.activity.result.contract.ActivityResultContracts;
import androidx.annotation.NonNull;
import androidx.appcompat.app.AppCompatActivity;
import androidx.core.content.ContextCompat;

import java.io.File;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.zip.ZipFile;

import dev.allofus.fusioncore.tools.CrashDetector;
import dev.allofus.fusioncore.tools.ItchAuth;
import dev.allofus.fusioncore.tools.LogBundle;
import dev.allofus.fusioncore.tools.PluginInstaller;
import dev.allofus.fusioncore.tools.Utilities;

/** Home screen: one launch card for Among Us, status rows, and the secondary actions. */
public class SelectorActivity extends AppCompatActivity {
    private static final String TAG = "FusionCore";
    private static final String[] UNITY_ABIS = {"arm64-v8a", "armeabi-v7a", "x86_64", "x86"};
    static final String TARGET_PACKAGE = "com.innersloth.spacemafia";
    /** The web role maker; a role made there is pasted into the game with /role import. */
    private static final String ROLE_MAKER_URL = "https://waffle-ful.github.io/Aeterna-End-K-not/editor/#role-maker";
    /** Set on the intent that brings the player back after the itch.io sign-in page. */
    static final String EXTRA_AFTER_SIGN_IN = "after_sign_in";
    private static final int AUTO_LAUNCH_SECONDS = 3;

    private String pendingLaunchPackage;
    private boolean targetInstalled;
    private Handler handler;
    private int countdownLeft;
    private final Runnable countdownTick = this::onCountdownTick;

    private View launchCard;
    private TextView launchHint;
    private View launchProgress;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_selector);
        Utilities.initStorage(this);

        View root = findViewById(R.id.selector_root);
        int basePadding = Math.round(getResources().getDisplayMetrics().density * 16f);
        Utilities.applyWindowInsets(root, basePadding);

        launchCard = findViewById(R.id.home_launch_card);
        launchHint = findViewById(R.id.home_launch_hint);
        launchProgress = findViewById(R.id.home_launch_progress);

        handler = new Handler(getMainLooper());
        findViewById(R.id.selector_itch_button).setOnClickListener(v -> {
            stopCountdown();
            if (ItchAuth.isSignedIn(this, TARGET_PACKAGE)) {
                ItchAuth.signOut(this, TARGET_PACKAGE);
                refreshItchStatus();
            } else {
                ItchAuth.startSignIn(this);
            }
        });
        launchCard.setOnClickListener(v -> {
            if (targetInstalled) {
                maybeLaunchBootstrap(TARGET_PACKAGE);
            }
        });
        findViewById(R.id.selector_action_settings).setOnClickListener(v -> {
            stopCountdown();
            Intent intent = new Intent(this, GameSettingsActivity.class);
            intent.putExtra(GameSettingsActivity.EXTRA_PACKAGE_NAME, TARGET_PACKAGE);
            startActivity(intent);
        });
        findViewById(R.id.home_open_role_maker).setOnClickListener(v -> {
            stopCountdown();
            openWebPage(ROLE_MAKER_URL);
        });
        findViewById(R.id.home_share_logs).setOnClickListener(v -> {
            stopCountdown();
            LogBundle.shareAsync(this, TARGET_PACKAGE);
        });
        handler.postDelayed(() -> {
            if (isFinishing() || isDestroyed()) {
                return;
            }
            // Consume a rejection the game left behind and encrypt any plaintext token from an
            // earlier build before the row reports the sign-in state. The one-time migration
            // touches the Keystore and syncs a file, so it stays off the main thread.
            new Thread(() -> {
                ItchAuth.reconcile(this, TARGET_PACKAGE);
                runOnUiThread(() -> {
                    if (isFinishing() || isDestroyed()) {
                        return;
                    }
                    targetInstalled = populateHome();
                    boolean signedIn = refreshItchStatus();
                    CrashDetector.init(this);
                    // Launch by itself only once an itch.io account is attached; otherwise wait for
                    // the player to sign in or to tap the card.
                    if (targetInstalled && signedIn) {
                        startCountdown();
                    }
                });
            }, "itch-reconcile").start();
        }, 100);
    }

    /** Updates the itch.io row and the launch hint; returns whether a token is stored. */
    private boolean refreshItchStatus() {
        boolean signedIn = ItchAuth.isSignedIn(this, TARGET_PACKAGE);
        TextView status = findViewById(R.id.selector_itch_status);
        Button button = findViewById(R.id.selector_itch_button);
        status.setText(signedIn ? R.string.selector_itch_signed_in : R.string.selector_itch_not_signed_in);
        button.setText(signedIn ? R.string.selector_itch_sign_out : R.string.selector_itch_sign_in);
        if (!targetInstalled) {
            launchHint.setText(R.string.selector_target_not_installed);
        } else if (!signedIn) {
            launchHint.setText(R.string.home_launch_sign_in_first);
        } else if (countdownLeft <= 0) {
            launchHint.setText(R.string.home_launch_tap);
        }
        return signedIn;
    }

    private void startCountdown() {
        stopCountdown();
        countdownLeft = AUTO_LAUNCH_SECONDS;
        launchProgress.setVisibility(View.VISIBLE);
        onCountdownTick();
    }

    private void onCountdownTick() {
        if (countdownLeft <= 0) {
            launchProgress.setVisibility(View.INVISIBLE);
            maybeLaunchBootstrap(TARGET_PACKAGE);
            return;
        }
        launchHint.setText(getString(R.string.home_launch_countdown, countdownLeft));
        countdownLeft--;
        handler.postDelayed(countdownTick, 1000L);
    }

    /** Opens a web page in a custom tab, falling back to the default browser. */
    private void openWebPage(String url) {
        android.net.Uri uri = android.net.Uri.parse(url);
        try {
            new androidx.browser.customtabs.CustomTabsIntent.Builder().build().launchUrl(this, uri);
        } catch (Exception e) {
            try {
                startActivity(new Intent(Intent.ACTION_VIEW, uri));
            } catch (Exception inner) {
                Log.e(TAG, "No browser available", inner);
            }
        }
    }

    private void stopCountdown() {
        if (handler != null) {
            handler.removeCallbacks(countdownTick);
        }
        if (countdownLeft > 0) {
            countdownLeft = 0;
            launchProgress.setVisibility(View.INVISIBLE);
            if (targetInstalled) {
                launchHint.setText(R.string.home_launch_tap);
            }
        }
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        if (!intent.getBooleanExtra(EXTRA_AFTER_SIGN_IN, false)) {
            return;
        }
        // Coming back from the itch.io page: the activity already exists, so refresh the row and
        // start the game the same way a fresh launch would once an account is attached.
        boolean signedIn = refreshItchStatus();
        if (signedIn && targetInstalled) {
            startCountdown();
        }
    }

    @Override
    protected void onPause() {
        super.onPause();
        stopCountdown();
    }

    @Override
    protected void onDestroy() {
        if (handler != null) {
            handler.removeCallbacksAndMessages(null);
        }
        super.onDestroy();
    }

    /** Fills the launch card and the status rows; returns whether the game is installed. */
    private boolean populateHome() {
        List<AppEntry> installedTargets = resolveInstalledTargets();
        AppEntry game = installedTargets.isEmpty() ? null : installedTargets.get(0);

        ImageView icon = findViewById(R.id.home_launch_icon);
        TextView gameVersion = findViewById(R.id.home_game_version);
        TextView modVersion = findViewById(R.id.home_mod_version);
        TextView missing = findViewById(R.id.selector_empty);

        if (game != null) {
            icon.setImageDrawable(game.icon != null ? game.icon : getPackageManager().getDefaultActivityIcon());
            gameVersion.setText(Utilities.formatVersionText(game.versionName, game.versionCode));
            missing.setVisibility(View.GONE);
            launchCard.setEnabled(true);
            launchCard.setAlpha(1f);
        } else {
            icon.setImageDrawable(getPackageManager().getDefaultActivityIcon());
            gameVersion.setText(R.string.home_row_game_none);
            missing.setVisibility(View.VISIBLE);
            launchCard.setEnabled(false);
            launchCard.setAlpha(0.6f);
        }

        String bundled = PluginInstaller.bundledVersion(this);
        modVersion.setText(bundled != null ? "v" + bundled : getString(R.string.home_row_mod_none));
        return game != null;
    }


    private List<AppEntry> resolveInstalledTargets() {
        PackageManager pm = getPackageManager();
        List<AppEntry> result = new ArrayList<>();
        Set<String> seenPackages = new HashSet<>();

        Intent launchIntent = new Intent(Intent.ACTION_MAIN);
        launchIntent.addCategory(Intent.CATEGORY_LAUNCHER);
        List<ResolveInfo> activities = pm.queryIntentActivities(launchIntent, PackageManager.MATCH_ALL);

        for (ResolveInfo resolveInfo : activities) {
            String packageName = resolveInfo.activityInfo.packageName;
            if (packageName == null || !seenPackages.add(packageName)) {
                continue;
            }
            if (packageName.equals(getPackageName())) {
                continue;
            }
            if (!TARGET_PACKAGE.equals(packageName)) {
                continue;
            }

            ApplicationInfo info;
            try {
                info = pm.getApplicationInfo(packageName, 0);
            }
            catch (PackageManager.NameNotFoundException e) {
                continue;
            }

            if ((info.flags & ApplicationInfo.FLAG_SYSTEM) != 0) {
                continue;
            }

            if (!hasIl2Cpp(info)) {
                continue;
            }

            String label = packageName;
            Drawable icon = pm.getDefaultActivityIcon();
            String versionName = "Unknown";
            long versionCode = 0L;
            try {
                label = pm.getApplicationLabel(info).toString();
                icon = pm.getApplicationIcon(info);

                PackageInfo packageInfo;
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                    packageInfo = pm.getPackageInfo(packageName, PackageManager.PackageInfoFlags.of(0));
                } else {
                    packageInfo = pm.getPackageInfo(packageName, 0);
                }
                if (packageInfo.versionName != null && !packageInfo.versionName.isEmpty()) {
                    versionName = packageInfo.versionName;
                }
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                    versionCode = packageInfo.getLongVersionCode();
                } else {
                    //noinspection deprecation
                    versionCode = packageInfo.versionCode;
                }
            } catch (Exception e) {
                Log.w(TAG, "Failed to resolve metadata for package: " + packageName, e);
            }

            Log.i(TAG, "Found installed target: " + packageName + " (" + label + ")");
            result.add(new AppEntry(packageName, label, icon, versionName, versionCode));
        }

        return result;
    }

    private static boolean hasIl2Cpp(ApplicationInfo info) {
        List<String> apkPaths = new ArrayList<>();
        if (info.sourceDir != null) {
            apkPaths.add(info.sourceDir);
        }
        if (info.splitSourceDirs != null) {
            Collections.addAll(apkPaths, info.splitSourceDirs);
        }
        for (String apk : apkPaths) {
            if (apkContainsIl2Cpp(apk)) {
                return true;
            }
        }

        String nativeDir = info.nativeLibraryDir;
        if (nativeDir != null && !nativeDir.isEmpty()) {
            File dir = new File(nativeDir);
            if (new File(dir, "libil2cpp.so").exists()) {
                return true;
            }
            File[] abiDirs = dir.listFiles();
            if (abiDirs != null) {
                for (File abiDir : abiDirs) {
                    if (abiDir.isDirectory() && new File(abiDir, "libil2cpp.so").exists()) {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static boolean apkContainsIl2Cpp(String apkPath) {
        try {
            try (ZipFile zip = new ZipFile(apkPath)) {
                for (String abi : UNITY_ABIS) {
                    if (zip.getEntry("lib/" + abi + "/libil2cpp.so") != null) {
                        return true;
                    }
                }
            }
        } catch (Exception e) {
            // Unreadable APK; fall through to the nativeLibraryDir check.
        }
        return false;
    }

    private void launchBootstrap(String packageName) {
        dev.allofus.fusioncore.tools.BootTimeline.reset();
        dev.allofus.fusioncore.tools.BootTimeline.mark("selector");
        Intent intent = new Intent(this, BootstrapActivity.class);
        intent.putExtra(BootstrapActivity.EXTRA_TARGET_PACKAGE, packageName);
        intent.addFlags(Intent.FLAG_ACTIVITY_NO_ANIMATION | Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        startActivity(intent);
        //noinspection deprecation
        overridePendingTransition(0, 0);
        finish();
        //noinspection deprecation
        overridePendingTransition(0, 0);
    }

    private final ActivityResultLauncher<String[]> requestPermissionsLauncher =
            registerForActivityResult(new ActivityResultContracts.RequestMultiplePermissions(), isGrantedMap -> {
                for (Map.Entry<String, Boolean> entry : isGrantedMap.entrySet()) {
                    String permission = entry.getKey();
                    boolean isGranted = entry.getValue();

                    if (isGranted) {
                        Log.i(TAG, "Got permission: " +permission);
                    } else {
                        Log.e(TAG, "Permission denied: " +permission);
                    }
                }

                String packageName = pendingLaunchPackage;
                pendingLaunchPackage = null;
                if (packageName != null) {
                    launchBootstrap(packageName);
                }
            });

    private boolean moveGameTaskToFront() {
        ActivityManager am = (ActivityManager) getSystemService(ACTIVITY_SERVICE);
        if (am == null) return false;
        String stub = StubActivity.class.getName();
        for (ActivityManager.AppTask task : am.getAppTasks()) {
            ActivityManager.RecentTaskInfo info = task.getTaskInfo();
            if (info == null) continue;
            boolean isGameTask = (info.topActivity != null && stub.equals(info.topActivity.getClassName()))
                    || (info.baseActivity != null && stub.equals(info.baseActivity.getClassName()))
                    || (info.baseIntent != null && info.baseIntent.getComponent() != null
                        && stub.equals(info.baseIntent.getComponent().getClassName()));
            if (isGameTask) {
                Log.i(TAG, "Moving game task to front");
                task.moveToFront();
                return true;
            }
        }
        return false;
    }

    private void maybeLaunchBootstrap(String packageName) {
        stopCountdown();

        if (BootstrapActivity.isGameActivityStarted()) {
            // The game already runs in this process. Bootstrapping it a second time would
            // create a second UnityPlayer, which Unity aborts on; bring the running game
            // back to the front instead.
            // Starting StubActivity here would create a plain stub (the game intent is only
            // swapped for the game activity in newActivity), so move the game task instead.
            Log.i(TAG, "Game already loaded in this process; returning to it");
            if (!moveGameTaskToFront()) {
                // The game task is gone but this process still holds the old UnityPlayer, so
                // it cannot bootstrap again; end the process so the next tap starts clean.
                Log.w(TAG, "Game task not found; ending the process so the next launch bootstraps again");
                finish();
                android.os.Process.killProcess(android.os.Process.myPid());
                return;
            }
            finish();
            return;
        }
        if (BootstrapActivity.isGameLoaded()) {
            // Bootstrap is still preparing the game in this process. Starting the stub now
            // would create a plain StubActivity that the later game intent could only reach
            // through onNewIntent, so the game would never start; leave the bootstrap alone.
            Log.i(TAG, "Bootstrap already in progress in this process; not starting again");
            finish();
            return;
        }

        try {
            var packageInfo = getPackageManager().getPackageInfo(packageName, PackageManager.GET_PERMISSIONS);
            var perms = packageInfo.requestedPermissions;
            if (perms != null) {
                // Only permissions this launcher declares itself can be granted; the game may
                // list more, and asking for those would just be refused every launch.
                var ownInfo = getPackageManager().getPackageInfo(getPackageName(), PackageManager.GET_PERMISSIONS);
                Set<String> declared = new HashSet<>();
                if (ownInfo.requestedPermissions != null) {
                    declared.addAll(Arrays.asList(ownInfo.requestedPermissions));
                }
                ArrayList<String> newPerms = new ArrayList<>();
                for (var p : perms) {
                    if (declared.contains(p) && ContextCompat.checkSelfPermission(this, p) != PackageManager.PERMISSION_GRANTED) {
                        newPerms.add(p);
                    }
                }

                if (!newPerms.isEmpty()) {
                    pendingLaunchPackage = packageName;
                    requestPermissionsLauncher.launch(newPerms.toArray(new String[0]));
                    return;
                }
            }
        } catch (Exception e) {
            Log.e(TAG, "Failure getting package info for " + packageName, e);
        }

        launchBootstrap(packageName);
    }

    private record AppEntry(String packageName, String label, Drawable icon, String versionName,
                            long versionCode) {

        @NonNull
        @Override
        public String toString() {
            if (label.equals(packageName)) {
                return packageName;
            }
            return label + " (" + packageName + ")";
        }
    }
}
