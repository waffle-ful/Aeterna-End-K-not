// Symbolon UI: the editor screens, shared by the Android and desktop shells (Compose Multiplatform).
plugins {
    id("org.jetbrains.kotlin.multiplatform")
    id("com.android.kotlin.multiplatform.library")
    id("org.jetbrains.compose")
    id("org.jetbrains.kotlin.plugin.compose")
}

kotlin {
    jvmToolchain(17)

    jvm()

    android {
        namespace = "net.symbolon.ui"
        compileSdk = 37
        minSdk = 26
    }

    sourceSets {
        commonMain.dependencies {
            api(project(":symbolon-core"))
            implementation(compose.runtime)
            implementation(compose.foundation)
            implementation(compose.ui)
            implementation(compose.material3)
        }
        androidMain.dependencies {
            api("androidx.activity:activity-compose:1.13.0")
        }
        jvmMain.dependencies {
            implementation(compose.desktop.common)
        }
    }
}
