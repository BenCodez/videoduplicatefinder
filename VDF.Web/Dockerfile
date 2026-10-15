# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY ["VDF.Core/VDF.Core.csproj", "VDF.Core/"]
COPY ["VDF.Web/VDF.Web.csproj", "VDF.Web/"]
RUN dotnet restore "VDF.Web/VDF.Web.csproj"
COPY VDF.Core/ VDF.Core/
COPY VDF.Web/ VDF.Web/
# Razor files determine the implicit framework-assets dependency. Restore again
# with the source present; the cached project-only restore cannot discover it.
RUN dotnet publish "VDF.Web/VDF.Web.csproj" -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VDF_SOURCE_REVISION=unknown
ARG VDF_SOURCE_URL=https://github.com/BenCodez/videoduplicatefinder
LABEL org.opencontainers.image.source=$VDF_SOURCE_URL \
      org.opencontainers.image.revision=$VDF_SOURCE_REVISION
# Web uses the ffmpeg/ffprobe executables by default. The distro libraries are
# not a promise of ABI compatibility with the optional FFmpeg.AutoGen binding.
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "VDF.Web.dll"]
