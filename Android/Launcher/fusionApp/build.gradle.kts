import java.net.URL
import java.security.MessageDigest

plugins {
    id("com.android.application")
    id("com.google.protobuf")
    id("kotlin-parcelize")
}

// we have a custom pine build that fixes 16KB library problem.
val pineAar = file("../libs/canyie-pine.aar")

// ---- Unity runtime shipped inside the APK ----
// The unstripped libunity (plus its symbol file, which the native side needs for a lookup) is
// pulled from the FusionCore.UnityDependencies release at build time, digest-checked, and
// packaged as assets under libunity/<version>/<abi>/. The launcher only bundles this one
// version; BootstrapActivity refuses a game on a different Unity version in store builds and
// falls back to downloading in debug builds (ALLOW_LIBUNITY_DOWNLOAD).
val bundledUnityVersion = "2022.3.62f3"
val bundledLibUnityAbi = "arm64-v8a"
val bundledLibUnitySha256 = "69e92df2e98dc260086e45e77a201df1d6f1759a146b7434667318da2cbbf5b6"
val bundledLibUnitySymSha256 = "f73e93f8b29ef844e38564ffd5ffe42a2ae398d6f363a5ae45af4fc9078c4b36"
val libUnityReleaseBase = "https://github.com/All-Of-Us-Mods/FusionCore.UnityDependencies/releases/download/"
val libUnityAssetsDir = layout.buildDirectory.dir("libunity-assets")

fun sha256Of(file: File): String {
    val digest = MessageDigest.getInstance("SHA-256")
    file.inputStream().use { input ->
        val buffer = ByteArray(1 shl 20)
        while (true) {
            val read = input.read(buffer)
            if (read <= 0) break
            digest.update(buffer, 0, read)
        }
    }
    return digest.digest().joinToString("") { b -> "%02x".format(b) }
}

val fetchLibUnity = tasks.register("fetchLibUnity") {
    description = "Downloads and verifies the bundled Unity runtime files"
    val outDir = libUnityAssetsDir.map { it.dir("libunity/$bundledUnityVersion/$bundledLibUnityAbi") }
    // Declared as inputs so a change to the pinned version or digests re-runs the fetch even
    // when build/ still holds the previous files.
    inputs.property("bundledUnityVersion", bundledUnityVersion)
    inputs.property("bundledLibUnityAbi", bundledLibUnityAbi)
    inputs.property("bundledLibUnitySha256", bundledLibUnitySha256)
    inputs.property("bundledLibUnitySymSha256", bundledLibUnitySymSha256)
    inputs.property("libUnityReleaseBase", libUnityReleaseBase)
    outputs.dir(libUnityAssetsDir)
    doLast {
        val dir = outDir.get().asFile
        dir.mkdirs()
        fun fetch(asset: String, target: File, expected: String) {
            if (target.isFile && sha256Of(target) == expected) return
            val url = "$libUnityReleaseBase$bundledUnityVersion/$asset"
            logger.lifecycle("Fetching $url")
            val temp = File(target.path + ".tmp")
            URL(url).openStream().use { input -> temp.outputStream().use { output -> input.copyTo(output) } }
            val seen = sha256Of(temp)
            if (seen != expected) {
                temp.delete()
                throw GradleException("Digest mismatch for $asset: expected $expected, got $seen")
            }
            if (target.exists()) target.delete()
            if (!temp.renameTo(target)) throw GradleException("Could not move $asset into place")
        }
        fetch("libunity.so.$bundledLibUnityAbi", File(dir, "libunity.so"), bundledLibUnitySha256)
        fetch("libunity.sym.so.$bundledLibUnityAbi", File(dir, "libunity.sym.so"), bundledLibUnitySymSha256)
    }
}
dependencies {
    implementation("androidx.core:core:1.19.0")
    implementation("androidx.annotation:annotation:1.10.0")
    implementation("androidx.appcompat:appcompat:1.8.0")
    implementation("androidx.browser:browser:1.9.0")
    implementation("androidx.coordinatorlayout:coordinatorlayout:1.3.0")
    implementation("com.google.android.material:material:1.14.0")
    implementation("com.google.protobuf:protobuf-javalite:4.36.1")
    implementation(files(pineAar))
}

android {
    namespace = "dev.allofus.fusioncore"
    compileSdk = 37
    ndkVersion = "28.2.13676358"

    buildFeatures {
        prefab = true
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    defaultConfig {
        minSdk = 27
        targetSdk = 36
        applicationId = "dev.waffleful.endknot"
        versionCode = 1
        versionName = "0.1.0"
        ndk {
            abiFilters.add("arm64-v8a")
            // abiFilters.add("armeabi-v7a")
        }
        buildConfigField("String", "BUNDLED_UNITY_VERSION", "\"$bundledUnityVersion\"")
        buildConfigField("String", "BUNDLED_LIBUNITY_ABI", "\"$bundledLibUnityAbi\"")
        buildConfigField("String", "BUNDLED_LIBUNITY_SHA256", "\"$bundledLibUnitySha256\"")
        buildConfigField("String", "BUNDLED_LIBUNITY_SYM_SHA256", "\"$bundledLibUnitySymSha256\"")
        // Store builds never fetch a runtime at run time; debug builds may, for a game that
        // moved past the bundled Unity version before the launcher was rebuilt.
        buildConfigField("boolean", "ALLOW_LIBUNITY_DOWNLOAD", "false")
    }

    externalNativeBuild {
        cmake {
            path = File("./src/main/jni/CMakeLists.txt")
            version = "3.22.1"
        }
    }

    // Release signing is optional: without a keystore the release build is simply left unsigned.
    val releaseKeystore = file(System.getenv("KEYSTORE_PATH") ?: "keystore.jks").takeIf { it.exists() }
    signingConfigs {
        if (releaseKeystore != null) {
            create("release") {
                storeFile = releaseKeystore
                storePassword = System.getenv("KEYSTORE_PASSWORD")
                keyAlias = System.getenv("KEY_ALIAS")
                keyPassword = System.getenv("KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        debug {
            buildConfigField("boolean", "ALLOW_LIBUNITY_DOWNLOAD", "true")
        }
        release {
            if (releaseKeystore != null) {
                signingConfig = signingConfigs.getByName("release")
            }
            // we don't need minify tbh
            isMinifyEnabled = false
            // resource ids from the game APK are resolved at runtime, so nothing may be stripped
            //noinspection NotShrinkingResources
            isShrinkResources = false
            proguardFiles("proguard-unity.txt", getDefaultProguardFile("proguard-android-optimize.txt"))
        }
    }

    packaging {
        jniLibs {
            useLegacyPackaging = true
            keepDebugSymbols += listOf("*/armeabi-v7a/*.so", "*/arm64-v8a/*.so")
        }
    }

    // The mod DLL is bundled from the C# build output (`dotnet build EndKnot.csproj -c Android`
    // writes build-android/apk-assets/plugins/). The directory may be absent when only the
    // launcher is being built; the APK then ships without a bundled plugin.
    sourceSets {
        getByName("main") {
            // A plain File (not a Provider): preBuild depends on fetchLibUnity, which fills it.
            assets.srcDirs("../../../build-android/apk-assets", libUnityAssetsDir.get().asFile)
        }
    }

    lint {
        abortOnError = false
    }
}


protobuf {
    protoc {
        artifact = "com.google.protobuf:protoc:4.35.1"
    }
    generateProtoTasks {
        all().forEach { task ->
            task.builtins {
                create("java") {
                    option("lite")
                }
            }
        }
    }
}

tasks.named("preBuild") {
    dependsOn(fetchLibUnity)
}
