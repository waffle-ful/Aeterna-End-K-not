plugins {
    id("com.android.application")
    id("com.google.protobuf")
    id("kotlin-parcelize")
}

// we have a custom pine build that fixes 16KB library problem.
val pineAar = file("../libs/canyie-pine.aar")
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
            // this can mess up ResourceHooks
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
            assets.srcDirs("../../../build-android/apk-assets")
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