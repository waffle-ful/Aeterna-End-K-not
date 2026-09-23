package dev.allofus.fusioncore.bridge;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.content.res.AssetManager;
import android.os.Bundle;
import android.util.Log;
import android.view.View;
import android.view.Window;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.nio.ByteBuffer;
import java.util.ArrayList;
import java.util.List;

import dalvik.system.InMemoryDexClassLoader;
import dev.allofus.fusioncore.tools.CustomContextWrapper;

/**
 * Hands the UnityPlayer its Context without a runtime hook. The game's main activity is
 * instantiated as {@link #BRIDGE_ACTIVITY_CLASS}, a subclass shipped in fusion-bridge.dex,
 * whose {@code onCreate} calls {@link #onCreate} instead of the inherited chain. This
 * replays {@code UnityPlayerActivity.onCreate} and {@code EosUnityPlayerActivity.onCreate}
 * verbatim, except that the UnityPlayer receives the {@link CustomContextWrapper} rather
 * than the activity, and gets its activity fields filled in before its constructor runs
 * (the constructor reads them to grab the window and register the back dispatcher).
 *
 * <p>Both steps that Java cannot express, calling {@code Activity.onCreate} past an
 * overriding subclass and running a constructor on a pre-populated instance, go through
 * {@link FusionJni}. Nothing here touches hidden framework members.
 *
 * <p>The replayed bodies are tied to the game build. {@link #install} checks every member
 * they rely on and reports failure instead of activating, so a game update that changes
 * them stops the start with an update notice rather than running a half-initialized player.
 */
public final class UnityActivityHost {

    private static final String TAG = "UnityActivityHost";

    public static final String BRIDGE_ACTIVITY_CLASS = "dev.allofus.fusioncore.bridge.FusionEosUnityPlayerActivity";
    private static final String BRIDGE_DEX_ASSET = "bridge/fusion-bridge.dex";
    private static final String EXPECTED_MAIN_ACTIVITY = "com.innersloth.spacemafia.EosUnityPlayerActivity";
    private static final String UNITY_PLAYER_ACTIVITY = "com.unity3d.player.UnityPlayerActivity";
    private static final String UNITY_PLAYER = "com.unity3d.player.UnityPlayer";
    private static final String LIFECYCLE_EVENTS = "com.unity3d.player.IUnityPlayerLifecycleEvents";
    private static final String EOSSDK = "com.epicgames.mobile.eossdk.EOSSDK";
    private static final String COMMAND_LINE_EXTRA = "unity";

    private static volatile ClassLoader bridgeLoader;
    private static Context gameContext;
    private static Method activityOnCreate;
    private static Method updateCommandLineArguments;
    private static Field unityPlayerField;
    private static Class<?> unityPlayerClass;
    private static Constructor<?> unityPlayerConstructor;
    private static Method eosInit;
    private static final List<Field> activityFields = new ArrayList<>();

    private UnityActivityHost() {
    }

