FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine as build
WORKDIR /app
EXPOSE 80
EXPOSE 443
COPY . .
RUN dotnet restore OneTransfert.srv.csproj
RUN dotnet publish -o /app/published-app

FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine as runtime
RUN apk add icu 
WORKDIR /app
COPY --from=build /app/published-app /app
ENTRYPOINT ["dotnet", "/app/OneTransfert.srv.dll"]