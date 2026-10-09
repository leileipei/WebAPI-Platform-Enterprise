ARG SDK_IMAGE
FROM ${SDK_IMAGE}
ARG SOURCE_REVISION
ARG HARNESS_REVISION
LABEL org.opencontainers.image.revision=${SOURCE_REVISION} com.webapi.performance.harness=${HARNESS_REVISION}
WORKDIR /app
COPY publish/ /app/
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
# The isolated harness needs its owner-only bind-mounted inputs (0600). Both
# measured versions use this same fixture UID; this is not a release image.
USER 0:0
