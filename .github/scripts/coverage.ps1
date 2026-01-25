Remove-Item -Path "SubathonManager.Tests\TestResults\*" -Recurse -Force
dotnet test --collect:"XPlat Code Coverage" /p:Exclude="[SubathonManager.Data.Migrations]*" /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura
reportgenerator -reports:**/coverage.cobertura.xml -targetdir:coverage-report -reporttypes:Html -classfilters:"+*;-SubathonManager.Data.Migrations.*"
Invoke-Item coverage-report/index.html