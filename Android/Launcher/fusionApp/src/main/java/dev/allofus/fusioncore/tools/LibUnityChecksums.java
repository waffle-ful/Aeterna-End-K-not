package dev.allofus.fusioncore.tools;

import android.util.Log;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;

/**
 * Expected SHA-256 digests for the unstripped libunity builds published by
 * FusionCore.UnityDependencies.
 *
 * Resolution order:
 *  1. the pinned table below (versions this launcher has been verified against);
 *  2. the GitHub release metadata for that Unity version, which carries a digest per asset.
 * When neither yields a digest the download proceeds unverified and the caller logs a warning,
 * so an unknown game update still boots instead of being blocked.
 */
public final class LibUnityChecksums {
    private static final String TAG = "FusionCore";
    private static final String RELEASE_API_URL =
            "https://api.github.com/repos/All-Of-Us-Mods/FusionCore.UnityDependencies/releases/tags/";

    /** key = "&lt;unityVersion&gt;/&lt;assetName&gt;", value = lowercase hex sha256. */
    private static final Map<String, String> PINNED = new HashMap<>();

    static {
        // Unity 2022.3.62f3 (Among Us 2026.8.18)
        pin("2022.3.62f3", "libunity.so.arm64-v8a", "69e92df2e98dc260086e45e77a201df1d6f1759a146b7434667318da2cbbf5b6");
        pin("2022.3.62f3", "libunity.sym.so.arm64-v8a", "f73e93f8b29ef844e38564ffd5ffe42a2ae398d6f363a5ae45af4fc9078c4b36");
        pin("2022.3.62f3", "libunity.so.armeabi-v7a", "18d894cad5251b11b8ef461976bd95c9d2d87904429373f9605e8694f089e098");
        pin("2022.3.62f3", "libunity.sym.so.armeabi-v7a", "947b84122b564e55cce9e441d0f7a17aeef1de8d5428fb60bc47d2ddf3873b37");
    }

    private LibUnityChecksums() {
    }

    private static void pin(String version, String assetName, String sha256) {
        PINNED.put(version + "/" + assetName, sha256.toLowerCase(Locale.ROOT));
    }

    /**
     * Returns the expected digests for every asset of the given release, or an empty map when
     * nothing is known. Never throws.
     */
    public static Map<String, String> resolve(String unityVersion, String... assetNames) {
        Map<String, String> result = new HashMap<>();
        boolean allPinned = true;
        for (String asset : assetNames) {
            String pinned = PINNED.get(unityVersion + "/" + asset);
            if (pinned != null) {
                result.put(asset, pinned);
            } else {
                allPinned = false;
            }
        }
        if (allPinned) {
            return result;
        }

        Map<String, String> remote = fetchReleaseDigests(unityVersion);
        for (String asset : assetNames) {
            if (!result.containsKey(asset) && remote.containsKey(asset)) {
                result.put(asset, remote.get(asset));
            }
        }
        return result;
    }

    private static Map<String, String> fetchReleaseDigests(String unityVersion) {
        Map<String, String> digests = new HashMap<>();
        HttpURLConnection connection = null;
        try {
            connection = (HttpURLConnection) new URL(RELEASE_API_URL + unityVersion).openConnection();
            connection.setRequestMethod("GET");
            connection.setRequestProperty("Accept", "application/vnd.github+json");
            connection.setRequestProperty("User-Agent", "EndKnot-Launcher");
            connection.setConnectTimeout(10000);
            connection.setReadTimeout(15000);

            int status = connection.getResponseCode();
            if (status < 200 || status >= 300) {
                Log.w(TAG, "Release metadata for Unity " + unityVersion + " unavailable, HTTP " + status);
                return digests;
            }

            StringBuilder body = new StringBuilder();
            try (BufferedReader reader = new BufferedReader(
                    new InputStreamReader(connection.getInputStream(), StandardCharsets.UTF_8))) {
                char[] buf = new char[8192];
                int n;
                while ((n = reader.read(buf)) != -1) {
                    body.append(buf, 0, n);
                }
            }

            JSONArray assets = new JSONObject(body.toString()).optJSONArray("assets");
            if (assets == null) {
                return digests;
            }
            for (int i = 0; i < assets.length(); i++) {
                JSONObject asset = assets.optJSONObject(i);
                if (asset == null) {
                    continue;
                }
                String name = asset.optString("name", "");
                String digest = asset.optString("digest", "");
                if (!name.isEmpty() && digest.startsWith("sha256:")) {
                    digests.put(name, digest.substring("sha256:".length()).toLowerCase(Locale.ROOT));
                }
            }
            Log.i(TAG, "Fetched " + digests.size() + " asset digests for Unity " + unityVersion);
        } catch (Exception e) {
            Log.w(TAG, "Failed to fetch release metadata for Unity " + unityVersion + ": " + e);
        } finally {
            if (connection != null) {
                connection.disconnect();
            }
        }
        return digests;
    }
}
