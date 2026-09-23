package dev.allofus.fusioncore.hooks;

import android.app.Activity;
import android.app.Instrumentation;
import android.content.ComponentName;
import android.content.Context;
import android.content.ContextWrapper;
import android.content.Intent;
import java.util.Set;
import java.util.HashSet;
import java.util.Collections;
import android.content.pm.PackageManager;
import android.content.pm.PackageInfo;
import android.content.pm.ActivityInfo;
import android.os.Bundle;
import android.util.Log;
import android.view.ContextThemeWrapper;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;

import java.lang.reflect.Field;
import java.util.List;
import java.util.Arrays;

import dev.allofus.fusioncore.BuildConfig;
import dev.allofus.fusioncore.R;
import dev.allofus.fusioncore.SecondaryStubActivity;
import dev.allofus.fusioncore.StubActivity;
import dev.allofus.fusioncore.tools.FallbackResources;
import dev.allofus.fusioncore.tools.FusionInstrumentation;

/**
 * Redirects Instrumentation.execStartActivity and Instrumentation.newActivity
 * for enabling dynamic loading of activities not declared in AndroidManifest.xml.
 * The process instrumentation is replaced by a {@link FusionInstrumentation} wrapper
 * that calls back into the handlers here.
 */
public class InstrumentationHooks {

    private static final String TAG = "InstrumentationHooks";

    public static final String EXTRA_IS_DYNAMIC_ACTIVITY = "fusioncore.is_dynamic_activity";
    public static final String EXTRA_ORIGINAL_INTENT = "fusioncore.original_intent";
    public static final String EXTRA_TARGET_ORIENTATION = "fusioncore.target_orientation";
    public static final String EXTRA_FUSION_CONFIG = "fusioncore.config";

    public static boolean areHooksInstalled = false;

    /** Loader that dynamically started game activities are instantiated from. */
    private static volatile ClassLoader gameClassLoader;
    private static volatile android.content.res.Resources fallbackGameResources;
    private static volatile android.content.res.Resources fallbackLauncherResources;

    /** Resources that game activities fall back to for ids missing from their own package. */
    public static void setFallbackResources(android.content.res.Resources game, android.content.res.Resources launcher) {
        fallbackGameResources = game;
        fallbackLauncherResources = launcher;
    }
    private static volatile String mainActivityClassName;
    /** Launcher context the loading overlay is inflated from. */
    private static volatile Context loadingViewContext;
    /** Activities declared in this launcher's own manifest; they must not be routed through a stub. */
    private static volatile Set<String> launcherActivities = Collections.emptySet();

    public static synchronized void install(Context fusionContext, ClassLoader gameLoader, String mainActivityClass) {
        if (areHooksInstalled) {
            Log.d(TAG, "Instrumentation wrapper already installed");
            return;
        }
        gameClassLoader = gameLoader;
        mainActivityClassName = mainActivityClass;
        loadingViewContext = fusionContext;
        launcherActivities = loadDeclaredActivities(fusionContext);

        try {
            installInstrumentation();
            areHooksInstalled = true;
            Log.d(TAG, "Successfully installed Instrumentation wrapper");
        } catch (Exception e) {
            Log.e(TAG, "Failed to install Instrumentation wrapper", e);
        }
    }

    /**
     * Swaps ActivityThread.mInstrumentation for the wrapper. Activities attached before this
     * point keep their own reference to the original instance for startActivity, which only
     * matters for the launcher's own screens; instantiation and lifecycle calls always go
     * through the ActivityThread field.
     *
     * <p>The two framework members read here are on the unsupported (grey) list, so plain
     * reflection reaches them without any hidden-API exemption; no ordering against the
     * remaining runtime hooks is required.
     */
    private static void installInstrumentation() throws ReflectiveOperationException {
        Class<?> activityThreadClass = Class.forName("android.app.ActivityThread");
        Object activityThread = activityThreadClass.getMethod("currentActivityThread").invoke(null);
        if (activityThread == null) {
            throw new IllegalStateException("currentActivityThread() returned null");
        }
        Field field = activityThreadClass.getDeclaredField("mInstrumentation");
        field.setAccessible(true);
        Instrumentation current = (Instrumentation) field.get(activityThread);
        if (current instanceof FusionInstrumentation) {
            Log.d(TAG, "Instrumentation wrapper already in place");
            return;
        }
        FusionInstrumentation wrapper = new FusionInstrumentation(current);
        field.set(activityThread, wrapper);
        Log.i(TAG, "Instrumentation wrapper installed over " + current.getClass().getName());

        Log.i(TAG, "Instrumentation execStartActivity overloads resolved: "
                + wrapper.getResolvedExecStartCount() + "/" + FusionInstrumentation.EXEC_START_SIGNATURE_COUNT);
        for (String failure : wrapper.getUnresolvedExecStart()) {
            Log.w(TAG, "Instrumentation execStartActivity overload unresolved: " + failure);
        }

        List<String> uncovered = FusionInstrumentation.reportUncoveredOverloads();
        if (uncovered.isEmpty()) {
            Log.i(TAG, "Instrumentation execStartActivity overloads: all covered (other methods inherit)");
        } else {
            for (String signature : uncovered) {
                Log.w(TAG, "Instrumentation execStartActivity overload not covered: " + signature);
            }
        }
    }

