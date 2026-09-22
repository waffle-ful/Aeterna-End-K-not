package dev.allofus.fusioncore;

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
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.ImageButton;
import android.widget.ImageView;
import android.widget.ListView;
import android.widget.TextView;
import android.widget.Toast;

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
import dev.allofus.fusioncore.tools.Utilities;

public class SelectorActivity extends AppCompatActivity {
    private static final String TAG = "FusionCore";
    private static final String[] UNITY_ABIS = {"arm64-v8a", "armeabi-v7a", "x86_64", "x86"};
    static final String TARGET_PACKAGE = "com.innersloth.spacemafia";
    /** Set on the intent that brings the player back after the itch.io sign-in page. */
    static final String EXTRA_AFTER_SIGN_IN = "after_sign_in";
    private static final long AUTO_LAUNCH_DELAY_MS = 1500L;

    private String pendingLaunchPackage;
    private boolean targetInstalled;
    private Handler handler;
    private final Runnable autoLaunchRunnable = () -> maybeLaunchBootstrap(TARGET_PACKAGE);

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_selector);
        Utilities.initStorage(this);

        View root = findViewById(R.id.selector_root);
        int basePadding = Math.round(getResources().getDisplayMetrics().density * 16f);
        Utilities.applyWindowInsets(root, basePadding);

        handler = new Handler(getMainLooper());
        findViewById(R.id.selector_itch_button).setOnClickListener(v -> {
            handler.removeCallbacks(autoLaunchRunnable);
            if (ItchAuth.isSignedIn(this, TARGET_PACKAGE)) {
                ItchAuth.signOut(this, TARGET_PACKAGE);
                refreshItchStatus();
            } else {
                ItchAuth.startSignIn(this);
            }
        });
        handler.postDelayed(()->{
            targetInstalled = populateList();
            boolean signedIn = refreshItchStatus();
            CrashDetector.init(this);
            // Launch by itself only once an itch.io account is attached; otherwise wait for
            // the player to sign in or to tap the card.
            if (targetInstalled && signedIn) {
                handler.postDelayed(autoLaunchRunnable, AUTO_LAUNCH_DELAY_MS);
            }
        }, 100);
    }

    /** Updates the itch.io row and the hint under the title; returns whether a token is stored. */
    private boolean refreshItchStatus() {
        boolean signedIn = ItchAuth.isSignedIn(this, TARGET_PACKAGE);
        TextView status = findViewById(R.id.selector_itch_status);
        Button button = findViewById(R.id.selector_itch_button);
        status.setText(signedIn ? R.string.selector_itch_signed_in : R.string.selector_itch_not_signed_in);
        button.setText(signedIn ? R.string.selector_itch_sign_out : R.string.selector_itch_sign_in);
        TextView subtitle = findViewById(R.id.selector_subtitle);
        if (!signedIn) {
            subtitle.setText(R.string.selector_itch_hint_not_signed_in);
        } else if (findViewById(R.id.selector_loading).getVisibility() == View.GONE) {
            subtitle.setText(R.string.selector_auto_launch_hint);
        }
        return signedIn;
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
            handler.removeCallbacks(autoLaunchRunnable);
            handler.postDelayed(autoLaunchRunnable, AUTO_LAUNCH_DELAY_MS);
        }
    }

    @Override
    protected void onPause() {
        super.onPause();
        if (handler != null) {
            handler.removeCallbacks(autoLaunchRunnable);
        }
    }

    private boolean populateList() {
        ListView listView = findViewById(R.id.selector_list);
        TextView emptyView = findViewById(R.id.selector_empty);
        emptyView.setText(R.string.selector_target_not_installed);
        listView.setEmptyView(emptyView);

        List<AppEntry> installedTargets = resolveInstalledTargets();
        if (!installedTargets.isEmpty()) {
            TextView subtitle = findViewById(R.id.selector_subtitle);
            subtitle.setText(R.string.selector_auto_launch_hint);
        }
        Drawable defaultIcon = getPackageManager().getDefaultActivityIcon();
        ArrayAdapter<AppEntry> adapter = new ArrayAdapter<>(
                this,
                R.layout.item_selector_target,
                installedTargets
        ) {
            @NonNull
            @Override
            public View getView(int position, View convertView, @NonNull ViewGroup parent) {
                RowHolder holder;
                if (convertView == null) {
                    convertView = LayoutInflater.from(getContext())
                            .inflate(R.layout.item_selector_target, parent, false);
                    holder = new RowHolder(
                            convertView.findViewById(R.id.row_icon),
                            convertView.findViewById(R.id.row_name),
                            convertView.findViewById(R.id.row_package),
                            convertView.findViewById(R.id.row_version)
                    );
                    convertView.setTag(holder);
                } else {
                    holder = (RowHolder) convertView.getTag();
                }

                AppEntry entry = getItem(position);
                if (entry != null) {
                    holder.icon.setImageDrawable(entry.icon != null ? entry.icon : defaultIcon);
                    holder.name.setText(entry.label);
                    holder.packageName.setText(entry.packageName);
                    holder.version.setText(Utilities.formatVersionText(entry.versionName, entry.versionCode));

                    ImageButton settingsButton = convertView.findViewById(R.id.selector_action_settings);
                    settingsButton.setOnClickListener(v -> {
                        handler.removeCallbacks(autoLaunchRunnable);
                        var intent = new Intent(getContext(), GameSettingsActivity.class);
                        intent.putExtra(GameSettingsActivity.EXTRA_PACKAGE_NAME, entry.packageName);
                        startActivity(intent);
                    });

                    convertView.setOnClickListener((v) -> maybeLaunchBootstrap(entry.packageName));
                }

                return convertView;
            }
        };
        listView.setAdapter(adapter);
        findViewById(R.id.selector_loading).setVisibility(View.GONE);
        return !installedTargets.isEmpty();
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
        Intent intent = new Intent(this, BootstrapActivity.class);
        intent.putExtra(BootstrapActivity.EXTRA_TARGET_PACKAGE, packageName);
        intent.putExtra(BootstrapActivity.EXTRA_USE_ORIGINAL_LIBUNITY,
                !FusionSettings.getUseUnstrippedLibUnityForGame(this, packageName));
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

                    String packageName = pendingLaunchPackage;
                    pendingLaunchPackage = null;
                    launchBootstrap(packageName);
                }
            });

    private void maybeLaunchBootstrap(String packageName) {
        handler.removeCallbacks(autoLaunchRunnable);

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

    private record RowHolder(ImageView icon, TextView name, TextView packageName,
                             TextView version) {
    }
}
