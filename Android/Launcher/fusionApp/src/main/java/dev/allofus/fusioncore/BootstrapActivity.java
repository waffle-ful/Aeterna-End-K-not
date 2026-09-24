package dev.allofus.fusioncore;

import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.content.pm.ActivityInfo;
import android.content.pm.PackageManager.NameNotFoundException;
import android.os.Bundle;
import android.os.Environment;
import android.os.Looper;
import android.util.Log;
import android.view.View;
import android.widget.ProgressBar;
import android.widget.TextView;
import android.widget.Toast;

import androidx.appcompat.app.AppCompatActivity;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;

import dev.allofus.fusioncore.hooks.InstrumentationHooks;
import dev.allofus.fusioncore.bridge.UnityActivityHost;
import dev.allofus.fusioncore.tools.CustomContextWrapper;
import dev.allofus.fusioncore.tools.FallbackResources;
import dev.allofus.fusioncore.tools.FusionConfig;
import dev.allofus.fusioncore.tools.GameClassLoaderFactory;
import dev.allofus.fusioncore.tools.LogBundle;
import dev.allofus.fusioncore.tools.Utilities;
import dev.allofus.fusioncore.tools.PluginInstaller;
import dev.allofus.fusioncore.tools.VersionLookup;

public class BootstrapActivity extends AppCompatActivity {

    private static final String TAG = "FusionCore";

    public static final String EXTRA_TARGET_PACKAGE = "target_package";
    public static final String BACKUP_UNITY_VERSION = "2017.0.0";
    private static final String GLOBAL_METADATA_FILE = "global-metadata.dat";
    private static ClassLoader sGameClassLoader;

    /** True once the game's class loader exists in this process, i.e. the game has been bootstrapped. */
    static boolean isGameLoaded() {
        return sGameClassLoader != null;
    }

    /** True once the game activity itself has been started; the class loader exists well before that. */
    private static volatile boolean sGameActivityStarted;

    static boolean isGameActivityStarted() {
        return sGameActivityStarted;
    }

    /** Where the player gets a newer launcher when this build no longer matches the game. */
    private static final String RELEASES_URL = "https://github.com/waffle-ful/Aeterna-End-K-not/releases/latest";

