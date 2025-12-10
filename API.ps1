# Navigate to the server project directory relative to this script
Set-Location "$PSScriptRoot/MusicSalesApp"

# Start the API with hot reload enabled
dotnet watch run --launch-profile https

# Launch Google Chrome at the development URL
Start-Process "chrome" "https://localhost:7173"



