package dev.allofus.fusioncore.tools;

import android.content.Context;
import android.content.ContextWrapper;
import android.content.res.Resources;
import android.util.Log;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;

import java.lang.reflect.Field;
import java.lang.reflect.Method;

/**
 * A Resources view that resolves ids across packages: the wrapped Resources first, then the
 * game's, then the launcher's. Game code running inside the launcher process resolves its own
 * string and drawable ids through whatever Context it holds, and those ids only exist in the
 * game APK, so a plain launcher Resources would throw NotFoundException for them.
 *
 * Installed per Context by {@link #install(Context, Resources, Resources)}; nothing is hooked, so
 * only the contexts it is installed on see the fallback.
 */
public class FallbackResources extends Resources {
    private static final String TAG = "FallbackResources";

    private final Resources base;
    private final Resources gameResources;
    private final Resources launcherResources;

    private static final Method GET_IMPL = findMethod("getImpl");
    private static final Method SET_IMPL = findMethod("setImpl", "android.content.res.ResourcesImpl");

    @SuppressWarnings("deprecation")
    private FallbackResources(Resources base, Resources gameResources, Resources launcherResources) {
        super(base.getAssets(), base.getDisplayMetrics(), base.getConfiguration());
        this.base = base;
        this.gameResources = gameResources;
        this.launcherResources = launcherResources;
        syncImpl();
    }

    /** Share the wrapped Resources' implementation so themes, caches and configuration stay one. */
    private void syncImpl() {
        if (GET_IMPL == null || SET_IMPL == null) return;
        try {
            Object theirs = GET_IMPL.invoke(base);
            Object ours = GET_IMPL.invoke(this);
            if (theirs != null && theirs != ours) {
                SET_IMPL.invoke(this, theirs);
            }
        } catch (Throwable t) {
            Log.w(TAG, "Could not share ResourcesImpl: " + t);
        }
    }

    @NonNull
    @Override
    public CharSequence getText(int id) throws NotFoundException {
        syncImpl();
        try {
            return super.getText(id);
        } catch (NotFoundException first) {
            try {
                CharSequence text = gameResources.getText(id);
                logHit("getText", id, "game");
                return text;
            } catch (NotFoundException ignored) {}
            try {
                CharSequence text = launcherResources.getText(id);
                logHit("getText", id, "launcher");
                return text;
            } catch (NotFoundException ignored) {}
            throw first;
        }
    }

    @NonNull
    @Override
    public String getString(int id) throws NotFoundException {
        return getText(id).toString();
    }

    @NonNull
    @Override
    public String getString(int id, Object... formatArgs) throws NotFoundException {
        syncImpl();
        try {
            return super.getString(id, formatArgs);
        } catch (NotFoundException first) {
            try {
                String text = gameResources.getString(id, formatArgs);
                logHit("getString", id, "game");
                return text;
            } catch (NotFoundException ignored) {}
            try {
                String text = launcherResources.getString(id, formatArgs);
                logHit("getString", id, "launcher");
                return text;
            } catch (NotFoundException ignored) {}
            throw first;
        }
    }

    @Override
    public int getIdentifier(String name, String defType, String defPackage) {
        syncImpl();
        int id = super.getIdentifier(name, defType, defPackage);
        if (id != 0) return id;
        id = gameResources.getIdentifier(name, defType, defPackage);
        if (id != 0) {
            logHit("getIdentifier", id, "game");
            return id;
        }
        id = launcherResources.getIdentifier(name, defType, defPackage);
        if (id != 0) logHit("getIdentifier", id, "launcher");
        return id;
    }

    private static final java.util.Set<Integer> loggedIds =
            java.util.Collections.synchronizedSet(new java.util.HashSet<>());

    /** Debug builds only: one line per resolved id, so a fallback that stops firing is visible. */
    private static void logHit(String method, int id, String source) {
        if (!dev.allofus.fusioncore.BuildConfig.DEBUG || loggedIds.size() >= 64) return;
        if (loggedIds.add(id)) {
            Log.i(TAG, method + " resolved 0x" + Integer.toHexString(id) + " from " + source + " resources");
        }
    }

    /**
     * Replaces the Resources of the innermost ContextImpl behind {@code context} (and the cached
     * copy on a ContextThemeWrapper such as an Activity) with a fallback view of it.
     * Returns the installed instance, or null when the context was left untouched.
     */
    @Nullable
    public static synchronized FallbackResources install(Context context, Resources gameResources, Resources launcherResources) {
        Context base = context;
        while (base instanceof ContextWrapper) {
            Context next = ((ContextWrapper) base).getBaseContext();
            if (next == null || next == base) break;
            base = next;
        }
        Resources current = base.getResources();
        if (current instanceof FallbackResources) {
            return (FallbackResources) current;
        }
        FallbackResources replacement = new FallbackResources(current, gameResources, launcherResources);
        if (!setField(base, "mResources", replacement)) {
            Log.w(TAG, "mResources not found on " + base.getClass().getName() + "; resources left untouched");
            return null;
        }
        // ContextThemeWrapper caches the Resources it first handed out; drop that copy so the
        // next getResources() walks down to the replaced one.
        Context c = context;
        while (c instanceof ContextWrapper) {
            if (c instanceof android.view.ContextThemeWrapper) {
                setField(c, "mResources", null);
            }
            Context next = ((ContextWrapper) c).getBaseContext();
            if (next == null || next == c) break;
            c = next;
        }
        boolean applied = context.getResources() == replacement;
        Log.i(TAG, "Installed on " + context.getClass().getName() + ": applied=" + applied);
        return applied ? replacement : null;
    }

    private static boolean setField(Object target, String name, Object value) {
        Class<?> clazz = target.getClass();
        while (clazz != null) {
            try {
                Field f = clazz.getDeclaredField(name);
                f.setAccessible(true);
                f.set(target, value);
                return true;
            } catch (NoSuchFieldException e) {
                clazz = clazz.getSuperclass();
            } catch (Throwable t) {
                Log.w(TAG, "Could not set " + name + " on " + target.getClass().getName() + ": " + t);
                return false;
            }
        }
        return false;
    }

    @Nullable
    private static Method findMethod(String name, String... paramClassNames) {
        try {
            Class<?>[] params = new Class<?>[paramClassNames.length];
            for (int i = 0; i < params.length; i++) {
                params[i] = Class.forName(paramClassNames[i]);
            }
            Method m = Resources.class.getDeclaredMethod(name, params);
            m.setAccessible(true);
            return m;
        } catch (Throwable t) {
            Log.w(TAG, "Resources." + name + " unavailable: " + t);
            return null;
        }
    }
}