    /**
     * Prepares the bridge for the given game. Returns false, with the reason logged, when
     * any expectation about the game's classes does not hold; the caller then refuses to
     * start the game.
     */
    public static synchronized boolean install(Context fusionContext, Context gameCtx,
                                               ClassLoader gameLoader, String mainActivityClass) {
        if (bridgeLoader != null) {
            Log.d(TAG, "Bridge already installed");
            return true;
        }
        try {
            if (!EXPECTED_MAIN_ACTIVITY.equals(mainActivityClass)) {
                throw new IllegalStateException("main activity is " + mainActivityClass
                        + ", bridge subclass extends " + EXPECTED_MAIN_ACTIVITY);
            }
            Class<?> mainClass = gameLoader.loadClass(mainActivityClass);
            Class<?> playerActivityClass = mainClass.getSuperclass();
            if (playerActivityClass == null || !UNITY_PLAYER_ACTIVITY.equals(playerActivityClass.getName())) {
                throw new IllegalStateException("superclass of " + mainActivityClass + " is " + playerActivityClass);
            }
            // The two onCreate bodies being replayed must be the ones this class was written
            // against: each must exist on its own class (not inherited from further up).
            playerActivityClass.getDeclaredMethod("onCreate", Bundle.class);
            mainClass.getDeclaredMethod("onCreate", Bundle.class);

            Class<?> playerClass = gameLoader.loadClass(UNITY_PLAYER);
            Class<?> eventsInterface = gameLoader.loadClass(LIFECYCLE_EVENTS);
            if (!eventsInterface.isAssignableFrom(mainClass)) {
                throw new IllegalStateException(mainActivityClass + " does not implement " + LIFECYCLE_EVENTS);
            }
            if (!View.class.isAssignableFrom(playerClass)) {
                throw new IllegalStateException(UNITY_PLAYER + " is not a View");
            }
            // AllocObject cannot instantiate an abstract class; that would surface inside
            // onCreate instead of here.
            if (Modifier.isAbstract(playerClass.getModifiers()) || playerClass.isInterface()) {
                throw new IllegalStateException(UNITY_PLAYER + " is not a concrete class");
            }
            Constructor<?> ctor = playerClass.getDeclaredConstructor(Context.class, eventsInterface);
            ctor.setAccessible(true);

            Field playerField = playerActivityClass.getDeclaredField("mUnityPlayer");
            if (!playerField.getType().isAssignableFrom(playerClass)) {
                throw new IllegalStateException("mUnityPlayer has type " + playerField.getType());
            }
            playerField.setAccessible(true);

            Method updateArgs = playerActivityClass.getDeclaredMethod("updateUnityCommandLineArguments", String.class);
            updateArgs.setAccessible(true);

            Method eos = gameLoader.loadClass(EOSSDK).getMethod("init", Activity.class);
            if (!Modifier.isStatic(eos.getModifiers())) {
                throw new IllegalStateException("EOSSDK.init is not static");
            }

            Method onCreate = Activity.class.getDeclaredMethod("onCreate", Bundle.class);

            List<Field> fields = collectActivityFields(playerClass);
            if (fields.isEmpty()) {
                throw new IllegalStateException("no Activity-typed fields found on " + UNITY_PLAYER);
            }

            byte[] dex = readAsset(fusionContext.getAssets(), BRIDGE_DEX_ASSET);
            ClassLoader loader = new InMemoryDexClassLoader(ByteBuffer.wrap(dex), gameLoader);
            Class<?> bridgeClass = loader.loadClass(BRIDGE_ACTIVITY_CLASS);
            if (bridgeClass.getSuperclass() != mainClass) {
                throw new IllegalStateException("bridge subclass resolved its superclass to "
                        + bridgeClass.getSuperclass() + " instead of the game's " + mainClass);
            }

            // Loads libfusionbridge.so now, so a missing library fails installation rather
            // than the first onCreate.
            Class.forName(FusionJni.class.getName(), true, UnityActivityHost.class.getClassLoader());

            gameContext = gameCtx;
            activityOnCreate = onCreate;
            updateCommandLineArguments = updateArgs;
            unityPlayerField = playerField;
            unityPlayerClass = playerClass;
            unityPlayerConstructor = ctor;
            eosInit = eos;
            activityFields.clear();
            activityFields.addAll(fields);
            bridgeLoader = loader;
            Log.i(TAG, "Bridge installed: " + BRIDGE_ACTIVITY_CLASS + " extends " + mainClass.getName()
                    + " (dex " + dex.length + " bytes, activity fields " + fields.size() + ")");
            return true;
        } catch (Throwable t) {
            Log.w(TAG, "Bridge not installed, the launcher must be updated: " + t);
            return false;
        }
    }

    public static boolean isActive() {
        return bridgeLoader != null;
    }

