@echo off
set list=win-x64

(for %%a in (%list%) do (
  dotnet publish Desktop -c Release -r %%a -o ./bin/%%a --self-contained true
  powershell "Compress-Archive -Path ./bin/%%a/* -DestinationPath ./bin/%%a.zip -CompressionLevel Optimal"
))

dotnet publish Desktop -c Release -o ./bin/portable -p:PublishSingleFile=false -p:PublishTrimmed=false --self-contained false
powershell "Compress-Archive -Path ./bin/portable/* -DestinationPath ./bin/portable.zip -CompressionLevel Optimal"

echo Done
pause
