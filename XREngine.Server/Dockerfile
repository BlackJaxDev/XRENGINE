FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .

ARG VERSION=0.0.0
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_NOLOGO=1

RUN dotnet restore XRENGINE.sln
RUN dotnet publish XREngine.Server/XREngine.Server.csproj -c Release -f net10.0 -o /app/publish /p:Version=$VERSION /p:TreatWarningsAsErrors=true /warnaserror

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:5000
COPY --from=build /app/publish .
EXPOSE 5000
ENTRYPOINT ["dotnet", "XREngine.Server.dll"]
