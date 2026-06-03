FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Biletix.Shared/Biletix.Shared.csproj Biletix.Shared/
COPY src/Biletix.Modules.Events/Biletix.Modules.Events.csproj Biletix.Modules.Events/
COPY src/Biletix.Modules.Bookings/Biletix.Modules.Bookings.csproj Biletix.Modules.Bookings/
COPY src/Biletix.Modules.Search/Biletix.Modules.Search.csproj Biletix.Modules.Search/
COPY src/Biletix.Api/Biletix.Api.csproj Biletix.Api/
RUN dotnet restore Biletix.Api/Biletix.Api.csproj
COPY src/Biletix.Shared/ Biletix.Shared/
COPY src/Biletix.Modules.Events/ Biletix.Modules.Events/
COPY src/Biletix.Modules.Bookings/ Biletix.Modules.Bookings/
COPY src/Biletix.Modules.Search/ Biletix.Modules.Search/
COPY src/Biletix.Api/ Biletix.Api/
RUN dotnet publish Biletix.Api/Biletix.Api.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Biletix.Api.dll"]
