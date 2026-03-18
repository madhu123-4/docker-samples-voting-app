# ════════════════════════════════════════════════════════════════════
#  Dockerfile.j  —  Java Worker  (multi-stage build)
#
#  What changed from the original and WHY:
#
#  ORIGINAL:
#    FROM java:openjdk-8-jdk-alpine
#    — 'java' image is deprecated and DELETED from Docker Hub (2017)
#      Building this image today will fail with "manifest unknown"
#    — Manually downloads Maven 3.3.3 via wget inside the image
#      → no checksum verification (supply chain risk)
#      → adds wget + extra layers to the final image
#      → Maven 3.3.3 is from 2015
#    — Single stage: JDK + Maven + source all stay in the final image
#    — Runs as root
#    — Java 8: EOL for most distributions
#    — RUN ["mvn", "verify"] runs all tests during image build (slow,
#      inappropriate for production builds)
#
#  NEW:
#    — eclipse-temurin: the current recommended OpenJDK distribution
#      on Docker Hub (maintained by Adoptium, used by most prod teams)
#    — Java 21 LTS — supported until 2029
#    — Maven is installed via apt in the build stage — no manual wget
#    — Multi-stage: JDK+Maven in stage 1, only JRE in stage 2
#      Final image is ~200 MB instead of ~500 MB
#    — Layer cache: pom.xml copied first → mvn dependency:go-offline
#      cached separately from source changes
#    — No mvn verify (tests) during image build
#    — Runs as non-root user
# ════════════════════════════════════════════════════════════════════


# ── Stage 1: BUILD ────────────────────────────────────────────────────────────
# eclipse-temurin is the official Docker Hub OpenJDK replacement for 'java:'
FROM eclipse-temurin:21-jdk AS build

# Install Maven via apt — verified, maintained, no manual wget needed
RUN apt-get update && \
    apt-get install -y --no-install-recommends maven && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /code

# --- Layer cache trick ---
# Copy pom.xml first and pre-download all dependencies.
# If only .java source files change (not pom.xml), this cached layer
# is reused and `mvn dependency:go-offline` is NOT re-run.
# Original ADD'd everything together so deps re-downloaded every build.
COPY pom.xml .
RUN mvn dependency:go-offline --batch-mode --quiet

# Now copy source and build the fat jar (skip tests — not appropriate here)
COPY src/ ./src/
RUN mvn package \
      --batch-mode \
      --quiet \
      -DskipTests          # tests belong in CI, not in docker build


# ── Stage 2: RUNTIME ─────────────────────────────────────────────────────────
# JRE only — no compiler, no Maven, no source code in the final image.
# Original shipped the full JDK + Maven + source + wget in the runtime image.
FROM eclipse-temurin:21-jre AS runtime

WORKDIR /app

# Copy only the fat jar from the build stage
COPY --from=build /code/target/worker-jar-with-dependencies.jar worker.jar

# Create and switch to a non-root user
# Original ran as root inside the container
RUN groupadd --system worker && \
    useradd  --system --gid worker --no-create-home worker
USER worker

# Use exec form (JSON array) — correct signal handling (SIGTERM reaches the JVM)
# Original CMD form runs via shell (/bin/sh -c) so SIGTERM hits the shell,
# not the JVM, and graceful shutdown never fires.
ENTRYPOINT ["java", "-jar", "worker.jar"]