    /** Loader that defines {@link #BRIDGE_ACTIVITY_CLASS}; null until {@link #install} succeeds. */
    public static ClassLoader getBridgeLoader() {
        return bridgeLoader;
    }

    /**
     * Called by the bridge subclass in place of {@code super.onCreate}. Mirrors, in order,
     * UnityPlayerActivity.onCreate and EosUnityPlayerActivity.onCreate of the game build
     * {@link #install} validated against.
     */
    public static void onCreate(Activity activity, Bundle icicle) {
        if (!isActive()) {
            throw new IllegalStateException("bridge onCreate reached without installation");
        }
        try {
            // UnityPlayerActivity.onCreate
            activity.requestWindowFeature(Window.FEATURE_NO_TITLE);
            FusionJni.invokeNonvirtualVoid(activity, Activity.class, activityOnCreate, new Object[]{icicle});
            Intent intent = activity.getIntent();
            String commandLine = intent.getStringExtra(COMMAND_LINE_EXTRA);
            commandLine = (String) updateCommandLineArguments.invoke(activity, commandLine);
            intent.putExtra(COMMAND_LINE_EXTRA, commandLine);

            Object player = FusionJni.allocObject(unityPlayerClass);
            // The constructor only fills these itself when its Context argument is an
            // Activity, and reads them further down.
            setActivityFields(player, activity, "before");
            Context wrapped = new CustomContextWrapper(gameContext, activity);
            Log.i(TAG, "Constructing UnityPlayer with " + wrapped.getClass().getSimpleName()
                    + " for " + activity.getClass().getName());
            FusionJni.invokeNonvirtualVoid(player, unityPlayerClass, unityPlayerConstructor,
                    new Object[]{wrapped, activity});
            setActivityFields(player, activity, "after");

            unityPlayerField.set(activity, player);
            View playerView = (View) player;
            activity.setContentView(playerView);
            playerView.requestFocus();

            // EosUnityPlayerActivity.onCreate
            eosInit.invoke(null, activity);
            Log.d("EosUnityPlayerActivity", "EOS Loaded!");
        } catch (InvocationTargetException e) {
            throw rethrow(e.getCause() != null ? e.getCause() : e);
        } catch (RuntimeException | Error e) {
            throw e;
        } catch (Exception e) {
            throw new IllegalStateException("bridge onCreate failed", e);
        }
    }

    private static RuntimeException rethrow(Throwable t) {
        if (t instanceof RuntimeException) return (RuntimeException) t;
        if (t instanceof Error) throw (Error) t;
        return new IllegalStateException("bridge onCreate failed", t);
    }

    private static void setActivityFields(Object player, Activity activity, String phase) {
        for (Field field : activityFields) {
            try {
                boolean isStatic = Modifier.isStatic(field.getModifiers());
                field.set(isStatic ? null : player, activity);
                Log.i(TAG, "Set activity field " + field.getName() + (isStatic ? " (static)" : "") + " " + phase);
            } catch (IllegalAccessException e) {
                Log.e(TAG, "Failed to set activity field " + field.getName(), e);
            }
        }
    }

    /** Every Activity-typed field on the class and its superclasses. */
    private static List<Field> collectActivityFields(Class<?> playerClass) {
        List<Field> fields = new ArrayList<>();
        Class<?> clazz = playerClass;
        while (clazz != null && clazz.getSuperclass() != null) {
            for (Field field : clazz.getDeclaredFields()) {
                if (field.getType().equals(Activity.class)) {
                    field.setAccessible(true);
                    fields.add(field);
                }
            }
            clazz = clazz.getSuperclass();
        }
        return fields;
    }

    private static byte[] readAsset(AssetManager assets, String name) throws IOException {
        try (InputStream in = assets.open(name)) {
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            byte[] buffer = new byte[16 * 1024];
            int read;
            while ((read = in.read(buffer)) > 0) {
                out.write(buffer, 0, read);
            }
            return out.toByteArray();
        }
    }
}
