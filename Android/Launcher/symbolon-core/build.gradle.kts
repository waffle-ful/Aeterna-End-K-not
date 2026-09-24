// Symbolon core: the EKM (map) / EKR (role) contract layer.
// Pure Kotlin — no Android, JVM-desktop or Compose dependencies may appear in commonMain.
plugins {
    id("org.jetbrains.kotlin.multiplatform")
    id("com.android.kotlin.multiplatform.library")
    id("org.jetbrains.kotlin.plugin.serialization")
}

kotlin {
    jvmToolchain(17)

    jvm()

    android {
        namespace = "net.symbolon.core"
        compileSdk = 37
        minSdk = 26
    }

    sourceSets {
        commonMain.dependencies {
            implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.11.0")
        }
        commonTest.dependencies {
            implementation(kotlin("test"))
        }
    }
}
