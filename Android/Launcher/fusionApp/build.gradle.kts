plugins {
    id("com.android.application")
    id("com.google.protobuf")
    id("kotlin-parcelize")
}


// ---- Unity version the launcher is built for ----
// The game's own libunity is used as-is. The bundled Unity support libraries that BepInEx
// generates its interop against are version-specific, so BootstrapActivity refuses a game on a
// different Unity version in store builds (debug builds proceed with a warning).
val bundledUnityVersion = "2022.3.62f3"

// The game's main activity is instantiated as a subclass shipped in its own dex
// (assets/bridge/fusion-bridge.dex) and defined under the game class loader, so it can extend
// the game's classes. Only that one class goes into the dex; src/bridge/stubs holds the
// compile-time stand-ins for the game classes and for the launcher-side host.
val bridgeAssetsDir = layout.buildDirectory.dir("bridge-assets")
val bridgeClassesDir = layout.buildDirectory.dir("bridge-classes")
val bridgeClassPath = "dev/allofus/fusioncore/bridge/FusionEosUnityPlayerActivity.class"
// SDK locations come from AGP: the boot classpath is the
// android.jar of compileSdk, and d8 is taken from the newest installed build-tools.
val bridgeBootClasspath = androidComponents.sdkComponents.bootClasspath
val bridgeD8 = androidComponents.sdkComponents.sdkDirectory.map { sdk ->
    val exe = if (System.getProperty("os.name").lowercase().contains("windows")) "d8.bat" else "d8"
    val candidates = sdk.asFile.resolve("build-tools").listFiles()
            ?.filter { File(it, exe).isFile }
            ?.sortedBy { it.name }
    val chosen = candidates?.lastOrNull()
            ?: throw GradleException("No build-tools with $exe under ${sdk.asFile}")
    File(chosen, exe)
}

val compileBridge = tasks.register<JavaCompile>("compileBridge") {
    description = "Compiles the game-activity bridge subclass against stubs"
    source(fileTree("src/bridge"))
    classpath = files(bridgeBootClasspath)
    destinationDirectory.set(bridgeClassesDir)
    options.release.set(17)
    options.isWarnings = false
}

val dexBridge = tasks.register<Exec>("dexBridge") {
    description = "Dexes the bridge subclass into assets/bridge/fusion-bridge.dex"
    dependsOn(compileBridge)
    val outDir = bridgeAssetsDir.map { it.dir("bridge") }
    inputs.dir(bridgeClassesDir)
    inputs.files(bridgeBootClasspath)
    inputs.file(bridgeD8)
    outputs.dir(bridgeAssetsDir)
    doFirst {
        outDir.get().asFile.mkdirs()
        val args = mutableListOf(bridgeD8.get().absolutePath, "--min-api", "27")
        bridgeBootClasspath.get().forEach { args += listOf("--lib", it.asFile.absolutePath) }
        args += listOf(
            "--classpath", bridgeClassesDir.get().asFile.absolutePath,
            "--output", outDir.get().asFile.absolutePath,
            File(bridgeClassesDir.get().asFile, bridgeClassPath).absolutePath
        )
        commandLine(args)
    }
    doLast {
        val produced = File(outDir.get().asFile, "classes.dex")
        val target = File(outDir.get().asFile, "fusion-bridge.dex")
        if (target.exists()) target.delete()
        if (!produced.renameTo(target)) throw GradleException("d8 did not produce classes.dex for the bridge")
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
    // Symbolon map / role editor (EditorActivity)
    implementation(project(":symbolon-ui"))
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
        versionCode = 90900
        versionName = "0.9.9-beta"
        ndk {
            abiFilters.add("arm64-v8a")
            // abiFilters.add("armeabi-v7a")
        }
        buildConfigField("String", "BUNDLED_UNITY_VERSION", "\"$bundledUnityVersion\"")
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
            // A plain File (not a Provider): preBuild depends on dexBridge, which fills it.
            assets.srcDirs("../../../build-android/apk-assets", bridgeAssetsDir.get().asFile)
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
    dependsOn(dexBridge)
}
