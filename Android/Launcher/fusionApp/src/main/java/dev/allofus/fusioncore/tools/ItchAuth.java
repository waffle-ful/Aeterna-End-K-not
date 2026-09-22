package dev.allofus.fusioncore.tools;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.net.Uri;
import android.util.Log;

import androidx.browser.customtabs.CustomTabsIntent;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;

/**
 * itch.io sign-in for the launcher.
 *
 * The launcher opens the itch.io OAuth page in a browser tab; itch.io sends the browser back to
 * {@code endknot://itch-auth} with the access token in the URL fragment, which
 * {@code ItchAuthCallbackActivity} receives. The token is stored in the launcher's private files
 * directory for the game package, where the mod reads it at start-up (the same directory the
 * native side exports as {@code FUSION_APP_DATA_DIR}). Nothing but the itch.io page ever sees the
 * token, and it is never written to the log.
 */
public final class ItchAuth {
    private static final String TAG = "ItchAuth";
    /** Public identifier of the launcher's OAuth application on itch.io (implicit grant, no secret). */
    private static final String CLIENT_ID = "01c313b3a6f82c2f0485804bfcd56c94";
    private static final String SCOPE = "profile:me";
    public static final String REDIRECT_SCHEME = "endknot";
    public static final String REDIRECT_HOST = "itch-auth";
    private static final String REDIRECT_URI = REDIRECT_SCHEME + "://" + REDIRECT_HOST;
    private static final String AUTHORIZE_URL = "https://itch.io/user/oauth";
    /** File name shared with the mod; the mod also accepts it in the legacy BepInEx/config location. */
    public static final String TOKEN_FILE_NAME = "EndKnot.ItchApiKey.txt";
    private static final String PREFS = "itch_auth";
    private static final String PREF_STATE = "pending_state";

    private ItchAuth() {
    }

    /** Private token file for the given game package (readable only by this app's uid). */
    public static File tokenFile(Context context, String targetPackage) {
        return new File(new File(context.getFilesDir(), targetPackage), TOKEN_FILE_NAME);
    }

    public static boolean isSignedIn(Context context, String targetPackage) {
        File file = tokenFile(context, targetPackage);
        return file.isFile() && file.length() > 0;
    }

    /** Opens the itch.io authorization page. The result arrives through the redirect activity. */
    public static void startSignIn(Activity activity) {
        String state = newState();
        prefs(activity).edit().putString(PREF_STATE, state).apply();
        Uri uri = Uri.parse(AUTHORIZE_URL).buildUpon()
                .appendQueryParameter("client_id", CLIENT_ID)
                .appendQueryParameter("scope", SCOPE)
                .appendQueryParameter("response_type", "token")
                .appendQueryParameter("redirect_uri", REDIRECT_URI)
                .appendQueryParameter("state", state)
                .build();
        try {
            new CustomTabsIntent.Builder().build().launchUrl(activity, uri);
        } catch (Exception e) {
            Log.w(TAG, "Custom tab unavailable, opening the default browser", e);
            try {
                activity.startActivity(new Intent(Intent.ACTION_VIEW, uri));
            } catch (Exception inner) {
                Log.e(TAG, "No browser available for the sign-in page", inner);
            }
        }
    }

    /** Consumes the redirect URI. Returns true when a token was stored. */
    public static boolean handleRedirect(Context context, String targetPackage, Uri uri) {
        if (uri == null || !REDIRECT_SCHEME.equals(uri.getScheme()) || !REDIRECT_HOST.equals(uri.getHost())) {
            Log.w(TAG, "Ignoring unexpected redirect");
            return false;
        }
        String fragment = uri.getEncodedFragment();
        if (fragment == null || fragment.isEmpty()) {
            Log.w(TAG, "Redirect carried no fragment");
            return false;
        }
        String token = null;
        String state = null;
        for (String pair : fragment.split("&")) {
            int eq = pair.indexOf('=');
            if (eq <= 0) {
                continue;
            }
            String key = Uri.decode(pair.substring(0, eq));
            String value = Uri.decode(pair.substring(eq + 1));
            if ("access_token".equals(key)) {
                token = value;
            } else if ("state".equals(key)) {
                state = value;
            }
        }
        SharedPreferences prefs = prefs(context);
        String expectedState = prefs.getString(PREF_STATE, null);
        // The activity behind this redirect is exported, so a mismatching redirect must not
        // disturb a sign-in that is genuinely in flight: the pending state is consumed only by
        // the redirect that matches it.
        if (expectedState == null || !expectedState.equals(state)) {
            Log.w(TAG, "Redirect state did not match the pending sign-in; discarding");
            return false;
        }
        if (token == null || token.trim().isEmpty()) {
            Log.w(TAG, "Redirect carried no access token");
            return false;
        }
        prefs.edit().remove(PREF_STATE).apply();
        return saveToken(context, targetPackage, token.trim());
    }

    public static boolean saveToken(Context context, String targetPackage, String token) {
        File file = tokenFile(context, targetPackage);
        File dir = file.getParentFile();
        if (dir != null && !dir.isDirectory() && !dir.mkdirs()) {
            Log.e(TAG, "Failed to create " + dir.getAbsolutePath());
            return false;
        }
        File temp = new File(dir, TOKEN_FILE_NAME + ".tmp");
        try (FileOutputStream out = new FileOutputStream(temp)) {
            out.write((token + "\n").getBytes(StandardCharsets.UTF_8));
            out.flush();
            out.getFD().sync();
        } catch (IOException e) {
            Log.e(TAG, "Failed to write the token file", e);
            //noinspection ResultOfMethodCallIgnored
            temp.delete();
            return false;
        }
        if (!temp.renameTo(file)) {
            Log.e(TAG, "Failed to move the token file into place");
            //noinspection ResultOfMethodCallIgnored
            temp.delete();
            return false;
        }
        Log.i(TAG, "Stored itch.io token (" + token.length() + " chars)");
        return true;
    }

    public static void signOut(Context context, String targetPackage) {
        File file = tokenFile(context, targetPackage);
        if (file.exists() && !file.delete()) {
            Log.w(TAG, "Failed to delete the token file");
        } else {
            Log.i(TAG, "Signed out of itch.io");
        }
    }

    private static SharedPreferences prefs(Context context) {
        return context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    private static String newState() {
        byte[] bytes = new byte[16];
        new SecureRandom().nextBytes(bytes);
        StringBuilder sb = new StringBuilder(bytes.length * 2);
        for (byte b : bytes) {
            sb.append(String.format("%02x", b));
        }
        return sb.toString();
    }
}
