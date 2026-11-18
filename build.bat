@echo off
chcp 65001 >nul
rem 重新编译并打包成单个 点歌机.exe（需要 .NET 8 SDK）
cd /d "%~dp0desktop"
set "DOTNET=dotnet"
if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
"%DOTNET%" publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "%~dp0desktop\bin\publish" || goto :fail
copy /y "%~dp0desktop\bin\publish\DianGeJi.exe" "%~dp0点歌机.exe" >nul || goto :fail
echo.
echo 已生成 点歌机.exe
pause
exit /b 0
:fail
echo 打包失败，请检查上面的错误信息。
pause
exit /b 1
