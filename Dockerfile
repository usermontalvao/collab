# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Jurius.CollabEditing.csproj ./
RUN dotnet restore
COPY . ./
# Projeto explícito: a pasta tests/ também tem um .csproj.
RUN dotnet publish Jurius.CollabEditing.csproj -c Release -o /app --no-restore

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# O Syncfusion.DocIO usa System.Drawing para medir/renderizar conteúdo do .docx.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgdiplus libc6-dev fontconfig fonts-liberation \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Jurius.CollabEditing.dll"]
