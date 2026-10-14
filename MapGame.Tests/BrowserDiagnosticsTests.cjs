const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

function createDiagnostics(fetch) {
  const window = { listeners: {}, addEventListener(type, handler) { this.listeners[type] = handler; } };
  let report;
  let download;
  const URLStub = {
    createObjectURL(blob) { report = blob; return "blob:test"; },
    revokeObjectURL() {}
  };
  const context = vm.createContext({
    window, fetch, console, Date, Math, Blob, URL: URLStub, URLSearchParams,
    navigator: { userAgent: "test-browser", onLine: true },
    location: { origin: "http://192.168.1.25:5080", search: "?room=ABC234" },
    setTimeout() {},
    document: { createElement() { return { click() { download = this.download; } }; } }
  });
  vm.runInContext(fs.readFileSync(path.join(__dirname, "..", "MapGame", "wwwroot", "diagnostics.js"), "utf8"), context);
  return { window, getReport: () => report, getDownload: () => download };
}

test("browser diagnostics exports failures even when the server cannot receive them", async () => {
  const c = createDiagnostics(async () => { throw new Error("Network unreachable"); });
  await new Promise(resolve => setImmediate(resolve));
  c.window.listeners.error({ message: "WebSocket failed", filename: "app.js", lineno: 1 });
  c.window.mapGameDiagnostics.record("socket-close", { code: 1006 });
  c.window.mapGameDiagnostics.download();
  const report = JSON.parse(await c.getReport().text());
  assert.equal(report.origin, "http://192.168.1.25:5080");
  assert.equal(report.room, "ABC234");
  assert.ok(report.entries.some(entry => entry.event === "diagnostic-upload-failed"));
  assert.ok(report.entries.some(entry => entry.event === "browser-error"));
  assert.ok(report.entries.some(entry => entry.event === "socket-close"));
  assert.match(c.getDownload(), /^MapGame-klient-\d+\.json$/);
});

test("browser diagnostics bounds history and sends correlated client ID", async () => {
  const uploads = [];
  const c = createDiagnostics(async (url, request) => {
    uploads.push(JSON.parse(request.body));
    return { ok: true };
  });
  for (let i = 0; i < 260; i++) c.window.mapGameDiagnostics.record("event", { index: i }, false);
  c.window.mapGameDiagnostics.download();
  const report = JSON.parse(await c.getReport().text());
  assert.equal(report.entries.length, 250);
  assert.equal(uploads[0].clientId, report.clientId);
  assert.equal(uploads[0].event, "page-open");
});
