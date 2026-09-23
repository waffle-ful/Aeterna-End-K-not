package dev.allofus.fusioncore.tools;

import android.app.Activity;
import android.app.Application;
import android.app.Instrumentation;
import android.app.UiAutomation;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.os.Bundle;
import android.os.IBinder;
import android.os.PersistableBundle;
import android.os.UserHandle;

import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

import dev.allofus.fusioncore.hooks.InstrumentationHooks;

/**
 * Instrumentation wrapper that stands in for the process instrumentation so that the
 * activity start / instantiate / lifecycle paths can be redirected without runtime
 * method hooks.
 *
 * <p>Every call is forwarded to the wrapped {@code base} instance, never to {@code super}:
 * this object is app-created, so its own framework state (thread, contexts, component)
 * is unset and the base implementations would silently degrade (for example
 * {@code newActivity} disables the AppComponentFactory when it sees no thread).
 *
 * <p>The {@code execStartActivity} overloads are framework-internal; their parameter
 * types are all public so the overrides compile, but the base versions have to be
 * reached by reflection. {@link #reportUncoveredOverloads} lists the overloads the
 * framework declares that this class does not, so a missing one shows up in logcat
 * instead of as an activity that silently fails to start.
 */
public class FusionInstrumentation extends Instrumentation {
    private static final String EXEC_START = "execStartActivity";

    /** Parameter lists of the framework overloads this class overrides, resolved eagerly. */
    private static final Class<?>[][] EXEC_START_SIGNATURES = {
            {Context.class, IBinder.class, IBinder.class, Activity.class, Intent.class, int.class, Bundle.class},
            {Context.class, IBinder.class, IBinder.class, String.class, Intent.class, int.class, Bundle.class},
            {Context.class, IBinder.class, IBinder.class, String.class, Intent.class, int.class, Bundle.class, UserHandle.class},
    };

    public static final int EXEC_START_SIGNATURE_COUNT = EXEC_START_SIGNATURES.length;

    private final Instrumentation base;
    private final Method[] execStartMethods = new Method[EXEC_START_SIGNATURES.length];
    private final List<String> unresolvedExecStart = new ArrayList<>();

    public FusionInstrumentation(Instrumentation base) {
        this.base = base;
        for (int i = 0; i < EXEC_START_SIGNATURES.length; i++) {
            try {
                Method m = Instrumentation.class.getDeclaredMethod(EXEC_START, EXEC_START_SIGNATURES[i]);
                m.setAccessible(true);
                execStartMethods[i] = m;
            } catch (NoSuchMethodException | SecurityException e) {
                unresolvedExecStart.add(EXEC_START + Arrays.toString(EXEC_START_SIGNATURES[i]) + ": " + e);
            }
        }
    }

    /** Overload signatures whose framework method could not be reached; empty when all resolved. */
    public List<String> getUnresolvedExecStart() {
        return unresolvedExecStart;
    }

    public int getResolvedExecStartCount() {
        return EXEC_START_SIGNATURES.length - unresolvedExecStart.size();
    }

    public Instrumentation getBase() {
        return base;
    }

    // ---- activity start ---------------------------------------------------------------

    public ActivityResult execStartActivity(Context who, IBinder contextThread, IBinder token,
                                            Activity target, Intent intent, int requestCode, Bundle options) {
        Object[] args = {who, contextThread, token, target, intent, requestCode, options};
        InstrumentationHooks.handleExecStartBeforeCall(args);
        return forwardExecStart(0, args);
    }

    public ActivityResult execStartActivity(Context who, IBinder contextThread, IBinder token,
                                            String target, Intent intent, int requestCode, Bundle options) {
        Object[] args = {who, contextThread, token, target, intent, requestCode, options};
        InstrumentationHooks.handleExecStartBeforeCall(args);
        return forwardExecStart(1, args);
    }

    public ActivityResult execStartActivity(Context who, IBinder contextThread, IBinder token,
                                            String resultWho, Intent intent, int requestCode, Bundle options,
                                            UserHandle user) {
        Object[] args = {who, contextThread, token, resultWho, intent, requestCode, options, user};
        InstrumentationHooks.handleExecStartBeforeCall(args);
        return forwardExecStart(2, args);
    }

    private ActivityResult forwardExecStart(int signature, Object[] args) {
        Method m = execStartMethods[signature];
        Class<?>[] types = EXEC_START_SIGNATURES[signature];
        if (m == null) {
            throw new IllegalStateException("Framework lacks " + EXEC_START + Arrays.toString(types));
        }
        try {
            return (ActivityResult) m.invoke(base, args);
        } catch (InvocationTargetException e) {
            Throwable cause = e.getCause();
            if (cause instanceof RuntimeException re) throw re;
            if (cause instanceof Error err) throw err;
            throw new RuntimeException(cause);
        } catch (IllegalAccessException | IllegalArgumentException e) {
            throw new IllegalStateException("Cannot invoke " + EXEC_START + Arrays.toString(types), e);
        }
    }

