$projectRoot = (Resolve-Path "$PSScriptRoot\..\..")

Push-Location $projectRoot
try {
    Remove-Item -Path "SubathonManager.Tests\TestResults\*" -Recurse -Force
    dotnet test --collect:"XPlat Code Coverage" `
        /p:Exclude="[SubathonManager.Data.Migrations]*" `
        /p:CollectCoverage=true `
        /p:CoverletOutputFormat=cobertura `
        /p:ExcludeByFile="**/*.g.cs"


    reportgenerator `
        -reports:"**\coverage.cobertura.xml" `
        -targetdir:"coverage-report" `
        -reporttypes:Html `
        -classfilters:"+*;-SubathonManager.Data.Migrations.*" `
        -filefilters:"-**/*.g.cs"
    
    Invoke-Item coverage-report/index.html
}
finally {
    Pop-Location
}