import org.gradle.api.artifacts.dsl.LockMode

buildscript {
    dependencies {
        classpath("org.jetbrains.kotlin:kotlin-gradle-plugin:2.4.21")
    }
}

plugins {
    id("com.android.application") version "9.5.1" apply false
}

allprojects {
    dependencyLocking {
        lockAllConfigurations()
        lockMode.set(LockMode.STRICT)
    }

    configurations.configureEach {
        resolutionStrategy.force(
            "com.google.protobuf:protobuf-java:4.36.2",
            "com.google.protobuf:protobuf-java-util:4.36.2",
            "com.google.protobuf:protobuf-kotlin:4.36.2",
            "commons-io:commons-io:2.22.0",
            "io.netty:netty-codec:4.2.20.Final",
            "io.netty:netty-codec-http:4.2.20.Final",
            "io.netty:netty-codec-http2:4.2.20.Final",
            "io.netty:netty-common:4.2.20.Final",
            "io.netty:netty-handler:4.2.20.Final",
            "io.netty:netty-handler-proxy:4.2.20.Final"
        )
        resolutionStrategy.eachDependency {
            when {
                requested.group == "io.netty" ->
                    useVersion("4.2.19.Final")
                requested.group == "com.google.protobuf" ->
                    useVersion("4.36.2")
                requested.group == "commons-io" && requested.name == "commons-io" ->
                    useVersion("2.22.0")
            }
        }
    }
}
