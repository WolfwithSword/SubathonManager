$projectRoot = (Resolve-Path "$PSScriptRoot\..\..")

Push-Location $projectRoot
try {
    Remove-Item -Path "SubathonManager.Tests\TestResults\*" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "coverage-local" -Recurse -Force -ErrorAction SilentlyContinue

    dotnet test SubathonManager.Tests\SubathonManager.Tests.csproj `
        --results-directory coverage-local `
        --coverage `
        --coverage-output-format cobertura `
        --coverage-output coverage.cobertura.xml

    reportgenerator `
        -reports:"**\*.cobertura.xml" `
        -targetdir:"coverage-report" `
        -reporttypes:Html `
        -classfilters:"+*;-SubathonManager.Data.Migrations.*" `
        -filefilters:"-**/*.g.cs"

    Invoke-Item coverage-report/index.html
}
finally {
    Pop-Location
}
