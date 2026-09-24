// Symbolon standalone Android app: development build used to measure the editor on
// devices that cannot install the launcher (minSdk 26). Not a distribution target.
plugins {
    id("com.android.application")
}

android {
    namespace = "net.symbolon.app"
    compileSdk = 37

    defaultConfig {
        applicationId = "net.symbolon.app"
        minSdk = 26
        targetSdk = 36
        versionCode = 1
        versionName = "0.1.0"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }
}

dependencies {
    implementation(project(":symbolon-ui"))
}
