// Painel mínimo do Brymen DMM REST: tudo local (o Raspberry Pi pode estar sem internet).
"use strict";

const api = "api/v1";
const $ = (id) => document.getElementById(id);
const readings = [];
const HISTORY_SIZE = 300;
let linkEnabled = false;
let comparator = null;
let audio = null;

async function request(method, path, body) {
  const init = { method, headers: { Accept: "application/json" } };
  if (body !== undefined) {
    init.headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(body);
  }
  const response = await fetch(`${api}${path}`, init);
  const text = await response.text();
  let data = null;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    // Resposta que não é JSON (ex.: página de erro de um proxy): vale o status HTTP.
  }
  if (!response.ok) {
    const code = data && data.code ? `[${data.code}] ` : "";
    const detail = data ? (data.detail || data.title) : response.statusText;
    throw new Error(`${code}${detail}`);
  }
  return data;
}

function toast(message) {
  const el = $("toast");
  el.textContent = message;
  el.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => { el.hidden = true; }, 6000);
}

async function run(action) {
  try {
    await action();
  } catch (error) {
    toast(error.message);
  }
}

// ---- Medidor ----

const stateLabels = {
  unlinked: ["Sem link", ""],
  connecting: ["Conectando", "warn"],
  connected: ["Conectado", "ok"],
  reconnecting: ["Reconectando", "error"],
};

function renderMeter(status) {
  linkEnabled = status.linkEnabled;
  const [label, cls] = stateLabels[status.state] || [status.state, ""];
  const badge = $("link-state");
  badge.textContent = status.importing ? "Importando" : label;
  badge.className = `badge ${status.importing ? "warn" : cls}`;
  badge.title = status.lastError ? `[${status.lastError.code}] ${status.lastError.message}` : (status.device ? status.device.path : "");
  $("link-toggle").textContent = linkEnabled ? "Unlink" : "Link";
}

async function refreshMeter() {
  renderMeter(await request("GET", "/meter"));
}

function formatDisplay(prefix, display) {
  $(`${prefix}-text`).textContent = display ? display.text : "";
  $(`${prefix}-unit`).textContent = display ? display.unitSymbol : "";
  $(`${prefix}-mode`).textContent = display && display.mode !== "NONE" ? display.mode : "";
}

function renderReading(reading) {
  formatDisplay("main", reading.main);
  formatDisplay("sub", reading.secondary);
  $("flags").replaceChildren(...reading.flags.map((name) => {
    const span = document.createElement("span");
    span.className = "flag";
    span.textContent = name;
    return span;
  }));
  $("reading-time").textContent = new Date(reading.timestamp).toLocaleTimeString("pt-BR");

  const result = $("comparator-result");
  result.hidden = !reading.comparator;
  if (reading.comparator) {
    result.textContent = reading.comparator;
    result.className = `badge ${reading.comparator}`;
    if (comparator && comparator.alarm && reading.comparator !== "Pass") {
      beep(reading.comparator === "High" ? 1047 : 523);
    }
  }

  const value = reading.main ? reading.main.siValue : null;
  readings.push(value);
  if (readings.length > HISTORY_SIZE) readings.shift();
  drawChart();
}

// O navegador só libera o áudio depois de um gesto do usuário: o contexto é criado (ou retomado) nos cliques.
function unlockAudio() {
  try {
    audio = audio || new AudioContext();
    if (audio.state === "suspended") audio.resume();
  } catch {
    // Sem Web Audio: o alarme fica só visual.
  }
}

// Bipes do alarme do original: 1047 Hz acima de Hi, 523 Hz abaixo de Lo, 100 ms.
function beep(frequency) {
  try {
    audio = audio || new AudioContext();
    if (audio.state !== "running") return;
    const oscillator = audio.createOscillator();
    oscillator.frequency.value = frequency;
    oscillator.connect(audio.destination);
    oscillator.start();
    oscillator.stop(audio.currentTime + 0.1);
  } catch {
    // Sem áudio disponível: o resultado continua visível no painel.
  }
}

