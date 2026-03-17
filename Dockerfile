# 1. 使用 .NET 8 官方镜像进行编译
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# 2. 把所有代码复制进去并还原依赖
COPY . ./
RUN dotnet restore

# 3. 发布项目
RUN dotnet publish -c Release -o out

# 4. 运行阶段：使用更轻量的运行镜像
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/out .

# 5. 暴露端口并启动你的 API
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "BadmintonFYP.Api.dll"]