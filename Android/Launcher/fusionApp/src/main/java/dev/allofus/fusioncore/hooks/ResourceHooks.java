package dev.allofus.fusioncore.hooks;

import android.content.res.Resources;
import android.util.Log;

import java.lang.reflect.Method;
import java.util.Arrays;

import top.canyie.pine.Pine;
import top.canyie.pine.callback.MethodHook;

public class ResourceHooks {
    private static final String TAG = "ResourceHooks";

    /** Pine hooks are process-wide; installing them twice would stack callbacks. Latched before
     *  hooking on purpose: a half-installed set is never retried (fewer hooks is the safer failure). */
    private static boolean areHooksInstalled = false;

    public static synchronized void installHooks(Resources gameResources, Resources ourResources) {
        if (areHooksInstalled) {
            Log.d(TAG, "Resource hooks already installed");
            return;
        }
        areHooksInstalled = true;

        try {
            // getIdentifier needs a separate hook because null values get converted to integer 0
            Method getIdentifierMethod = Resources.class.getMethod("getIdentifier", String.class, String.class, String.class);
            Pine.hook(getIdentifierMethod, new MethodHook() {
                @Override
                public void afterCall(Pine.CallFrame callFrame) {
                    if ((int) callFrame.getResult() != 0) {
                        return;
                    }

                    // try game resources
                    try {
                        Object gameResResult = callFrame.invokeOriginalMethod(gameResources, callFrame.args);
                        if (gameResResult instanceof Integer && (int) gameResResult != 0) {
                            Log.i(TAG, "Found identifier in game resources!");
                            callFrame.setResult(gameResResult);
                            return;
                        }
                    } catch (Throwable ignored) {}

                    // try fusion resources
                    try {
                        Object ourResResult = callFrame.invokeOriginalMethod(ourResources, callFrame.args);
                        if (ourResResult instanceof Integer && (int) ourResResult != 0) {
                            Log.i(TAG, "Found identifier in our resources!");
                            callFrame.setResult(ourResResult);
                            return;
                        }
                    } catch (Throwable ignored) {}

                    Log.e(TAG, "Could not find identifier in game or our resources! Args: " + Arrays.toString(callFrame.args));
                }
            });

            // Only the string lookups below have been observed to need the cross-package
            // fallback (game code resolving its own string ids through the launcher's
            // Resources). Every hooked method carries a Pine bridge on each call, so hot
            // framework paths such as getSystem / getAssets / getLayout / getInteger stay
            // unhooked; a lookup that misses there throws NotFoundException and is visible
            // in logcat, while these string entry points are hooked individually because
            // the AOT-compiled getString may inline getText and bypass its hook.
            Method[] fallbackMethods = {
                    Resources.class.getMethod("getText", int.class),
                    Resources.class.getMethod("getString", int.class),
                    Resources.class.getMethod("getString", int.class, Object[].class),
            };
            for (Method method : fallbackMethods) {
                Pine.hook(method, new MethodHook() {
                    @Override
                    public void afterCall(Pine.CallFrame callFrame) {
                        if (!callFrame.hasThrowable() && callFrame.getResult() != null) {
                            return;
                        }

                        // try game resources
                        try {
                            Object gameResResult = callFrame.invokeOriginalMethod(gameResources, callFrame.args);
                            if (gameResResult != null) {
                                Log.i(TAG, method.getName() + " found in game resources!");
                                callFrame.setThrowable(null);
                                callFrame.setResult(gameResResult);
                                return;
                            }
                        } catch (Throwable ignored) {}

                        // try our resources
                        try {
                            Object ourResResult = callFrame.invokeOriginalMethod(ourResources, callFrame.args);
                            if (ourResResult != null) {
                                Log.i(TAG, method.getName() + " found in our resources!");
                                callFrame.setThrowable(null);
                                callFrame.setResult(ourResResult);
                                return;
                            }
                        } catch (Throwable ignored) {}

                        Log.e(TAG, "Could not resolve " + method.getName() + " across resources! Args: " + Arrays.toString(callFrame.args));
                    }
                });
            }
        } catch (Exception e) {
            Log.e(TAG, "Failed to hook Resources: " + e.getMessage());
        }
    }
}