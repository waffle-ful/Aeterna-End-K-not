plugins {
    id("com.android.application") version "9.3.2" apply false
    id("org.jetbrains.kotlin.android") version "2.4.10" apply false
    id("com.google.protobuf") version "0.10.0" apply false
    // Symbolon (map / role editor) modules: Kotlin Multiplatform + Compose Multiplatform
    id("org.jetbrains.kotlin.multiplatform") version "2.4.10" apply false
    id("org.jetbrains.kotlin.jvm") version "2.4.10" apply false
    id("com.android.kotlin.multiplatform.library") version "9.3.2" apply false
    id("org.jetbrains.kotlin.plugin.serialization") version "2.4.10" apply false
    id("org.jetbrains.kotlin.plugin.compose") version "2.4.10" apply false
    id("org.jetbrains.compose") version "1.12.1" apply false
}

tasks.register("clean", Delete::class) {
    description = "clean build files"
    delete(rootProject.layout.buildDirectory)
}