function drawChart() {
  const canvas = $("chart");
  const ratio = window.devicePixelRatio || 1;
  const width = canvas.clientWidth;
  const height = canvas.clientHeight;
  const bitmapWidth = Math.round(width * ratio);
  const bitmapHeight = Math.round(height * ratio);
  if (canvas.width !== bitmapWidth || canvas.height !== bitmapHeight) {
    canvas.width = bitmapWidth;
    canvas.height = bitmapHeight;
  }
  const ctx = canvas.getContext("2d");
  ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
  ctx.clearRect(0, 0, width, height);

  const style = getComputedStyle(document.documentElement);
  const values = readings.filter((v) => v !== null);
  ctx.strokeStyle = style.getPropertyValue("--chart-grid");
  ctx.lineWidth = 1;
  for (let i = 1; i < 4; i++) {
    const y = Math.round((height * i) / 4) + 0.5;
    ctx.beginPath();
    ctx.moveTo(0, y);
    ctx.lineTo(width, y);
    ctx.stroke();
  }
  if (values.length < 2) return;

  let min = Math.min(...values);
  let max = Math.max(...values);
  if (min === max) { min -= 0.5; max += 0.5; }
  const pad = (max - min) * 0.1;
  min -= pad;
  max += pad;

  ctx.strokeStyle = style.getPropertyValue("--chart-line");
  ctx.lineWidth = 2;
  ctx.beginPath();
  // A leitura mais recente fica sempre na borda direita.
  const offset = HISTORY_SIZE - readings.length;
  let started = false;
  readings.forEach((v, i) => {
    if (v === null) { started = false; return; }
    const x = ((i + offset) / (HISTORY_SIZE - 1)) * width;
    const y = height - ((v - min) / (max - min)) * height;
    if (started) ctx.lineTo(x, y); else ctx.moveTo(x, y);
    started = true;
  });
  ctx.stroke();

  ctx.fillStyle = style.getPropertyValue("--muted");
  ctx.font = "12px system-ui, sans-serif";
  ctx.fillText(max.toPrecision(4), 4, 14);
  ctx.fillText(min.toPrecision(4), 4, height - 4);
}

function connectStream() {
  const source = new EventSource(`${api}/readings/stream`);
  source.addEventListener("reading", (event) => renderReading(JSON.parse(event.data)));
  source.onerror = () => {
    // O EventSource reconecta sozinho; só atualiza o estado do link.
    run(refreshMeter);
  };
}

// ---- Aquisição ----

// Campos editados e ainda não aplicados: a atualização periódica não pode desfazer o que o usuário digitou.
let acqDirty = false;

async function refreshAcquisition() {
  const status = await request("GET", "/acquisition");
  if (!acqDirty) {
    $("acq-mode").value = status.mode;
    $("acq-interval").value = status.intervalSeconds;
  }
  const recording = status.state === "recording";
  $("acq-start").disabled = recording;
  $("acq-stop").disabled = !recording;
  $("acq-manual").disabled = !(recording && status.mode === "manual");
  $("acq-apply").disabled = recording;
  let text = `${recording ? "Gravando" : "Parado"} · ${status.sampleCount} de ${status.maxSamples} amostras`;
  if (status.bufferFull) text += " · buffer cheio";
  if (status.mainRange) {
    const range = status.mainRange;
    text += ` · principal ${range.min} a ${range.max} ${range.unit}${range.mode && range.mode !== "NONE" ? " " + range.mode : ""}`;
  }
  if (status.backupFile) text += ` · backup ${status.backupFile}`;
  if (status.backupError) text += ` · [${status.backupError.code}] ${status.backupError.message}`;
  $("acq-status").textContent = text;
}

// ---- Comparador ----

async function refreshComparator() {
  comparator = await request("GET", "/comparator");
  $("cmp-enabled").checked = comparator.enabled;
  $("cmp-alarm").checked = comparator.alarm;
  $("cmp-high").value = comparator.high;
  $("cmp-low").value = comparator.low;
}

async function saveComparator() {
  comparator = await request("PUT", "/comparator", {
    enabled: $("cmp-enabled").checked,
    alarm: $("cmp-alarm").checked,
    high: Number($("cmp-high").value),
    low: Number($("cmp-low").value),
  });
  toast("Comparador salvo.");
}

