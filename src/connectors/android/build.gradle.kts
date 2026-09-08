import org.gradle.api.artifacts.dsl.LockMode

plugins {
    id("com.android.application") version "9.4.0" apply false
    id("org.jetbrains.kotlin.android") version "2.4.10" apply false
}

allprojects {
    dependencyLocking {
        lockAllConfigurations()
        lockMode.set(LockMode.STRICT)
    }

    configurations.configureEach {
        resolutionStrategy.force(
            "com.google.protobuf:protobuf-java:4.36.1",
            "com.google.protobuf:protobuf-java-util:4.36.1",
            "com.google.protobuf:protobuf-kotlin:4.36.1",
            "commons-io:commons-io:2.22.0",
            "io.netty:netty-codec:4.2.17.Final",
            "io.netty:netty-codec-http:4.2.17.Final",
            "io.netty:netty-codec-http2:4.2.17.Final",
            "io.netty:netty-common:4.2.17.Final",
            "io.netty:netty-handler:4.2.17.Final",
            "io.netty:netty-handler-proxy:4.2.17.Final"
        )
        resolutionStrategy.eachDependency {
            when {
                requested.group == "io.netty" && requested.version?.startsWith("4.1.") == true ->
                    useVersion("4.1.137.Final")
                requested.group == "com.google.protobuf" && requested.version?.startsWith("3.") == true ->
                    useVersion("3.25.9")
                requested.group == "commons-io" && requested.name == "commons-io" ->
                    useVersion("2.22.0")
            }
        }
    }
}
