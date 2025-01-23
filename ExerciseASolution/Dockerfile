
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore "ExerciseASolution.csproj"
RUN dotnet publish "ExerciseASolution.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
EXPOSE 5000
EXPOSE 8181
ENTRYPOINT ["dotnet", "ExerciseASolution.dll"]