    /** Runs before the activity's onCreate; icicle is the saved state the framework hands over. */
    public static void beforeActivityCreate(Activity activity, Bundle icicle) {
        applyGameClassLoader(activity);
        applySavedStateClassLoader(activity, icicle);
        applyFallbackResources(activity);
        applyTargetOrientation(activity);
    }

    /** Runs after the activity's onCreate has returned. */
    public static void afterActivityCreate(Activity activity) {
        // Only the main game activity gets the loading overlay: the bridge clears it
        // there once the runtime is up, while secondary activities (ads, sign-in)
        // would keep it on screen forever.
        if (!activity.getClass().getName().equals(mainActivityClassName)) {
            return;
        }
        Context fusionContext = loadingViewContext;
        if (fusionContext == null) {
            return;
        }
        try {
            ViewGroup decorView = (ViewGroup) activity.getWindow().getDecorView();
            Context themedFusionContext = new ContextThemeWrapper(fusionContext, androidx.appcompat.R.style.Theme_AppCompat);
            LayoutInflater inflater = LayoutInflater.from(themedFusionContext);
            View loadingView = inflater.inflate(R.layout.loading_view, decorView, false);
            decorView.addView(loadingView);
            Log.i(TAG, "Loading view attached to " + activity.getClass().getName());
        } catch (Throwable t) {
            Log.w(TAG, "Failed to attach loading view to " + activity.getClass().getName() + ": " + t);
        }
    }

    public static void beforeActivityResume(Activity activity) {
        applyTargetOrientation(activity);
    }

    private static void applyTargetOrientation(Activity activity) {
        try {
            Intent intent = activity.getIntent();
            if (intent == null) {
                return;
            }
            int orientation = readTargetOrientation(intent);
            if (orientation == ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED) {
                return;
            }
            activity.setRequestedOrientation(orientation);
            Log.i(TAG, "Applied target orientation " + orientation
                    + " to " + activity.getClass().getName());
        } catch (Exception e) {
            Log.e(TAG, "Failed to apply target orientation", e);
        }
    }

    /**
     * Points the activity's base context at the game class loader so that
     * framework paths going through Context.getClassLoader() (view inflation,
     * fragment instantiation, etc.) resolve game classes. The field is a hidden
     * framework member, so failure is tolerated and only logged.
     */
    private static void applyGameClassLoader(Activity activity) {
        ClassLoader loader = gameClassLoader;
        if (loader == null) {
            return;
        }
        try {
            Intent intent = activity.getIntent();
            if (!isDynamicIntent(intent)) {
                return;
            }

            Context base = activity;
            while (base instanceof ContextWrapper) {
                Context next = ((ContextWrapper) base).getBaseContext();
                if (next == null || next == base) {
                    break;
                }
                base = next;
            }

            Field field = null;
            Class<?> clazz = base.getClass();
            while (clazz != null && field == null) {
                try {
                    field = clazz.getDeclaredField("mClassLoader");
                } catch (NoSuchFieldException e) {
                    clazz = clazz.getSuperclass();
                }
            }
            if (field == null) {
                Log.w(TAG, "mClassLoader field not found on " + base.getClass().getName()
                        + "; leaving base context loader untouched");
                return;
            }

            field.setAccessible(true);
            field.set(base, loader);
            boolean applied = activity.getClassLoader() == loader;
            Log.i(TAG, "Base context class loader override for " + activity.getClass().getName()
                    + ": applied=" + applied);
        } catch (Throwable t) {
            Log.w(TAG, "Failed to override base context class loader for "
                    + activity.getClass().getName() + ": " + t);
        }
    }