    /**
     * Names the framework's {@code execStartActivity} overloads that this class does not override.
     * Only that family is checked: it is the one reached by reflection, so a missing overload
     * would bypass the intent rewrite. Other Instrumentation methods fall back to the inherited
     * implementation, which is stateless for the lifecycle callbacks the framework uses.
     */
    public static List<String> reportUncoveredOverloads() {
        List<String> missing = new ArrayList<>();
        for (Method m : Instrumentation.class.getDeclaredMethods()) {
            if (!m.getName().equals(EXEC_START)) continue;
            try {
                FusionInstrumentation.class.getDeclaredMethod(EXEC_START, m.getParameterTypes());
            } catch (NoSuchMethodException e) {
                missing.add(m.toString());
            }
        }
        return missing;
    }

    // ---- instantiate + lifecycle ------------------------------------------------------

    @Override
    public Activity newActivity(ClassLoader cl, String className, Intent intent)
            throws InstantiationException, IllegalAccessException, ClassNotFoundException {
        Object[] args = {cl, className, intent};
        InstrumentationHooks.handleNewActivityBeforeCall(args);
        return base.newActivity((ClassLoader) args[0], (String) args[1], (Intent) args[2]);
    }

    @Override
    public void callActivityOnCreate(Activity activity, Bundle icicle) {
        InstrumentationHooks.beforeActivityCreate(activity, icicle);
        base.callActivityOnCreate(activity, icicle);
        InstrumentationHooks.afterActivityCreate(activity);
    }

    @Override
    public void callActivityOnCreate(Activity activity, Bundle icicle, PersistableBundle persistentState) {
        InstrumentationHooks.beforeActivityCreate(activity, icicle);
        base.callActivityOnCreate(activity, icicle, persistentState);
        InstrumentationHooks.afterActivityCreate(activity);
    }

    @Override
    public void callActivityOnResume(Activity activity) {
        InstrumentationHooks.beforeActivityResume(activity);
        base.callActivityOnResume(activity);
    }

    // ---- plain forwarding: keep the framework's own state in play ----------------------

    @Override public Context getContext() { return base.getContext(); }
    @Override public Context getTargetContext() { return base.getTargetContext(); }
    @Override public ComponentName getComponentName() { return base.getComponentName(); }
    @Override public String getProcessName() { return base.getProcessName(); }
    @Override public boolean isProfiling() { return base.isProfiling(); }
    @Override public void startProfiling() { base.startProfiling(); }
    @Override public void stopProfiling() { base.stopProfiling(); }
    @Override public UiAutomation getUiAutomation() { return base.getUiAutomation(); }
    @Override public UiAutomation getUiAutomation(int flags) { return base.getUiAutomation(flags); }
    @Override public boolean onException(Object obj, Throwable e) { return base.onException(obj, e); }

    @Override
    public Application newApplication(ClassLoader cl, String className, Context context)
            throws InstantiationException, IllegalAccessException, ClassNotFoundException {
        return base.newApplication(cl, className, context);
    }

    @Override public void callApplicationOnCreate(Application app) { base.callApplicationOnCreate(app); }
    @Override public void callActivityOnNewIntent(Activity activity, Intent intent) { base.callActivityOnNewIntent(activity, intent); }
    @Override public void callActivityOnStart(Activity activity) { base.callActivityOnStart(activity); }
    @Override public void callActivityOnRestart(Activity activity) { base.callActivityOnRestart(activity); }
    @Override public void callActivityOnPause(Activity activity) { base.callActivityOnPause(activity); }
    @Override public void callActivityOnStop(Activity activity) { base.callActivityOnStop(activity); }
    @Override public void callActivityOnDestroy(Activity activity) { base.callActivityOnDestroy(activity); }
    @Override public void callActivityOnUserLeaving(Activity activity) { base.callActivityOnUserLeaving(activity); }
    @Override public void callActivityOnPostCreate(Activity activity, Bundle savedInstanceState) { base.callActivityOnPostCreate(activity, savedInstanceState); }
    @Override public void callActivityOnPostCreate(Activity activity, Bundle savedInstanceState, PersistableBundle persistentState) { base.callActivityOnPostCreate(activity, savedInstanceState, persistentState); }
    @Override public void callActivityOnSaveInstanceState(Activity activity, Bundle outState) { base.callActivityOnSaveInstanceState(activity, outState); }
    @Override public void callActivityOnSaveInstanceState(Activity activity, Bundle outState, PersistableBundle outPersistentState) { base.callActivityOnSaveInstanceState(activity, outState, outPersistentState); }
    @Override public void callActivityOnRestoreInstanceState(Activity activity, Bundle savedInstanceState) { base.callActivityOnRestoreInstanceState(activity, savedInstanceState); }
    @Override public void callActivityOnRestoreInstanceState(Activity activity, Bundle savedInstanceState, PersistableBundle persistentState) { base.callActivityOnRestoreInstanceState(activity, savedInstanceState, persistentState); }
}
