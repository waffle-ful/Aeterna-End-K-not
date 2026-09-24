package dev.allofus.fusioncore.tools;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.net.Uri;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.system.Os;
import android.util.Log;

import androidx.browser.customtabs.CustomTabsIntent;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.security.KeyStore;
import java.security.SecureRandom;
import java.util.Arrays;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/**
 * itch.io sign-in for the launcher.
 *
 * The launcher opens the itch.io OAuth page in a browser tab; itch.io sends the browser back to
 * {@code endknot://itch-auth} with the access token in the URL fragment, which
 * {@code ItchAuthCallbackActivity} receives. The token is encrypted with a key that lives in the
 * Android Keystore and stored in the launcher's private files directory for the game package.
 * When the game starts, {@code BootstrapActivity} decrypts it and hands it to the game process
 * through an environment variable; no plaintext copy is kept on disk and the token never goes
 * into an Intent or the log.
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
    /** Environment variable the game reads the token from (set for this process only). */
    public static final String TOKEN_ENV = "ENDKNOT_ITCH_TOKEN";
    /** Encrypted token file: one version byte, the 12-byte GCM nonce, then the ciphertext. */
    public static final String ENCRYPTED_FILE_NAME = "EndKnot.ItchApiKey.enc";
    /** Plaintext token file written by earlier launcher builds; migrated and deleted on sight. */
    public static final String LEGACY_FILE_NAME = "EndKnot.ItchApiKey.txt";
    /** Written by the game when the token was rejected repeatedly; consumed on the next launcher start. */
    public static final String FAILED_MARKER_NAME = "EndKnot.ItchApiKey.failed";
    private static final String KEY_ALIAS = "endknot.itch.token";
    private static final String KEYSTORE = "AndroidKeyStore";
    private static final String CIPHER = "AES/GCM/NoPadding";
    private static final byte FORMAT_VERSION = 1;
    private static final int NONCE_BYTES = 12;
    private static final int TAG_BITS = 128;
    private static final String PREFS = "itch_auth";
    private static final String PREF_STATE = "pending_state";

    private ItchAuth() {
    }

    /** Private data directory of the game package (readable only by this app's uid). */
    private static File dataDir(Context context, String targetPackage) {
        return new File(context.getFilesDir(), targetPackage);
    }

    public static File encryptedFile(Context context, String targetPackage) {
        return new File(dataDir(context, targetPackage), ENCRYPTED_FILE_NAME);
    }

    private static File failedMarker(Context context, String targetPackage) {
        return new File(dataDir(context, targetPackage), FAILED_MARKER_NAME);
    }

    /** Places earlier builds (and the game itself) may have left a plaintext token. */
    private static File[] plaintextFiles(Context context, String targetPackage) {
        File dir = dataDir(context, targetPackage);
        File config = new File(new File(dir, "BepInEx"), "config");
        return new File[]{
                new File(dir, LEGACY_FILE_NAME),
                new File(dir, LEGACY_FILE_NAME + ".failed"),
                new File(config, LEGACY_FILE_NAME),
                new File(config, LEGACY_FILE_NAME + ".failed"),
        };
    }

    public static boolean isSignedIn(Context context, String targetPackage) {
        File file = encryptedFile(context, targetPackage);
        return file.isFile() && file.length() > 0 && !failedMarker(context, targetPackage).exists();
    }

    /**
     * Brings the stored state up to date: a failure marker left by the game signs the account out,
     * and a plaintext token from an earlier build is encrypted and the plaintext removed.
     * Cheap when there is nothing to do; call it before reading the sign-in state.
     */
    public static void reconcile(Context context, String targetPackage) {
        File marker = failedMarker(context, targetPackage);
        if (marker.exists()) {
            Log.w(TAG, "The game reported the stored token as rejected; signing out");
            signOut(context, targetPackage);
            return;
        }
        for (File plain : plaintextFiles(context, targetPackage)) {
            if (!plain.isFile() || plain.getName().endsWith(".failed")) {
                continue;
            }
            String token;
            try {
                token = new String(Files.readAllBytes(plain.toPath()), StandardCharsets.UTF_8).trim();
            } catch (IOException e) {
                Log.w(TAG, "Could not read the plaintext token file", e);
                continue;
            }
            if (token.isEmpty()) {
                deleteQuietly(plain);
                continue;
            }
            if (encryptedFile(context, targetPackage).isFile()) {
                // An encrypted token already exists; the plaintext copy is only a leftover.
                deleteQuietly(plain);
                Log.i(TAG, "Removed a leftover plaintext token file");
                continue;
            }
            if (saveToken(context, targetPackage, token)) {
                Log.i(TAG, "Migrated the plaintext token into encrypted storage");
            }
            // saveToken removes every plaintext copy on success; on failure the file stays for the next start.
        }
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

    /** Encrypts the token and stores it; every plaintext copy and failure marker is removed. */
    public static boolean saveToken(Context context, String targetPackage, String token) {
        File file = encryptedFile(context, targetPackage);
        File dir = file.getParentFile();
        if (dir != null && !dir.isDirectory() && !dir.mkdirs()) {
            Log.e(TAG, "Failed to create " + dir.getAbsolutePath());
            return false;
        }
        byte[] payload;
        try {
            payload = encrypt(token.getBytes(StandardCharsets.UTF_8));
        } catch (Exception e) {
            Log.e(TAG, "Failed to encrypt the token", e);
            return false;
        }
        File temp = new File(dir, ENCRYPTED_FILE_NAME + ".tmp");
        try (FileOutputStream out = new FileOutputStream(temp)) {
            out.write(payload);
            out.flush();
            out.getFD().sync();
        } catch (IOException e) {
            Log.e(TAG, "Failed to write the token file", e);
            deleteQuietly(temp);
            return false;
        }
        if (!temp.renameTo(file)) {
            Log.e(TAG, "Failed to move the token file into place");
            deleteQuietly(temp);
            return false;
        }
        for (File plain : plaintextFiles(context, targetPackage)) {
            deleteQuietly(plain);
        }
        deleteQuietly(failedMarker(context, targetPackage));
        Log.i(TAG, "Stored itch.io token (" + token.length() + " chars, encrypted)");
        return true;
    }

    /**
     * Decrypts the stored token. Any failure (missing or invalidated key, corrupt file) counts as
     * signed out: the file is removed and the player signs in again from the launcher.
     */
    public static String loadToken(Context context, String targetPackage) {
        File file = encryptedFile(context, targetPackage);
        if (!file.isFile()) {
            return null;
        }
        try {
            byte[] payload = Files.readAllBytes(file.toPath());
            String token = new String(decrypt(payload), StandardCharsets.UTF_8).trim();
            if (token.isEmpty()) {
                throw new IOException("empty token");
            }
            return token;
        } catch (Exception e) {
            Log.w(TAG, "Stored token could not be read (" + e.getClass().getSimpleName() + "); signing out");
            signOut(context, targetPackage);
            return null;
        }
    }

    /**
     * Makes the stored token visible to the game as {@link #TOKEN_ENV}. Must run in the process
     * that will host the game, before the runtime starts. Returns whether a token was exported.
     */
    public static boolean exportToEnvironment(Context context, String targetPackage) {
        String token = loadToken(context, targetPackage);
        try {
            if (token == null) {
                Os.unsetenv(TOKEN_ENV);
                return false;
            }
            Os.setenv(TOKEN_ENV, token, true);
            Log.i(TAG, "itch.io token exported to the game environment");
            return true;
        } catch (Exception e) {
            Log.e(TAG, "Failed to set the game environment", e);
            return false;
        }
    }

    public static void signOut(Context context, String targetPackage) {
        boolean had = deleteQuietly(encryptedFile(context, targetPackage));
        for (File plain : plaintextFiles(context, targetPackage)) {
            had |= deleteQuietly(plain);
        }
        deleteQuietly(failedMarker(context, targetPackage));
        try {
            Os.unsetenv(TOKEN_ENV);
        } catch (Exception ignored) {
        }
        if (had) {
            Log.i(TAG, "Signed out of itch.io");
        }
    }

    // --- Keystore-backed AES-GCM ---

    private static SecretKey key() throws Exception {
        KeyStore keyStore = KeyStore.getInstance(KEYSTORE);
        keyStore.load(null);
        KeyStore.Entry entry = keyStore.getEntry(KEY_ALIAS, null);
        if (entry instanceof KeyStore.SecretKeyEntry) {
            return ((KeyStore.SecretKeyEntry) entry).getSecretKey();
        }
        KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, KEYSTORE);
        generator.init(new KeyGenParameterSpec.Builder(KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build());
        return generator.generateKey();
    }

    private static byte[] encrypt(byte[] plain) throws Exception {
        Cipher cipher = Cipher.getInstance(CIPHER);
        // The Keystore insists on choosing the nonce itself; it is read back after init.
        cipher.init(Cipher.ENCRYPT_MODE, key());
        byte[] nonce = cipher.getIV();
        if (nonce == null || nonce.length != NONCE_BYTES) {
            throw new IllegalStateException("unexpected nonce length");
        }
        byte[] body = cipher.doFinal(plain);
        byte[] out = new byte[1 + NONCE_BYTES + body.length];
        out[0] = FORMAT_VERSION;
        System.arraycopy(nonce, 0, out, 1, NONCE_BYTES);
        System.arraycopy(body, 0, out, 1 + NONCE_BYTES, body.length);
        return out;
    }

    private static byte[] decrypt(byte[] payload) throws Exception {
        if (payload.length < 1 + NONCE_BYTES + TAG_BITS / 8 || payload[0] != FORMAT_VERSION) {
            throw new IOException("unrecognised token file");
        }
        byte[] nonce = Arrays.copyOfRange(payload, 1, 1 + NONCE_BYTES);
        byte[] body = Arrays.copyOfRange(payload, 1 + NONCE_BYTES, payload.length);
        Cipher cipher = Cipher.getInstance(CIPHER);
        cipher.init(Cipher.DECRYPT_MODE, key(), new GCMParameterSpec(TAG_BITS, nonce));
        return cipher.doFinal(body);
    }

    private static boolean deleteQuietly(File file) {
        return file.exists() && file.delete();
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
