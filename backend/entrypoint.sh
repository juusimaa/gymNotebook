#!/bin/sh

set -e

dotnet GymNotebook.Api.dll --migrate
exec dotnet GymNotebook.Api.dll
