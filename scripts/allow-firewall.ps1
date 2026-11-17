# Allow the phone to reach the Phone Touchpad agent through Windows Firewall.
#
# Run this yourself in an ELEVATED PowerShell (right-click > Run as administrator).
# The agent never runs it for you.
#
#   .\scripts\allow-firewall.ps1                 # Private networks only (recommended)
#   .\scripts\allow-firewall.ps1 -Profile Any    # also Public networks, e.g. the Windows Mobile Hotspot
#   .\scripts\allow-firewall.ps1 -Remove         # delete the rule again
param(
    [int]$Port = 8765,
    [ValidateSet("Private", "Public", "Domain", "Any")]
    [string]$Profile = "Private",
    [switch]$Remove
)

$ruleName = "Phone Touchpad (TCP $Port)"

if ($Remove) {
    netsh advfirewall firewall delete rule name="$ruleName"
    exit $LASTEXITCODE
}

netsh advfirewall firewall add rule name="$ruleName" dir=in action=allow protocol=TCP localport=$Port profile=$Profile
if ($LASTEXITCODE -eq 0) {
    Write-Host "Allowed inbound TCP $Port on $Profile networks. Remove with: .\scripts\allow-firewall.ps1 -Remove"
}
