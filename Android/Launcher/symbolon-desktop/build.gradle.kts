// Symbolon desktop shell (Windows): Compose for Desktop application packaged with jpackage.
import org.jetbrains.compose.desktop.application.dsl.TargetFormat

plugins {
    id("org.jetbrains.kotlin.jvm")
    id("org.jetbrains.compose")
    id("org.jetbrains.kotlin.plugin.compose")
}

kotlin {
    jvmToolchain(17)
}

dependencies {
    implementation(project(":symbolon-core"))
    implementation(project(":symbolon-ui"))
    implementation(compose.desktop.currentOs)
}

compose.desktop {
    application {
        mainClass = "net.symbolon.desktop.MainKt"

        nativeDistributions {
            targetFormats(TargetFormat.AppImage)
            packageName = "Symbolon"
            packageVersion = "0.1.0"
            description = "Map and role editor"
            vendor = "waffle-ful"

            windows {
                menu = false
                shortcut = false
            }
        }
    }
}
