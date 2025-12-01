# Development

## Local Setup

Open the solution project in your IDE of choice and install the
dependencies and resolve the project using 
`dotnet restore SubathonManager.sln`

When you build and run locally through your IDE, 
you should be running `SubathonManager.UI` as the main project.

## Issues and Feature Requests

Please submit a github issue if you have a bug or feature request. 

For bugs, if requested, you may be asked to attached a logfile as well,
which you can get from opening the data folder from the settings page, 
and going to the logs directory.

Please check if an issue or feature request exists similar to yours before submitting!

## Pull Requests

For all pull requests, they must tie to an issue or enhancement before they will be reviewed.

For fixes or new features, please describe in detail use cases or situations your changes will affect and, if possible, provide screenshots or recordings of said change.

Maintainers will review all PR's prior to merging.

## New Widget Presets

If you are submitting a new preset to include, please make sure your PR *only* includes net-new files in the presets folder.

Ideally, if you make your own preset widget(s), that you would like to share freely, you can have them in your own repo and link to this project.

We would like to have a diverse set of presets in the base project, 
but also want to avoid bloating it for users. Eventually,
we may create a list of overlays linked within the wiki here. 
So submit an issue or discussion for these! We'd love to see them.