    private static void applyFallbackResources(Activity activity) {
        android.content.res.Resources game = fallbackGameResources;
        android.content.res.Resources launcher = fallbackLauncherResources;
        if (game == null || launcher == null) {
            return;
        }
        try {
            if (!isDynamicIntent(activity.getIntent())) {
                return;
            }
            FallbackResources.install(activity, game, launcher);
        } catch (Throwable t) {
            Log.w(TAG, "Failed to install fallback resources on " + activity.getClass().getName() + ": " + t);
        }
    }

    /**
     * The framework hands the restored state Bundle to the activity with the launcher's
     * loader; its lazily unparcelled values pin that loader on first read, so the game
     * loader has to be set before the game's onCreate touches it.
     */
    private static void applySavedStateClassLoader(Activity activity, Bundle savedState) {
        ClassLoader loader = gameClassLoader;
        if (loader == null || savedState == null) {
            return;
        }
        if (!isDynamicIntent(activity.getIntent())) {
            return;
        }
        savedState.setClassLoader(loader);
    }

    private static int readTargetOrientation(Intent intent) {
        int orientation = intent.getIntExtra(EXTRA_TARGET_ORIENTATION,
                ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED);
        if (orientation != ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED) {
            return orientation;
        }
        ClassLoader loader = gameClassLoader != null
                ? gameClassLoader
                : InstrumentationHooks.class.getClassLoader();
        Intent original = resolveOriginalIntent(intent, loader);
        if (original != null) {
            return original.getIntExtra(EXTRA_TARGET_ORIENTATION,
                    ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED);
        }
        return ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED;
    }

    /** Rewrites the Intent argument in place so an unregistered activity is routed via a stub. */
    public static void handleExecStartBeforeCall(Object[] args) {
        try {
            Log.i(TAG, "handling exec start for " + Arrays.toString(args));
            int intentIdx = -1;

            if (args != null) {
                for (int i = 0; i < args.length; i++) {
                    Object arg = args[i];
                    if (arg == null) continue;
                    if (Intent.class.isAssignableFrom(arg.getClass())) {
                        intentIdx = i;
                        break;
                    }
                }

                if (intentIdx < 0) {
                    Log.e(TAG, "No intent found in arguments for execStartActivity!");
                    return;
                }

                Intent intent = (Intent) args[intentIdx];
                if (intent == null) {
                    Log.e(TAG, "Intent was null!");
                    return;
                }

                if (intent.getComponent() == null) {
                    Log.d(TAG, "execStartActivity: Passing through implicit intent (action=" + intent.getAction() + ")");
                    return;
                }

                String targetClass = intent.getComponent().getClassName();

                // Game code builds intents with the launcher-owned activity context, so the
                // component package alone cannot tell a launcher activity from a game one.
                if (launcherActivities.contains(targetClass)) {
                    Log.d(TAG, "execStartActivity: Passing through launcher activity " + targetClass);
                    return;
                }


                if (isDynamicIntent(intent)) return;

                // Only the main game activity belongs in the single-task stub; anything else
                // (ad controllers, sign-in pages) needs a fresh standard-launch activity.
                Class<?> stub = targetClass.equals(mainActivityClassName)
                        ? StubActivity.class
                        : SecondaryStubActivity.class;
                args[intentIdx] = getInjectedIntent(intent, stub);
                Log.d(TAG, "execStartActivity: intercepted unregistered activity: " + targetClass
                        + " via " + stub.getSimpleName());
            } else {
                Log.e(TAG, "No arguments to handle execStartActivity!");
            }
        } catch (Exception e) {
            Log.e(TAG, "Error in execStartActivity beforeCall", e);
        }
    }

    private static Set<String> loadDeclaredActivities(Context context) {
        Set<String> names = new HashSet<>();
        try {
            PackageInfo info = context.getPackageManager().getPackageInfo(context.getPackageName(), PackageManager.GET_ACTIVITIES);
            if (info.activities != null) {
                for (ActivityInfo activity : info.activities) {
                    names.add(activity.name);
                }
            }
        } catch (Exception e) {
            Log.e(TAG, "Failed to read declared activities; only the known stubs are exempt", e);
        }
        names.add(StubActivity.class.getName());
        names.add(SecondaryStubActivity.class.getName());
        Log.i(TAG, "Launcher activities exempt from stub routing: " + names.size());
        return Collections.unmodifiableSet(names);
    }

