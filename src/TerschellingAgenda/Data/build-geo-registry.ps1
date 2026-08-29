param(
  [string]$RawPath = "$PSScriptRoot\geo-raw.json",
  [string]$OutPath = "$PSScriptRoot\geo-registry.json"
)

$raw = Get-Content $RawPath -Raw | ConvertFrom-Json

function Map-Type($t) {
  switch -Regex ($t) {
    '^dorp$'                                   { 'dorp'; break }
    '^buurtschap$'                             { 'buurtschap'; break }
    '^gehucht$'                                { 'gehucht'; break }
    'venue|paviljoen|camping|hotel|jeugdherberg|theater|museum|kerk|caf' { 'venue'; break }
    'strand'                                   { 'strand'; break }
    'natuurgebied|meer|duin|bos|polder|zandplaat|eiland|vallei' { 'natuurgebied'; break }
    'landmark|baken|historisch|haven'          { 'landmark'; break }
    'festival'                                 { 'evenement'; break }
    default                                    { 'overig' }
  }
}

# Namen die te generiek zijn om los als zoekterm te gebruiken (te veel ruis).
$noSearch = @('Terschelling','Waddenzee','Noordzee','Griend','Wadden')

$places = foreach ($p in $raw.places) {
  $type = Map-Type $p.type
  $variants = @()
  if ($p.variants) { $variants = @($p.variants | Where-Object { $_ -and $_.Trim().Length -gt 2 }) }

  # Varianten die elders in NL een echte plaatsnaam zijn, weren we als zoekterm-alias.
  $risky = @('Zeerijp','Kaart','West','Hoorn','Lies','Oosterend','Hee','Noordhoek','Halfweg','Duunt')
  $variants = @($variants | Where-Object { $risky -notcontains $_ })

  [pscustomobject]@{
    name            = $p.name
    type            = $type
    parent          = $(if ($p.parent) { $p.parent } else { $null })
    variants        = $variants
    source          = $p.source
    ambiguous       = [bool]$p.ambiguous
    useAsSearchTerm = -not ($noSearch -contains $p.name)
  }
}

$registry = [pscustomobject]@{
  island     = 'Terschelling'
  municipality = 'Gemeente Terschelling'
  province   = 'Fryslan'
  compiledAt = (Get-Date).ToUniversalTime().ToString('o')
  verificationSources = @(
    'https://nl.wikipedia.org/wiki/Terschelling',
    'https://nl.wikipedia.org/wiki/Lijst_van_plaatsen_in_de_gemeente_Terschelling',
    'https://www.plaatsengids.nl/terschelling',
    'https://www.terschelling.nl/',
    'https://www.vvvterschelling.nl/',
    'https://bagviewer.kadaster.nl/',
    'https://www.openstreetmap.org/relation/Terschelling'
  )
  places = @($places)
}

$json = $registry | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($OutPath, $json, [System.Text.UTF8Encoding]::new($false))
Write-Host "Geschreven: $OutPath ($($places.Count) plaatsen)"
$places | Group-Object type | Sort-Object Count -Descending | ForEach-Object { "  $($_.Name): $($_.Count)" }
