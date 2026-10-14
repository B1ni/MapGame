(() => {
  const entries = [];
  const clientId = globalThis.crypto?.randomUUID?.()
    || `client-${Date.now()}-${Math.random().toString(16).slice(2)}`;
  let uploadWarningShown = false;
  function record(event, detail = {}, upload = true) {
    const entry = { timestampUtc: new Date().toISOString(), event, detail };
    entries.push(entry);
    if (entries.length > 250) entries.shift();
    if (!upload) return;
    void fetch("/api/diagnostics", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ clientId, event, detail: JSON.stringify(detail).slice(0, 1500) }),
      keepalive: true
    }).then(response => {
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
    }).catch(error => {
      if (!uploadWarningShown) {
        uploadWarningShown = true;
        console.warn("Diagnostiku sa nepodarilo odoslať; zostáva dostupná na stiahnutie.", error);
        record("diagnostic-upload-failed", { message: error.message }, false);
      }
    });
  }
  window.mapGameDiagnostics = {
    clientId,
    record,
    download() {
      record("diagnostic-export", {}, false);
      const report = {
        clientId, timestampUtc: new Date().toISOString(),
        origin: location.origin,
        room: new URLSearchParams(location.search).get("room"),
        userAgent: navigator.userAgent, online: navigator.onLine,
        entries
      };
      const url = URL.createObjectURL(new Blob([JSON.stringify(report, null, 2)], { type: "application/json" }));
      const link = document.createElement("a");
      link.href = url;
      link.download = `MapGame-klient-${Date.now()}.json`;
      link.click();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    }
  };
  window.addEventListener("error", event => record("browser-error", {
    message: event.message || "Resource failed to load",
    file: event.filename || event.target?.src || event.target?.href,
    line: event.lineno, stack: event.error?.stack?.slice(0, 1000)
  }), true);
  window.addEventListener("unhandledrejection", event => record("promise-error", {
    message: String(event.reason?.message || event.reason).slice(0, 1000)
  }));
  window.addEventListener("online", () => record("network-online"));
  window.addEventListener("offline", () => record("network-offline"));
  record("page-open", { origin: location.origin, room: new URLSearchParams(location.search).get("room"),
    online: navigator.onLine, userAgent: navigator.userAgent });
})();