    /** Restores the original intent, class name and loader in place for a stub-routed activity. */
    public static void handleNewActivityBeforeCall(Object[] args) {
        try {
            if (args == null) return;

            int intentIdx = -1;
            int strIdx = -1;
            int loaderIdx = -1;

            for (int i = 0; i < args.length; i++) {
                Object arg = args[i];
                if (arg == null) continue;
                if (Intent.class.isAssignableFrom(arg.getClass())) {
                    intentIdx = i;
                }
                else if (String.class.isAssignableFrom(arg.getClass())) {
                    strIdx = i;
                }
                else if (ClassLoader.class.isAssignableFrom(arg.getClass())) {
                    loaderIdx = i;
                }
            }

            if (intentIdx < 0 || strIdx < 0) {
                Log.e(TAG, "Intent or String not found in arguments!");
                return;
            }

            Intent intent = (Intent) args[intentIdx];

            // The framework only calls setExtrasClassLoader after newActivity returns, and the
            // first get* on a binder-delivered Bundle pins its loader into every lazily
            // unparcelled value. Set ours before touching any extra.
            ClassLoader extrasLoader = gameClassLoader != null
                    ? gameClassLoader
                    : InstrumentationHooks.class.getClassLoader();
            intent.setExtrasClassLoader(extrasLoader);

            if (!isDynamicIntent(intent)) return;

            Intent original = resolveOriginalIntent(intent, extrasLoader);
            materializeFusionConfig(intent);

            if (original != null && original.getComponent() != null) {
                args[intentIdx] = original;
                args[strIdx] = original.getComponent().getClassName();
                ClassLoader loader = gameClassLoader;
                if (loaderIdx >= 0 && loader != null) {
                    args[loaderIdx] = loader;
                    Log.i(TAG, "newActivity: using game class loader for "
                            + original.getComponent().getClassName());
                } else if (loaderIdx >= 0) {
                    Log.w(TAG, "newActivity: game class loader not registered, keeping original loader");
                }
                Log.d(TAG, "newActivity: intercepted StubActivity for dynamic origin");
            } else {
                Log.e(TAG, "Failed to resolve original intent or component was null!");
            }
        } catch (Exception e) {
            Log.e(TAG, "Error in newActivity beforeCall", e);
        }
    }

    private static Intent resolveOriginalIntent(Intent currentIntent, ClassLoader extrasLoader) {
        try {
            currentIntent.setExtrasClassLoader(extrasLoader);

            Intent originalIntent = currentIntent.getParcelableExtra(EXTRA_ORIGINAL_INTENT);

            if (originalIntent != null && originalIntent.getComponent() != null) {
                originalIntent.setExtrasClassLoader(extrasLoader);
                Log.d(TAG, "Resolved original intent for " + originalIntent.getComponent().getClassName());
                return originalIntent;
            }
        } catch (Exception e) {
            Log.e(TAG, "Error resolving original intent", e);
        }
        return null;
    }

    /**
     * Reads the config extra once on the Java side so the value is unparcelled under a
     * known loader and cached in the Bundle; the native reader then gets the cached object.
     */
    private static void materializeFusionConfig(Intent intent) {
        try {
            Object config = intent.getParcelableExtra(EXTRA_FUSION_CONFIG);
            if (config == null) {
                Log.w(TAG, "newActivity: fusion config extra missing from intent");
            } else {
                Log.d(TAG, "newActivity: fusion config materialized via "
                        + config.getClass().getClassLoader().getClass().getSimpleName());
            }
        } catch (Exception e) {
            Log.e(TAG, "newActivity: failed to unparcel fusion config", e);
        }
    }

    private static Intent getInjectedIntent(Intent intent, Class<?> stubClass) {
        Intent newIntent = new Intent(intent);
        newIntent.putExtra(EXTRA_IS_DYNAMIC_ACTIVITY, true);
        newIntent.putExtra(EXTRA_ORIGINAL_INTENT, intent);
        newIntent.setComponent(new ComponentName(BuildConfig.APPLICATION_ID, stubClass.getName()));
        return newIntent;
    }

    private static boolean isDynamicIntent(Intent intent) {
        if (intent == null) return false;

        return intent.getBooleanExtra(EXTRA_IS_DYNAMIC_ACTIVITY, false);
    }
}
