plugins {
	id("org.jetbrains.kotlin.jvm") version "2.4.21"
	id("org.jetbrains.intellij.platform") version "2.19.0"
}

group = "com.anixias.cella"
version = rootDir.resolve("../../version.txt").readText().trim()

val riderPath = providers.gradleProperty("riderPath")
	.orElse(providers.environmentVariable("LOCALAPPDATA").map { "$it/Programs/Rider 2" })

kotlin {
	jvmToolchain(21)
}

repositories {
	mavenCentral()
	intellijPlatform {
		defaultRepositories()
	}
}

dependencies {
	intellijPlatform {
		local(riderPath)
		bundledModule("intellij.spellchecker")
	}
	
	testImplementation(kotlin("test-junit5"))
	testRuntimeOnly("org.junit.platform:junit-platform-launcher:1.12.2")
}

intellijPlatform {
	pluginConfiguration {
		ideaVersion {
			sinceBuild = "261"
			untilBuild = provider { null }
		}
	}
	
	buildSearchableOptions = false
	instrumentCode = false
}

tasks.test {
	useJUnitPlatform()
	systemProperty("cella.cases", rootDir.resolve("../csharp/Cella.Tests/Cases").absolutePath)
}
