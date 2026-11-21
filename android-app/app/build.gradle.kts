plugins {
    id("com.android.application")
}

android {
    namespace = "com.demo.pcmonitor"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.demo.pcmonitor"
        // Android 7.1.2 = API 25; minSdk 24 covers 7.0+ cleanly
        minSdk = 24
        targetSdk = 33
        versionCode = 2
        versionName = "26.1121"
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            signingConfig = signingConfigs.getByName("debug")
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        viewBinding = true
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.12.0")
    implementation("androidx.appcompat:appcompat:1.6.1")
    // Material 2 components — lightweight for 1GB RAM / Cortex-A7 (SM-G531H)
    implementation("com.google.android.material:material:1.11.0")
    implementation("androidx.constraintlayout:constraintlayout:2.1.4")
}
