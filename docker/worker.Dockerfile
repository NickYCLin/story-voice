FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /source
COPY . .
RUN dotnet restore StoryVoice.sln
RUN dotnet publish src/StoryVoice.Worker/StoryVoice.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS final
WORKDIR /app
RUN apk add --no-cache krb5-libs python3 ffmpeg
COPY --from=build /app .
ENTRYPOINT ["dotnet", "StoryVoice.Worker.dll"]
