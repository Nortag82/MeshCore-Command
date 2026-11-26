#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
# Run exactly the production verification function against synthetic NetSecurity objects.
$code = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'MeshCore.Windows.Preview/MercuryFirewall.cs')
$match = [regex]::Match($code, 'private const string Protection = """\r?\n(?<script>[\s\S]*?)\r?\n\s*""";')
if (!$match.Success) { throw 'Could not load the Mercury firewall condition.' }
$setup = [regex]::Match($code, 'const string configure = """\r?\n(?<script>[\s\S]*?)\r?\n\s*""";')
if (!$setup.Success) { throw 'Could not load the Mercury firewall setup.' }
[void][scriptblock]::Create($setup.Groups['script'].Value)
. ([scriptblock]::Create($match.Groups['script'].Value))
$exe = 'C:\MeshCore Test\mercury.exe'
$name = 'test-rule'
$profile = [pscustomobject]@{Enabled='True'; AllowLocalFirewallRules='True'; DisabledInterfaceAliases=@()}
$rule = [pscustomobject]@{Enabled='True'; Direction='Inbound'; Action='Block'; Profile='Any'}
$address = [pscustomobject]@{LocalAddress='Any'; RemoteAddress=$scope}
function Get-Service { [pscustomobject]@{ Status = 'Running' } }
function Get-NetFirewallProfile { $profile; $profile; $profile }
function Get-NetFirewallRule { $rule }
function Get-NetFirewallApplicationFilter { [pscustomobject]@{Program=$exe; Package='Any'} }
function Get-NetFirewallAddressFilter { $address }
function Get-NetFirewallPortFilter { [pscustomobject]@{Protocol='TCP'; LocalPort='Any'; RemotePort='Any'} }
function Get-NetFirewallInterfaceFilter { [pscustomobject]@{InterfaceAlias='Any'} }
function Get-NetFirewallInterfaceTypeFilter { [pscustomobject]@{InterfaceType='Any'} }
function Get-NetFirewallServiceFilter { [pscustomobject]@{Service='Any'} }
if (!(Test-Protection)) { throw 'Valid firewall fixture was rejected.' }
$profile.Enabled = 'False'
if (Test-Protection) { throw 'Disabled firewall must prevent startup.' }
$profile.Enabled = 'True'
$rule.Action = 'Allow'
if (Test-Protection) { throw 'An inbound allow rule must prevent startup.' }
$rule.Action = 'Block'
$address.RemoteAddress = @($scope[0], $scope[1])
if (Test-Protection) { throw 'A missing IPv6 block must prevent startup.' }
$address.RemoteAddress = $scope
$profile.AllowLocalFirewallRules = 'False'
if (Test-Protection) { throw 'Policy exclusions must prevent startup.' }
Write-Output 'Mercury firewall rule verification fixtures passed.'