    private TextView titleView;
    private TextView statusView;
    private TextView progressDetailsView;
    private View actionsRow;
    private ProgressBar spinnerProgress;
    private ProgressBar downloadProgress;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_bootstrap);
        Utilities.initStorage(this);
        titleView = findViewById(R.id.bootstrap_title);
        statusView = findViewById(R.id.bootstrap_status);
        progressDetailsView = findViewById(R.id.bootstrap_progress_details);
        spinnerProgress = findViewById(R.id.bootstrap_progress);
        downloadProgress = findViewById(R.id.bootstrap_download_progress);
        setPhaseStatus(getString(R.string.bootstrap_status_preparing));

        String targetPackage = getIntent().getStringExtra(EXTRA_TARGET_PACKAGE);
        actionsRow = findViewById(R.id.bootstrap_actions);
        findViewById(R.id.bootstrap_action_update).setOnClickListener(v -> {
            Intent open = new Intent(Intent.ACTION_VIEW, Uri.parse(RELEASES_URL));
            try {
                startActivity(open);
            } catch (Exception e) {
                Toast.makeText(this, RELEASES_URL, Toast.LENGTH_LONG).show();
            }
        });
        String logsPackage = targetPackage != null ? targetPackage : SelectorActivity.TARGET_PACKAGE;
        findViewById(R.id.bootstrap_action_share_logs).setOnClickListener(v -> LogBundle.shareAsync(this, logsPackage));
        if (targetPackage == null || targetPackage.isEmpty()) {
            failAndFinish("No target package specified in intent extras!", null);
            return;
        }

        // Let the loading screen render first, then perform initialization work.
        statusView.post(() -> new Thread(() -> runBootstrapFlow(targetPackage), "bootstrap-flow").start());
    }

    private void runBootstrapFlow(String targetPackage) {
        Context gameContext;
        try {
            gameContext = createPackageContext(targetPackage, CONTEXT_IGNORE_SECURITY | CONTEXT_INCLUDE_CODE);
        } catch (Exception e) {
            failAndFinish("Failed to create package context for target package: " + targetPackage, e);
            return;
        }

        // Build our own loader for the game code, parented to the launcher's loader.
        // It is created once per process: the game's ApplicationInfo is modified at runtime
        // after the first launch, and every hook must keep seeing the same loader instance.
        // Never call gameContext.getClassLoader() - that would create a second, unrelated loader.
        ClassLoader gameClassLoader;
        try {
            if (sGameClassLoader == null) {
                // Native library search order: patched libs in code_cache (libil2cpp),
                // then the launcher's own libs (libmain), then the game's original directory.
                // The files in the first two directories are written before the game asks for them.
                File codeCacheScoped = new File(getApplicationContext().getCodeCacheDir(), targetPackage);
                File launcherLibDir = new File(getApplicationContext().getApplicationInfo().nativeLibraryDir);
                sGameClassLoader = GameClassLoaderFactory.create(gameContext.getApplicationInfo(), getClassLoader(),
                        codeCacheScoped, launcherLibDir);
            }
            gameClassLoader = sGameClassLoader;
            CustomContextWrapper.setGameClassLoader(gameClassLoader);
        } catch (Exception e) {
            failAndFinish("Failed to create class loader for target package: " + targetPackage, e);
            return;
        }

        Intent launchIntent = getPackageManager().getLaunchIntentForPackage(targetPackage);
        if (launchIntent == null) {
            failAndFinish("No launch intent for target package: " + targetPackage, null);
            return;
        }

        ComponentName launcherComponent = launchIntent.getComponent();
        if (launcherComponent == null) {
            launcherComponent = launchIntent.resolveActivity(getPackageManager());
        }

        if (FusionSettings.dropActivityOverrideForGame(this, targetPackage)) {
            Log.i(TAG, "Dropped a stored launch activity override; the main activity is used.");
        }

        if (launcherComponent == null) {
            failAndFinish("Failed to resolve launcher activity for target package: " + targetPackage, null);
            return;
        }

        final int targetOrientation = resolveTargetOrientation(launcherComponent);

        FusionConfig config;

        try {
            config = prepareConfig(
                    getApplicationContext(),
                    gameContext,
                    launcherComponent,
                    targetPackage
            );
        } catch (LauncherUpdateRequiredException e) {
            failAndStay(getString(R.string.bootstrap_launcher_update_required, e.builtFor, e.installed));
            return;
        } catch (Throwable t) {
            failAndFinish("Failed while preparing Fusion runtime.", t);
            return;
        }

        Class<?> launcherClass;
        try {
            launcherClass = gameClassLoader.loadClass(launcherComponent.getClassName());
        } catch (ClassNotFoundException e) {
            Log.e(TAG, "Failed to get class for launcher activity!");
            return;
        }

        setPhaseStatus(getString(R.string.bootstrap_status_installing_hooks));
        // The bridge subclass hands the UnityPlayer its Context. It replays the game's
        // onCreate, so a game build it does not recognize is a launcher update, not a
        // degraded start.
        if (!UnityActivityHost.install(getApplicationContext(), gameContext, gameClassLoader,
                launcherComponent.getClassName())) {
            failAndStay(getString(R.string.bootstrap_game_layout_unsupported));
            return;
        }
        try {
            InstrumentationHooks.install(getApplicationContext(), gameClassLoader, launcherComponent.getClassName());
            android.content.res.Resources launcherResources = getApplicationContext().getResources();
            InstrumentationHooks.setFallbackResources(gameContext.getResources(), launcherResources);
            FallbackResources.install(getApplicationContext(), gameContext.getResources(), launcherResources);
        } catch (Exception e) {
            Log.e(TAG, "Failed to install base hooks", e);
        }

        var className = launcherComponent.getClassName();

        try {
            setPhaseStatus(getString(R.string.bootstrap_status_launching));
            initializeFusion(config);
            runOnMainThread(() -> {
                try {
                    var intent = new Intent(this, launcherClass);

                    // Using the stub activity intent here avoids one extra layer of hooks running.
                    // Its not necessary but could be more performant.
                    var intentWrapped = new Intent(this, StubActivity.class);
                    intentWrapped.putExtra(InstrumentationHooks.EXTRA_IS_DYNAMIC_ACTIVITY, true);
                    intentWrapped.putExtra(InstrumentationHooks.EXTRA_ORIGINAL_INTENT, intent);
                    intentWrapped.putExtra(InstrumentationHooks.EXTRA_FUSION_CONFIG, config);
                    intentWrapped.putExtra(InstrumentationHooks.EXTRA_TARGET_ORIENTATION, targetOrientation);

                    sGameActivityStarted = true;
                    startActivity(intentWrapped);
                    finish();
                } catch (Throwable t) {
                    failAndFinish("Failed to launch target app's launcher activity: " + className, t);
                }
            });
        } catch (Exception e) {
            failAndFinish("Failed to launch target app's launcher activity: " + className, e);
        }
    }

    private void setPhaseStatus(String status) {
        runOnMainThread(() -> {
            if (statusView != null) {
                statusView.setText(status);
            }
            if (spinnerProgress != null) {
                spinnerProgress.setVisibility(View.VISIBLE);
            }
            if (downloadProgress != null) {
                downloadProgress.setVisibility(View.GONE);
                downloadProgress.setIndeterminate(false);
                downloadProgress.setProgress(0);
            }
            if (progressDetailsView != null) {
                progressDetailsView.setVisibility(View.GONE);
                progressDetailsView.setText("");
            }
        });
    }



    private void failAndFinish(String message, Throwable error) {
        runOnMainThread(() -> {
            if (error != null) {
                Log.e(TAG, message, error);
            } else {
                Log.e(TAG, message);
            }
            if (statusView != null) {
                statusView.setText(getString(R.string.bootstrap_status_error));
            }
            Toast.makeText(this, message, Toast.LENGTH_LONG).show();
            finish();
        });
    }

    /** Shows a message the player has to act on and leaves the screen up instead of closing it. */
    private void failAndStay(String message) {
        runOnMainThread(() -> {
            Log.e(TAG, message);
            if (titleView != null) {
                titleView.setText(R.string.bootstrap_status_error);
            }
            if (statusView != null) {
                statusView.setText(message);
            }
            if (spinnerProgress != null) {
                spinnerProgress.setVisibility(View.GONE);
            }
            if (downloadProgress != null) {
                downloadProgress.setVisibility(View.GONE);
            }
            if (progressDetailsView != null) {
                progressDetailsView.setVisibility(View.GONE);
            }
            if (actionsRow != null) {
                actionsRow.setVisibility(View.VISIBLE);
            }
        });
    }

    /** The installed game moved to a Unity version this build was not made for. */
    private static final class LauncherUpdateRequiredException extends RuntimeException {
        final String builtFor;
        final String installed;

        LauncherUpdateRequiredException(String builtFor, String installed) {
            super("Launcher is built for Unity " + builtFor + " but the game uses " + installed);
            this.builtFor = builtFor;
            this.installed = installed;
        }
    }

    private void runOnMainThread(Runnable runnable) {
        if (Looper.myLooper() == Looper.getMainLooper()) {
            runnable.run();
        } else {
            runOnUiThread(runnable);
        }
    }

    private void initializeFusion(FusionConfig config) {
        Log.i(TAG, "Initializing Fusion for " + config.gamePackageId + " via " + config.gameLauncherName);
    }

    private FusionConfig prepareConfig(Context appContext,
                                       Context gameContext,
                                       ComponentName launcherComponent,
                                       String targetPackage) {

        String gameLibDir = gameContext.getApplicationInfo().nativeLibraryDir;
        String appLibDir = appContext.getApplicationInfo().nativeLibraryDir;

        File appDataDir = new File(appContext.getFilesDir(), targetPackage);

        File dataOnSdCard = Utilities.getExternalFusionCoreDirectory(targetPackage);
        File codeCacheScoped = new File(appContext.getCodeCacheDir(), targetPackage);

        // Earlier launcher builds copied an unstripped libunity into this directory. It comes first
        // in the native library search order, so a leftover copy must not stay behind.
        for (String stale : new String[]{"libunity.so", "libunity.sym.so", "libunity.cache.properties"}) {
            File file = new File(codeCacheScoped, stale);
            if (file.exists() && !file.delete()) {
                Log.w(TAG, "Could not remove stale " + file);
            }
        }

        setPhaseStatus(getString(R.string.bootstrap_status_copy_assets));
        File copiedData = new File(appDataDir, "Data_copy");
        boolean copied = Utilities.copyAssets(gameContext.getAssets(), "bin/Data", copiedData);
        if (!copied) {
            Log.e(TAG, "Failed to copy Unity Data assets! BepInEx may not work correctly.");
        } else {
            applyGlobalMetadataOverride(dataOnSdCard, copiedData);
        }

        setPhaseStatus(getString(R.string.bootstrap_status_detecting_version));
        String version = VersionLookup.TryLookup(copiedData);
        if (version == null) {
            Log.e(TAG, "Failed to determine Unity version! BepInEx may not work correctly.");
            version = BACKUP_UNITY_VERSION;
        } else {
            Log.i(TAG, "Determined Unity version: " + version);
            if (!version.trim().equals(BuildConfig.BUNDLED_UNITY_VERSION)) {
                // The game's own libunity is used, but the bundled Unity support libraries that
                // BepInEx builds its interop against are tied to one version: a game update past
                // it means the launcher itself needs updating. Debug builds carry on regardless.
                if (!BuildConfig.DEBUG) {
                    throw new LauncherUpdateRequiredException(BuildConfig.BUNDLED_UNITY_VERSION, version);
                }
                Log.w(TAG, "Game uses Unity " + version + " but the launcher is built for " + BuildConfig.BUNDLED_UNITY_VERSION);
            }
        }

        setPhaseStatus(getString(R.string.bootstrap_status_extracting_runtime));

        File dotnetDir = new File(appContext.getCodeCacheDir(), "dotnet");
        File bepInExDir = new File(dataOnSdCard, "BepInEx");

        Utilities.extractZipFromAssets(appContext, "BepInEx-arm64.zip", bepInExDir);
        Utilities.extractZipFromAssets(appContext, "dotnet-arm64.zip", dotnetDir);

        setPhaseStatus(getString(R.string.bootstrap_status_installing_plugin));
        if (!PluginInstaller.installBundledPlugins(appContext, bepInExDir)) {
            Log.w(TAG, "Bundled plugin install did not complete; continuing with whatever is in plugins/");
        }

        return new FusionConfig(
                targetPackage,
                launcherComponent.flattenToString(),
                gameLibDir,
                appLibDir,
                appDataDir.getAbsolutePath(),
                codeCacheScoped.getAbsolutePath(),
                bepInExDir.getAbsolutePath(),
                dotnetDir.getAbsolutePath(),
                copiedData.getAbsolutePath(),
                version,
                new String[]{},
                new String[]{}
        );
    }

    private void applyGlobalMetadataOverride(File dataOnSdCard, File copiedData) {
        File overrideMetadata = new File(dataOnSdCard, GLOBAL_METADATA_FILE);
        if (!overrideMetadata.isFile()) {
            Log.i(TAG, "No global-metadata override found at " + overrideMetadata.getAbsolutePath());
            return;
        }

        File targetMetadata = new File(new File(copiedData, "Managed/Metadata"), GLOBAL_METADATA_FILE);
        try {
            copyFile(overrideMetadata, targetMetadata);
            Log.i(TAG, "Applied global-metadata override from " + overrideMetadata.getAbsolutePath());
        } catch (IOException e) {
            throw new IllegalStateException("Failed to apply global-metadata override from "
                    + overrideMetadata.getAbsolutePath(), e);
        }
    }

    private static void copyFile(File source, File target) throws IOException {
        File parent = target.getParentFile();
        if (parent != null && !parent.exists() && !parent.mkdirs()) {
            throw new IOException("Failed to create parent directory: " + parent.getAbsolutePath());
        }

        byte[] buffer = new byte[8192];
        try (FileInputStream in = new FileInputStream(source);
             FileOutputStream out = new FileOutputStream(target, false)) {
            int count;
            while ((count = in.read(buffer)) != -1) {
                out.write(buffer, 0, count);
            }
        }
    }

    private int resolveTargetOrientation(ComponentName launcher) {
        try {
            ActivityInfo info = getPackageManager().getActivityInfo(launcher, 0);
            if (info.screenOrientation == ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED) {
                Log.i(TAG, "Target orientation unspecified; defaulting to landscape");
                return ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE;
            }
            return info.screenOrientation;
        } catch (NameNotFoundException e) {
            return ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE;
        }
    }


}