// ---- Datalogger ----

async function importLogger() {
  const job = await request("POST", "/logger/imports");
  await followImport(job.id);
}

const importStates = { running: "Em andamento", completed: "Concluída", failed: "Falhou", canceled: "Cancelada" };

async function followImport(id) {
  $("log-import").disabled = true;
  $("log-progress").hidden = false;
  try {
    for (;;) {
      const job = await request("GET", `/logger/imports/${id}`);
      $("log-progress").value = job.progress.percent;
      $("log-status").textContent = `${importStates[job.state] || job.state} · ${job.progress.blocksRead}/${job.progress.blocksTotal} blocos`
        + ` · ${job.progress.retries} retentativa(s), ${job.progress.restarts} recomeço(s)`;
      if (job.state !== "running") {
        if (job.error) toast(`[${job.error.code}] ${job.error.message}`);
        if (job.state === "completed") await showSessions(job);
        return;
      }
      await new Promise((resolve) => setTimeout(resolve, 300));
    }
  } finally {
    // Também numa falha de rede no meio do acompanhamento: o botão não pode ficar travado.
    $("log-import").disabled = false;
    $("log-progress").hidden = true;
  }
}

async function showSessions(job) {
  const sessions = await request("GET", `/logger/imports/${job.id}/sessions`);
  $("log-status").textContent = `Concluída: ${sessions.length} sessão(ões), ${job.sampleCount} amostra(s).`;
  $("log-sessions").replaceChildren(...sessions.map((s) => {
    const li = document.createElement("li");
    const secondary = s.secondaryFunction ? ` + ${s.secondaryFunction}` : "";
    li.append(`Sessão ${s.number}: ${s.mainFunction}${secondary}, ${s.sampleCount} amostras a cada ${s.intervalSeconds} s · `);
    const link = document.createElement("a");
    link.href = `${api}/logger/imports/${job.id}/sessions/${s.number}/samples.csv`;
    link.textContent = "CSV";
    link.download = "";
    li.append(link);
    return li;
  }));
}

async function loadLastImport() {
  const jobs = await request("GET", "/logger/imports");
  const running = jobs.find((j) => j.state === "running");
  if (running) return followImport(running.id);
  const last = jobs.find((j) => j.state === "completed");
  if (last) await showSessions(last);
}

// ---- Eventos ----

$("link-toggle").addEventListener("click", () => run(async () => {
  renderMeter(await request("PUT", "/meter/link", { enabled: !linkEnabled }));
}));
$("acq-mode").addEventListener("change", () => { acqDirty = true; });
$("acq-interval").addEventListener("input", () => { acqDirty = true; });
$("acq-apply").addEventListener("click", () => run(async () => {
  await request("PUT", "/acquisition/settings", { mode: $("acq-mode").value, intervalSeconds: Number($("acq-interval").value) });
  acqDirty = false;
  await refreshAcquisition();
}));
$("acq-start").addEventListener("click", () => run(async () => { await request("POST", "/acquisition/start"); await refreshAcquisition(); }));
$("acq-stop").addEventListener("click", () => run(async () => { await request("POST", "/acquisition/stop"); await refreshAcquisition(); }));
$("acq-manual").addEventListener("click", () => run(async () => { await request("POST", "/acquisition/samples"); await refreshAcquisition(); }));
$("acq-clear").addEventListener("click", () => run(async () => { await request("DELETE", "/acquisition/samples"); await refreshAcquisition(); }));
$("cmp-save").addEventListener("click", () => { unlockAudio(); run(saveComparator); });
$("cmp-alarm").addEventListener("change", unlockAudio);
document.addEventListener("pointerdown", unlockAudio, { once: true });
$("log-import").addEventListener("click", () => run(importLogger));
window.addEventListener("resize", drawChart);

run(refreshMeter);
run(refreshAcquisition);
run(refreshComparator);
run(loadLastImport);
connectStream();
setInterval(() => { run(refreshMeter); run(refreshAcquisition); }, 2000);
