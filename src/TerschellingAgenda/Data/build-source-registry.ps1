param(
  [string]$Batch1 = "$PSScriptRoot\sources-raw.json",
  [string]$Batch2 = "$PSScriptRoot\sources-raw-2.json",
  [string]$Manual = "$PSScriptRoot\sources-manual.json",
  [string]$OutPath = "$PSScriptRoot\source-registry.json"
)

function Load-Batch($path) {
  if (-not (Test-Path $path)) { return @() }
  $o = Get-Content $path -Raw | ConvertFrom-Json
  return @($o.sources)
}

$all = @()
$all += Load-Batch $Batch1
$all += Load-Batch $Batch2
$all += Load-Batch $Manual

$tierMap = @{
  'primary_organizer' = 'PrimaryOrganizer'
  'official_venue'    = 'OfficialVenue'
  'official_local'    = 'OfficialLocal'
  'tourist_calendar'  = 'TouristCalendar'
  'aggregator'        = 'Aggregator'
  'social'            = 'Social'
}

# Categorie-normalisatie naar Nederlandse labels voor het transparantiepaneel.
function Map-Category($raw, $name) {
  $c = ("$raw $name").ToLower()
  if ($c -match 'municipal|gemeente|official municipal|bestuurlijk') { return 'officiële lokale bron' }
  if ($c -match 'tourist|vvv|wadden|friesland\.nl')                  { return 'toeristische website' }
  if ($c -match 'museum')                                            { return 'museum' }
  if ($c -match 'theat|cinema|bioscoop|cultur|podium|concertzaal')    { return 'cultuur en theater' }
  if ($c -match 'festival')                                          { return 'festival' }
  if ($c -match 'nature|natuur|staatsbosbeheer|natuurmonument')       { return 'natuurorganisatie' }
  if ($c -match 'excursion|excursie|rondleiding|wadlopen|boat|rederij|vaart') { return 'excursieorganisatie' }
  if ($c -match 'sport|race|run|loop|marathon|zeil|toernooi')          { return 'sport en wedstrijden' }
  if ($c -match 'horeca|caf|restaurant|bar|paviljoen|pub|kroeg')       { return 'horeca en live muziek' }
  if ($c -match 'camping|holiday|park|resort|vakantie|stayokay|jeugdherberg') { return 'camping en vakantiepark' }
  if ($c -match 'church|kerk|parochie|gemeente protest')               { return 'kerk en gemeenschap' }
  if ($c -match 'associat|vereniging|dorpsbelang|plaatselijk|dorpshuis') { return 'lokale vereniging' }
  if ($c -match 'ticket|weeztix|eventbrite|paylogic|stager|yesplan')    { return 'ticketplatform' }
  if ($c -match 'news|nieuws|courant|omrop|krant|weekblad')             { return 'lokaal nieuws' }
  if ($c -match 'aggregat|uitagenda|allevents|festivalinfo|agenda')     { return 'evenementenkalender' }
  if ($c -match 'organizer|organisator')                               { return 'organisator' }
  return 'overige openbare bron'
}

$seen = @{}
$sources = foreach ($s in $all) {
  if (-not $s.name) { continue }

  $agenda = @()
  if ($s.agendaUrl -and $s.agendaUrl.Trim())   { $agenda += $s.agendaUrl.Trim() }
  if ($s.agendaUrls) { $agenda += @($s.agendaUrls | Where-Object { $_ }) }
  if ($agenda.Count -eq 0 -and $s.homepage)    { $agenda += $s.homepage }
  $agenda = @($agenda | Select-Object -Unique)
  if ($agenda.Count -eq 0) { continue }

  $key = ($agenda -join '|').ToLower()
  if ($seen.ContainsKey($key)) { continue }
  $seen[$key] = $true

  $id = ($s.name -replace '[^\w]+','-').Trim('-').ToLower()
  if ($id.Length -gt 60) { $id = $id.Substring(0,60) }

  $tier = 'Aggregator'
  if ($s.tier -and $tierMap.ContainsKey([string]$s.tier)) { $tier = $tierMap[[string]$s.tier] }

  $feedType = $null
  if ($s.feedType) { $feedType = [string]$s.feedType }
  elseif ($s.feedUrl -and $s.feedUrl -match '\.ics') { $feedType = 'ics' }
  elseif ($s.feedUrl -and $s.feedUrl -match 'feed|rss') { $feedType = 'rss' }

  # SPA's en geblokkeerde bronnen blijven in het register staan (transparantie),
  # maar we zetten ze uit zodat ze de run niet vertragen.
  $blocked = [bool]$s.blocked
  $enabled = -not $blocked

  [pscustomobject]@{
    id                 = $id
    name               = [string]$s.name
    homepage           = [string]$s.homepage
    agendaUrls         = @($agenda)
    category           = Map-Category $s.category $s.name
    tier               = $tier
    hasJsonLd          = [bool]$s.hasJsonLd
    feedUrl            = $(if ($s.feedUrl) { [string]$s.feedUrl } else { $null })
    feedType           = $feedType
    rendering          = $(if ($s.rendering) { [string]$s.rendering } else { 'server' })
    blocked            = $blocked
    selectorHint       = $(if ($s.selectorHint) { [string]$s.selectorHint } else { $null })
    dateQueryTemplate  = $null
    notes              = $(if ($s.notes) { [string]$s.notes } else { '' })
    enabled            = $enabled
    defaultVillage     = $(if ($s.defaultVillage) { [string]$s.defaultVillage } else { $null })
    defaultLocationName= $(if ($s.defaultLocationName) { [string]$s.defaultLocationName } else { $null })
    defaultAddress     = $(if ($s.defaultAddress) { [string]$s.defaultAddress } else { $null })
    defaultCategories  = @()
    maxDetailPages     = $(if ($tier -in @('PrimaryOrganizer','OfficialVenue','OfficialLocal')) { 25 } else { 12 })
  }
}

$registry = [pscustomobject]@{
  compiledAt = (Get-Date).ToUniversalTime().ToString('o')
  sources    = @($sources)
}

$json = $registry | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($OutPath, $json, [System.Text.UTF8Encoding]::new($false))

Write-Host "Geschreven: $OutPath"
Write-Host "  totaal:      $($sources.Count)"
Write-Host "  ingeschakeld:$(@($sources | Where-Object enabled).Count)"
$sources | Group-Object category | Sort-Object Count -Descending | ForEach-Object { "  {0,-28} {1}" -f $_.Name, $_.Count }
