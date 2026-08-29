'use strict';

/* ----------------------------------------------------------------- helpers */
const $ = (sel) => document.querySelector(sel);
const $$ = (sel) => Array.from(document.querySelectorAll(sel));

const NL_DAYS = ['zondag', 'maandag', 'dinsdag', 'woensdag', 'donderdag', 'vrijdag', 'zaterdag'];
const NL_MONTHS = ['januari', 'februari', 'maart', 'april', 'mei', 'juni',
                   'juli', 'augustus', 'september', 'oktober', 'november', 'december'];

const UNKNOWN = 'Onbekend';

function esc(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function isUnknown(v) {
  return v === null || v === undefined || String(v).trim() === '' ||
         String(v).trim().toLowerCase() === 'onbekend';
}

/** Rendert een waarde, of een cursief "Onbekend" als hij ontbreekt. */
function val(v) {
  return isUnknown(v) ? `<span class="unknown">${UNKNOWN}</span>` : esc(v);
}

function toIsoDate(d) {
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

function parseIsoDate(s) {
  if (!s) return null;
  const [y, m, d] = String(s).split('T')[0].split('-').map(Number);
  if (!y || !m || !d) return null;
  return new Date(y, m - 1, d);
}

function formatDateNl(iso, opts = {}) {
  const d = parseIsoDate(iso);
  if (!d) return UNKNOWN;
  const day = NL_DAYS[d.getDay()];
  const base = `${d.getDate()} ${NL_MONTHS[d.getMonth()]}${opts.year === false ? '' : ' ' + d.getFullYear()}`;
  return opts.weekday === false ? base : `${day} ${base}`;
}

function formatTime(t) {
  if (!t) return null;
  const parts = String(t).split(':');
  return `${parts[0].padStart(2, '0')}:${(parts[1] || '00').padStart(2, '0')}`;
}

/** "[DATUM] | [STARTTIJD - EINDTIJD]" */
function whenLine(ev) {
  let datePart = formatDateNl(ev.date);
  if (ev.endDate && ev.endDate !== ev.date) {
    datePart += ` t/m ${formatDateNl(ev.endDate)}`;
  }
  const s = formatTime(ev.startTime);
  const e = formatTime(ev.endTime);
  let timePart;
  if (s && e) timePart = `${s} - ${e}`;
  else if (s) timePart = `vanaf ${s}`;
  else timePart = 'tijd onbekend';
  return `${datePart} | ${timePart}`;
}

function hostOf(url) {
  try { return new URL(url).hostname.replace(/^www\./, ''); }
  catch { return url || ''; }
}

const TIER_LABEL = {
  PrimaryOrganizer: 'organisator',
  OfficialVenue: 'officiële locatie',
  OfficialLocal: 'officiële lokale bron',
  TouristCalendar: 'toeristische kalender',
  Aggregator: 'evenementensite',
  Social: 'sociale media'
};

const STRATEGY_LABEL = {
  Direct: 'direct opgehaald',
  HostVariant: 'via www-variant',
  AlternatePath: 'via feed of API',
  Browser: 'via echte browser',
  WebArchive: 'via webarchief',
  Failed: 'niet gelukt'
};

const STRATEGY_HELP = {
  Direct: 'Een gewoon HTTP-verzoek volstond.',
  HostVariant: 'De server weigerde www. of juist het kale domein; de andere schrijfwijze werkte wel.',
  AlternatePath: 'De pagina zelf was onbruikbaar, maar er bleek een ICS-, RSS- of JSON-feed te zijn.',
  Browser: 'De agenda bestaat pas nadat JavaScript is uitgevoerd, of de site weigert eenvoudige clients.',
  WebArchive: 'De site was onbereikbaar; er is een publieke momentopname gebruikt (mogelijk verouderd).',
  Failed: 'Ook met alle terugvalopties niet op te halen.'
};

/* ----------------------------------------------------------------- state */
const state = {
  events: [],
  report: null,
  view: 'cards',
  searching: false
};

/* ----------------------------------------------------------------- datums */
function setRange(fromDate, toDate) {
  $('#fromDate').value = toIsoDate(fromDate);
  $('#toDate').value = toIsoDate(toDate);
}

function applyQuickRange(value) {
  const today = new Date();
  today.setHours(0, 0, 0, 0);

  if (value === 'today') return setRange(today, today);
  if (value === 'tomorrow') {
    const t = new Date(today); t.setDate(t.getDate() + 1);
    return setRange(t, t);
  }
  if (value === 'weekend') {
    const d = new Date(today);
    // eerstvolgende vrijdag (of vandaag als het al vrijdag/za/zo is)
    const dow = d.getDay(); // 0=zo
    let toFriday = (5 - dow + 7) % 7;
    if (dow === 6 || dow === 0) toFriday = 0; // al weekend
    const start = new Date(d); start.setDate(d.getDate() + (dow === 0 ? 0 : toFriday));
    const end = new Date(start);
    end.setDate(start.getDate() + (start.getDay() === 5 ? 2 : (start.getDay() === 6 ? 1 : 0)));
    return setRange(start, end);
  }
  const days = parseInt(value, 10);
  if (!Number.isNaN(days)) {
    const end = new Date(today);
    end.setDate(today.getDate() + days - 1);
    return setRange(today, end);
  }
}

/* ----------------------------------------------------------------- rendering */
function badgeHtml(ev) {
  const out = [];
  out.push(`<span class="badge badge-conf-${esc(ev.confidence)}" title="Betrouwbaarheid van de gegevens">${esc(ev.confidence)}</span>`);
  (ev.categories || []).forEach((c) => out.push(`<span class="badge badge-cat">${esc(c)}</span>`));
  if (ev.priceKind === 'Gratis') out.push('<span class="badge badge-free">Gratis</span>');
  if (ev.duplicateCount > 1) {
    out.push(`<span class="badge badge-dup" title="Deze activiteit is op ${ev.duplicateCount} bronnen gevonden en samengevoegd">${ev.duplicateCount} bronnen</span>`);
  }
  return out.join('');
}

function metaHtml(ev) {
  const contact = [];
  if (!isUnknown(ev.contactPerson)) contact.push(esc(ev.contactPerson));
  if (!isUnknown(ev.phone)) contact.push(esc(ev.phone));
  if (!isUnknown(ev.email)) contact.push(`<a href="mailto:${esc(ev.email)}">${esc(ev.email)}</a>`);
  if (!isUnknown(ev.website)) contact.push(`<a href="${esc(ev.website)}" target="_blank" rel="noopener noreferrer">${esc(hostOf(ev.website))}</a>`);

  const rows = [
    ['Categorie', (ev.categories || []).join(', ')],
    ['Dorp/plaats', ev.village],
    ['Locatie', ev.locationName],
    ['Adres', ev.address],
    ['Organisator', ev.organizer],
    ['Contact', contact.length ? contact.join(' · ') : UNKNOWN],
    ['Prijs', ev.price],
    ['Reserveren', ev.reservationRequired],
    ['Bron', ev.primarySourceUrl
      ? `<a href="${esc(ev.primarySourceUrl)}" target="_blank" rel="noopener noreferrer">${esc(hostOf(ev.primarySourceUrl))}</a>`
      : UNKNOWN]
  ];

  return `<dl class="ev-meta">${rows.map(([k, v]) => {
    const isHtml = /^<a /.test(String(v));
    return `<div><dt>${esc(k)}:</dt><dd>${isHtml ? v : val(v)}</dd></div>`;
  }).join('')}</dl>`;
}

function cardHtml(ev) {
  const compact = [ev.village, ev.locationName].filter((x) => !isUnknown(x)).join(' · ')
    || (ev.categories || []).join(', ');

  return `
    <article class="ev" data-conf="${esc(ev.confidence)}" data-id="${esc(ev.id)}">
      <div class="ev-when">${esc(whenLine(ev))}</div>
      <h3 class="ev-title">${esc(ev.name)}</h3>
      <div class="badges">${badgeHtml(ev)}</div>
      ${isUnknown(ev.description) ? '' : `<p class="ev-desc">${esc(truncate(ev.description, 190))}</p>`}
      <div class="ev-compact-meta">${esc(compact)}</div>
      ${metaHtml(ev)}
      <div class="ev-actions">
        <button type="button" class="link-btn secondary js-detail" data-id="${esc(ev.id)}">Details</button>
        ${ev.primarySourceUrl ? `<a class="link-btn" href="${esc(ev.primarySourceUrl)}" target="_blank" rel="noopener noreferrer">Bekijk oorspronkelijke bron</a>` : ''}
        ${!isUnknown(ev.ticketUrl) ? `<a class="link-btn secondary" href="${esc(ev.ticketUrl)}" target="_blank" rel="noopener noreferrer">Tickets</a>` : ''}
      </div>
    </article>`;
}

function truncate(s, n) {
  s = String(s || '');
  return s.length <= n ? s : s.slice(0, n).trimEnd() + '…';
}

function renderResults() {
  const box = $('#results');
  const events = state.events;

  $('#resultCount').textContent = events.length;
  $('#emptyState').hidden = events.length > 0;
  box.className = 'results ' + state.view;

  if (!events.length) { box.innerHTML = ''; return; }

  // In lijstweergave groeperen we per dag met een datumkop.
  if (state.view === 'list') {
    let html = '';
    let currentDay = null;
    for (const ev of events) {
      if (ev.date !== currentDay) {
        currentDay = ev.date;
        html += `<div class="day-heading">${esc(formatDateNl(ev.date))}</div>`;
      }
      html += cardHtml(ev);
    }
    box.innerHTML = html;
  } else {
    box.innerHTML = events.map(cardHtml).join('');
  }
}

/* ----------------------------------------------------------------- detail */
function openDetail(id) {
  const ev = state.events.find((e) => e.id === id);
  if (!ev) return;

  const sources = (ev.sources || []).map((s) => `
    <li>
      <span class="tier-tag">${esc(TIER_LABEL[s.tier] || s.tier)}</span>
      <a href="${esc(s.url)}" target="_blank" rel="noopener noreferrer">${esc(s.sourceName || hostOf(s.url))}</a>
      <span class="hint">(${esc(s.method)}, gecontroleerd ${esc(formatDateTimeNl(s.retrievedAt))})</span>
    </li>`).join('');

  const realConflicts = (ev.conflicts || []).filter((c) => c.affectsConfidence !== false);
  const softDiffs = (ev.conflicts || []).filter((c) => c.affectsConfidence === false);

  const renderConflict = (c) => `
    <div class="conflict">
      <strong>${esc(c.field)}${c.resolved || c.affectsConfidence === false ? '' : ' — onzeker'}:</strong>
      gekozen “${esc(c.chosenValue)}”${c.chosenFrom ? ` (${esc(c.chosenFrom)})` : ''}.
      Andere bron(nen) noemen: ${c.rejectedValues.map((v) => `“${esc(v)}”`).join(', ')}.
      <br><span class="hint">${esc(c.reason)}</span>
    </div>`;

  const conflicts = realConflicts.map(renderConflict).join('');
  const differences = softDiffs.map(renderConflict).join('');

  $('#detailContent').innerHTML = `
    <div class="ev-when">${esc(whenLine(ev))}</div>
    <h3 id="detailTitle">${esc(ev.name)}</h3>
    <div class="badges">${badgeHtml(ev)}</div>
    ${isUnknown(ev.description) ? `<p class="hint" style="margin-top:.7rem">Geen beschrijving beschikbaar in de bron.</p>`
                                : `<p style="margin-top:.7rem">${esc(ev.description)}</p>`}
    ${metaHtml(ev)}
    ${conflicts ? `<div style="margin-top:.8rem"><h4 style="font-size:.9rem;margin-bottom:.2rem">Tegenstrijdige informatie</h4>${conflicts}</div>` : ''}
    ${differences ? `<div style="margin-top:.8rem"><h4 style="font-size:.9rem;margin-bottom:.2rem">Verschillen tussen bronnen</h4>${differences}</div>` : ''}
    <div style="margin-top:1rem">
      <h4 style="font-size:.9rem;margin-bottom:.2rem">Bronnen (${(ev.sources || []).length})</h4>
      <ul class="source-list">${sources}</ul>
    </div>
    <p class="hint" style="margin-top:.9rem">
      Laatst gecontroleerd: ${esc(formatDateTimeNl(ev.lastCheckedAt))}.
      ${ev.matchedPlaceTerms && ev.matchedPlaceTerms.length
        ? `Herkende plaatsnamen: ${esc(ev.matchedPlaceTerms.join(', '))}.` : ''}
      ${!isUnknown(ev.discoveryQuery) ? `Gevonden via: ${esc(ev.discoveryQuery)}.` : ''}
    </p>
    <div class="ev-actions" style="margin-top:1rem">
      ${ev.primarySourceUrl ? `<a class="link-btn" href="${esc(ev.primarySourceUrl)}" target="_blank" rel="noopener noreferrer">Bekijk oorspronkelijke bron</a>` : ''}
      ${!isUnknown(ev.ticketUrl) ? `<a class="link-btn secondary" href="${esc(ev.ticketUrl)}" target="_blank" rel="noopener noreferrer">Tickets</a>` : ''}
    </div>`;

  $('#detailDialog').showModal();
}

function formatDateTimeNl(iso) {
  if (!iso) return UNKNOWN;
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return UNKNOWN;
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getDate()} ${NL_MONTHS[d.getMonth()]} ${d.getFullYear()}, ${p(d.getHours())}:${p(d.getMinutes())}`;
}

/* ----------------------------------------------------------------- transparantie */
function renderTransparency() {
  const r = state.report;
  const box = $('#transparency');
  if (!r) {
    box.innerHTML = '<p class="hint">Nog geen zoekopdracht uitgevoerd in deze sessie.</p>';
    return;
  }

  const stat = (v, l) => `<div class="stat"><div class="stat-value">${esc(v)}</div><div class="stat-label">${esc(l)}</div></div>`;

  const outcomes = (r.sourceOutcomes || []);
  const okList = outcomes.filter((o) => o.status === 'ok' || o.inRangeEvents > 0);
  const failList = outcomes.filter((o) => o.status === 'fout' || o.status === 'geblokkeerd');

  const productive = outcomes.filter((o) => o.inRangeEvents > 0)
    .sort((a, b) => b.inRangeEvents - a.inRangeEvents);

  const fetchRows = Object.entries(r.fetchStrategies || {})
    .sort((a, b) => b[1] - a[1]);

  box.innerHTML = `
    <div class="stat-grid">
      ${stat(formatDateTimeNl(r.searchedAt), 'Gezocht op')}
      ${stat(`${formatDateNl(r.from, { weekday: false })} t/m ${formatDateNl(r.to, { weekday: false })}`, 'Geselecteerde periode')}
      ${stat(r.uniqueEvents, 'Unieke activiteiten')}
      ${stat(r.duplicatesMerged, 'Duplicaten samengevoegd')}
      ${stat(r.rawEventsCollected, 'Ruwe vondsten')}
      ${stat(`${r.sourcesOk}/${r.sourcesTotal}`, 'Bronnen bereikbaar')}
      ${stat(r.eventsConfirmed, 'Bevestigd')}
      ${stat(r.eventsUncertain, 'Onzeker')}
      ${stat(Math.round((r.durationMs || 0) / 1000) + ' s', 'Zoekduur')}
    </div>

    <div class="trans-block">
      <h3>Meegenomen plaatsen (${(r.placesIncluded || []).length})</h3>
      <div class="chip-row">${(r.placesIncluded || []).map((p) => `<span class="chip">${esc(p)}</span>`).join('')}</div>
    </div>

    <div class="trans-block">
      <h3>Onderzochte typen bronnen (${(r.sourceTypesInvestigated || []).length})</h3>
      <div class="chip-row">${(r.sourceTypesInvestigated || []).map((t) => `<span class="chip">${esc(t)}</span>`).join('')}</div>
    </div>

    ${(r.unverifiedFields || []).length ? `
    <div class="trans-block">
      <h3>Gegevens die niet konden worden geverifieerd</h3>
      <p class="hint">Per veld het aantal activiteiten waarvoor geen enkele geraadpleegde bron een waarde gaf.</p>
      <table class="trans-table">
        <thead><tr><th>Veld</th><th class="num">Ontbreekt</th><th class="num">Van</th></tr></thead>
        <tbody>${r.unverifiedFields.map((u) =>
          `<tr><td>${esc(u.field)}</td><td class="num">${u.missingCount}</td><td class="num">${u.totalEvents}</td></tr>`).join('')}
        </tbody>
      </table>
    </div>` : ''}

    ${fetchRows.length ? `
    <div class="trans-block">
      <h3>Hoe de bronnen zijn opgehaald</h3>
      <p class="hint">Wanneer een gewoon verzoek niet volstaat, schakelt de zoekopdracht automatisch op
         naar een zwaardere methode. Zo blijven ook JavaScript-agenda's en weigerende sites bruikbaar.</p>
      <div class="chip-row">${fetchRows.map(([k, v]) =>
        `<span class="chip ${k === 'Failed' ? 'bad' : 'ok'}" title="${esc(STRATEGY_HELP[k] || '')}">${esc(STRATEGY_LABEL[k] || k)}: ${v}</span>`).join('')}</div>
      ${(r.sourcesNeedingBrowser || []).length ? `
        <p class="hint" style="margin-top:.5rem"><strong>Echte browser nodig voor:</strong>
          ${esc(r.sourcesNeedingBrowser.map((s) => truncate(s, 50)).join(' · '))}</p>` : ''}
      ${(r.sourcesFromArchive || []).length ? `
        <div class="conflict" style="margin-top:.5rem">
          <strong>Uit gearchiveerde momentopname (mogelijk verouderd):</strong>
          ${esc(r.sourcesFromArchive.join(' · '))}.
          Deze activiteiten staan als “Onzeker” gemarkeerd.
        </div>` : ''}
      ${(r.skippedHosts || []).length ? `
        <p class="hint" style="margin-top:.5rem"><strong>Overgeslagen omdat de site niet reageerde:</strong>
          ${esc(r.skippedHosts.join(' · '))}. Deze worden over enkele uren vanzelf opnieuw geprobeerd.</p>` : ''}
      ${r.browserAvailable === false ? `
        <p class="hint" style="margin-top:.5rem">Geen browser beschikbaar op deze machine —
          JavaScript-only agenda's konden niet worden uitgelezen.</p>` : ''}
    </div>` : ''}

    ${productive.length ? `
    <details class="trans-details">
      <summary>Bronnen die activiteiten opleverden (${productive.length})</summary>
      <div class="scroll-box">
        <table class="trans-table">
          <thead><tr><th>Bron</th><th>Type</th><th>Methode</th><th class="num">Activiteiten</th></tr></thead>
          <tbody>${productive.map((o) =>
            `<tr><td>${esc(truncate(o.sourceName, 60))}</td><td>${esc(o.category)}</td>
                 <td>${esc((o.methods || []).join(', '))}</td><td class="num">${o.inRangeEvents}</td></tr>`).join('')}
          </tbody>
        </table>
      </div>
    </details>` : ''}

    ${failList.length ? `
    <details class="trans-details">
      <summary>Bronnen die niet konden worden gecontroleerd (${failList.length})</summary>
      <div class="scroll-box">
        <table class="trans-table">
          <thead><tr><th>Bron</th><th>Status</th><th>Reden</th></tr></thead>
          <tbody>${failList.map((o) =>
            `<tr><td>${esc(truncate(o.sourceName, 55))}</td><td>${esc(o.status)}</td>
                 <td>${esc(truncate(o.error || '', 120))}</td></tr>`).join('')}
          </tbody>
        </table>
      </div>
    </details>` : ''}

    ${(r.searchQueriesUsed || []).length ? `
    <details class="trans-details">
      <summary>Gebruikte zoekopdrachten (${r.searchQueriesUsed.length})</summary>
      <div class="scroll-box" style="padding:.6rem">
        <div class="chip-row">${r.searchQueriesUsed.map((q) => `<span class="chip">${esc(q)}</span>`).join('')}</div>
      </div>
    </details>` : ''}

    ${(r.warnings || []).length ? `
    <details class="trans-details">
      <summary>Waarschuwingen (${r.warnings.length})</summary>
      <div class="scroll-box" style="padding:.6rem">
        ${r.warnings.map((w) => `<p class="hint">${esc(w)}</p>`).join('')}
      </div>
    </details>` : ''}

    <p class="hint"><strong>${esc(r.disclaimer || '')}</strong></p>`;
}

/* ----------------------------------------------------------------- API */
function filterParams() {
  const p = new URLSearchParams();
  p.set('from', $('#fromDate').value);
  p.set('to', $('#toDate').value);
  const add = (key, sel) => {
    const v = $(sel).value;
    if (v && v !== '*') p.set(key, v);
  };
  add('village', '#villageFilter');
  add('category', '#categoryFilter');
  add('price', '#priceFilter');
  add('confidence', '#confidenceFilter');
  add('reservation', '#reservationFilter');
  add('sort', '#sortSelect');
  const q = $('#qFilter').value.trim();
  if (q) p.set('q', q);
  return p;
}

async function applyFilters() {
  try {
    const res = await fetch('/api/events?' + filterParams().toString());
    if (!res.ok) {
      const err = await res.json().catch(() => ({}));
      return showAlert(err.error || 'Filteren mislukt.');
    }
    const data = await res.json();
    state.events = data.events || [];
    if (data.report) state.report = data.report;
    renderResults();
    renderTransparency();
    updateRangeLabel();
    hideAlert();
  } catch (e) {
    showAlert('Kon de resultaten niet ophalen: ' + e.message);
  }
}

/* --------------------------------------------------------------- voortgang */

const PROGRESS_HINT = 'Bronnen worden parallel geraadpleegd. Een bron die niet reageert, wordt overgeslagen.';

function formatEta(ms) {
  const s = Math.round(ms / 1000);
  if (s < 60) return `${s} seconde${s === 1 ? '' : 'n'}`;
  const m = Math.round(s / 60);
  return `${m} minu${m === 1 ? 'ut' : 'ten'}`;
}

function resetProgress() {
  $('#progressText').textContent = 'Bezig met zoeken…';
  $('#progressPercent').textContent = '0%';
  $('#progressFill').style.width = '0%';
  $('#progressBar').setAttribute('aria-valuenow', '0');
  $('#progressSteps').innerHTML = '';
  $('#progressHint').textContent = PROGRESS_HINT;
}

function renderProgress(p) {
  if (!p) return;

  const pct = Math.max(0, Math.min(100, p.percent || 0));
  $('#progressText').textContent = p.summary || 'Bezig met zoeken…';
  $('#progressPercent').textContent = pct + '%';
  $('#progressFill').style.width = pct + '%';
  $('#progressBar').setAttribute('aria-valuenow', String(pct));

  $('#progressSteps').innerHTML = (p.steps || []).map((s, i) => {
    const skipped = s.state === 'overgeslagen';
    const count = skipped ? 'overgeslagen'
      : s.state === 'afgebroken' ? `afgebroken bij ${s.done}/${s.total}`
      : (s.total > 0 ? `${s.done}/${s.total}` : '');
    return `
    <li class="step" data-state="${esc(s.state)}">
      <span class="step-index" aria-hidden="true">${i + 1}</span>
      <span class="step-label">${esc(s.label)}</span>
      <span class="step-count">${esc(count)}</span>
      <span class="step-pct">${skipped ? '—' : s.percent + '%'}</span>
      <div class="bar"><div class="bar-fill" style="width:${skipped ? 0 : s.percent}%"></div></div>
    </li>`;
  }).join('');

  $('#progressHint').textContent = p.remainingMsEstimate > 3000
    ? `Nog ongeveer ${formatEta(p.remainingMsEstimate)} te gaan.`
    : PROGRESS_HINT;
}

async function runSearch() {
  if (state.searching) return;
  state.searching = true;

  const btn = $('#searchBtn');
  btn.disabled = true;
  btn.querySelector('.btn-label').textContent = 'Bezig met zoeken…';
  resetProgress();
  $('#progress').hidden = false;
  hideAlert();

  const poll = setInterval(async () => {
    try {
      const s = await (await fetch('/api/status')).json();
      if (s.detail) renderProgress(s.detail);
      else if (s.progress) $('#progressText').textContent = s.progress;
    } catch { /* status is optioneel */ }
  }, 700);

  const body = {
    from: $('#fromDate').value,
    to: $('#toDate').value,
    deepSearch: $('#deepSearch').checked,
    useBrowser: $('#useBrowser').checked,
    useArchive: $('#useArchive').checked,
    maxQueries: parseInt($('#maxQueries').value, 10),
    maxPages: parseInt($('#maxPages').value, 10),
    village: $('#villageFilter').value,
    category: $('#categoryFilter').value,
    price: $('#priceFilter').value,
    confidence: $('#confidenceFilter').value,
    reservation: $('#reservationFilter').value,
    sort: $('#sortSelect').value,
    q: $('#qFilter').value.trim()
  };

  try {
    const res = await fetch('/api/search', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
    if (!res.ok) {
      const err = await res.json().catch(() => ({}));
      throw new Error(err.error || `Server gaf status ${res.status}`);
    }
    const data = await res.json();
    state.events = data.events || [];
    state.report = data.report || null;
    renderResults();
    renderTransparency();
    updateRangeLabel();
    await loadFacets();

    if (state.report && state.report.sourcesFailed > 0) {
      showAlert(`Let op: ${state.report.sourcesFailed} bron(nen) waren niet bereikbaar. ` +
                `De zoekopdracht is met de overige bronnen voltooid — zie het transparantiepaneel.`);
    }
  } catch (e) {
    showAlert('Zoeken mislukt: ' + e.message);
  } finally {
    clearInterval(poll);
    state.searching = false;
    btn.disabled = false;
    btn.querySelector('.btn-label').textContent = 'Zoeken op internet';

    // Nog even op 100% laten staan, zodat zichtbaar is dát het klaar is
    // in plaats van dat de balk halverwege verdwijnt.
    try {
      const s = await (await fetch('/api/status')).json();
      if (s.detail) renderProgress(s.detail);
    } catch { /* afronding mag stil mislukken */ }

    setTimeout(() => { if (!state.searching) $('#progress').hidden = true; }, 1200);
  }
}

async function loadFacets() {
  try {
    const f = await (await fetch('/api/facets')).json();
    fillSelect('#villageFilter', f.villages, 'Alle plaatsen');
    fillSelect('#categoryFilter', f.categories, 'Alle categorieën');
  } catch { /* facetten zijn optioneel */ }
}

function fillSelect(sel, values, allLabel) {
  const el = $(sel);
  const current = el.value;
  el.innerHTML = `<option value="*">${esc(allLabel)}</option>` +
    (values || []).map((v) => `<option value="${esc(v)}">${esc(v)}</option>`).join('');
  if (current && Array.from(el.options).some((o) => o.value === current)) el.value = current;
}

function updateRangeLabel() {
  const from = $('#fromDate').value, to = $('#toDate').value;
  const same = from === to;
  const when = state.report ? ` · gezocht op ${formatDateTimeNl(state.report.searchedAt)}` : '';
  $('#rangeLabel').textContent = same
    ? `Periode: ${formatDateNl(from)}${when}`
    : `Periode: ${formatDateNl(from)} t/m ${formatDateNl(to)}${when}`;
}

function showAlert(msg) { const a = $('#alert'); a.textContent = msg; a.hidden = false; }
function hideAlert() { $('#alert').hidden = true; }

/* ----------------------------------------------------------------- init */
function initTheme() {
  const saved = localStorage.getItem('ts-theme');
  const prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
  document.documentElement.dataset.theme = saved || (prefersDark ? 'dark' : 'light');
  $('#themeToggle').addEventListener('click', () => {
    const next = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
    document.documentElement.dataset.theme = next;
    localStorage.setItem('ts-theme', next);
  });
}

function init() {
  initTheme();
  applyQuickRange('14');

  $('#quickRange').addEventListener('change', (e) => {
    if (e.target.value) { applyQuickRange(e.target.value); applyFilters(); }
  });
  ['#fromDate', '#toDate'].forEach((s) => $(s).addEventListener('change', () => {
    $('#quickRange').value = '';
    if ($('#fromDate').value && $('#toDate').value &&
        $('#toDate').value < $('#fromDate').value) $('#toDate').value = $('#fromDate').value;
    applyFilters();
  }));

  $('#searchBtn').addEventListener('click', runSearch);

  ['#villageFilter', '#categoryFilter', '#priceFilter', '#confidenceFilter',
   '#reservationFilter', '#sortSelect'].forEach((s) =>
    $(s).addEventListener('change', applyFilters));

  let t;
  $('#qFilter').addEventListener('input', () => { clearTimeout(t); t = setTimeout(applyFilters, 260); });

  $('#resetBtn').addEventListener('click', () => {
    ['#villageFilter', '#categoryFilter', '#priceFilter', '#confidenceFilter', '#reservationFilter']
      .forEach((s) => { $(s).value = '*'; });
    $('#sortSelect').value = 'datum';
    $('#qFilter').value = '';
    applyFilters();
  });

  $$('.toggle-btn').forEach((b) => b.addEventListener('click', () => {
    $$('.toggle-btn').forEach((x) => { x.classList.remove('is-active'); x.setAttribute('aria-pressed', 'false'); });
    b.classList.add('is-active');
    b.setAttribute('aria-pressed', 'true');
    state.view = b.dataset.view;
    renderResults();
  }));

  $('#maxQueries').addEventListener('input', (e) => { $('#maxQueriesOut').textContent = e.target.value; });
  $('#maxPages').addEventListener('input', (e) => { $('#maxPagesOut').textContent = e.target.value; });

  $('#results').addEventListener('click', (e) => {
    const btn = e.target.closest('.js-detail');
    if (btn) openDetail(btn.dataset.id);
  });

  const dlg = $('#detailDialog');
  dlg.querySelector('.dialog-close').addEventListener('click', () => dlg.close());
  dlg.addEventListener('click', (e) => { if (e.target === dlg) dlg.close(); });

  updateRangeLabel();
  loadFacets();
  applyFilters();
}

document.addEventListener('DOMContentLoaded', init);
