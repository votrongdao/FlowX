# The CRM sample's API.
#
# TWO STAGES, AND THE SDK NEVER SHIPS. The build image carries a compiler, a package cache and
# the whole source tree; the runtime image carries the assemblies. Collapsing them would put a
# toolchain — and every credential the restore used — inside the thing that faces the network.
#
# CHISELED RATHER THAN THE FULL RUNTIME. No shell, no package manager, no busybox: a chiselled
# image has nothing for an attacker who reaches code execution to pivot with, and it is a
# smaller surface for a CVE feed to have opinions about. The cost is that `docker exec` cannot
# give you a prompt, which is the point.
#
# Build from the repository root, because this project references src/ and plugins/:
#   docker build -f samples/crm/Dockerfile -t flowx-crm .

# ---------------------------------------------------------------------------------- build

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build

WORKDIR /src

# Restore before the source is copied, so a change to a .cs file does not re-download the world.
# The glob keeps every project's file at the path the solution expects.
#
# `SonarLint.xml` is in this list because `Directory.Build.props` declares it as an
# `AdditionalFiles` for every project. Left out, the file is missing at the path the analyzers are
# handed, and the build does not degrade to "no Sonar rules" — it fails, with `CS2001` for the
# missing source file and one `AD0001` per rule that tried to read it. CI never sees this: it
# checks the whole repository out, so the file is simply there. Only a build whose context is
# assembled by hand, like this one, can leave it behind.
COPY global.json Directory.Build.props FlowX.slnx .editorconfig SonarLint.xml ./
COPY src/ src/
COPY plugins/ plugins/
COPY samples/crm/ samples/crm/

RUN dotnet restore samples/crm/Crm.csproj

# No `--no-restore` fallback and no `-p:TreatWarningsAsErrors=false`: this repository builds
# warning-free, and a container that quietly relaxed that would be the one build nobody checks.
RUN dotnet publish samples/crm/Crm.csproj \
    -c Release \
    --no-restore \
    -o /app

# ---------------------------------------------------------------------------------- runtime

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime

# 8080 rather than 80: a port above 1024 needs no capability to bind, so the process can drop
# every one of them. Binding 80 is the usual reason a container ends up running as root.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0

WORKDIR /app

COPY --from=build --chown=$APP_UID:$APP_UID /app ./

# The chiselled images define a non-root APP_UID (64198) and no shell to escalate with. Stated
# rather than inherited, so a base-image change that dropped it is a build-time difference.
USER $APP_UID

EXPOSE 8080

# The runtime probes itself, because there is no shell here to probe it with — see CrmProbe.
# `--start-period` covers the two migrators and, when one is configured, the seed.
HEALTHCHECK --interval=15s --timeout=10s --start-period=45s --retries=3 \
    CMD ["dotnet", "Crm.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "Crm.dll"]
