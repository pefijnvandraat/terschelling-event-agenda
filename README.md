# Agenda Terschelling

Verzamelt activiteiten op Terschelling uit openbare bronnen en toont ze als één
doorzoekbare agenda, met per activiteit de herkomst en een onderbouwde inschatting
van de betrouwbaarheid.

## Wat het doet

- Raadpleegt ruim honderd geregistreerde bronnen: gemeentelijke agenda's, VVV,
  organisatoren, musea, verenigingen en evenementenkalenders.
- Vult dat aan met zoekmachine-ontdekking, zodat ook niet-geregistreerde pagina's
  worden gevonden.
- Voegt dezelfde activiteit uit verschillende bronnen samen tot één vermelding.
- Laat per activiteit zien welke websites hem noemen en waar bronnen elkaar tegenspreken.

## Betrouwbaarheid

Elke activiteit krijgt een label:

| Label | Betekenis |
| --- | --- |
| Bevestigd | Afkomstig van de organisator of een officiële bron, of bevestigd door twee onafhankelijke websites. |
| Onzeker | Eén enkele bron, of bronnen die elkaar op een feitelijk punt tegenspreken. |
| Onbekend | Kerngegevens zoals datum of naam ontbreken. |

Verschillen in formulering tellen niet als tegenspraak: twee websites beschrijven
dezelfde activiteit vrijwel nooit in dezelfde woorden. Alleen feitelijke verschillen
— locatie, adres, starttijd — verlagen de betrouwbaarheid.

Meerdere pagina's van dezelfde website gelden als één bron. Twee vermeldingen op
één site bevestigen elkaar immers niet.

## Ophalen van bronnen

Wanneer een gewoon verzoek niet volstaat, schakelt de applicatie stapsgewijs op:

1. Gewoon HTTP-verzoek.
2. Hostvariant (`www.` erbij of eraf).
3. Machineleesbare varianten: iCalendar, RSS, WordPress REST.
4. Een echte browser, voor agenda's die pas door JavaScript worden opgebouwd.
5. Een gearchiveerde momentopname, uitsluitend als laatste redmiddel en altijd
   gemarkeerd als mogelijk verouderd.

Er wordt geen enkele beveiliging omzeild: geen proxyrotatie, geen CAPTCHA-omzeiling,
geen IP-verhulling. Weigert een site ook een echte browser, dan blijft dat een
geregistreerde en gerapporteerde weigering.

Websites krijgen een beleefdheidspauze tussen verzoeken en een beperkt aantal
gelijktijdige verbindingen. Een site die niet reageert, wordt onthouden en enkele
uren overgeslagen — daarna krijgt hij vanzelf weer een kans.

## Draaien

Vereist de .NET 10 SDK.

```bash
cd src/TerschellingAgenda
dotnet run
```

Open daarna <http://localhost:8477>.

## Transparantie

Het rapport onder de resultaten laat zien welke bronnen zijn geraadpleegd, welke
niet bereikbaar waren, waarvoor een browser of archief nodig was, en welke velden
niet konden worden geverifieerd. Volledigheid wordt niet gegarandeerd: er staat
alleen in wat in de geraadpleegde openbare bronnen te vinden was.

## Licentie

MIT — zie [LICENSE](LICENSE).
